using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using FabrCore.Sdk;
using OpenCaddis.Server;
using OpenCaddis.Server.Builder;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class OpenCaddisServerHostTests
{
    [TestMethod]
    public async Task Host_can_start_stop_and_restart_on_the_configured_port()
    {
        var port = FindAvailablePort();
        var baseUri = new Uri($"http://localhost:{port}/");
        var addOnPath = Path.Combine(Path.GetTempPath(), "OpenCaddis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(addOnPath);
        using var client = new HttpClient { BaseAddress = baseUri };
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(OpenCaddisCloudTarget.Server);
        var options = new OpenCaddisServerHostOptions
        {
            CloudServer = cloud.Connection(OpenCaddisCloudTarget.Server)
        };

        try
        {
            await using (var firstHost = OpenCaddisServerHost.Create(baseUri, addOnPath, options))
            {
                await firstHost.StartAsync();
                var homePage = await client.GetStringAsync(string.Empty);
                Assert.Contains("OpenCaddis Server", homePage);

                using var surfaceResponse = await client.GetAsync("surface");
                Assert.AreEqual(HttpStatusCode.OK, surfaceResponse.StatusCode);
                var surfacePage = await surfaceResponse.Content.ReadAsStringAsync();
                Assert.Contains("_framework/blazor.web.js", surfacePage);
                Assert.Contains("surface-command-center-standalone", surfacePage);

                using var surfaceCssResponse = await client.GetAsync("_content/FabrCore.Surface/surface.css");
                Assert.AreEqual(HttpStatusCode.OK, surfaceCssResponse.StatusCode);
                Assert.AreEqual("text/css", surfaceCssResponse.Content.Headers.ContentType?.MediaType);
                var surfaceCss = await surfaceCssResponse.Content.ReadAsStringAsync();
                Assert.Contains(".surface-command-center", surfaceCss);

                using var adaptiveCardsScriptResponse =
                    await client.GetAsync("_content/FabrCore.Surface/adaptiveCardsSurface.js");
                Assert.AreEqual(HttpStatusCode.OK, adaptiveCardsScriptResponse.StatusCode);
                Assert.AreEqual(
                    "text/javascript",
                    adaptiveCardsScriptResponse.Content.Headers.ContentType?.MediaType);

                using var blazorScriptResponse = await client.GetAsync("_framework/blazor.web.js");
                Assert.AreEqual(HttpStatusCode.OK, blazorScriptResponse.StatusCode);

                using var discoveryResponse = await client.GetAsync("fabrcoreapi/discovery");
                Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);

                using var healthResponse = await client.GetAsync("health");
                Assert.AreEqual(HttpStatusCode.OK, healthResponse.StatusCode);
                await firstHost.StopAsync();
            }

            await using (var restartedHost = OpenCaddisServerHost.Create(baseUri, addOnPath, options))
            {
                await restartedHost.StartAsync();
                using var response = await client.GetAsync(string.Empty);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                await restartedHost.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(addOnPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Restart_loads_only_assemblies_currently_in_the_add_on_path()
    {
        var port = FindAvailablePort();
        var baseUri = new Uri($"http://localhost:{port}/");
        var addOnPath = Path.Combine(Path.GetTempPath(), "OpenCaddis.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(addOnPath);
        var addOnAssemblyPath = Path.Combine(addOnPath, "MSTest.TestFramework.dll");
        File.Copy(typeof(Assert).Assembly.Location, addOnAssemblyPath);
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(OpenCaddisCloudTarget.Server);
        var options = new OpenCaddisServerHostOptions
        {
            CloudServer = cloud.Connection(OpenCaddisCloudTarget.Server)
        };

        try
        {
            await using (var firstHost = OpenCaddisServerHost.Create(baseUri, addOnPath, options))
            {
                Assert.HasCount(1, firstHost.AdditionalAssemblies);
                Assert.AreEqual("MSTest.TestFramework", firstHost.AdditionalAssemblies[0].GetName().Name);
                await firstHost.StartAsync();
                await firstHost.StopAsync();
            }

            // Loading from streams leaves package files replaceable while the app is running.
            File.Delete(addOnAssemblyPath);

            await using (var restartedHost = OpenCaddisServerHost.Create(baseUri, addOnPath, options))
            {
                Assert.IsEmpty(restartedHost.AdditionalAssemblies);
                await restartedHost.StartAsync();
                await restartedHost.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(addOnPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Host_loads_add_on_compiled_against_fabrcore_sdk_with_shared_orleans_dependencies()
    {
        var port = FindAvailablePort();
        var baseUri = new Uri($"http://localhost:{port}/");
        var addOnPath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.FabrCore.AddOn.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(addOnPath);
        File.Copy(
            typeof(OpenCaddisServerBuilderHost).Assembly.Location,
            Path.Combine(addOnPath, "OpenCaddis.Server.Builder.dll"));
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(OpenCaddisCloudTarget.Server);

        try
        {
            await using var host = OpenCaddisServerHost.Create(
                baseUri,
                addOnPath,
                new OpenCaddisServerHostOptions
                {
                    CloudServer = cloud.Connection(OpenCaddisCloudTarget.Server)
                });

            Assert.HasCount(1, host.AdditionalAssemblies);
            Assert.AreEqual("OpenCaddis.Server.Builder", host.AdditionalAssemblies[0].GetName().Name);
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = baseUri };
            var discovery = await client.GetStringAsync("fabrcoreapi/discovery");
            Assert.Contains("addon-builder-agent", discovery);
            await host.StopAsync();
        }
        finally
        {
            Directory.Delete(addOnPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Host_discovers_collectible_add_on_without_default_context_reload()
    {
        var port = FindAvailablePort();
        var baseUri = new Uri($"http://localhost:{port}/");
        var addOnPath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Collectible.AddOn.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(addOnPath);
        var assemblyName = $"OpenCaddis.GeneratedAddOn.{Guid.NewGuid():N}";
        var agentAlias = $"generated-agent-{Guid.NewGuid():N}";
        CreateManagedAssembly(
            Path.Combine(addOnPath, $"{assemblyName}.dll"),
            assemblyName,
            agentAlias);
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(OpenCaddisCloudTarget.Server);

        try
        {
            await using var host = OpenCaddisServerHost.Create(
                baseUri,
                addOnPath,
                new OpenCaddisServerHostOptions
                {
                    CloudServer = cloud.Connection(OpenCaddisCloudTarget.Server)
                });

            Assert.HasCount(1, host.AdditionalAssemblies);
            Assert.AreEqual(assemblyName, host.AdditionalAssemblies[0].GetName().Name);
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = baseUri };
            var discovery = await client.GetStringAsync("fabrcoreapi/discovery");
            Assert.Contains(agentAlias, discovery);
            await host.StopAsync();
        }
        finally
        {
            Directory.Delete(addOnPath, recursive: true);
        }
    }

    private static void CreateManagedAssembly(
        string assemblyPath,
        string assemblyName,
        string agentAlias)
    {
        var builder = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName),
            typeof(object).Assembly);
        var module = builder.DefineDynamicModule(assemblyName);
        var type = module.DefineType(
            $"{assemblyName}.Marker",
            TypeAttributes.Public | TypeAttributes.Class);
        var aliasConstructor = typeof(AgentAliasAttribute).GetConstructor([typeof(string)])
            ?? throw new InvalidOperationException("AgentAliasAttribute constructor was not found.");
        type.SetCustomAttribute(new CustomAttributeBuilder(aliasConstructor, [agentAlias]));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.CreateType();
        builder.Save(assemblyPath);
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using OpenCaddis.Sdk.Addons;
using OpenCaddis.Sdk.Connections;
using OpenCaddis.Server;
using OpenCaddis.Server.Builder;
using OpenCaddis.Server.Connections;

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

    [TestMethod]
    public async Task Collectible_add_on_registers_provider_neutral_connections_and_unregisters_on_dispose()
    {
        var port = FindAvailablePort();
        var baseUri = new Uri($"http://localhost:{port}/");
        var root = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Connection.AddOn.Tests",
            Guid.NewGuid().ToString("N"));
        var addOnPath = Path.Combine(root, "AddOns");
        var connectionPath = Path.Combine(root, "Connections");
        Directory.CreateDirectory(addOnPath);
        await CompileConnectionAddonAsync(root, addOnPath);
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(OpenCaddisCloudTarget.Server);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var runtime = new OpenCaddisConnectionRuntime(
            connectionPath,
            new TestSecretStore(),
            new TestBrowser(),
            services);
        var options = new OpenCaddisServerHostOptions
        {
            CloudServer = cloud.Connection(OpenCaddisCloudTarget.Server),
            ConnectionRuntime = runtime
        };

        try
        {
            await using (var host = OpenCaddisServerHost.Create(baseUri, addOnPath, options))
            {
                Assert.HasCount(1, host.AdditionalAssemblies);
                Assert.AreEqual(3, runtime.GetProviders().Count);
                Assert.AreEqual(3, runtime.GetRequirements().Count);
                Assert.IsTrue(runtime.GetProviders().Any(provider => provider.Id == "synthetic-microsoft"));
                Assert.IsTrue(runtime.GetProviders().Any(provider => provider.Id == "synthetic-google"));
                Assert.IsTrue(runtime.GetProviders().Any(provider => provider.Id == "synthetic-service"));
            }

            Assert.IsEmpty(runtime.GetProviders());
            Assert.IsEmpty(runtime.GetRequirements());

            await using (var restarted = OpenCaddisServerHost.Create(baseUri, addOnPath, options))
            {
                Assert.AreEqual(3, runtime.GetProviders().Count);
                Assert.AreEqual(3, runtime.GetRequirements().Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CompileConnectionAddonAsync(string root, string addOnPath)
    {
        var projectPath = Path.Combine(root, "SyntheticConnections.csproj");
        var sourcePath = Path.Combine(root, "SyntheticConnections.cs");
        var sdkPath = SecurityElement.Escape(typeof(IOpenCaddisAddonModule).Assembly.Location);
        var fabrCorePath = SecurityElement.Escape(typeof(IFabrCorePlugin).Assembly.Location);
        await File.WriteAllTextAsync(projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="OpenCaddis.Sdk" HintPath="{{sdkPath}}" Private="false" />
                <Reference Include="FabrCore.Sdk" HintPath="{{fabrCorePath}}" Private="false" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(sourcePath, """
            using OpenCaddis.Sdk.Addons;
            using OpenCaddis.Sdk.Connections;

            public sealed class SyntheticConnectionsModule : IOpenCaddisAddonModule
            {
                public void Configure(OpenCaddisAddonBuilder builder)
                {
                    builder.SetIdentity("synthetic.connections", "Synthetic connections", "1.0.0")
                        .AddConnectionProvider(Delegated(
                            "synthetic-microsoft",
                            "Synthetic Microsoft",
                            "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                            "https://login.microsoftonline.com/common/oauth2/v2.0/token"))
                        .AddConnectionProvider(Delegated(
                            "synthetic-google",
                            "Synthetic Google",
                            "https://accounts.google.com/o/oauth2/v2/auth",
                            "https://oauth2.googleapis.com/token"))
                        .AddConnectionProvider(new ConnectionProviderDescriptor
                        {
                            Id = "synthetic-service",
                            DisplayName = "Synthetic service",
                            AuthenticationKind = ConnectionAuthenticationKind.OAuthClientCredentials,
                            TokenEndpoint = new Uri("https://identity.example.test/token"),
                            ConfigurationFields = ClientFields()
                        })
                        .AddConnectionRequirement(Requirement(
                            "synthetic.connections.microsoft", "synthetic-microsoft", "Mail.Read"))
                        .AddConnectionRequirement(Requirement(
                            "synthetic.connections.gmail", "synthetic-google",
                            "https://www.googleapis.com/auth/gmail.readonly"))
                        .AddConnectionRequirement(Requirement(
                            "synthetic.connections.service", "synthetic-service", "records.read"));
                }

                private static ConnectionProviderDescriptor Delegated(
                    string id, string name, string authorizationEndpoint, string tokenEndpoint) => new()
                {
                    Id = id,
                    DisplayName = name,
                    AuthenticationKind = ConnectionAuthenticationKind.OAuthAuthorizationCodePkce,
                    AuthorizationEndpoint = new Uri(authorizationEndpoint),
                    TokenEndpoint = new Uri(tokenEndpoint),
                    ConfigurationFields =
                    [
                        new ConnectionConfigurationField
                        {
                            Name = "clientId", Label = "Client ID", Required = true
                        }
                    ]
                };

                private static IReadOnlyList<ConnectionConfigurationField> ClientFields() =>
                [
                    new ConnectionConfigurationField
                    {
                        Name = "clientId", Label = "Client ID", Required = true
                    },
                    new ConnectionConfigurationField
                    {
                        Name = "clientSecret", Label = "Client secret", Required = true,
                        Kind = ConnectionConfigurationFieldKind.Secret, Sensitive = true
                    }
                ];

                private static ConnectionRequirement Requirement(string id, string providerId, string scope) => new()
                {
                    Id = id,
                    AddonId = "synthetic.connections",
                    ProviderId = providerId,
                    DisplayName = id,
                    Purpose = "Synthetic acceptance coverage",
                    Scopes = [scope]
                };
            }
            """);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "build", projectPath, "--configuration", "Release", "--output", addOnPath,
                     "--nologo"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet to build the synthetic add-on.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask + await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Synthetic connection add-on build failed: {output}");
        }
    }

    private sealed class TestSecretStore : IOpenCaddisSecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.Remove(key));
    }

    private sealed class TestBrowser : IOpenCaddisInteractiveBrowser
    {
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

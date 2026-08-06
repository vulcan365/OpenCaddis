using System.Net;
using System.Net.Sockets;
using OpenCaddis.Server;

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

        try
        {
            await using (var firstHost = OpenCaddisServerHost.Create(baseUri, addOnPath))
            {
                await firstHost.StartAsync();
                var homePage = await client.GetStringAsync(string.Empty);
                Assert.Contains("OpenCaddis Server", homePage);

                using var surfaceResponse = await client.GetAsync("surface");
                Assert.AreEqual(HttpStatusCode.OK, surfaceResponse.StatusCode);
                var surfacePage = await surfaceResponse.Content.ReadAsStringAsync();
                Assert.Contains("_framework/blazor.web.js", surfacePage);
                Assert.Contains("surface-command-center", surfacePage);

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

            await using (var restartedHost = OpenCaddisServerHost.Create(baseUri, addOnPath))
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

        try
        {
            await using (var firstHost = OpenCaddisServerHost.Create(baseUri, addOnPath))
            {
                Assert.HasCount(1, firstHost.AdditionalAssemblies);
                Assert.AreEqual("MSTest.TestFramework", firstHost.AdditionalAssemblies[0].GetName().Name);
                await firstHost.StartAsync();
                await firstHost.StopAsync();
            }

            // Loading from streams leaves package files replaceable while the app is running.
            File.Delete(addOnAssemblyPath);

            await using (var restartedHost = OpenCaddisServerHost.Create(baseUri, addOnPath))
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

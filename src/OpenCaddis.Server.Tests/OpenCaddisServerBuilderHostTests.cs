using System.Net;
using System.Net.Sockets;
using OpenCaddis.Server.Builder;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class OpenCaddisServerBuilderHostTests
{
    [TestMethod]
    public async Task Builder_host_serves_the_same_fabrcore_and_surface_endpoints()
    {
        var port = FindAvailablePort();
        var baseUri = new Uri($"http://localhost:{port}/");
        var addOnPath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Builder.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(addOnPath);
        using var client = new HttpClient { BaseAddress = baseUri };

        try
        {
            await using var host = OpenCaddisServerBuilderHost.Create(baseUri, addOnPath);
            Assert.AreEqual("OpenCaddis Server Builder", host.DisplayName);
            Assert.AreEqual(Path.GetFullPath(addOnPath), host.AddOnPath);

            await host.StartAsync();

            var homePage = await client.GetStringAsync(string.Empty);
            Assert.Contains("OpenCaddis Server Builder", homePage);

            using var surfaceResponse = await client.GetAsync("surface");
            Assert.AreEqual(HttpStatusCode.OK, surfaceResponse.StatusCode);
            var surfacePage = await surfaceResponse.Content.ReadAsStringAsync();
            Assert.Contains("surface-command-center", surfacePage);

            using var discoveryResponse = await client.GetAsync("fabrcoreapi/discovery");
            Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);

            using var addOnsResponse = await client.GetAsync("addons");
            Assert.AreEqual(HttpStatusCode.OK, addOnsResponse.StatusCode);

            await host.StopAsync();
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

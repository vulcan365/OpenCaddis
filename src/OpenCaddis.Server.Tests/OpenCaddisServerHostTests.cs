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
        using var client = new HttpClient { BaseAddress = baseUri };

        await using (var firstHost = OpenCaddisServerHost.Create(baseUri))
        {
            await firstHost.StartAsync();
            var homePage = await client.GetStringAsync(string.Empty);
            Assert.Contains("OpenCaddis Server", homePage);

            using var healthResponse = await client.GetAsync("health");
            Assert.AreEqual(HttpStatusCode.OK, healthResponse.StatusCode);
            await firstHost.StopAsync();
        }

        await using (var restartedHost = OpenCaddisServerHost.Create(baseUri))
        {
            await restartedHost.StartAsync();
            using var response = await client.GetAsync(string.Empty);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await restartedHost.StopAsync();
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

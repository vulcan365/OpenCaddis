using System.Net;
using System.Net.Http.Json;
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
        File.Copy(
            typeof(Assert).Assembly.Location,
            Path.Combine(addOnPath, "MSTest.TestFramework.dll"));
        using var client = new HttpClient { BaseAddress = baseUri };
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(
            OpenCaddis.Server.OpenCaddisCloudTarget.ServerBuilder);

        try
        {
            await using var host = OpenCaddisServerBuilderHost.Create(
                baseUri,
                addOnPath,
                cloud.Connection(OpenCaddis.Server.OpenCaddisCloudTarget.ServerBuilder));
            Assert.AreEqual("OpenCaddis Server Builder", host.DisplayName);
            Assert.AreEqual(Path.GetFullPath(addOnPath), host.AddOnPath);
            Assert.IsEmpty(
                host.AdditionalAssemblies,
                "Builder must not load assemblies from its publish-output directory.");

            await host.StartAsync();

            var homePage = await client.GetStringAsync(string.Empty);
            Assert.Contains("OpenCaddis Server Builder", homePage);

            using var surfaceResponse = await client.GetAsync("surface");
            Assert.AreEqual(HttpStatusCode.OK, surfaceResponse.StatusCode);
            var surfacePage = await surfaceResponse.Content.ReadAsStringAsync();
            Assert.Contains("surface-command-center", surfacePage);

            using var discoveryResponse = await client.GetAsync("fabrcoreapi/discovery");
            Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);
            var discovery = await discoveryResponse.Content.ReadAsStringAsync();
            Assert.Contains("addon-builder-agent", discovery);
            Assert.Contains("roslyn-code", discovery);
            Assert.Contains("dotnet-cli", discovery);
            Assert.Contains("project-files", discovery);

            using var createAgentRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "fabrcoreapi/agent/create?detailLevel=Detailed");
            createAgentRequest.Headers.Add("x-user-handle", "local-user");
            createAgentRequest.Content = JsonContent.Create(Array.Empty<object>());
            using var createAgentResponse = await client.SendAsync(createAgentRequest);
            Assert.AreEqual(HttpStatusCode.BadRequest, createAgentResponse.StatusCode);

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

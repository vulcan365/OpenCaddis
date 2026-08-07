using FabrCore.Core.CloudServer;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OpenCaddis.Server;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class OpenCaddisCloudServerTests
{
    [TestMethod]
    public async Task Configuration_persists_and_authentication_survives_store_recreation()
    {
        var storagePath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Cloud.Persistence.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var first = new OpenCaddisCloudConfigurationStore(storagePath);
            await first.SaveConfigurationAsync(
                OpenCaddisCloudTarget.Server,
                TestOpenCaddisCloudServer.ConfigurationJson(OpenCaddisCloudTarget.Server));
            var firstConnection = first.CreateConnection(
                OpenCaddisCloudTarget.Server,
                new Uri("http://localhost:5082/"));

            var restarted = new OpenCaddisCloudConfigurationStore(storagePath);
            var restartedConnection = restarted.CreateConnection(
                OpenCaddisCloudTarget.Server,
                new Uri("http://localhost:5082/"));

            Assert.IsTrue(restarted.IsConfigured(OpenCaddisCloudTarget.Server));
            Assert.IsFalse(restarted.IsConfigured(OpenCaddisCloudTarget.ServerBuilder));
            Assert.AreEqual(firstConnection.ApiKey, restartedConnection.ApiKey);
            Assert.Contains("server-model", restarted.GetConfigurationJson(OpenCaddisCloudTarget.Server));
        }
        finally
        {
            if (Directory.Exists(storagePath))
            {
                Directory.Delete(storagePath, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Configuration_endpoint_authenticates_returns_target_document_and_honors_etag()
    {
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync(
            OpenCaddisCloudTarget.Server,
            OpenCaddisCloudTarget.ServerBuilder);
        using var client = new HttpClient { BaseAddress = cloud.BaseUri };
        var connection = cloud.Connection(OpenCaddisCloudTarget.ServerBuilder);

        using var unauthorized = await client.GetAsync(CloudServerProtocol.ConfigurationPath);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var serverConnection = cloud.Connection(OpenCaddisCloudTarget.Server);
        using var wrongClusterRequest = CreateConfigurationRequest(
            serverConnection with { ClusterId = connection.ClusterId });
        using var wrongClusterResponse = await client.SendAsync(wrongClusterRequest);
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrongClusterResponse.StatusCode);

        using var request = CreateConfigurationRequest(connection);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(response.Headers.ETag);
        var envelope = await response.Content.ReadFromJsonAsync<CloudConfigurationEnvelope>();
        Assert.IsNotNull(envelope);
        Assert.AreEqual("builder-model", envelope.Configuration.ModelConfigurations.Single().Model);

        using var unchangedRequest = CreateConfigurationRequest(connection);
        unchangedRequest.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var unchanged = await client.SendAsync(unchangedRequest);
        Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
    }

    [TestMethod]
    public async Task Server_host_fails_to_start_when_its_cloud_configuration_is_missing()
    {
        await using var cloud = await TestOpenCaddisCloudServer.CreateAsync();
        var addOnPath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Cloud.Startup.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(addOnPath);

        try
        {
            await using var host = OpenCaddisServerHost.Create(
                new Uri($"http://localhost:{FindAvailablePort()}/"),
                addOnPath,
                new OpenCaddisServerHostOptions
                {
                    CloudServer = cloud.Connection(OpenCaddisCloudTarget.Server)
                });

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => host.StartAsync());
            Assert.Contains("Cloud server configuration could not be fetched", exception.Message);
        }
        finally
        {
            Directory.Delete(addOnPath, recursive: true);
        }
    }

    private static HttpRequestMessage CreateConfigurationRequest(
        OpenCaddisCloudServerConnection connection)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, CloudServerProtocol.ConfigurationPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        request.Headers.Add(CloudServerProtocol.ClusterIdHeader, connection.ClusterId);
        request.Headers.Add(CloudServerProtocol.EnvironmentHeader, "Production");
        return request;
    }

    private static int FindAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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

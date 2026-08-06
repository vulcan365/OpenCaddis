using System.Net;
using System.Net.Sockets;
using OpenCaddis.Server;

namespace OpenCaddis.Server.Tests;

internal sealed class TestOpenCaddisCloudServer : IAsyncDisposable
{
    private TestOpenCaddisCloudServer(
        string storagePath,
        OpenCaddisCloudConfigurationStore store,
        OpenCaddisCloudServerHost host)
    {
        StoragePath = storagePath;
        Store = store;
        Host = host;
    }

    public string StoragePath { get; }

    public OpenCaddisCloudConfigurationStore Store { get; }

    public OpenCaddisCloudServerHost Host { get; }

    public Uri BaseUri => Host.BaseUri;

    public static async Task<TestOpenCaddisCloudServer> CreateAsync(
        params OpenCaddisCloudTarget[] configuredTargets)
    {
        var storagePath = Path.Combine(
            Path.GetTempPath(),
            "OpenCaddis.Cloud.Tests",
            Guid.NewGuid().ToString("N"));
        var store = new OpenCaddisCloudConfigurationStore(storagePath);
        foreach (var target in configuredTargets)
        {
            await store.SaveConfigurationAsync(target, ConfigurationJson(target));
        }

        var host = OpenCaddisCloudServerHost.Create(
            new Uri($"http://localhost:{FindAvailablePort()}/"),
            store);
        await host.StartAsync();
        return new TestOpenCaddisCloudServer(storagePath, store, host);
    }

    public OpenCaddisCloudServerConnection Connection(OpenCaddisCloudTarget target) =>
        Host.CreateConnection(target);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (Directory.Exists(StoragePath))
        {
            Directory.Delete(StoragePath, recursive: true);
        }
    }

    public static string ConfigurationJson(OpenCaddisCloudTarget target)
    {
        var model = target == OpenCaddisCloudTarget.Server ? "server-model" : "builder-model";
        return $$"""
        {
          "modelConfigurations": [
            {
              "name": "default",
              "provider": "OpenAI",
              "uri": "https://api.openai.test/v1",
              "model": "{{model}}",
              "apiKeyAlias": "test"
            }
          ],
          "apiKeys": [
            {
              "alias": "test",
              "value": "sk-test"
            }
          ]
        }
        """;
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

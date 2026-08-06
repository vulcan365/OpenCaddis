using OpenCaddis.Server;
using System.Reflection;

namespace OpenCaddis.Server.Builder;

public sealed class OpenCaddisServerBuilderHost : IOpenCaddisServerHost
{
    private readonly OpenCaddisServerHost host;

    private OpenCaddisServerBuilderHost(OpenCaddisServerHost host)
    {
        this.host = host;
    }

    public Uri BaseUri => host.BaseUri;

    public string DisplayName => host.DisplayName;

    public string AddOnPath => host.AddOnPath;

    public IReadOnlyList<Assembly> AdditionalAssemblies => host.AdditionalAssemblies;

    public static OpenCaddisServerBuilderHost Create(Uri baseUri, string addOnPath)
    {
        var options = new OpenCaddisServerHostOptions
        {
            DisplayName = "OpenCaddis Server Builder"
        };

        // Builder-owned agents and tools can be added to this assembly later. Registering
        // it now establishes the extension point while the two hosts otherwise stay alike.
        options.AdditionalAssemblies.Add(typeof(OpenCaddisServerBuilderHost).Assembly);

        return new OpenCaddisServerBuilderHost(
            OpenCaddisServerHost.Create(baseUri, addOnPath, options));
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        host.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        host.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => host.DisposeAsync();
}

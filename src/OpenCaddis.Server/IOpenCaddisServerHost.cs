using System.Reflection;

namespace OpenCaddis.Server;

public interface IOpenCaddisServerHost : IAsyncDisposable
{
    Uri BaseUri { get; }

    string DisplayName { get; }

    string AddOnPath { get; }

    IReadOnlyList<Assembly> AdditionalAssemblies { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

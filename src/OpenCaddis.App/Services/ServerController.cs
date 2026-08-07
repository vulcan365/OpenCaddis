using Microsoft.Maui.Storage;
using OpenCaddis.Server;
using OpenCaddis.Server.Builder;

namespace OpenCaddis.App.Services;

public enum OpenCaddisServerMode
{
    Server,
    Builder
}

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

public sealed class ServerController : IDisposable
{
    private const int DefaultPort = 5083;
    private const string PortPreferenceKey = "OpenCaddis.Server.Port";
    private const string AddOnPathPreferenceKey = "OpenCaddis.Server.AddOnPath";
    private const string ModePreferenceKey = "OpenCaddis.Server.Mode";
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly OpenCaddisCloudConfigurationStore cloudConfigurationStore;
    private readonly OpenCaddisCloudServerHost cloudServer;
    private IOpenCaddisServerHost? serverHost;
    private bool disposed;

    public ServerController(
        OpenCaddisCloudConfigurationStore cloudConfigurationStore,
        OpenCaddisCloudServerHost cloudServer)
    {
        this.cloudConfigurationStore = cloudConfigurationStore;
        this.cloudServer = cloudServer;
        CurrentPort = Preferences.Default.Get(PortPreferenceKey, DefaultPort);
        var defaultAddOnPath = Path.Combine(FileSystem.Current.AppDataDirectory, "AddOns");
        CurrentAddOnPath = NormalizeAddOnPath(
            Preferences.Default.Get(AddOnPathPreferenceKey, defaultAddOnPath));
        var savedMode = Preferences.Default.Get(ModePreferenceKey, (int)OpenCaddisServerMode.Server);
        CurrentMode = Enum.IsDefined(typeof(OpenCaddisServerMode), savedMode)
            ? (OpenCaddisServerMode)savedMode
            : OpenCaddisServerMode.Server;
        Directory.CreateDirectory(CurrentAddOnPath);
    }

    public event EventHandler? StatusChanged;

    public ServerState State { get; private set; } = ServerState.Stopped;

    public string StatusMessage { get; private set; } = "No OpenCaddis server mode is running.";

    public int CurrentPort { get; private set; }

    public OpenCaddisServerMode CurrentMode { get; private set; }

    public string CurrentAddOnPath { get; private set; }

    public int LoadedAddOnAssemblyCount { get; private set; }

    public Uri ServerUri => CreateServerUri(CurrentPort);

    public Uri SurfaceUri => new(ServerUri, "surface");

    public Uri CloudServerUri => cloudServer.BaseUri;

    internal string CurrentAdminApiKey { get; private set; } = string.Empty;

    public static Uri CreateServerUri(int port) => new($"http://localhost:{port}/");

    public static string GetModeDisplayName(OpenCaddisServerMode mode) => mode switch
    {
        OpenCaddisServerMode.Server => "OpenCaddis Server",
        OpenCaddisServerMode.Builder => "OpenCaddis Server Builder",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public bool HasCloudConfiguration(OpenCaddisServerMode mode) =>
        cloudConfigurationStore.IsConfigured(GetCloudTarget(mode));

    public async Task StartAsync(
        OpenCaddisServerMode mode,
        int port,
        string addOnPath,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        var normalizedAddOnPath = NormalizeAddOnPath(addOnPath);
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (serverHost is not null)
            {
                return;
            }

            await StartCoreAsync(mode, port, normalizedAddOnPath, cancellationToken);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(
        OpenCaddisServerMode mode,
        int port,
        string addOnPath,
        CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        var normalizedAddOnPath = NormalizeAddOnPath(addOnPath);
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopCoreAsync(cancellationToken);
            await StartCoreAsync(mode, port, normalizedAddOnPath, cancellationToken);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lifecycleLock.Wait();
        try
        {
            disposed = true;
            var host = serverHost;
            serverHost = null;
            if (host is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    host.StopAsync(timeout.Token).GetAwaiter().GetResult();
                }
                finally
                {
                    host.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lifecycleLock.Release();
            lifecycleLock.Dispose();
        }
    }

    private async Task StartCoreAsync(
        OpenCaddisServerMode mode,
        int port,
        string addOnPath,
        CancellationToken cancellationToken)
    {
        CurrentMode = mode;
        CurrentPort = port;
        CurrentAddOnPath = addOnPath;
        Directory.CreateDirectory(CurrentAddOnPath);
        Preferences.Default.Set(PortPreferenceKey, port);
        Preferences.Default.Set(AddOnPathPreferenceKey, CurrentAddOnPath);
        Preferences.Default.Set(ModePreferenceKey, (int)mode);
        var displayName = GetModeDisplayName(mode);
        var cloudTarget = GetCloudTarget(mode);
        if (!cloudConfigurationStore.IsConfigured(cloudTarget))
        {
            throw new InvalidOperationException(
                $"Configure models and API keys for {displayName} on the Cloud configuration tab before starting it.");
        }

        SetStatus(
            ServerState.Starting,
            mode == OpenCaddisServerMode.Server
                ? $"Starting {displayName} and loading assemblies from {CurrentAddOnPath}..."
                : $"Starting {displayName}; project output will be published to {CurrentAddOnPath}...");

        IOpenCaddisServerHost? newHost = null;
        try
        {
            await cloudServer.StartAsync(cancellationToken);
            var cloudConnection = cloudServer.CreateConnection(cloudTarget);
            CurrentAdminApiKey = cloudConnection.ApiKey;
            newHost = mode switch
            {
                OpenCaddisServerMode.Server => OpenCaddisServerHost.Create(
                    ServerUri,
                    CurrentAddOnPath,
                    new OpenCaddisServerHostOptions { CloudServer = cloudConnection }),
                OpenCaddisServerMode.Builder => OpenCaddisServerBuilderHost.Create(
                    ServerUri,
                    CurrentAddOnPath,
                    cloudConnection),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            await newHost.StartAsync(cancellationToken);
            serverHost = newHost;
            LoadedAddOnAssemblyCount = newHost.AdditionalAssemblies.Count;
            SetStatus(
                ServerState.Running,
                mode == OpenCaddisServerMode.Server
                    ? $"{newHost.DisplayName} is running at {ServerUri} with {LoadedAddOnAssemblyCount} add-on assemblies."
                    : $"{newHost.DisplayName} is running at {ServerUri}; add-on directory loading is disabled.");
        }
        catch (Exception exception)
        {
            if (newHost is not null)
            {
                await newHost.DisposeAsync();
            }

            CurrentAdminApiKey = string.Empty;
            SetStatus(ServerState.Failed, exception.Message);
            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var host = serverHost;
        serverHost = null;
        if (host is null)
        {
            CurrentAdminApiKey = string.Empty;
            SetStatus(ServerState.Stopped, "No OpenCaddis server mode is running.");
            return;
        }

        SetStatus(ServerState.Stopping, $"Stopping {host.DisplayName}...");
        try
        {
            await host.StopAsync(cancellationToken);
            SetStatus(ServerState.Stopped, "No OpenCaddis server mode is running.");
        }
        catch (Exception exception)
        {
            SetStatus(ServerState.Failed, exception.Message);
            throw;
        }
        finally
        {
            await host.DisposeAsync();
            CurrentAdminApiKey = string.Empty;
            LoadedAddOnAssemblyCount = 0;
        }
    }

    private void SetStatus(ServerState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The port must be from 1 to 65535.");
        }

        if (port == CloudServerUri.Port)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                $"Port {port} is reserved for the OpenCaddis.App cloud server.");
        }
    }

    public static string NormalizeAddOnPath(string addOnPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addOnPath);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(addOnPath.Trim()));
    }

    private static OpenCaddisCloudTarget GetCloudTarget(OpenCaddisServerMode mode) => mode switch
    {
        OpenCaddisServerMode.Server => OpenCaddisCloudTarget.Server,
        OpenCaddisServerMode.Builder => OpenCaddisCloudTarget.ServerBuilder,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

using Microsoft.Maui.Storage;
using OpenCaddis.Server;

namespace OpenCaddis.App.Services;

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
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private OpenCaddisServerHost? serverHost;
    private bool disposed;

    public ServerController()
    {
        CurrentPort = Preferences.Default.Get(PortPreferenceKey, DefaultPort);
        var defaultAddOnPath = Path.Combine(FileSystem.Current.AppDataDirectory, "AddOns");
        CurrentAddOnPath = NormalizeAddOnPath(
            Preferences.Default.Get(AddOnPathPreferenceKey, defaultAddOnPath));
        Directory.CreateDirectory(CurrentAddOnPath);
    }

    public event EventHandler? StatusChanged;

    public ServerState State { get; private set; } = ServerState.Stopped;

    public string StatusMessage { get; private set; } = "The server is not running.";

    public int CurrentPort { get; private set; }

    public string CurrentAddOnPath { get; private set; }

    public int LoadedAddOnAssemblyCount { get; private set; }

    public Uri ServerUri => CreateServerUri(CurrentPort);

    public static Uri CreateServerUri(int port) => new($"http://localhost:{port}/");

    public async Task StartAsync(
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

            await StartCoreAsync(port, normalizedAddOnPath, cancellationToken);
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
            await StartCoreAsync(port, normalizedAddOnPath, cancellationToken);
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
        int port,
        string addOnPath,
        CancellationToken cancellationToken)
    {
        CurrentPort = port;
        CurrentAddOnPath = addOnPath;
        Directory.CreateDirectory(CurrentAddOnPath);
        Preferences.Default.Set(PortPreferenceKey, port);
        Preferences.Default.Set(AddOnPathPreferenceKey, CurrentAddOnPath);
        SetStatus(ServerState.Starting, $"Loading add-ons from {CurrentAddOnPath}...");

        OpenCaddisServerHost? newHost = null;
        try
        {
            newHost = OpenCaddisServerHost.Create(ServerUri, CurrentAddOnPath);
            await newHost.StartAsync(cancellationToken);
            serverHost = newHost;
            LoadedAddOnAssemblyCount = newHost.AdditionalAssemblies.Count;
            SetStatus(
                ServerState.Running,
                $"Running at {ServerUri} with {LoadedAddOnAssemblyCount} add-on assemblies.");
        }
        catch (Exception exception)
        {
            if (newHost is not null)
            {
                await newHost.DisposeAsync();
            }

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
            SetStatus(ServerState.Stopped, "The server is not running.");
            return;
        }

        SetStatus(ServerState.Stopping, "Stopping the server...");
        try
        {
            await host.StopAsync(cancellationToken);
            SetStatus(ServerState.Stopped, "The server is not running.");
        }
        catch (Exception exception)
        {
            SetStatus(ServerState.Failed, exception.Message);
            throw;
        }
        finally
        {
            await host.DisposeAsync();
            LoadedAddOnAssemblyCount = 0;
        }
    }

    private void SetStatus(ServerState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The port must be from 1 to 65535.");
        }
    }

    public static string NormalizeAddOnPath(string addOnPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addOnPath);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(addOnPath.Trim()));
    }
}

using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using OpenCaddis.Sdk.Connections;

namespace OpenCaddis.App.Services;

public sealed class MauiSecretStore : IOpenCaddisSecretStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await SecureStorage.Default.GetAsync(key);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SecureStorage.Default.SetAsync(key, value);
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SecureStorage.Default.Remove(key));
    }
}

public sealed class MauiInteractiveBrowser : IOpenCaddisInteractiveBrowser
{
    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Launcher.Default.TryOpenAsync(uri))
        {
            throw new InvalidOperationException($"The system browser could not open '{uri.Host}'.");
        }
    }
}

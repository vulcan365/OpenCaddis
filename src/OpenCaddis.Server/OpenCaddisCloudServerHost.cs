using FabrCore.Core.CloudServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace OpenCaddis.Server;

public sealed class OpenCaddisCloudServerHost : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly OpenCaddisCloudConfigurationStore configurationStore;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private bool started;
    private bool disposed;

    private OpenCaddisCloudServerHost(
        WebApplication application,
        Uri baseUri,
        OpenCaddisCloudConfigurationStore configurationStore)
    {
        this.application = application;
        this.configurationStore = configurationStore;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static OpenCaddisCloudServerHost Create(
        Uri baseUri,
        OpenCaddisCloudConfigurationStore configurationStore)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(configurationStore);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttp || !baseUri.IsLoopback)
        {
            throw new ArgumentException(
                "The OpenCaddis cloud server URL must be an absolute loopback HTTP URL.",
                nameof(baseUri));
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OpenCaddisCloudServerHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
            Args = []
        });
        builder.WebHost.UseUrls(baseUri.ToString());
        var application = builder.Build();
        var host = new OpenCaddisCloudServerHost(application, baseUri, configurationStore);
        host.MapEndpoints();
        return host;
    }

    public OpenCaddisCloudServerConnection CreateConnection(OpenCaddisCloudTarget target) =>
        configurationStore.CreateConnection(target, BaseUri);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }

            await application.StartAsync(cancellationToken);
            started = true;
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
            if (!started)
            {
                return;
            }

            await application.StopAsync(cancellationToken);
            started = false;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleLock.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (started)
            {
                await application.StopAsync();
                started = false;
            }

            await application.DisposeAsync();
        }
        finally
        {
            lifecycleLock.Release();
            lifecycleLock.Dispose();
        }
    }

    private void MapEndpoints()
    {
        application.MapGet(CloudServerProtocol.ConfigurationPath, (HttpContext context) =>
        {
            if (!TryAuthorize(context, out var clusterId))
            {
                return Results.Unauthorized();
            }

            if (!configurationStore.TryGetPublishedConfiguration(
                    clusterId,
                    out var envelope,
                    out var error))
            {
                return Results.NotFound(new { error });
            }

            var etag = $"\"{envelope!.ConfigurationVersion}\"";
            if (context.Request.Headers.IfNoneMatch.Any(value =>
                    string.Equals(value, etag, StringComparison.Ordinal)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            context.Response.Headers.ETag = etag;
            return Results.Json(envelope);
        });

        application.MapPost(CloudServerProtocol.HeartbeatPath, (HttpContext context) =>
        {
            if (!TryAuthorize(context, out var clusterId))
            {
                return Results.Unauthorized();
            }

            configurationStore.TryGetPublishedConfiguration(clusterId, out var envelope, out _);
            return Results.Ok(new
            {
                refreshRequested = false,
                latestConfigurationVersion = envelope?.ConfigurationVersion
            });
        });
    }

    private bool TryAuthorize(HttpContext context, out string clusterId)
    {
        clusterId = context.Request.Headers[CloudServerProtocol.ClusterIdHeader].ToString();
        return configurationStore.IsAuthorized(
            clusterId,
            context.Request.Headers.Authorization.ToString());
    }
}

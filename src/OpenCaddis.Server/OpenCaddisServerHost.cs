using FabrCore.Host;
using FabrCore.Surface;
using FabrCore.Surface.Contracts;
using FabrCore.Surface.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace OpenCaddis.Server;

public sealed class OpenCaddisServerHost : IAsyncDisposable
{
    private const string BlazorWebScriptResourceName =
        "OpenCaddis.Server.StaticAssets.blazor.web.js";
    private const string SurfaceStyleResourceName =
        "OpenCaddis.Server.StaticAssets.surface.css";
    private const string AdaptiveCardsScriptResourceName =
        "OpenCaddis.Server.StaticAssets.adaptiveCardsSurface.js";

    private readonly WebApplication application;
    private readonly AddOnAssemblyCatalog addOnCatalog;
    private bool disposed;

    private OpenCaddisServerHost(
        WebApplication application,
        Uri baseUri,
        string addOnPath,
        AddOnAssemblyCatalog addOnCatalog)
    {
        this.application = application;
        this.addOnCatalog = addOnCatalog;
        BaseUri = baseUri;
        AddOnPath = addOnPath;
    }

    public Uri BaseUri { get; }

    public string AddOnPath { get; }

    public IReadOnlyList<Assembly> AdditionalAssemblies => addOnCatalog.Assemblies;

    public static OpenCaddisServerHost Create(Uri baseUri, string addOnPath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The OpenCaddis server URL must be an absolute http://localhost URL.",
                nameof(baseUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(addOnPath);
        var fullAddOnPath = Path.GetFullPath(addOnPath);
        var addOnCatalog = AddOnAssemblyCatalog.Load(fullAddOnPath);

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(OpenCaddisServerHost).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production,
                Args = []
            });

            builder.WebHost.UseUrls(baseUri.ToString());

            // FabrCore's advertised URL is separate from Kestrel's listen URL. Keeping
            // both aligned here makes this host ready for FabrCore server registration.
            builder.Configuration["FabrCore:HostUrl"] = baseUri.ToString();

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            var additionalAssemblies = new List<Assembly>
            {
                typeof(SurfaceMessageTypes).Assembly
            };
            additionalAssemblies.AddRange(addOnCatalog.Assemblies);

            builder.AddFabrCoreServer(new FabrCoreServerOptions
            {
                AdditionalAssemblies = additionalAssemblies
            });

            var surfaceDefinitionPath = Path.Combine(AppContext.BaseDirectory, "fabrcore-surface.json");
            builder.AddFabrCoreSurfaceFromConfig(surfaceDefinitionPath, "default");
            builder.Services.AddFabrCoreSurfaceComponents();
            builder.Services.Configure<SurfaceOptions>(options =>
            {
                options.FabrCoreHostUrl = baseUri.ToString();
                options.DevelopmentFallbackPrincipalId = "local-user";
                options.EnableAgentDirectory = true;
                options.EnableAgentChat = true;
                options.EnableLiveStatus = true;
                options.EnableSharedAgents = true;
                options.EnableAdaptiveCards = true;
                options.EnableAgentCreate = false;
                options.EnableDiagnostics = true;
            });

            var application = builder.Build();
            application.UseAntiforgery();
            application.UseFabrCoreServer(new FabrCoreServerOptions());
            MapEmbeddedAsset(
                application,
                "/_framework/blazor.web.js",
                BlazorWebScriptResourceName,
                "text/javascript; charset=utf-8");
            MapEmbeddedAsset(
                application,
                "/_content/FabrCore.Surface/surface.css",
                SurfaceStyleResourceName,
                "text/css; charset=utf-8");
            MapEmbeddedAsset(
                application,
                "/_content/FabrCore.Surface/adaptiveCardsSurface.js",
                AdaptiveCardsScriptResourceName,
                "text/javascript; charset=utf-8");

            application.MapRazorComponents<Components.App>()
                .AddInteractiveServerRenderMode()
                .AddFabrCoreSurfaceRoutes();

            application.MapGet("/", () => Results.Content(HomePageHtml, "text/html"));
            application.MapGet("/addons", () => Results.Ok(new
            {
                Path = fullAddOnPath,
                Count = addOnCatalog.Assemblies.Count,
                Assemblies = addOnCatalog.Assemblies.Select(assembly => assembly.FullName).ToArray()
            }));

            return new OpenCaddisServerHost(application, baseUri, fullAddOnPath, addOnCatalog);
        }
        catch
        {
            addOnCatalog.Dispose();
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        application.StartAsync(cancellationToken);

    private static void MapEmbeddedAsset(
        WebApplication application,
        string route,
        string resourceName,
        string contentType)
    {
        application.MapGet(route, () =>
        {
            var stream = typeof(OpenCaddisServerHost).Assembly
                .GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded web asset '{resourceName}' is missing.");

            return Results.Stream(stream, contentType);
        });
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        application.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await application.DisposeAsync();
        }
        finally
        {
            addOnCatalog.Dispose();
        }
    }

    private const string HomePageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>OpenCaddis Server</title>
            <style>
                body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f6f7f9; color: #172033; }
                main { text-align: center; padding: 3rem; }
                h1 { margin: 0 0 .5rem; }
                p { color: #596579; }
            </style>
        </head>
        <body>
            <main>
                <h1>OpenCaddis Server</h1>
                <p>The server is running.</p>
            </main>
        </body>
        </html>
        """;
}

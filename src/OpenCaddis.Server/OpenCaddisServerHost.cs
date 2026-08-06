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
using System.Text.Encodings.Web;

namespace OpenCaddis.Server;

public sealed class OpenCaddisServerHost : IOpenCaddisServerHost
{
    private const int MaxHarnessSkillUploadBytes = 4 * 1024 * 1024;
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
        string displayName,
        string addOnPath,
        AddOnAssemblyCatalog addOnCatalog)
    {
        this.application = application;
        this.addOnCatalog = addOnCatalog;
        BaseUri = baseUri;
        DisplayName = displayName;
        AddOnPath = addOnPath;
    }

    public Uri BaseUri { get; }

    public string DisplayName { get; }

    public string AddOnPath { get; }

    public IReadOnlyList<Assembly> AdditionalAssemblies => addOnCatalog.Assemblies;

    public static OpenCaddisServerHost Create(
        Uri baseUri,
        string addOnPath,
        OpenCaddisServerHostOptions? options = null)
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
        options ??= new OpenCaddisServerHostOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DisplayName);
        var cloudServer = options.CloudServer ?? throw new InvalidOperationException(
            "OpenCaddis Server requires an OpenCaddis.App cloud-server connection.");
        if (!cloudServer.CloudServerUri.IsAbsoluteUri ||
            cloudServer.CloudServerUri.Scheme != Uri.UriSchemeHttp ||
            !cloudServer.CloudServerUri.IsLoopback)
        {
            throw new InvalidOperationException(
                "The OpenCaddis cloud-server connection must use an absolute loopback HTTP URL.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(cloudServer.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(cloudServer.ClusterId);
        var displayName = options.DisplayName.Trim();
        var fullAddOnPath = Path.GetFullPath(addOnPath);
        var addOnCatalog = options.LoadAssembliesFromAddOnPath
            ? AddOnAssemblyCatalog.Load(fullAddOnPath)
            : AddOnAssemblyCatalog.Empty();
        var hostApiBaseUrl = baseUri.AbsoluteUri.TrimEnd('/');

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
            builder.Configuration["FabrCore:HostUrl"] = hostApiBaseUrl;
            builder.Configuration["FabrCore:CloudServer:Enabled"] = "true";
            builder.Configuration["FabrCore:CloudServer:Url"] =
                cloudServer.CloudServerUri.AbsoluteUri.TrimEnd('/');
            builder.Configuration["FabrCore:CloudServer:ApiKey"] = cloudServer.ApiKey;
            builder.Configuration["FabrCore:CloudServer:ClusterId"] = cloudServer.ClusterId;
            builder.Configuration["FabrCore:CloudServer:Environment"] = "Production";
            builder.Configuration["FabrCore:CloudServer:RefreshInterval"] = "00:00:05";
            builder.Configuration["FabrCore:CloudServer:RequestTimeout"] = "00:00:10";
            builder.Configuration["FabrCore:CloudServer:CacheLastKnownGood"] = "false";
            builder.Configuration["FabrCore:CloudServer:StartupFailureBehavior"] = "Fail";
            builder.Configuration["FabrCore:CloudServer:Heartbeat:Enabled"] = "true";
            builder.Configuration["FabrCore:CloudServer:Heartbeat:Interval"] = "00:00:10";
            // The desktop app provisions principal-scoped Harness Skills through FabrCore's
            // protected administration API. Reuse this mode's generated loopback cloud key;
            // it is already persisted outside the repository and never exposed over the UI.
            builder.Configuration["FabrCore:AdminAuthentication:ApiKey"] = cloudServer.ApiKey;
            builder.Configuration["FabrCore:AdminAuthentication:PrincipalId"] = "opencaddis-app";

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            var additionalAssemblies = new List<Assembly>
            {
                typeof(SurfaceMessageTypes).Assembly
            };
            foreach (var assembly in options.AdditionalAssemblies)
            {
                ArgumentNullException.ThrowIfNull(assembly);
                if (!additionalAssemblies.Contains(assembly))
                {
                    additionalAssemblies.Add(assembly);
                }
            }

            // FabrCore's registry discovers already-loaded agent/plugin assemblies from the
            // AppDomain. Do not also pass collectible add-ons to FabrCore's Orleans assembly
            // list: FabrCore reloads every entry by simple name in the default context, which
            // cannot resolve (or reference) an assembly from a collectible context.

            builder.AddFabrCoreServer(new FabrCoreServerOptions
            {
                AdditionalAssemblies = additionalAssemblies
            });

            var surfaceDefinitionPath = Path.Combine(AppContext.BaseDirectory, "fabrcore-surface.json");
            builder.AddFabrCoreSurfaceFromConfig(surfaceDefinitionPath, "default");
            builder.Services.AddFabrCoreSurfaceComponents();
            builder.Services.Configure<SurfaceOptions>(options =>
            {
                options.FabrCoreHostUrl = hostApiBaseUrl;
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
            UseBufferedHarnessSkillUploads(application);
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

            application.MapGet("/", () => Results.Content(CreateHomePageHtml(displayName), "text/html"));
            application.MapGet("/addons", () => Results.Ok(new
            {
                Path = fullAddOnPath,
                Count = addOnCatalog.Assemblies.Count,
                Assemblies = addOnCatalog.Assemblies.Select(assembly => assembly.FullName).ToArray()
            }));

            return new OpenCaddisServerHost(
                application,
                baseUri,
                displayName,
                fullAddOnPath,
                addOnCatalog);
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

    private static void UseBufferedHarnessSkillUploads(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var isSkillUpload = HttpMethods.IsPut(context.Request.Method) &&
                path is not null &&
                path.StartsWith(
                    "/fabrcoreapi/admin/v1/principals/",
                    StringComparison.OrdinalIgnoreCase) &&
                path.Contains("/skills/", StringComparison.OrdinalIgnoreCase);
            if (!isSkillUpload)
            {
                await next();
                return;
            }

            // FabrCore's ZIP reader requires a seekable stream and currently opens the
            // request synchronously. Buffer this one protected upload route asynchronously
            // instead of enabling synchronous Kestrel I/O for the whole application.
            var originalBody = context.Request.Body;
            await using var bufferedBody = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await originalBody.ReadAsync(buffer, context.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                if (bufferedBody.Length + read > MaxHarnessSkillUploadBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                await bufferedBody.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }

            bufferedBody.Position = 0;
            context.Request.Body = bufferedBody;
            try
            {
                await next();
            }
            finally
            {
                context.Request.Body = originalBody;
            }
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

    private static string CreateHomePageHtml(string displayName)
    {
        var encodedDisplayName = HtmlEncoder.Default.Encode(displayName);
        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{encodedDisplayName}}</title>
            <style>
                body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f6f7f9; color: #172033; }
                main { text-align: center; padding: 3rem; }
                h1 { margin: 0 0 .5rem; }
                p { color: #596579; }
            </style>
        </head>
        <body>
            <main>
                <h1>{{encodedDisplayName}}</h1>
                <p>The server is running.</p>
            </main>
        </body>
        </html>
        """;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace OpenCaddis.Server;

public sealed class OpenCaddisServerHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private OpenCaddisServerHost(WebApplication application, Uri baseUri)
    {
        this.application = application;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static OpenCaddisServerHost Create(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The OpenCaddis server URL must be an absolute http://localhost URL.",
                nameof(baseUri));
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OpenCaddisServerHost).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
            Args = []
        });

        builder.WebHost.UseUrls(baseUri.ToString());

        // FabrCore's advertised URL is separate from Kestrel's listen URL. Keeping
        // both aligned here makes this host ready for FabrCore server registration.
        builder.Configuration["FabrCore:HostUrl"] = baseUri.ToString();

        var application = builder.Build();
        application.MapGet("/", () => Results.Content(HomePageHtml, "text/html"));
        application.MapGet("/health", () => Results.Ok(new
        {
            Status = "Healthy",
            Timestamp = DateTimeOffset.UtcNow
        }));

        return new OpenCaddisServerHost(application, baseUri);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        application.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        application.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => application.DisposeAsync();

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

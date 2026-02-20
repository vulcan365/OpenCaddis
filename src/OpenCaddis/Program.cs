using Fabr.Client;
using Fabr.Host;
using Fabr.Sdk;
using Microsoft.AspNetCore.DataProtection;
using OpenCaddis.Components;
using OpenCaddis.Services;

namespace OpenCaddis
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            var keysPath = Path.Combine(builder.Environment.ContentRootPath, ".keys");
            Directory.CreateDirectory(keysPath);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
                .SetApplicationName("OpenCaddis");
            builder.Services.AddSingleton<FabrConfigService>();
            builder.Services.AddSingleton<OpenCaddisConfigService>();
            builder.Services.AddSingleton<Microsoft365AuthService>();
            builder.Services.AddSingleton<AgentManagerService>();
            builder.Services.AddSingleton<AgentDiscoveryService>();
            builder.Services.AddSingleton<VectorStoreService>();
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<EmbeddingService>();
            builder.Services.AddSingleton<MemoryService>();
            builder.Services.AddSingleton<CaddisFlyRuntimeService>();
            builder.Services.AddSingleton<CaddisFlyRunStore>();
            builder.Services.AddSingleton<CommandExecutorFactory>();
            builder.Services.AddSingleton<CompactionService>();
            builder.Services.AddSingleton<SipRegistrationService>();
            builder.Services.AddHostedService<AgentBootstrapService>();
            builder.Services.AddHostedService<VectorStoreBootstrapService>();
            builder.Services.AddHostedService<SipPhoneBootstrapService>();

            builder.Services.AddOpenApi();

            builder.AddFabrServer();
            builder.AddFabrClient();

            builder.Services.AddSingleton<AgentEventLoggerProvider>();
            builder.Services.AddSingleton<ILoggerProvider>(sp =>
                sp.GetRequiredService<AgentEventLoggerProvider>());

            var app = builder.Build();

            // Restore Microsoft 365 session from persisted tokens
            var m365Auth = app.Services.GetRequiredService<Microsoft365AuthService>();
            var ocConfig = app.Services.GetRequiredService<OpenCaddisConfigService>();
            if (ocConfig.ConfigurationExists())
            {
                var ocCfg = ocConfig.LoadConfigurationAsync().GetAwaiter().GetResult();
                if (ocCfg.Microsoft365 is { ClientId.Length: > 0 } m365)
                {
                    m365Auth.Configure(m365.ClientId);
                    m365Auth.TryRestoreSessionAsync().GetAwaiter().GetResult();
                }
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/openapi/v1.json", "OpenCaddis API");
                });
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.UseFabrServer();

            app.Run();
        }
    }
}

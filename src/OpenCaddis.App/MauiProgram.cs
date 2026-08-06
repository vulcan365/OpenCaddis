using Microsoft.Extensions.Logging;

namespace OpenCaddis.App
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton(_ => new OpenCaddis.Server.OpenCaddisCloudConfigurationStore(
                Path.Combine(FileSystem.Current.AppDataDirectory, "CloudServer")));
            builder.Services.AddSingleton(services => OpenCaddis.Server.OpenCaddisCloudServerHost.Create(
                new Uri("http://localhost:5082/"),
                services.GetRequiredService<OpenCaddis.Server.OpenCaddisCloudConfigurationStore>()));
            builder.Services.AddSingleton<Services.ServerController>();
            builder.Services.AddSingleton<OpenCaddis.Server.Builder.BuilderWorkspaceService>();
            builder.Services.AddSingleton<OpenCaddis.Server.Builder.AddonBuilderAgentProvisioner>();
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddSingleton<ServerPage>();
            builder.Services.AddSingleton<BuilderPage>();
            builder.Services.AddSingleton<SurfacePage>();
            builder.Services.AddSingleton<AppShell>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}

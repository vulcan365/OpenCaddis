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

            builder.Services.AddSingleton<Services.ServerController>();
            builder.Services.AddSingleton<OpenCaddis.Server.Builder.BuilderWorkspaceService>();
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

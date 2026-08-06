using Microsoft.Extensions.DependencyInjection;

namespace OpenCaddis.App
{
    public partial class App : Application
    {
        private readonly AppShell appShell;
        private readonly Services.ServerController serverController;
        private readonly OpenCaddis.Server.OpenCaddisCloudServerHost cloudServer;

        public App(
            IServiceProvider services,
            Services.ServerController serverController,
            OpenCaddis.Server.OpenCaddisCloudServerHost cloudServer)
        {
            InitializeComponent();
            appShell = services.GetRequiredService<AppShell>();
            this.serverController = serverController;
            this.cloudServer = cloudServer;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(appShell)
            {
                Title = "OpenCaddis",
                Width = 960,
                Height = 680,
                MinimumWidth = 720,
                MinimumHeight = 520
            };

            _ = StartCloudServerAsync();
            window.Destroying += (_, _) =>
            {
                serverController.Dispose();
                cloudServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
            return window;
        }

        private async Task StartCloudServerAsync()
        {
            try
            {
                await cloudServer.StartAsync();
            }
            catch
            {
                // ServerController retries and reports the concrete error when a host is started.
            }
        }
    }
}

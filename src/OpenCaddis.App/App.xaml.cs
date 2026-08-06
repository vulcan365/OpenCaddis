using Microsoft.Extensions.DependencyInjection;

namespace OpenCaddis.App
{
    public partial class App : Application
    {
        private readonly AppShell appShell;
        private readonly Services.ServerController serverController;

        public App(IServiceProvider services, Services.ServerController serverController)
        {
            InitializeComponent();
            appShell = services.GetRequiredService<AppShell>();
            this.serverController = serverController;
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

            window.Destroying += (_, _) => serverController.Dispose();
            return window;
        }
    }
}

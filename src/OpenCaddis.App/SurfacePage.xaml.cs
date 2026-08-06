using OpenCaddis.App.Services;

namespace OpenCaddis.App;

public partial class SurfacePage : ContentPage
{
    private readonly ServerController serverController;

    public SurfacePage(ServerController serverController)
    {
        InitializeComponent();
        this.serverController = serverController;
        serverController.StatusChanged += OnServerStatusChanged;
        RefreshSurface();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshSurface();
    }

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.Dispatch(RefreshSurface);
    }

    private void RefreshSurface()
    {
        if (serverController.State == ServerState.Running)
        {
            UnavailableView.IsVisible = false;
            SurfaceWebView.IsVisible = true;
            SurfaceWebView.Source = serverController.SurfaceUri.ToString();
            return;
        }

        SurfaceWebView.IsVisible = false;
        SurfaceWebView.Source = new HtmlWebViewSource { Html = "<html><body></body></html>" };
        UnavailableView.IsVisible = true;
        UnavailableMessageLabel.Text = serverController.State switch
        {
            ServerState.Starting => serverController.StatusMessage,
            ServerState.Stopping => serverController.StatusMessage,
            ServerState.Failed => serverController.StatusMessage,
            _ => "Start OpenCaddis Server or Server Builder to use Surface."
        };
    }
}

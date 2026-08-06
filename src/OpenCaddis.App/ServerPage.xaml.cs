using OpenCaddis.App.Services;

namespace OpenCaddis.App;

public partial class ServerPage : ContentPage
{
    private readonly ServerController serverController;

    public ServerPage(ServerController serverController)
    {
        InitializeComponent();
        this.serverController = serverController;
        PortEntry.Text = serverController.CurrentPort.ToString();
        serverController.StatusChanged += OnServerStatusChanged;
        RefreshStatus();
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (!TryGetPort(out var port))
        {
            return;
        }

        try
        {
            await serverController.StartAsync(port);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Unable to start server", exception.Message, "OK");
        }
    }

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        try
        {
            await serverController.StopAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Unable to stop server", exception.Message, "OK");
        }
    }

    private async void OnRestartClicked(object? sender, EventArgs e)
    {
        if (!TryGetPort(out var port))
        {
            return;
        }

        try
        {
            await serverController.RestartAsync(port);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Unable to restart server", exception.Message, "OK");
        }
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        await Launcher.Default.OpenAsync(serverController.ServerUri);
    }

    private void OnPortTextChanged(object? sender, TextChangedEventArgs e)
    {
        UrlLabel.Text = int.TryParse(e.NewTextValue, out var port) && port is >= 1 and <= 65535
            ? ServerController.CreateServerUri(port).ToString()
            : "Enter a port from 1 to 65535";
    }

    private bool TryGetPort(out int port)
    {
        if (int.TryParse(PortEntry.Text, out port) && port is >= 1 and <= 65535)
        {
            return true;
        }

        _ = DisplayAlertAsync("Invalid port", "Enter a port from 1 to 65535.", "OK");
        return false;
    }

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.Dispatch(RefreshStatus);
    }

    private void RefreshStatus()
    {
        StatusLabel.Text = serverController.State.ToString();
        StatusMessageLabel.Text = serverController.StatusMessage;

        var isBusy = serverController.State is ServerState.Starting or ServerState.Stopping;
        var isRunning = serverController.State == ServerState.Running;

        PortEntry.IsEnabled = !isBusy && !isRunning;
        StartButton.IsEnabled = !isBusy && !isRunning;
        StopButton.IsEnabled = !isBusy && isRunning;
        RestartButton.IsEnabled = !isBusy && isRunning;
        OpenButton.IsEnabled = isRunning;
        StatusIndicator.Color = serverController.State switch
        {
            ServerState.Running => Colors.Green,
            ServerState.Starting or ServerState.Stopping => Colors.Orange,
            ServerState.Failed => Colors.Red,
            _ => Colors.Gray
        };
    }
}

using OpenCaddis.App.Services;

namespace OpenCaddis.App;

public partial class ServerPage : ContentPage
{
    private readonly ServerController serverController;

    public ServerPage(ServerController serverController)
    {
        InitializeComponent();
        this.serverController = serverController;
        ModePicker.SelectedIndex = serverController.CurrentMode == OpenCaddisServerMode.Builder ? 1 : 0;
        PortEntry.Text = serverController.CurrentPort.ToString();
        AddOnPathEntry.Text = serverController.CurrentAddOnPath;
        serverController.StatusChanged += OnServerStatusChanged;
        RefreshStatus();
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (!TryGetSettings(out var mode, out var port, out var addOnPath))
        {
            return;
        }

        try
        {
            await serverController.StartAsync(mode, port, addOnPath);
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
        if (!TryGetSettings(out var mode, out var port, out var addOnPath))
        {
            return;
        }

        try
        {
            await serverController.RestartAsync(mode, port, addOnPath);
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

    private void OnModeSelectionChanged(object? sender, EventArgs e)
    {
        ModeDescriptionLabel.Text = ModePicker.SelectedIndex == 1
            ? "Builder currently hosts the same FabrCore and Surface server; custom project-building agents come next."
            : "Server loads compiled add-on assemblies from the configured path.";
    }

    private bool TryGetSettings(
        out OpenCaddisServerMode mode,
        out int port,
        out string addOnPath)
    {
        mode = ModePicker.SelectedIndex == 1
            ? OpenCaddisServerMode.Builder
            : OpenCaddisServerMode.Server;
        addOnPath = string.Empty;
        if (!int.TryParse(PortEntry.Text, out port) || port is < 1 or > 65535)
        {
            _ = DisplayAlertAsync("Invalid port", "Enter a port from 1 to 65535.", "OK");
            return false;
        }

        try
        {
            addOnPath = ServerController.NormalizeAddOnPath(AddOnPathEntry.Text ?? string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            _ = DisplayAlertAsync("Invalid add-on path", exception.Message, "OK");
            return false;
        }
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

        ModePicker.IsEnabled = !isBusy && !isRunning;
        PortEntry.IsEnabled = !isBusy && !isRunning;
        AddOnPathEntry.IsEnabled = !isBusy && !isRunning;
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

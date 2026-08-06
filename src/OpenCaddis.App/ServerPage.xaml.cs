using OpenCaddis.App.Services;
using OpenCaddis.Server;

namespace OpenCaddis.App;

public partial class ServerPage : ContentPage
{
    private readonly ServerController serverController;
    private readonly OpenCaddisCloudConfigurationStore cloudConfigurationStore;

    public ServerPage(
        ServerController serverController,
        OpenCaddisCloudConfigurationStore cloudConfigurationStore)
    {
        InitializeComponent();
        this.serverController = serverController;
        this.cloudConfigurationStore = cloudConfigurationStore;
        ModePicker.SelectedIndex = serverController.CurrentMode == OpenCaddisServerMode.Builder ? 1 : 0;
        ConfigurationTargetPicker.SelectedIndex = ModePicker.SelectedIndex;
        PortEntry.Text = serverController.CurrentPort.ToString();
        AddOnPathEntry.Text = serverController.CurrentAddOnPath;
        CloudServerUrlLabel.Text = $"Cloud server: {serverController.CloudServerUri}";
        serverController.StatusChanged += OnServerStatusChanged;
        cloudConfigurationStore.ConfigurationChanged += OnCloudConfigurationChanged;
        LoadConfigurationEditor();
        ShowHostTab();
        RefreshStatus();
    }

    private void OnHostTabClicked(object? sender, EventArgs e) => ShowHostTab();

    private void OnConfigurationTabClicked(object? sender, EventArgs e)
    {
        ConfigurationTargetPicker.SelectedIndex = ModePicker.SelectedIndex;
        LoadConfigurationEditor();
        HostTab.IsVisible = false;
        ConfigurationTab.IsVisible = true;
        HostTabButton.Opacity = 0.65;
        ConfigurationTabButton.Opacity = 1;
    }

    private void ShowHostTab()
    {
        HostTab.IsVisible = true;
        ConfigurationTab.IsVisible = false;
        HostTabButton.Opacity = 1;
        ConfigurationTabButton.Opacity = 0.65;
    }

    private void OnConfigurationTargetChanged(object? sender, EventArgs e) =>
        LoadConfigurationEditor();

    private async void OnSaveConfigurationClicked(object? sender, EventArgs e)
    {
        SaveConfigurationButton.IsEnabled = false;
        try
        {
            await cloudConfigurationStore.SaveConfigurationAsync(
                GetSelectedCloudTarget(),
                ConfigurationEditor.Text ?? string.Empty);
            LoadConfigurationEditor();
            RefreshStatus();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            await DisplayAlertAsync("Invalid configuration", exception.Message, "OK");
            ConfigurationStatusLabel.Text = $"Not ready: {exception.Message}";
            ConfigurationStatusLabel.TextColor = Colors.Red;
        }
        finally
        {
            SaveConfigurationButton.IsEnabled = true;
        }
    }

    private void OnReloadConfigurationClicked(object? sender, EventArgs e) =>
        LoadConfigurationEditor();

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
            ? port == serverController.CloudServerUri.Port
                ? $"Port {port} is reserved for the cloud server"
                : ServerController.CreateServerUri(port).ToString()
            : "Enter a port from 1 to 65535";
    }

    private void OnModeSelectionChanged(object? sender, EventArgs e)
    {
        ModeDescriptionLabel.Text = ModePicker.SelectedIndex == 1
            ? "Builder hosts FabrCore, Surface, and the project-building agents and tools."
            : "Server loads compiled add-on assemblies from the configured path.";
        RefreshConfigurationState();
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

        if (port == serverController.CloudServerUri.Port)
        {
            _ = DisplayAlertAsync(
                "Invalid port",
                $"Port {port} is reserved for the OpenCaddis.App cloud server.",
                "OK");
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

    private void OnCloudConfigurationChanged(object? sender, EventArgs e)
    {
        Dispatcher.Dispatch(() =>
        {
            LoadConfigurationEditor();
            RefreshStatus();
        });
    }

    private void RefreshStatus()
    {
        StatusLabel.Text = serverController.State.ToString();
        StatusMessageLabel.Text = serverController.StatusMessage;

        var isBusy = serverController.State is ServerState.Starting or ServerState.Stopping;
        var isRunning = serverController.State == ServerState.Running;
        var hasConfiguration = serverController.HasCloudConfiguration(GetSelectedMode());

        ModePicker.IsEnabled = !isBusy && !isRunning;
        PortEntry.IsEnabled = !isBusy && !isRunning;
        AddOnPathEntry.IsEnabled = !isBusy && !isRunning;
        StartButton.IsEnabled = !isBusy && !isRunning && hasConfiguration;
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
        RefreshConfigurationState();
    }

    private void LoadConfigurationEditor()
    {
        if (ConfigurationTargetPicker.SelectedIndex < 0)
        {
            return;
        }

        var target = GetSelectedCloudTarget();
        ConfigurationEditor.Text = cloudConfigurationStore.GetConfigurationJson(target);
        ConfigurationStatusLabel.Text = cloudConfigurationStore.GetValidationMessage(target);
        ConfigurationStatusLabel.TextColor = cloudConfigurationStore.IsConfigured(target)
            ? Colors.Green
            : Colors.Red;
    }

    private void RefreshConfigurationState()
    {
        var configured = serverController.HasCloudConfiguration(GetSelectedMode());
        ModeConfigurationLabel.Text = configured
            ? "Cloud configuration is ready."
            : "Cloud configuration is required before this mode can start.";
        ModeConfigurationLabel.TextColor = configured ? Colors.Green : Colors.Red;
    }

    private OpenCaddisServerMode GetSelectedMode() => ModePicker.SelectedIndex == 1
        ? OpenCaddisServerMode.Builder
        : OpenCaddisServerMode.Server;

    private OpenCaddisCloudTarget GetSelectedCloudTarget() =>
        ConfigurationTargetPicker.SelectedIndex == 1
            ? OpenCaddisCloudTarget.ServerBuilder
            : OpenCaddisCloudTarget.Server;
}

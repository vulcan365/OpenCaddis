using FabrCore.Sdk;
using OpenCaddis.App.Services;
using OpenCaddis.Server;

namespace OpenCaddis.App;

public partial class ServerPage : ContentPage
{
    private readonly ServerController serverController;
    private readonly OpenCaddisCloudConfigurationStore cloudConfigurationStore;
    private readonly OpenCaddisAgentRegistryService agentRegistryService;
    private List<RegistrySelectionItem> agentTypes = [];
    private List<RegistrySelectionItem> plugins = [];
    private List<RegistrySelectionItem> tools = [];
    private bool registryLoaded;
    private bool registryLoading;
    private bool agentCreating;
    private string? suggestedAgentHandle;

    public ServerPage(
        ServerController serverController,
        OpenCaddisCloudConfigurationStore cloudConfigurationStore,
        OpenCaddisAgentRegistryService agentRegistryService)
    {
        InitializeComponent();
        this.serverController = serverController;
        this.cloudConfigurationStore = cloudConfigurationStore;
        this.agentRegistryService = agentRegistryService;
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
        AgentsTab.IsVisible = false;
        HostTabButton.Opacity = 0.65;
        ConfigurationTabButton.Opacity = 1;
        AgentsTabButton.Opacity = 0.65;
    }

    private async void OnAgentsTabClicked(object? sender, EventArgs e)
    {
        if (serverController.State != ServerState.Running)
        {
            return;
        }

        HostTab.IsVisible = false;
        ConfigurationTab.IsVisible = false;
        AgentsTab.IsVisible = true;
        HostTabButton.Opacity = 0.65;
        ConfigurationTabButton.Opacity = 0.65;
        AgentsTabButton.Opacity = 1;
        await LoadRegistryAsync(force: false);
    }

    private void ShowHostTab()
    {
        HostTab.IsVisible = true;
        ConfigurationTab.IsVisible = false;
        AgentsTab.IsVisible = false;
        HostTabButton.Opacity = 1;
        ConfigurationTabButton.Opacity = 0.65;
        AgentsTabButton.Opacity = 0.65;
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

    private async void OnRefreshRegistryClicked(object? sender, EventArgs e) =>
        await LoadRegistryAsync(force: true);

    private void OnAgentTypeSelectionChanged(object? sender, EventArgs e)
    {
        if (AgentTypePicker.SelectedItem is not RegistrySelectionItem selected)
        {
            AgentTypeDetails.IsVisible = false;
            RefreshCreateAgentState();
            return;
        }

        AgentTypeDetails.IsVisible = true;
        AgentTypeNameLabel.Text = selected.Entry.TypeName;
        AgentTypeDescriptionLabel.Text = selected.Entry.Description ?? "No description was registered.";
        AgentTypeCapabilitiesLabel.Text = string.IsNullOrWhiteSpace(selected.Entry.Capabilities)
            ? "Capabilities: not specified"
            : $"Capabilities: {selected.Entry.Capabilities}";
        AgentTypeNotesLabel.Text = selected.Entry.Notes.Count == 0
            ? "Notes: none"
            : $"Notes: {string.Join(" • ", selected.Entry.Notes)}";

        if (string.IsNullOrWhiteSpace(AgentHandleEntry.Text) ||
            string.Equals(AgentHandleEntry.Text, suggestedAgentHandle, StringComparison.Ordinal))
        {
            suggestedAgentHandle = $"{selected.Alias}-{Guid.NewGuid():N}"[..(selected.Alias.Length + 9)];
            AgentHandleEntry.Text = suggestedAgentHandle;
        }

        if (string.IsNullOrWhiteSpace(AgentDescriptionEntry.Text) &&
            !string.IsNullOrWhiteSpace(selected.Entry.Description))
        {
            AgentDescriptionEntry.Text = selected.Entry.Description;
        }

        RefreshCreateAgentState();
    }

    private async void OnCreateAgentClicked(object? sender, EventArgs e)
    {
        if (serverController.State != ServerState.Running ||
            AgentTypePicker.SelectedItem is not RegistrySelectionItem selectedAgent)
        {
            return;
        }

        agentCreating = true;
        CreateAgentActivityIndicator.IsVisible = true;
        CreateAgentActivityIndicator.IsRunning = true;
        AgentCreationStatusLabel.TextColor = Colors.Gray;
        AgentCreationStatusLabel.Text = "Creating and configuring the agent...";
        RefreshCreateAgentState();
        try
        {
            var options = new OpenCaddisAgentCreationOptions
            {
                PrincipalHandle = AgentPrincipalEntry.Text ?? string.Empty,
                AgentHandle = AgentHandleEntry.Text ?? string.Empty,
                AgentType = selectedAgent.Alias,
                ModelName = AgentModelEntry.Text ?? string.Empty,
                Description = AgentDescriptionEntry.Text,
                SystemPrompt = AgentSystemPromptEditor.Text,
                Plugins = plugins.Where(plugin => plugin.IsSelected).Select(plugin => plugin.Alias).ToArray(),
                Tools = tools.Where(tool => tool.IsSelected).Select(tool => tool.Alias).ToArray(),
                AddToSurface = AddAgentToSurfaceSwitch.IsToggled
            };
            var health = await agentRegistryService.CreateAgentAsync(
                serverController.ServerUri,
                options);
            AgentCreationStatusLabel.TextColor = Colors.Green;
            AgentCreationStatusLabel.Text = options.AddToSurface
                ? $"{health.Handle} is {health.State} and is visible in Surface."
                : $"{health.Handle} is {health.State}.";
            suggestedAgentHandle = $"{selectedAgent.Alias}-{Guid.NewGuid():N}"[..(selectedAgent.Alias.Length + 9)];
            AgentHandleEntry.Text = suggestedAgentHandle;
        }
        catch (Exception exception)
        {
            AgentCreationStatusLabel.TextColor = Colors.Red;
            AgentCreationStatusLabel.Text = exception.Message;
            await DisplayAlertAsync("Agent creation failed", exception.Message, "OK");
        }
        finally
        {
            agentCreating = false;
            CreateAgentActivityIndicator.IsRunning = false;
            CreateAgentActivityIndicator.IsVisible = false;
            RefreshCreateAgentState();
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
        AgentsTabButton.IsVisible = isRunning;
        if (!isRunning && AgentsTab.IsVisible)
        {
            ShowHostTab();
        }

        if (!isRunning)
        {
            registryLoaded = false;
        }
        StatusIndicator.Color = serverController.State switch
        {
            ServerState.Running => Colors.Green,
            ServerState.Starting or ServerState.Stopping => Colors.Orange,
            ServerState.Failed => Colors.Red,
            _ => Colors.Gray
        };
        RefreshConfigurationState();
        RefreshCreateAgentState();
    }

    private async Task LoadRegistryAsync(bool force)
    {
        if (serverController.State != ServerState.Running ||
            registryLoading ||
            (registryLoaded && !force))
        {
            return;
        }

        registryLoading = true;
        RegistryActivityIndicator.IsVisible = true;
        RegistryActivityIndicator.IsRunning = true;
        RegistryStatusLabel.TextColor = Colors.Gray;
        RegistryStatusLabel.Text = "Loading the FabrCore registry...";
        RefreshCreateAgentState();
        try
        {
            var selectedAgentAlias = (AgentTypePicker.SelectedItem as RegistrySelectionItem)?.Alias;
            var selectedPlugins = plugins.Where(item => item.IsSelected)
                .Select(item => item.Alias)
                .ToHashSet(StringComparer.Ordinal);
            var selectedTools = tools.Where(item => item.IsSelected)
                .Select(item => item.Alias)
                .ToHashSet(StringComparer.Ordinal);
            var registry = await agentRegistryService.GetRegistryAsync(serverController.ServerUri);
            agentTypes = CreateRegistryItems(
                registry.Agents,
                new HashSet<string>(StringComparer.Ordinal));
            plugins = CreateRegistryItems(registry.Plugins, selectedPlugins);
            tools = CreateRegistryItems(registry.Tools, selectedTools);
            AgentTypePicker.ItemsSource = agentTypes;
            PluginsList.BindingContext = plugins;
            ToolsList.BindingContext = tools;
            AgentTypePicker.SelectedItem = agentTypes.FirstOrDefault(
                    item => string.Equals(item.Alias, selectedAgentAlias, StringComparison.Ordinal))
                ?? agentTypes.FirstOrDefault();

            registryLoaded = true;
            RegistryStatusLabel.TextColor = Colors.Green;
            RegistryStatusLabel.Text =
                $"Registry loaded: {agentTypes.Count} agents, {plugins.Count} plugins, and {tools.Count} tools.";
            var collisions = registry.Collisions ?? [];
            RegistryCollisionLabel.IsVisible = collisions.Count > 0;
            RegistryCollisionLabel.Text = collisions.Count == 0
                ? string.Empty
                : "Registry alias collisions: " + string.Join(
                    "; ",
                    collisions.Select(collision =>
                        $"{collision.Category} '{collision.Alias}' ({string.Join(", ", collision.Types)})"));
        }
        catch (Exception exception)
        {
            registryLoaded = false;
            RegistryStatusLabel.TextColor = Colors.Red;
            RegistryStatusLabel.Text = $"Could not load the FabrCore registry: {exception.Message}";
        }
        finally
        {
            registryLoading = false;
            RegistryActivityIndicator.IsRunning = false;
            RegistryActivityIndicator.IsVisible = false;
            RefreshCreateAgentState();
        }
    }

    private void RefreshCreateAgentState()
    {
        RefreshRegistryButton.IsEnabled =
            serverController.State == ServerState.Running && !registryLoading && !agentCreating;
        CreateAgentButton.IsEnabled =
            serverController.State == ServerState.Running &&
            registryLoaded &&
            !registryLoading &&
            !agentCreating &&
            AgentTypePicker.SelectedItem is RegistrySelectionItem;
    }

    private static List<RegistrySelectionItem> CreateRegistryItems(
        IEnumerable<DiscoveryRegistryEntry> entries,
        IReadOnlySet<string> selectedAliases) =>
        entries
            .Select(entry => new RegistrySelectionItem(
                entry,
                selectedAliases.Contains(RegistrySelectionItem.GetAlias(entry))))
            .OrderBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

public sealed class RegistrySelectionItem
{
    public RegistrySelectionItem(DiscoveryRegistryEntry entry, bool isSelected)
    {
        Entry = entry;
        IsSelected = isSelected;
        Alias = GetAlias(entry);
        DetailText = CreateDetailText(entry);
    }

    public DiscoveryRegistryEntry Entry { get; }

    public string Alias { get; }

    public string DetailText { get; }

    public bool IsSelected { get; set; }

    public static string GetAlias(DiscoveryRegistryEntry entry) =>
        entry.Aliases.FirstOrDefault() ?? entry.TypeName;

    private static string CreateDetailText(DiscoveryRegistryEntry entry)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            details.Add(entry.Description);
        }

        if (!string.IsNullOrWhiteSpace(entry.Capabilities))
        {
            details.Add($"Capabilities: {entry.Capabilities}");
        }

        if (entry.Notes.Count > 0)
        {
            details.Add($"Notes: {string.Join(" • ", entry.Notes)}");
        }

        if (entry.Methods.Count > 0)
        {
            details.Add("Methods: " + string.Join(
                "; ",
                entry.Methods.Select(method => string.IsNullOrWhiteSpace(method.Description)
                    ? method.Name
                    : $"{method.Name} — {method.Description}")));
        }

        details.Add(entry.TypeName);
        return string.Join(Environment.NewLine, details);
    }
}

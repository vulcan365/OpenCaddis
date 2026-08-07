using OpenCaddis.Sdk.Connections;

namespace OpenCaddis.App;

public partial class ConnectionsPage : ContentPage
{
    private readonly IOpenCaddisConnectionAdministration administration;
    private readonly Dictionary<string, View> fieldControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> requirementControls = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ConnectionProviderDescriptor> providers = [];
    private IReadOnlyList<ConnectionRequirement> requirements = [];
    private IReadOnlyList<ConnectionDescriptor> connections = [];
    private string? editingConnectionId;
    private bool refreshing;

    public ConnectionsPage(IOpenCaddisConnectionAdministration administration)
    {
        InitializeComponent();
        this.administration = administration;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async Task RefreshAsync(string? selectConnectionId = null)
    {
        refreshing = true;
        try
        {
            providers = administration.GetProviders();
            requirements = administration.GetRequirements();
            connections = await administration.GetConnectionsAsync();
            ProviderPicker.ItemsSource = providers.Select(provider => provider.DisplayName).ToArray();
            ConnectionPicker.ItemsSource = connections.Select(ConnectionLabel).ToArray();
            ConnectionPicker.SelectedIndex = FindConnectionIndex(selectConnectionId);

            if (ProviderPicker.SelectedIndex < 0 && providers.Count > 0)
            {
                ProviderPicker.SelectedIndex = 0;
            }
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message);
        }
        finally
        {
            refreshing = false;
        }
    }

    private int FindConnectionIndex(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return -1;
        }

        for (var index = 0; index < connections.Count; index++)
        {
            if (string.Equals(connections[index].Id, connectionId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void OnProviderChanged(object? sender, EventArgs e)
    {
        if (ProviderPicker.SelectedIndex >= 0 && ProviderPicker.SelectedIndex < providers.Count)
        {
            RenderProvider(providers[ProviderPicker.SelectedIndex]);
        }
    }

    private void RenderProvider(ConnectionProviderDescriptor provider, ConnectionDescriptor? connection = null)
    {
        ProviderDescriptionLabel.Text = provider.Description ?? provider.AuthenticationKind.ToString();
        ConfigurationFieldsLayout.Clear();
        ProviderActionsLayout.Clear();
        RequirementsLayout.Clear();
        fieldControls.Clear();
        requirementControls.Clear();

        foreach (var field in provider.ConfigurationFields)
        {
            ConfigurationFieldsLayout.Add(new Label
            {
                Text = field.Required ? $"{field.Label} *" : field.Label,
                FontAttributes = FontAttributes.Bold
            });

            var currentValue = connection?.Configuration.GetValueOrDefault(field.Name) ?? field.DefaultValue;
            View control = field.Kind switch
            {
                ConnectionConfigurationFieldKind.Boolean => new Switch
                {
                    IsToggled = bool.TryParse(currentValue, out var enabled) && enabled
                },
                ConnectionConfigurationFieldKind.Choice => CreateChoicePicker(field, currentValue),
                _ => new Entry
                {
                    Text = currentValue,
                    Placeholder = field.Placeholder,
                    IsPassword = field.Sensitive || field.Kind == ConnectionConfigurationFieldKind.Secret,
                    Keyboard = field.Kind == ConnectionConfigurationFieldKind.Integer ? Keyboard.Numeric : Keyboard.Default
                }
            };
            fieldControls[field.Name] = control;
            ConfigurationFieldsLayout.Add(control);
            if (!string.IsNullOrWhiteSpace(field.HelpText))
            {
                ConfigurationFieldsLayout.Add(new Label
                {
                    Text = field.HelpText,
                    TextColor = Colors.Gray,
                    FontSize = 12
                });
            }
        }

        foreach (var requirement in requirements.Where(item =>
                     string.Equals(item.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var checkBox = new CheckBox
            {
                IsChecked = connection?.GrantedRequirements.Contains(requirement.Id, StringComparer.OrdinalIgnoreCase) == true
            };
            requirementControls[requirement.Id] = checkBox;
            var row = new HorizontalStackLayout { Spacing = 6 };
            row.Add(checkBox);
            row.Add(new Label
            {
                Text = $"{requirement.DisplayName} — {requirement.Purpose}",
                VerticalTextAlignment = TextAlignment.Center
            });
            RequirementsLayout.Add(row);
        }

        if (requirementControls.Count == 0)
        {
            RequirementsLayout.Add(new Label
            {
                Text = "No installed add-on currently requests this provider.",
                TextColor = Colors.Gray
            });
        }

        if (connection is not null)
        {
            foreach (var action in provider.Actions)
            {
                var button = new Button
                {
                    Text = action.Label,
                    CommandParameter = action.Id
                };
                button.Clicked += OnProviderActionClicked;
                ProviderActionsLayout.Add(button);
            }
        }
    }

    private static Picker CreateChoicePicker(ConnectionConfigurationField field, string? value)
    {
        var picker = new Picker { ItemsSource = field.AllowedValues.ToArray(), SelectedIndex = -1 };
        for (var index = 0; index < field.AllowedValues.Count; index++)
        {
            if (string.Equals(field.AllowedValues[index], value, StringComparison.OrdinalIgnoreCase))
            {
                picker.SelectedIndex = index;
                break;
            }
        }

        return picker;
    }

    private async void OnSaveClicked(object? sender, EventArgs e) => await SaveAsync(connect: false);

    private async void OnSaveAndConnectClicked(object? sender, EventArgs e) => await SaveAsync(connect: true);

    private async Task SaveAsync(bool connect)
    {
        if (ProviderPicker.SelectedIndex < 0 || ProviderPicker.SelectedIndex >= providers.Count)
        {
            ShowMessage("Select a provider.");
            return;
        }

        var provider = providers[ProviderPicker.SelectedIndex];
        var configuration = provider.ConfigurationFields.ToDictionary(
            field => field.Name,
            field => ReadControlValue(fieldControls[field.Name]),
            StringComparer.OrdinalIgnoreCase);
        var result = await administration.SaveAsync(new ConnectionSaveRequest
        {
            ConnectionId = editingConnectionId,
            ProviderId = provider.Id,
            PrincipalHandle = PrincipalEntry.Text?.Trim() ?? string.Empty,
            DisplayName = DisplayNameEntry.Text?.Trim() ?? string.Empty,
            Configuration = configuration
        });
        if (!result.Success || result.Connection is null)
        {
            ShowMessage(result.Message ?? "The connection could not be saved.");
            return;
        }

        editingConnectionId = result.Connection.Id;
        if (connect)
        {
            result = await administration.ConnectAsync(
                editingConnectionId,
                requirementControls.Where(item => item.Value.IsChecked).Select(item => item.Key).ToArray());
        }

        ShowMessage(result.Message ?? (result.Success ? "Connection saved." : "Connection operation failed."), !result.Success);
        await RefreshAsync(editingConnectionId);
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (refreshing || ConnectionPicker.SelectedIndex < 0 || ConnectionPicker.SelectedIndex >= connections.Count)
        {
            return;
        }

        var connection = connections[ConnectionPicker.SelectedIndex];
        var providerIndex = -1;
        for (var index = 0; index < providers.Count; index++)
        {
            if (string.Equals(providers[index].Id, connection.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                providerIndex = index;
                break;
            }
        }

        if (providerIndex < 0)
        {
            ConnectionStatusLabel.Text = $"{connection.Status}: provider {connection.ProviderId} is unavailable";
            return;
        }

        editingConnectionId = connection.Id;
        ProviderPicker.SelectedIndex = providerIndex;
        PrincipalEntry.Text = connection.PrincipalHandle;
        DisplayNameEntry.Text = connection.DisplayName;
        RenderProvider(providers[providerIndex], connection);
        ConnectionStatusLabel.Text = $"{connection.Status}: {connection.StatusMessage ?? connection.AccountIdentifier ?? "No details"}";
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (TryGetSelectedConnection(out var connection))
        {
            var result = await administration.ConnectAsync(
                connection.Id,
                requirementControls.Where(item => item.Value.IsChecked).Select(item => item.Key).ToArray());
            await CompleteOperationAsync(result, connection.Id);
        }
    }

    private async void OnValidateClicked(object? sender, EventArgs e)
    {
        if (TryGetSelectedConnection(out var connection))
        {
            await CompleteOperationAsync(await administration.ValidateAsync(connection.Id), connection.Id);
        }
    }

    private async void OnDisconnectClicked(object? sender, EventArgs e)
    {
        if (TryGetSelectedConnection(out var connection))
        {
            await CompleteOperationAsync(await administration.DisconnectAsync(connection.Id), connection.Id);
        }
    }

    private async void OnToggleEnabledClicked(object? sender, EventArgs e)
    {
        if (TryGetSelectedConnection(out var connection))
        {
            var result = await administration.SetEnabledAsync(
                connection.Id,
                connection.Status == ConnectionStatus.Disabled);
            await CompleteOperationAsync(result, connection.Id);
        }
    }

    private async void OnProviderActionClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string actionId } &&
            TryGetSelectedConnection(out var connection))
        {
            var action = providers
                .First(provider => string.Equals(provider.Id, connection.ProviderId, StringComparison.OrdinalIgnoreCase))
                .Actions.First(item => string.Equals(item.Id, actionId, StringComparison.OrdinalIgnoreCase));
            if (action.IsDestructive &&
                !await DisplayAlertAsync(
                    "Confirm provider action",
                    action.HelpText ?? $"Run '{action.Label}' for this connection?",
                    action.Label,
                    "Cancel"))
            {
                return;
            }

            var result = await administration.ExecuteActionAsync(connection.Id, actionId);
            await CompleteOperationAsync(result, connection.Id);
        }
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (!TryGetSelectedConnection(out var connection) ||
            !await DisplayAlertAsync("Delete connection?", $"Delete '{connection.DisplayName}' and its stored secrets?", "Delete", "Cancel"))
        {
            return;
        }

        var deleted = await administration.DeleteAsync(connection.Id);
        editingConnectionId = null;
        ShowMessage(deleted ? "Connection deleted." : "Connection was not found.", !deleted);
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync(editingConnectionId);

    private void OnClearClicked(object? sender, EventArgs e)
    {
        editingConnectionId = null;
        DisplayNameEntry.Text = string.Empty;
        ConnectionPicker.SelectedIndex = -1;
        ConnectionStatusLabel.Text = string.Empty;
        if (ProviderPicker.SelectedIndex >= 0)
        {
            RenderProvider(providers[ProviderPicker.SelectedIndex]);
        }
    }

    private async Task CompleteOperationAsync(ConnectionOperationResult result, string connectionId)
    {
        ShowMessage(result.Message ?? result.Status.ToString(), !result.Success);
        await RefreshAsync(connectionId);
    }

    private bool TryGetSelectedConnection(out ConnectionDescriptor connection)
    {
        if (ConnectionPicker.SelectedIndex >= 0 && ConnectionPicker.SelectedIndex < connections.Count)
        {
            connection = connections[ConnectionPicker.SelectedIndex];
            return true;
        }

        connection = null!;
        ShowMessage("Select a connection first.");
        return false;
    }

    private static string ReadControlValue(View control) => control switch
    {
        Entry entry => entry.Text ?? string.Empty,
        Switch toggle => toggle.IsToggled.ToString(),
        Picker picker => picker.SelectedItem?.ToString() ?? string.Empty,
        _ => string.Empty
    };

    private static string ConnectionLabel(ConnectionDescriptor connection) =>
        $"{connection.DisplayName} ({connection.PrincipalHandle}) — {connection.Status}";

    private void ShowMessage(string message, bool error = true)
    {
        MessageLabel.TextColor = error ? Color.FromArgb("#B42318") : Color.FromArgb("#067647");
        MessageLabel.Text = message;
    }
}

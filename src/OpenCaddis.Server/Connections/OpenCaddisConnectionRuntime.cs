using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCaddis.Sdk.Addons;
using OpenCaddis.Sdk.Connections;

namespace OpenCaddis.Server.Connections;

public sealed class OpenCaddisConnectionRuntime :
    IOpenCaddisConnectionAdministration,
    IOpenCaddisConnectionClientFactory,
    IAsyncDisposable
{
    private const string MetadataFileName = "connections.json";
    private const string SensitiveMask = "••••••";
    private readonly string metadataPath;
    private readonly IOpenCaddisSecretStore secretStore;
    private readonly IOpenCaddisInteractiveBrowser interactiveBrowser;
    private readonly IServiceProvider appServices;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ProviderEntry> providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RequirementEntry> requirements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoredConnection> connections = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public OpenCaddisConnectionRuntime(
        string storageDirectory,
        IOpenCaddisSecretStore secretStore,
        IOpenCaddisInteractiveBrowser interactiveBrowser,
        IServiceProvider appServices,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        this.secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        this.interactiveBrowser = interactiveBrowser ?? throw new ArgumentNullException(nameof(interactiveBrowser));
        this.appServices = appServices ?? throw new ArgumentNullException(nameof(appServices));
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        ownsHttpClient = httpClient is null;
        var fullDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(fullDirectory);
        metadataPath = Path.Combine(fullDirectory, MetadataFileName);
        LoadMetadata();
    }

    public IDisposable RegisterBuiltInProvider(
        IOpenCaddisConnectionProvider provider,
        IServiceProvider? services = null,
        string ownerId = "opencaddis")
    {
        ArgumentNullException.ThrowIfNull(provider);
        ConnectionValidation.Validate(provider.Descriptor);
        lock (providers)
        {
            AddProvider(new ProviderEntry(ownerId, provider, services ?? appServices));
        }

        return new Registration(() => RemoveOwner(ownerId));
    }

    public IDisposable RegisterAddon(OpenCaddisAddonRegistration registration, IServiceProvider services)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(services);
        ConnectionValidation.Validate(registration);

        lock (providers)
        {
            if (providers.Values.Any(entry =>
                    string.Equals(entry.OwnerId, registration.Id, StringComparison.OrdinalIgnoreCase)) ||
                requirements.Values.Any(entry =>
                    string.Equals(entry.OwnerId, registration.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Add-on '{registration.Id}' is already registered.");
            }

            try
            {
                foreach (var providerRegistration in registration.Providers)
                {
                    IOpenCaddisConnectionProvider provider;
                    if (providerRegistration.ImplementationType is null)
                    {
                        provider = StandardConnectionProviderFactory.Create(
                            providerRegistration.Descriptor,
                            httpClient);
                    }
                    else
                    {
                        provider = (IOpenCaddisConnectionProvider)services.GetRequiredService(
                            providerRegistration.ImplementationType);
                        if (!string.Equals(
                                provider.Descriptor.Id,
                                providerRegistration.Descriptor.Id,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Provider type '{providerRegistration.ImplementationType.FullName}' reports ID " +
                                $"'{provider.Descriptor.Id}' instead of '{providerRegistration.Descriptor.Id}'.");
                        }
                    }

                    AddProvider(new ProviderEntry(registration.Id, provider, services));
                }

                foreach (var requirement in registration.Requirements)
                {
                    if (!providers.ContainsKey(requirement.ProviderId))
                    {
                        throw new InvalidOperationException(
                            $"Requirement '{requirement.Id}' references unavailable provider '{requirement.ProviderId}'.");
                    }

                    if (!requirements.TryAdd(requirement.Id, new RequirementEntry(registration.Id, requirement)))
                    {
                        throw new InvalidOperationException(
                            $"Connection requirement '{requirement.Id}' is already registered.");
                    }
                }
            }
            catch
            {
                RemoveOwner(registration.Id);
                throw;
            }
        }

        return new Registration(() => RemoveOwner(registration.Id));
    }

    public IReadOnlyList<ConnectionProviderDescriptor> GetProviders()
    {
        lock (providers)
        {
            return providers.Values
                .Select(entry => entry.Provider.Descriptor)
                .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IReadOnlyList<ConnectionRequirement> GetRequirements()
    {
        lock (providers)
        {
            return requirements.Values
                .Select(entry => entry.Requirement)
                .OrderBy(requirement => requirement.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public async Task<IReadOnlyList<ConnectionDescriptor>> GetConnectionsAsync(
        string? principalHandle = null,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return connections.Values
                .Where(connection => principalHandle is null || string.Equals(
                    connection.PrincipalHandle,
                    principalHandle,
                    StringComparison.OrdinalIgnoreCase))
                .Select(ToDescriptor)
                .OrderBy(connection => connection.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConnectionOperationResult> SaveAsync(
        ConnectionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ConnectionValidation.ValidateId(request.ProviderId, nameof(request.ProviderId));
        ConnectionValidation.ValidatePrincipalHandle(request.PrincipalHandle, nameof(request.PrincipalHandle));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var provider = GetProvider(request.ProviderId);
            var descriptor = provider.Provider.Descriptor;
            var connectionId = string.IsNullOrWhiteSpace(request.ConnectionId)
                ? Guid.NewGuid().ToString("N")
                : request.ConnectionId;
            ConnectionValidation.ValidateId(connectionId, nameof(request.ConnectionId));

            connections.TryGetValue(connectionId, out var existing);
            if (existing is not null &&
                (!string.Equals(existing.ProviderId, request.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(existing.PrincipalHandle, request.PrincipalHandle, StringComparison.OrdinalIgnoreCase)))
            {
                return ConnectionOperationResult.Failed(
                    ConnectionStatus.ConfigurationInvalid,
                    "A connection's provider and principal cannot be changed. Create a new connection instead.");
            }

            var knownFields = descriptor.ConfigurationFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
            var unknownField = request.Configuration.Keys.FirstOrDefault(key => !knownFields.ContainsKey(key));
            if (unknownField is not null)
            {
                return ConnectionOperationResult.Failed(
                    ConnectionStatus.ConfigurationInvalid,
                    $"Provider '{descriptor.DisplayName}' does not define setting '{unknownField}'.");
            }

            var settings = existing is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(existing.Configuration, StringComparer.OrdinalIgnoreCase);
            var sensitiveFields = descriptor.ConfigurationFields
                .Where(field => field.Sensitive || field.Kind == ConnectionConfigurationFieldKind.Secret)
                .Select(field => field.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var field in descriptor.ConfigurationFields)
            {
                request.Configuration.TryGetValue(field.Name, out var value);
                value = value?.Trim() ?? string.Empty;
                if (sensitiveFields.Contains(field.Name))
                {
                    if (!string.IsNullOrEmpty(value) && value != SensitiveMask)
                    {
                        await secretStore.SetAsync(SecretKey(connectionId, field.Name), value, cancellationToken);
                    }

                    continue;
                }

                settings[field.Name] = string.IsNullOrEmpty(value)
                    ? field.DefaultValue ?? string.Empty
                    : value;
            }

            var missing = new List<string>();
            foreach (var field in descriptor.ConfigurationFields.Where(field => field.Required))
            {
                if (sensitiveFields.Contains(field.Name))
                {
                    if (await secretStore.GetAsync(SecretKey(connectionId, field.Name), cancellationToken) is null)
                    {
                        missing.Add(field.Label);
                    }
                }
                else if (!settings.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    missing.Add(field.Label);
                }
            }

            if (missing.Count > 0)
            {
                return ConnectionOperationResult.Failed(
                    ConnectionStatus.ConfigurationInvalid,
                    $"Required settings are missing: {string.Join(", ", missing)}.");
            }

            var now = DateTimeOffset.UtcNow;
            var stored = new StoredConnection
            {
                Id = connectionId,
                ProviderId = descriptor.Id,
                PrincipalHandle = request.PrincipalHandle,
                DisplayName = request.DisplayName.Trim(),
                Configuration = settings,
                SensitiveFields = sensitiveFields.ToList(),
                Status = existing?.Status ?? ConnectionStatus.NotConnected,
                StatusMessage = existing?.StatusMessage,
                AccountIdentifier = existing?.AccountIdentifier,
                GrantedRequirements = existing?.GrantedRequirements ?? [],
                CreatedUtc = existing?.CreatedUtc ?? now,
                UpdatedUtc = now
            };
            connections[connectionId] = stored;
            await SaveMetadataAsync(cancellationToken);
            return ConnectionOperationResult.Succeeded(ToDescriptor(stored), "Connection settings saved.");
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ConnectionOperationResult> ConnectAsync(
        string connectionId,
        IReadOnlyList<string>? requirementIds = null,
        CancellationToken cancellationToken = default) =>
        RunProviderOperationAsync(connectionId, requirementIds, connect: true, cancellationToken);

    public async Task<ConnectionOperationResult> ValidateAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var stored))
            {
                return ConnectionOperationResult.Failed(ConnectionStatus.NotConnected, "Connection not found.");
            }

            var provider = GetProvider(stored.ProviderId);
            var context = await CreateContextAsync(stored, provider, cancellationToken);
            var errors = await provider.Provider.ValidateConfigurationAsync(context, cancellationToken);
            var status = errors.Count == 0 ? stored.Status : ConnectionStatus.ConfigurationInvalid;
            stored.Status = status;
            stored.StatusMessage = errors.Count == 0 ? "Configuration is valid." : string.Join(" ", errors);
            stored.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveMetadataAsync(cancellationToken);
            return errors.Count == 0
                ? ConnectionOperationResult.Succeeded(ToDescriptor(stored), stored.StatusMessage)
                : ConnectionOperationResult.Failed(status, stored.StatusMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConnectionOperationResult.Failed(ConnectionStatus.Error, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConnectionOperationResult> DisconnectAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var stored))
            {
                return ConnectionOperationResult.Failed(ConnectionStatus.NotConnected, "Connection not found.");
            }

            if (TryGetProvider(stored.ProviderId, out var provider))
            {
                var context = await CreateContextAsync(stored, provider, cancellationToken);
                await provider.Provider.DisconnectAsync(context, cancellationToken);
            }

            stored.Status = ConnectionStatus.NotConnected;
            stored.StatusMessage = "Disconnected locally. Provider-side consent may still need to be revoked separately.";
            stored.AccountIdentifier = null;
            stored.GrantedRequirements = [];
            stored.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveMetadataAsync(cancellationToken);
            return ConnectionOperationResult.Succeeded(ToDescriptor(stored), stored.StatusMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConnectionOperationResult.Failed(ConnectionStatus.Error, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(connectionId, cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.Remove(connectionId, out var removed))
            {
                return false;
            }

            foreach (var field in removed.SensitiveFields)
            {
                await secretStore.RemoveAsync(SecretKey(connectionId, field), cancellationToken);
            }
            await secretStore.RemoveAsync(SecretKey(connectionId, "oauth-refresh"), cancellationToken);

            await SaveMetadataAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConnectionOperationResult> SetEnabledAsync(
        string connectionId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var stored))
            {
                return ConnectionOperationResult.Failed(ConnectionStatus.NotConnected, "Connection not found.");
            }

            stored.Status = enabled ? ConnectionStatus.NotConnected : ConnectionStatus.Disabled;
            stored.StatusMessage = enabled
                ? "Connection enabled. Connect it before use."
                : "Connection disabled. Stored configuration and secrets were retained.";
            stored.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveMetadataAsync(cancellationToken);
            return ConnectionOperationResult.Succeeded(ToDescriptor(stored), stored.StatusMessage);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConnectionOperationResult> ExecuteActionAsync(
        string connectionId,
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ConnectionValidation.ValidateId(actionId, nameof(actionId));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var stored))
            {
                return ConnectionOperationResult.Failed(ConnectionStatus.NotConnected, "Connection not found.");
            }

            var provider = GetProvider(stored.ProviderId);
            if (!provider.Provider.Descriptor.Actions.Any(action =>
                    string.Equals(action.Id, actionId, StringComparison.OrdinalIgnoreCase)))
            {
                return ConnectionOperationResult.Failed(
                    ConnectionStatus.ProviderUnavailable,
                    $"Provider '{stored.ProviderId}' does not declare action '{actionId}'.");
            }

            var context = await CreateContextAsync(stored, provider, cancellationToken);
            var result = await provider.Provider.ExecuteActionAsync(context, actionId, cancellationToken);
            stored.Status = result.Status;
            stored.StatusMessage = result.Message;
            stored.AccountIdentifier = result.AccountIdentifier ?? stored.AccountIdentifier;
            stored.GrantedRequirements = stored.GrantedRequirements
                .Concat(result.GrantedRequirements)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            stored.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveMetadataAsync(cancellationToken);
            var descriptor = ToDescriptor(stored);
            return result.Status is ConnectionStatus.Connected or
                ConnectionStatus.NotConnected or ConnectionStatus.Disabled
                ? ConnectionOperationResult.Succeeded(descriptor, result.Message)
                : ConnectionOperationResult.Failed(result.Status, result.Message ?? "Provider action failed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConnectionOperationResult.Failed(ConnectionStatus.Error, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public IOpenCaddisConnectionClient ForAgent(FabrCore.Sdk.IFabrCoreAgentHost agentHost, string addonId)
    {
        ArgumentNullException.ThrowIfNull(agentHost);
        ConnectionValidation.ValidateId(addonId, nameof(addonId));
        if (!agentHost.HasUserHandle())
        {
            throw new InvalidOperationException("Connection-enabled agents require a principal-qualified handle.");
        }

        return ForPrincipal(agentHost.GetUserHandle(), addonId);
    }

    internal IOpenCaddisConnectionClient ForPrincipal(string principalHandle, string addonId)
    {
        ConnectionValidation.ValidatePrincipalHandle(principalHandle, nameof(principalHandle));
        ConnectionValidation.ValidateId(addonId, nameof(addonId));
        return new PrincipalConnectionClient(this, principalHandle, addonId);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }

        await Task.CompletedTask;
    }

    internal async Task<ConnectionCredentialResult> GetCredentialAsync(
        string principalHandle,
        string addonId,
        string requirementId,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequirementEntry requirementEntry;
            lock (providers)
            {
                if (!requirements.TryGetValue(requirementId, out requirementEntry!))
                {
                    return ConnectionCredentialResult.Denied(
                        ConnectionStatus.ProviderUnavailable,
                        $"Connection requirement '{requirementId}' is not registered.");
                }
            }

            var requirement = requirementEntry.Requirement;
            if (!string.Equals(requirement.AddonId, addonId, StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ForbiddenPrincipal,
                    $"Requirement '{requirementId}' is not declared by add-on '{addonId}'.");
            }

            var stored = connectionId is null
                ? connections.Values.FirstOrDefault(item =>
                    string.Equals(item.PrincipalHandle, principalHandle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ProviderId, requirement.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                    item.Status == ConnectionStatus.Connected &&
                    item.GrantedRequirements.Contains(requirementId, StringComparer.OrdinalIgnoreCase))
                : connections.GetValueOrDefault(connectionId);

            if (stored is null ||
                !string.Equals(stored.PrincipalHandle, principalHandle, StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.NotConnected,
                    "No eligible connection is assigned to this principal.");
            }

            if (!string.Equals(stored.ProviderId, requirement.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ProviderUnavailable,
                    "The selected connection uses a different provider.");
            }

            if (stored.Status != ConnectionStatus.Connected)
            {
                return ConnectionCredentialResult.Denied(
                    stored.Status,
                    stored.Status == ConnectionStatus.Disabled
                        ? "This connection is disabled."
                        : "Connect this credential from the OpenCaddis Connections page before use.");
            }

            if (!stored.GrantedRequirements.Contains(requirementId, StringComparer.OrdinalIgnoreCase))
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ConsentRequired,
                    "Grant this connection requirement from the OpenCaddis Connections page.",
                    requirement.Scopes);
            }

            if (!TryGetProvider(stored.ProviderId, out var provider))
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ProviderUnavailable,
                    $"Provider '{stored.ProviderId}' is not currently loaded.");
            }

            var context = await CreateContextAsync(stored, provider, cancellationToken);
            var result = await provider.Provider.GetCredentialAsync(context, requirement, cancellationToken);
            if (result.Success && result.Credential?.Kind != requirement.CredentialKind)
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ConfigurationInvalid,
                    $"Provider '{stored.ProviderId}' returned {result.Credential?.Kind} for a " +
                    $"{requirement.CredentialKind} requirement.");
            }
            if (!result.Success && stored.Status != result.Status)
            {
                stored.Status = result.Status;
                stored.StatusMessage = result.Message;
                stored.UpdatedUtc = DateTimeOffset.UtcNow;
                await SaveMetadataAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConnectionCredentialResult.Denied(ConnectionStatus.Error, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ConnectionOperationResult> RunProviderOperationAsync(
        string connectionId,
        IReadOnlyList<string>? requirementIds,
        bool connect,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var stored))
            {
                return ConnectionOperationResult.Failed(ConnectionStatus.NotConnected, "Connection not found.");
            }

            var provider = GetProvider(stored.ProviderId);
            var selectedRequirements = ResolveRequirements(stored.ProviderId, requirementIds);
            var context = await CreateContextAsync(stored, provider, cancellationToken);
            var validationErrors = await provider.Provider.ValidateConfigurationAsync(context, cancellationToken);
            if (validationErrors.Count > 0)
            {
                stored.Status = ConnectionStatus.ConfigurationInvalid;
                stored.StatusMessage = string.Join(" ", validationErrors);
                await SaveMetadataAsync(cancellationToken);
                return ConnectionOperationResult.Failed(stored.Status, stored.StatusMessage);
            }

            var result = connect
                ? await provider.Provider.ConnectAsync(context, selectedRequirements, cancellationToken)
                : throw new InvalidOperationException("Unsupported provider operation.");
            stored.Status = result.Status;
            stored.StatusMessage = result.Message;
            stored.AccountIdentifier = result.AccountIdentifier;
            stored.GrantedRequirements = stored.GrantedRequirements
                .Concat(result.GrantedRequirements)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            stored.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveMetadataAsync(cancellationToken);
            var descriptor = ToDescriptor(stored);
            return result.Status == ConnectionStatus.Connected
                ? ConnectionOperationResult.Succeeded(descriptor, result.Message)
                : ConnectionOperationResult.Failed(result.Status, result.Message ?? "Connection failed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConnectionOperationResult.Failed(ConnectionStatus.Error, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<ConnectionRequirement> ResolveRequirements(
        string providerId,
        IReadOnlyList<string>? requirementIds)
    {
        lock (providers)
        {
            if (requirementIds is null || requirementIds.Count == 0)
            {
                return [];
            }

            var selected = new List<ConnectionRequirement>();
            foreach (var requirementId in requirementIds)
            {
                if (!requirements.TryGetValue(requirementId, out var entry))
                {
                    throw new InvalidOperationException($"Connection requirement '{requirementId}' is not registered.");
                }

                if (!string.Equals(entry.Requirement.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Connection requirement '{requirementId}' belongs to a different provider.");
                }

                selected.Add(entry.Requirement);
            }

            return selected;
        }
    }

    private async Task<ConnectionProviderContext> CreateContextAsync(
        StoredConnection stored,
        ProviderEntry provider,
        CancellationToken cancellationToken)
    {
        var configuration = new Dictionary<string, string>(stored.Configuration, StringComparer.OrdinalIgnoreCase);
        foreach (var field in stored.SensitiveFields)
        {
            var value = await secretStore.GetAsync(SecretKey(stored.Id, field), cancellationToken);
            if (value is not null)
            {
                configuration[field] = value;
            }
        }

        return new ConnectionProviderContext(
            ToDescriptor(stored),
            configuration,
            secretStore,
            interactiveBrowser,
            provider.Services);
    }

    private void AddProvider(ProviderEntry entry)
    {
        if (!providers.TryAdd(entry.Provider.Descriptor.Id, entry))
        {
            throw new InvalidOperationException(
                $"Connection provider '{entry.Provider.Descriptor.Id}' is already registered.");
        }
    }

    private ProviderEntry GetProvider(string providerId) =>
        TryGetProvider(providerId, out var provider)
            ? provider
            : throw new InvalidOperationException($"Connection provider '{providerId}' is not currently loaded.");

    private bool TryGetProvider(string providerId, out ProviderEntry provider)
    {
        lock (providers)
        {
            return providers.TryGetValue(providerId, out provider!);
        }
    }

    private void RemoveOwner(string ownerId)
    {
        lock (providers)
        {
            foreach (var id in requirements
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                requirements.Remove(id);
            }

            foreach (var id in providers
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                providers.Remove(id);
            }
        }
    }

    private ConnectionDescriptor ToDescriptor(StoredConnection stored)
    {
        var configuration = new Dictionary<string, string>(stored.Configuration, StringComparer.OrdinalIgnoreCase);
        foreach (var field in stored.SensitiveFields)
        {
            configuration[field] = SensitiveMask;
        }

        var status = TryGetProvider(stored.ProviderId, out _)
            ? stored.Status
            : ConnectionStatus.ProviderUnavailable;
        return new ConnectionDescriptor
        {
            Id = stored.Id,
            ProviderId = stored.ProviderId,
            PrincipalHandle = stored.PrincipalHandle,
            DisplayName = stored.DisplayName,
            Status = status,
            StatusMessage = status == ConnectionStatus.ProviderUnavailable
                ? $"Provider '{stored.ProviderId}' is not currently loaded."
                : stored.StatusMessage,
            AccountIdentifier = stored.AccountIdentifier,
            Configuration = configuration,
            GrantedRequirements = stored.GrantedRequirements.ToArray(),
            CreatedUtc = stored.CreatedUtc,
            UpdatedUtc = stored.UpdatedUtc
        };
    }

    private void LoadMetadata()
    {
        if (!File.Exists(metadataPath))
        {
            return;
        }

        var json = File.ReadAllText(metadataPath);
        var document = JsonSerializer.Deserialize<ConnectionStoreDocument>(json, JsonOptions);
        if (document?.Connections is null)
        {
            return;
        }

        foreach (var connection in document.Connections)
        {
            connections[connection.Id] = connection;
        }
    }

    private async Task SaveMetadataAsync(CancellationToken cancellationToken)
    {
        var document = new ConnectionStoreDocument { Connections = connections.Values.ToList() };
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var temporaryPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, metadataPath, overwrite: true);
    }

    private static string SecretKey(string connectionId, string field) =>
        $"opencaddis.connection.{connectionId}.{field}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed record ProviderEntry(
        string OwnerId,
        IOpenCaddisConnectionProvider Provider,
        IServiceProvider Services);

    private sealed record RequirementEntry(string OwnerId, ConnectionRequirement Requirement);

    private sealed class ConnectionStoreDocument
    {
        public int Version { get; set; } = 1;

        public List<StoredConnection> Connections { get; set; } = [];
    }

    private sealed class StoredConnection
    {
        public string Id { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string PrincipalHandle { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SensitiveFields { get; set; } = [];
        public ConnectionStatus Status { get; set; }
        public string? StatusMessage { get; set; }
        public string? AccountIdentifier { get; set; }
        public List<string> GrantedRequirements { get; set; } = [];
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private sealed class Registration(Action unregister) : IDisposable
    {
        private Action? unregister = unregister;

        public void Dispose() => Interlocked.Exchange(ref unregister, null)?.Invoke();
    }

    private sealed class PrincipalConnectionClient(
        OpenCaddisConnectionRuntime runtime,
        string principalHandle,
        string addonId) : IOpenCaddisConnectionClient
    {
        public Task<ConnectionCredentialResult> GetCredentialAsync(
            string requirementId,
            string? connectionId = null,
            CancellationToken cancellationToken = default) =>
            runtime.GetCredentialAsync(
                principalHandle,
                addonId,
                requirementId,
                connectionId,
                cancellationToken);

        public async Task<ConnectionCredentialResult> AuthorizeHttpRequestAsync(
            string requirementId,
            string? connectionId,
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = await GetCredentialAsync(requirementId, connectionId, cancellationToken);
            var credential = result.Credential;
            if (!result.Success || credential is null)
            {
                return result;
            }

            if (credential.Kind == ConnectionCredentialKind.BearerToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    credential.Scheme,
                    credential.Value);
            }
            else if (credential.Kind == ConnectionCredentialKind.ApiKey)
            {
                if (string.IsNullOrWhiteSpace(credential.HeaderName))
                {
                    return ConnectionCredentialResult.Denied(
                        ConnectionStatus.ConfigurationInvalid,
                        "The API-key provider did not specify a header name.");
                }

                request.Headers.Remove(credential.HeaderName);
                request.Headers.TryAddWithoutValidation(credential.HeaderName, credential.Value);
            }
            else
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ProviderUnavailable,
                    "Custom credentials must be applied by the provider-specific SDK.");
            }

            return result;
        }
    }
}

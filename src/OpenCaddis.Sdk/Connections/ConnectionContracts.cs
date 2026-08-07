using FabrCore.Sdk;

namespace OpenCaddis.Sdk.Connections;

public interface IOpenCaddisSecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public interface IOpenCaddisInteractiveBrowser
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface IOpenCaddisConnectionProvider
{
    ConnectionProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<string>> ValidateConfigurationAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default);

    Task<ConnectionProviderResult> ConnectAsync(
        ConnectionProviderContext context,
        IReadOnlyList<ConnectionRequirement> requirements,
        CancellationToken cancellationToken = default);

    Task<ConnectionCredentialResult> GetCredentialAsync(
        ConnectionProviderContext context,
        ConnectionRequirement requirement,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default);

    Task<ConnectionProviderResult> ExecuteActionAsync(
        ConnectionProviderContext context,
        string actionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConnectionProviderResult
        {
            Status = ConnectionStatus.ProviderUnavailable,
            Message = $"Provider '{Descriptor.DisplayName}' does not implement action '{actionId}'."
        });
}

public sealed class ConnectionProviderContext
{
    public ConnectionProviderContext(
        ConnectionDescriptor connection,
        IReadOnlyDictionary<string, string> configuration,
        IOpenCaddisSecretStore secretStore,
        IOpenCaddisInteractiveBrowser interactiveBrowser,
        IServiceProvider services)
    {
        Connection = connection;
        Configuration = configuration;
        SecretStore = secretStore;
        InteractiveBrowser = interactiveBrowser;
        Services = services;
    }

    public ConnectionDescriptor Connection { get; }

    public IReadOnlyDictionary<string, string> Configuration { get; }

    public IOpenCaddisSecretStore SecretStore { get; }

    public IOpenCaddisInteractiveBrowser InteractiveBrowser { get; }

    public IServiceProvider Services { get; }

    public string GetRequiredSetting(string name) =>
        Configuration.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Connection setting '{name}' is required.");
}

public sealed record ConnectionProviderResult
{
    public required ConnectionStatus Status { get; init; }

    public string? Message { get; init; }

    public string? AccountIdentifier { get; init; }

    public IReadOnlyList<string> GrantedRequirements { get; init; } = [];
}

public interface IOpenCaddisConnectionAdministration
{
    IReadOnlyList<ConnectionProviderDescriptor> GetProviders();

    IReadOnlyList<ConnectionRequirement> GetRequirements();

    Task<IReadOnlyList<ConnectionDescriptor>> GetConnectionsAsync(
        string? principalHandle = null,
        CancellationToken cancellationToken = default);

    Task<ConnectionOperationResult> SaveAsync(
        ConnectionSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<ConnectionOperationResult> ConnectAsync(
        string connectionId,
        IReadOnlyList<string>? requirementIds = null,
        CancellationToken cancellationToken = default);

    Task<ConnectionOperationResult> ValidateAsync(
        string connectionId,
        CancellationToken cancellationToken = default);

    Task<ConnectionOperationResult> DisconnectAsync(
        string connectionId,
        CancellationToken cancellationToken = default);

    Task<ConnectionOperationResult> SetEnabledAsync(
        string connectionId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<ConnectionOperationResult> ExecuteActionAsync(
        string connectionId,
        string actionId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string connectionId, CancellationToken cancellationToken = default);
}

public interface IOpenCaddisConnectionClientFactory
{
    IOpenCaddisConnectionClient ForAgent(IFabrCoreAgentHost agentHost, string addonId);
}

public interface IOpenCaddisConnectionClient
{
    Task<ConnectionCredentialResult> GetCredentialAsync(
        string requirementId,
        string? connectionId = null,
        CancellationToken cancellationToken = default);

    Task<ConnectionCredentialResult> AuthorizeHttpRequestAsync(
        string requirementId,
        string? connectionId,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default);
}

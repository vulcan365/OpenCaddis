using System.Text.Json.Serialization;

namespace OpenCaddis.Sdk.Connections;

public enum ConnectionAuthenticationKind
{
    OAuthAuthorizationCodePkce,
    OAuthClientCredentials,
    ApiKey,
    Custom
}

public enum ConnectionConfigurationFieldKind
{
    Text,
    Secret,
    Boolean,
    Integer,
    Choice
}

public enum ConnectionCredentialKind
{
    BearerToken,
    ApiKey,
    Custom
}

public enum ConnectionStatus
{
    NotConnected,
    Connected,
    Disabled,
    ConsentRequired,
    ReauthenticationRequired,
    AdminApprovalRequired,
    ForbiddenPrincipal,
    ProviderUnavailable,
    ConfigurationInvalid,
    Error
}

public sealed record ConnectionConfigurationField
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public ConnectionConfigurationFieldKind Kind { get; init; } = ConnectionConfigurationFieldKind.Text;

    public bool Required { get; init; }

    public bool Sensitive { get; init; }

    public string? Placeholder { get; init; }

    public string? HelpText { get; init; }

    public string? DefaultValue { get; init; }

    public IReadOnlyList<string> AllowedValues { get; init; } = [];
}

public sealed record ConnectionProviderActionDescriptor
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public string? HelpText { get; init; }

    public bool IsDestructive { get; init; }
}

public sealed record ConnectionProviderDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required ConnectionAuthenticationKind AuthenticationKind { get; init; }

    public Uri? Authority { get; init; }

    public Uri? AuthorizationEndpoint { get; init; }

    public Uri? TokenEndpoint { get; init; }

    public Uri? RevocationEndpoint { get; init; }

    public Uri? UserInfoEndpoint { get; init; }

    public string CredentialScheme { get; init; } = "Bearer";

    public string? ApiKeyHeaderName { get; init; }

    public string ApiKeyConfigurationField { get; init; } = "apiKey";

    public string ClientIdConfigurationField { get; init; } = "clientId";

    public string ClientSecretConfigurationField { get; init; } = "clientSecret";

    public IReadOnlyList<string> DefaultScopes { get; init; } = [];

    public IReadOnlyDictionary<string, string> AuthorizationParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> TokenParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ConnectionConfigurationField> ConfigurationFields { get; init; } = [];

    public IReadOnlyList<ConnectionProviderActionDescriptor> Actions { get; init; } = [];
}

public sealed record ConnectionRequirement
{
    public required string Id { get; init; }

    public required string AddonId { get; init; }

    public required string ProviderId { get; init; }

    public required string DisplayName { get; init; }

    public required string Purpose { get; init; }

    public ConnectionCredentialKind CredentialKind { get; init; } = ConnectionCredentialKind.BearerToken;

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public string? Audience { get; init; }

    public string? Resource { get; init; }

    public bool MayRequireAdministratorApproval { get; init; }
}

public sealed record ConnectionDescriptor
{
    public required string Id { get; init; }

    public required string ProviderId { get; init; }

    public required string PrincipalHandle { get; init; }

    public required string DisplayName { get; init; }

    public ConnectionStatus Status { get; init; }

    public string? StatusMessage { get; init; }

    public string? AccountIdentifier { get; init; }

    public IReadOnlyDictionary<string, string> Configuration { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GrantedRequirements { get; init; } = [];

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record ConnectionSaveRequest
{
    public string? ConnectionId { get; init; }

    public required string ProviderId { get; init; }

    public required string PrincipalHandle { get; init; }

    public required string DisplayName { get; init; }

    public IReadOnlyDictionary<string, string> Configuration { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ConnectionOperationResult
{
    public required bool Success { get; init; }

    public required ConnectionStatus Status { get; init; }

    public string? Message { get; init; }

    public ConnectionDescriptor? Connection { get; init; }

    public static ConnectionOperationResult Succeeded(ConnectionDescriptor connection, string? message = null) =>
        new() { Success = true, Status = connection.Status, Message = message, Connection = connection };

    public static ConnectionOperationResult Failed(ConnectionStatus status, string message) =>
        new() { Success = false, Status = status, Message = message };
}

public sealed class ConnectionCredential
{
    public ConnectionCredential(
        ConnectionCredentialKind kind,
        string value,
        string scheme = "Bearer",
        string? headerName = null,
        DateTimeOffset? expiresOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Kind = kind;
        Value = value;
        Scheme = scheme;
        HeaderName = headerName;
        ExpiresOn = expiresOn;
    }

    public ConnectionCredentialKind Kind { get; }

    [JsonIgnore]
    public string Value { get; }

    public string Scheme { get; }

    public string? HeaderName { get; }

    public DateTimeOffset? ExpiresOn { get; }

    public override string ToString() => $"{Kind} credential (redacted)";
}

public sealed record ConnectionCredentialResult
{
    public required bool Success { get; init; }

    public required ConnectionStatus Status { get; init; }

    [JsonIgnore]
    public ConnectionCredential? Credential { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> MissingScopes { get; init; } = [];

    public static ConnectionCredentialResult Granted(ConnectionCredential credential) =>
        new() { Success = true, Status = ConnectionStatus.Connected, Credential = credential };

    public static ConnectionCredentialResult Denied(
        ConnectionStatus status,
        string message,
        IReadOnlyList<string>? missingScopes = null) =>
        new()
        {
            Success = false,
            Status = status,
            Message = message,
            MissingScopes = missingScopes ?? []
        };
}

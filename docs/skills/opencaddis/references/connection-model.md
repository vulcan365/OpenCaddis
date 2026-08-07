# OpenCaddis connection model

## Architecture

OpenCaddis uses four layers:

1. An add-on module declares identity, connection providers, requirements, and DI services.
2. `OpenCaddis.App` renders provider schemas, initiates interactive authentication, owns metadata, and supplies OS-backed secret storage.
3. `OpenCaddis.Server` loads modules before FabrCore plugin resolution and exposes only `IOpenCaddisConnectionClientFactory` to server DI.
4. A FabrCore plugin obtains a client bound to its hosting agent principal and add-on ID.

Desktop use needs no separate gateway. Interactive administration stays App-initiated. Add-ons run as trusted in-process assemblies.

## Add-on module

Expose exactly one public module with a public parameterless constructor:

```csharp
using OpenCaddis.Sdk.Addons;

public sealed class ContosoAddonModule : IOpenCaddisAddonModule
{
    public void Configure(OpenCaddisAddonBuilder builder)
    {
        builder.SetIdentity("contoso.crm", "Contoso CRM", "1.0.0");
        builder.Services.AddHttpClient("contoso-crm", client =>
            client.BaseAddress = new Uri("https://api.contoso.example/"));
    }
}
```

Register standard providers with `AddConnectionProvider(descriptor)`. Register custom providers with `AddConnectionProvider<TProvider>(descriptor)`; the builder registers the implementation as a singleton. Register other provider/plugin dependencies through `builder.Services`.

Module registrations are validated before server startup. Registration is atomic: a failure must not leave a partial provider catalog. Registrations are removed before the collectible add-on assembly context unloads.

## Provider descriptor

`ConnectionProviderDescriptor` supplies schema and protocol metadata:

| Property | Purpose |
|---|---|
| `Id`, `DisplayName`, `Description` | Stable identity and App display metadata |
| `AuthenticationKind` | PKCE, client credentials, API key, or custom |
| `Authority` | Optional provider authority metadata |
| `AuthorizationEndpoint`, `TokenEndpoint` | OAuth protocol endpoints |
| `RevocationEndpoint`, `UserInfoEndpoint` | Optional disconnect and account-discovery endpoints |
| `ConfigurationFields` | Schema-rendered App settings |
| `DefaultScopes` | Provider scopes always included |
| `AuthorizationParameters`, `TokenParameters` | Provider-specific protocol parameters |
| `ApiKeyHeaderName`, `ApiKeyConfigurationField` | API-key request injection |
| `ClientIdConfigurationField`, `ClientSecretConfigurationField` | Semantic OAuth field names |
| `Actions` | Safe labels for custom provider actions; no executable UI |

Configuration fields support `Text`, `Secret`, `Boolean`, `Integer`, and `Choice`, plus required/default/placeholder/help/allowed-value metadata. Set `Sensitive = true` for all secret material. `Secret` fields are treated as sensitive even if the flag is omitted.

Endpoints must be absolute HTTPS URIs. Loopback HTTP is allowed for local callbacks. OAuth scopes are individual strings and cannot contain whitespace.

## Connection requirement

A `ConnectionRequirement` is a fixed capability requested by one add-on:

```csharp
new ConnectionRequirement
{
    Id = "contoso.crm.contacts.read",
    AddonId = "contoso.crm",
    ProviderId = "contoso-oauth",
    DisplayName = "Read CRM contacts",
    Purpose = "Find contacts requested through CRM agent tools.",
    CredentialKind = ConnectionCredentialKind.BearerToken,
    Scopes = ["contacts.read"],
    Audience = "https://api.contoso.example",
    MayRequireAdministratorApproval = true
}
```

Use requirement IDs at runtime. Do not expose a scope parameter in a FabrCore tool. The runtime rejects requirements owned by a different add-on, connections for a different provider, undeclared grants, disabled/unconnected connections, and connections bound to another principal.

Use `Resource` only for providers that require a resource parameter. Use `Audience` for APIs that require an audience. Keep Microsoft/Google-specific scopes in the add-on that needs them, not in OpenCaddis core.

## Connections page and lifecycle

The App renders fields without provider-specific UI code and supports:

- Save and validate configuration.
- Connect or incrementally grant delegated requirements.
- Validate client credentials and API keys.
- Edit/rotate sensitive fields without revealing the saved value.
- Enable or disable a connection while retaining configuration.
- Execute descriptor-declared custom actions, with confirmation for destructive actions.
- Disconnect or delete credentials.

Connections are assigned to a FabrCore principal. A service credential may be configured separately for several principals, but one principal cannot discover or use another principal's binding.

Statuses include `NotConnected`, `Connected`, `Disabled`, `ConsentRequired`, `ReauthenticationRequired`, `AdminApprovalRequired`, `ForbiddenPrincipal`, `ProviderUnavailable`, `ConfigurationInvalid`, and `Error`. Preserve these distinctions when returning guidance from tools.

## Storage and credential behavior

- Store non-secret metadata atomically in App data.
- Store sensitive schema fields and generic refresh tokens through `IOpenCaddisSecretStore`.
- Keep client-credentials access tokens in memory, refresh shortly before expiry, and coalesce concurrent refreshes.
- Use an OS-protected MSAL cache for the bundled Microsoft provider.
- Return only currently usable access/API credentials. `ConnectionCredential.Value` and `ConnectionCredentialResult.Credential` are JSON-ignored, and `ToString()` is redacted.
- Delete provider-owned refresh material when disconnecting/deleting. Never persist temporary access tokens.

## Custom providers and actions

Implement `IOpenCaddisConnectionProvider` for certificates, signatures, proprietary exchanges, or nonstandard account behavior. Implement:

- `ValidateConfigurationAsync`
- `ConnectAsync`
- `GetCredentialAsync`
- `DisconnectAsync`
- `ExecuteActionAsync` when descriptor actions are declared

Use `ConnectionProviderContext.Configuration` for resolved settings, including sensitive values supplied only for the provider call. Use `SecretStore` for provider cache material and `Services` for add-on DI dependencies. Return only granted requirement IDs from a successful connect.

`AuthorizeHttpRequestAsync` handles bearer tokens and API-key headers. For `ConnectionCredentialKind.Custom`, use `GetCredentialAsync` and a provider-specific request signer/SDK. Do not return custom secret material from a FabrCore tool.

# Provider recipes

## Bundled Microsoft provider

Use provider ID `microsoft`; do not register a descriptor or custom MSAL implementation:

```csharp
builder.SetIdentity("contoso.mail", "Contoso Mail", "1.0.0")
    .AddConnectionRequirement(new ConnectionRequirement
    {
        Id = "contoso.mail.read",
        AddonId = "contoso.mail",
        ProviderId = "microsoft",
        DisplayName = "Read Microsoft mail",
        Purpose = "Read messages requested through mail tools.",
        Scopes = ["Mail.Read"],
        MayRequireAdministratorApproval = true
    });
```

The user supplies a tenant and Entra application client ID on the Connections page. The Entra registration must be a public desktop client with exact redirect URI `http://localhost` under **Mobile and desktop applications**. Do not request or store a client secret. If `AADSTS7000218` occurs, the redirect is commonly registered as Web/confidential; move `http://localhost` to the Mobile and desktop platform and enable public client flows.

MSAL handles interactive consent, account selection, protected token caching, silent acquisition, and account removal. OpenCaddis always includes `User.Read` for basic account access and adds installed add-on requirements during consent.

## Delegated OAuth with PKCE

Use for Gmail and standards-compliant user accounts:

```csharp
builder.SetIdentity("contoso.gmail", "Contoso Gmail", "1.0.0")
    .AddConnectionProvider(new ConnectionProviderDescriptor
    {
        Id = "google",
        DisplayName = "Google",
        AuthenticationKind = ConnectionAuthenticationKind.OAuthAuthorizationCodePkce,
        AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth"),
        TokenEndpoint = new("https://oauth2.googleapis.com/token"),
        RevocationEndpoint = new("https://oauth2.googleapis.com/revoke"),
        UserInfoEndpoint = new("https://openidconnect.googleapis.com/v1/userinfo"),
        AuthorizationParameters = new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        },
        ConfigurationFields =
        [
            new ConnectionConfigurationField
            {
                Name = "clientId",
                Label = "OAuth client ID",
                Required = true
            }
        ]
    })
    .AddConnectionRequirement(new ConnectionRequirement
    {
        Id = "contoso.gmail.read",
        AddonId = "contoso.gmail",
        ProviderId = "google",
        DisplayName = "Read Gmail",
        Purpose = "Read messages requested through Gmail tools.",
        Scopes = ["https://www.googleapis.com/auth/gmail.readonly"]
    });
```

The standard engine creates state and PKCE verifier/challenge values, starts a loopback callback, exchanges the code, rotates refresh tokens, optionally calls user info, performs incremental consent, and revokes on disconnect. Add `clientSecret` only when a provider explicitly requires it for installed/native clients; mark it sensitive.

## OAuth client credentials

Use for service principals and daemon APIs:

```csharp
builder.SetIdentity("contoso.records", "Contoso Records", "1.0.0")
    .AddConnectionProvider(new ConnectionProviderDescriptor
    {
        Id = "contoso-service",
        DisplayName = "Contoso Records API",
        AuthenticationKind = ConnectionAuthenticationKind.OAuthClientCredentials,
        TokenEndpoint = new("https://identity.contoso.example/oauth2/token"),
        ConfigurationFields =
        [
            new ConnectionConfigurationField
            {
                Name = "clientId", Label = "Client ID", Required = true
            },
            new ConnectionConfigurationField
            {
                Name = "clientSecret", Label = "Client secret", Required = true,
                Kind = ConnectionConfigurationFieldKind.Secret, Sensitive = true
            }
        ]
    })
    .AddConnectionRequirement(new ConnectionRequirement
    {
        Id = "contoso.records.read",
        AddonId = "contoso.records",
        ProviderId = "contoso-service",
        DisplayName = "Read records",
        Purpose = "Read records requested through agent tools.",
        Scopes = ["records.read"],
        Audience = "https://api.contoso.example"
    });
```

The standard engine sends `client_credentials`, client ID/secret, declared scopes, audience, resource, and descriptor token parameters. It caches access tokens only in memory, coalesces concurrent refresh, and retries transient 429/502/503/504 token responses. Persist the client secret, not the access token.

Use `ClientIdConfigurationField` and `ClientSecretConfigurationField` when field names differ from the defaults.

## API key

```csharp
builder.SetIdentity("contoso.weather", "Contoso Weather", "1.0.0")
    .AddConnectionProvider(new ConnectionProviderDescriptor
    {
        Id = "contoso-weather-key",
        DisplayName = "Contoso Weather API",
        AuthenticationKind = ConnectionAuthenticationKind.ApiKey,
        ApiKeyHeaderName = "X-Api-Key",
        ApiKeyConfigurationField = "apiKey",
        ConfigurationFields =
        [
            new ConnectionConfigurationField
            {
                Name = "apiKey", Label = "API key", Required = true,
                Kind = ConnectionConfigurationFieldKind.Secret, Sensitive = true
            }
        ]
    })
    .AddConnectionRequirement(new ConnectionRequirement
    {
        Id = "contoso.weather.forecast",
        AddonId = "contoso.weather",
        ProviderId = "contoso-weather-key",
        DisplayName = "Weather forecasts",
        Purpose = "Retrieve forecasts through weather tools.",
        CredentialKind = ConnectionCredentialKind.ApiKey
    });
```

`AuthorizeHttpRequestAsync` removes any existing header with that name and injects the configured key. Never add the key to the `HttpClient` default headers because that broadens its lifetime and destination scope.

## Custom provider

Declare `AuthenticationKind.Custom`, configuration fields, and optional actions, then register the implementation:

```csharp
public static readonly ConnectionProviderDescriptor Descriptor = new()
{
    Id = "contoso-signed",
    DisplayName = "Contoso Signed API",
    AuthenticationKind = ConnectionAuthenticationKind.Custom,
    ConfigurationFields =
    [
        new ConnectionConfigurationField
        {
            Name = "privateKey", Label = "Private key", Required = true,
            Kind = ConnectionConfigurationFieldKind.Secret, Sensitive = true
        }
    ],
    Actions =
    [
        new ConnectionProviderActionDescriptor
        {
            Id = "rotate-key", Label = "Rotate key", IsDestructive = true,
            HelpText = "Replace the provider signing key."
        }
    ]
};

builder.AddConnectionProvider<ContosoSignedProvider>(Descriptor);
```

Return a `Custom` credential only to a provider-specific SDK/signer inside the plugin. Prefer returning a short-lived signing handle or adapter instead of long-lived private material when the proprietary SDK permits it.

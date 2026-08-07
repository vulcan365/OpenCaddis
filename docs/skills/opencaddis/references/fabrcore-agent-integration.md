# FabrCore agent integration

## Use a plugin

Use `IFabrCorePlugin` because authenticated calls need server DI and `IFabrCoreAgentHost`. A standalone `[ToolAlias]` method cannot resolve the principal-bound connection factory.

Register an HTTP client from the add-on module:

```csharp
public void Configure(OpenCaddisAddonBuilder builder)
{
    builder.SetIdentity("contoso.records", "Contoso Records", "1.0.0");
    builder.Services.AddHttpClient("contoso-records", client =>
    {
        client.BaseAddress = new Uri("https://api.contoso.example/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    // Register provider/requirements here.
}
```

Consume the requirement in a FabrCore plugin:

```csharp
using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.DependencyInjection;
using OpenCaddis.Sdk.Connections;

[PluginAlias("contoso-records")]
[Description("Authenticated Contoso Records tools")]
[FabrCoreCapabilities("Reads Contoso records using the principal's OpenCaddis connection.")]
[FabrCoreNote("Configure contoso.records.read on the OpenCaddis Connections page.")]
public sealed class ContosoRecordsPlugin : IFabrCorePlugin
{
    private const string AddonId = "contoso.records";
    private const string ReadRequirement = "contoso.records.read";
    private IOpenCaddisConnectionClient connections = default!;
    private IHttpClientFactory httpClients = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider services)
    {
        var agentHost = services.GetRequiredService<IFabrCoreAgentHost>();
        connections = services.GetRequiredService<IOpenCaddisConnectionClientFactory>()
            .ForAgent(agentHost, AddonId);
        httpClients = services.GetRequiredService<IHttpClientFactory>();
        return Task.CompletedTask;
    }

    [Description("Gets one record from Contoso Records.")]
    public async Task<string> GetRecordAsync(
        [Description("Record identifier")] string id,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"records/{Uri.EscapeDataString(id)}");
        var authorization = await connections.AuthorizeHttpRequestAsync(
            ReadRequirement,
            connectionId: null,
            request,
            cancellationToken);
        if (!authorization.Success)
        {
            return authorization.Status switch
            {
                ConnectionStatus.ConsentRequired =>
                    "Connection required: grant record access on the OpenCaddis Connections page.",
                ConnectionStatus.ReauthenticationRequired =>
                    "Connection required: reconnect the account on the OpenCaddis Connections page.",
                ConnectionStatus.Disabled =>
                    "Connection required: this connection is disabled.",
                _ => $"Connection unavailable: {authorization.Message}"
            };
        }

        var client = httpClients.CreateClient("contoso-records");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? body
            : $"Contoso Records returned HTTP {(int)response.StatusCode}.";
    }
}
```

Do not return raw response bodies on authentication or server errors unless they are known safe; providers sometimes include identifiers or sensitive diagnostics.

## Connection selection

Pass `connectionId: null` to select an eligible connected binding for the current principal and provider. Pass a connection ID only when the tool has a safe, non-secret connection selector configured outside user input. The runtime still enforces principal, provider, add-on ownership, status, and granted requirement.

Never ask the model/user to supply a principal handle, token, API key, scope, audience, or connection ID as an arbitrary tool argument.

## Third-party credential adapters

Use `GetCredentialAsync` only when an SDK cannot accept an authorized `HttpRequestMessage`:

```csharp
var result = await connections.GetCredentialAsync(ReadRequirement, cancellationToken: cancellationToken);
if (!result.Success || result.Credential is null)
{
    return $"Connection unavailable: {result.Message}";
}

// Construct the SDK's short-lived token adapter in memory.
// Do not serialize, cache, log, or return result.Credential.Value.
```

For custom signed credentials, apply the credential inside a narrowly scoped signer/SDK. `AuthorizeHttpRequestAsync` intentionally rejects `Custom` credentials because OpenCaddis cannot infer proprietary request semantics.

## Configure the agent

Add the plugin alias to the agent configuration or blueprint:

```json
{
  "Handle": "local-user:records-agent",
  "AgentType": "your-agent-alias",
  "Plugins": ["contoso-records"]
}
```

Do not add connection secrets to `Args`. The plugin's `ForAgent` call derives `local-user` from the hosting agent handle and binds access to that principal.

If an agent resolves configured tools itself, call `ResolveConfiguredToolsAsync()` as usual; OpenCaddis connection access is a dependency of the configured plugin, not a separate tool exposed to the model.

## Tool behavior

- Add specific `[Description]` attributes to methods and parameters.
- Use `[FabrCoreCapabilities]` to describe external API operations.
- Use `[FabrCoreNote]` to name required connection capabilities, never secret values.
- Set and clear `agentHost.SetStatusMessage()` around long API operations.
- Handle cancellation throughout HTTP and provider calls.
- Return concise actionable connection states instead of throwing for expected consent/reconnect cases.
- Consider FabrCore verifiable-execution HTTP effect recording for consequential external writes; record safe hashes/metadata, never credentials or raw customer data.

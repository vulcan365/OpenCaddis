---
name: opencaddis
description: Build, extend, and troubleshoot OpenCaddis add-ons and provider-neutral connections using OpenCaddis.Sdk and FabrCore. Use for IOpenCaddisAddonModule, OpenCaddisAddonBuilder, ConnectionProviderDescriptor, ConnectionRequirement, delegated OAuth/PKCE, OAuth client credentials, API keys, custom connection providers/actions, Microsoft or Gmail-style connectors, secure credential use, authenticated FabrCore plugins/tools, add-on packaging, Connections UI behavior, and principal-bound agent access.
---

# OpenCaddis Add-ons and Connections

Build independently deployable OpenCaddis add-ons that declare connection capabilities and expose authenticated FabrCore tools. Keep provider-specific authentication and API behavior in the add-on; keep credentials in the OpenCaddis connection layer.

## Route the work

Read only the references needed for the task:

- Read [references/connection-model.md](references/connection-model.md) for architecture, contracts, lifecycle, validation, Connections UI, storage, principal binding, and custom actions.
- Read [references/provider-recipes.md](references/provider-recipes.md) when declaring Microsoft requirements or implementing delegated OAuth, client credentials, API-key, or custom providers.
- Read [references/fabrcore-agent-integration.md](references/fabrcore-agent-integration.md) when building FabrCore plugins/tools that call connected APIs or configuring agents to use them.
- Read [references/development-and-operations.md](references/development-and-operations.md) for project setup, publishing, installation, restart behavior, testing, and authentication troubleshooting.

## Choose the connection design

| Need | Authentication kind | Implementation |
|---|---|---|
| Microsoft delegated permissions | Bundled provider ID `microsoft` | Declare requirements only; do not register another Microsoft provider |
| Standards-based user account such as Gmail | `OAuthAuthorizationCodePkce` | Declare endpoints and schema; use the standard engine |
| Service principal or daemon API | `OAuthClientCredentials` | Declare token endpoint, sensitive client secret, scopes/audience/resource |
| Header token or key | `ApiKey` | Declare a sensitive API-key field and header name |
| Certificates, signatures, proprietary exchange | `Custom` | Implement `IOpenCaddisConnectionProvider` |

Prefer the standard engines. Implement a custom provider only when protocol behavior cannot be expressed by a descriptor.

## Build an add-on

1. Use the `OpenCaddis.Sdk` and `FabrCore.Sdk` versions scaffolded by Server Builder. Do not independently upgrade one package.
2. Expose exactly one public, parameterless `IOpenCaddisAddonModule` in the add-on assembly.
3. Call `SetIdentity` with a stable add-on ID and version.
4. Register any new provider descriptor or custom provider. Skip this for bundled provider IDs such as `microsoft`.
5. Declare every capability as a fixed `ConnectionRequirement`. Set `AddonId` to the module identity exactly.
6. Register provider-specific DI services through `builder.Services`.
7. Build FabrCore plugins that request the declared requirement ID through `IOpenCaddisConnectionClientFactory`.
8. Build and test the solution. Publish the add-on into the configured AddOns directory only when requested, then restart OpenCaddis Server.
9. Configure and grant the connection on the App Connections page for the same FabrCore principal that owns the agent.

Use stable IDs as runtime contracts. Changing an add-on, provider, or requirement ID breaks existing metadata, consent, and plugin calls.

## Enforce the security boundary

- Never put access tokens, refresh tokens, client secrets, API keys, certificates, or provider cache blobs in `fabrcore.json`, `AgentConfiguration.Args`, agent state, FabrCore storage, messages, prompts, skills, tool results, monitor events, logs, or HTTP responses.
- Never add a client secret to the bundled Microsoft delegated provider. It is a desktop public client using MSAL and PKCE.
- Call `AuthorizeHttpRequestAsync` by default. It attaches the credential without returning its value to plugin code.
- Use `GetCredentialAsync` only when a third-party SDK requires an adapter or custom credential handling.
- Pass a declared requirement ID at runtime. Never accept scopes from tool parameters or construct scopes from user input.
- Bind the client with `ForAgent(agentHost, addonId)`. Never accept a principal handle from a tool argument.
- Treat installed add-on DLLs as trusted in-process code. The connection framework reduces accidental disclosure and scope expansion; it is not a sandbox.
- Return connection-required guidance on authorization failure. Do not echo exception bodies that may contain sensitive provider data.

## Use FabrCore correctly

Implement authenticated API tools as `IFabrCorePlugin`, not standalone tools, because connection access requires DI and the hosting `IFabrCoreAgentHost`. Resolve the connection client once in `InitializeAsync`, then authorize each outgoing request immediately before sending it.

Keep non-secret behavior settings in plugin settings if necessary, but keep authentication configuration on the Connections page. Add `[Description]` to every tool and parameter, plus `[FabrCoreCapabilities]` and safe `[FabrCoreNote]` metadata on the plugin. Configure the plugin alias in `AgentConfiguration.Plugins` or the corresponding blueprint.

## Verify before publishing

- Validate unique and well-formed add-on, provider, requirement, field, and action IDs.
- Verify OAuth endpoints use HTTPS, except loopback HTTP callbacks.
- Mark every client secret and API key field as `Sensitive = true` or `Kind = Secret`.
- Verify the returned credential kind matches the requirement.
- Test missing consent, wrong principal, disabled connection, provider unload/reload, cancellation, expiry/refresh, and redaction.
- Confirm no secret appears in metadata JSON, logs, exceptions returned to tools, or serialized credential results.
- Build the whole solution and run its tests before publishing the DLL.

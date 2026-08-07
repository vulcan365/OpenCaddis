# OpenCaddis

OpenCaddis is under active development.

## Local FabrCore cloud configuration

`OpenCaddis.App` hosts a loopback FabrCore Cloud Server at `http://localhost:5082/`.
Open the Server page's **Cloud configuration** tab to maintain separate `fabrcore.json`
documents for OpenCaddis Server and OpenCaddis Server Builder. Each mode must have at least
one valid model and API key before it can start.

The documents and generated per-mode cloud authentication keys are stored as plaintext in the
app-data `CloudServer` folder. They persist across app restarts; this local store is intentionally
simple and is not a secrets vault.

## Provider-neutral connections

OpenCaddis add-ons can declare delegated OAuth with PKCE, OAuth client credentials, API keys, or a
custom authentication provider through `OpenCaddis.Sdk`. Microsoft is the bundled reference
provider, but the runtime has no Microsoft Graph dependency or Microsoft-specific SDK contract.
Gmail, GitHub, Salesforce, and private APIs can use the same registration and Connections UI
without changing or rebuilding OpenCaddis.

An add-on assembly exposes one public, parameterless `IOpenCaddisAddonModule`. Its module registers
its identity, providers, fixed connection requirements, and any services used by its FabrCore
plugins. Copy the published add-on into the configured AddOns directory and restart OpenCaddis
Server. Module registrations are loaded before FabrCore plugin discovery and removed before the
collectible add-on load context is unloaded.

The SDK NuGet package includes templates for delegated OAuth, client credentials, API keys, custom
providers, and an authenticated FabrCore plugin. A tool obtains a principal-bound client from
`IOpenCaddisConnectionClientFactory` and normally calls `AuthorizeHttpRequestAsync`. The requirement
ID—not caller-provided scopes—is the authorization boundary. Use `GetCredentialAsync` only when an
external SDK requires a credential adapter.

### Microsoft BYO app registration

The bundled Microsoft provider uses MSAL and a developer-owned Entra public-client registration:

1. Register a Mobile and desktop application in Microsoft Entra ID.
2. Add `http://localhost` as a redirect URI and allow public-client flows.
3. In OpenCaddis Connections, choose Microsoft and enter the application client ID and tenant
   (`organizations`, a tenant GUID, or a verified domain).
4. Select the capabilities declared by installed add-ons and choose **Save and connect**.

Add-on requirements contain Microsoft scopes such as `Mail.Read`; the provider requests them only
during App-initiated consent and acquires tokens silently for agent tools afterward. Tenant policy
can still require administrator approval.

### Storage and trust boundary

Connection metadata and non-secret settings are written atomically beneath the App data
`Connections` directory. Sensitive descriptor fields, API keys, client secrets, and generic OAuth
refresh tokens go through the App-owned OS secure-store abstraction. Microsoft uses an
OS-protected MSAL cache. Temporary client-credentials tokens stay in memory and are coalesced and
refreshed shortly before expiry.

Only the silent, principal-bound connection client is registered in the FabrCore server. Interactive
authentication and credential administration stay in the App process. Credentials must never be
placed in `fabrcore.json`, agent arguments, FabrCore storage, messages, prompts, skills, logs, or
HTTP responses. Add-ons are trusted in-process DLLs: these APIs prevent accidental disclosure and
scope expansion, but they do not sandbox malicious installed code. No separate gateway is needed
for the desktop/localhost deployment; remote and headless authentication are outside the current
boundary.

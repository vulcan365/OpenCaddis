# Development and operations

## Create the project

Use OpenCaddis Server Builder to create the class library. It adds matching `FabrCore.Sdk` and bundled `OpenCaddis.Sdk` package references. If working manually, use the versions shipped with the running OpenCaddis release.

Keep provider/module/plugin code in the add-on project. Keep tests in a sibling test project. Do not modify OpenCaddis core for a new Gmail, Salesforce, GitHub, API-key, or custom OAuth connector.

An add-on assembly may contain agents, FabrCore plugins/tools, one OpenCaddis module, skills, and supporting services. Publish all runtime dependencies needed by the collectible assembly context.

## Build and install

1. Restore and build the project.
2. Run solution tests.
3. Publish/copy the add-on output into the AddOns directory configured on the Server page.
4. Restart OpenCaddis Server. A newly installed provider is discovered only at server startup.
5. Open Connections. Verify the provider and schema appear.
6. Create a connection for the agent's FabrCore principal.
7. Select declared capabilities and connect/validate.
8. Start or reprovision the agent with the plugin alias configured.

Do not load add-ons in Server Builder mode from its publish-output directory; Builder owns its own coding agents/plugins. Test the produced add-on by restarting normal Server mode.

## Test matrix

Test at least:

- Descriptor and registration validation, including duplicate IDs and bad endpoints.
- Sensitive-field masking and absence from metadata/logs/JSON.
- Successful connection and credential kind.
- Declared-requirement enforcement and rejection of caller-supplied scope expansion.
- Principal and add-on isolation.
- Disabled, disconnected, missing-consent, admin-approval, and reauthentication states.
- Token expiry, cached reuse, concurrent refresh coalescing, throttling, and cancellation.
- PKCE challenge/state mismatch, consent denial, refresh rotation, and revocation for delegated OAuth.
- API-key header injection without default-header or diagnostic leakage.
- Provider unload/reload and persisted metadata behavior.
- A synthetic add-on copied into AddOns and loaded from a collectible assembly without an OpenCaddis rebuild.

Use fake token/authorization endpoints in automated tests. Do not make test suites depend on live tenant credentials.

## Troubleshoot registration

If the provider does not appear:

- Confirm the assembly contains exactly one public parameterless `IOpenCaddisAddonModule`.
- Confirm the add-on and dependencies are in AddOns.
- Restart normal Server mode.
- Check duplicate IDs, invalid endpoints, sensitive-field requirements, mismatched custom provider descriptor IDs, or a requirement referencing an unavailable provider.

If a plugin cannot resolve the connection factory:

- Confirm normal Server mode received the App-owned connection runtime.
- Confirm the plugin is initialized by FabrCore server DI.
- Confirm the project references the matching `OpenCaddis.Sdk` identity shared by the host load context.

If no eligible connection is found:

- Match the connection principal to `agentHost.GetUserHandle()`.
- Match the plugin's add-on ID and requirement ID exactly.
- Confirm the connection provider matches the requirement and the requirement was granted.
- Enable and connect/reconnect the connection.

## Troubleshoot Microsoft delegated authentication

Use a public Entra desktop registration:

- Add `http://localhost` under **Mobile and desktop applications**, not Web.
- Enable public client flows.
- Use the application client ID and tenant/verified domain on the Connections page.
- Configure delegated Microsoft Graph permissions required by the add-on; do not create a client secret for this flow.

`AADSTS7000218` means Entra classified the token request as confidential and demanded a secret/assertion. The public-client toggle alone may not fix authorization code exchange when `http://localhost` is registered under Web; move that exact redirect to Mobile and desktop applications.

`AADSTS50011` indicates redirect mismatch. Use exactly `http://localhost` for the bundled provider.

Admin approval and conditional-access/MFA policies remain tenant decisions. Return `AdminApprovalRequired`, `ConsentRequired`, or `ReauthenticationRequired` guidance rather than bypassing policy.

## Operational boundary

The current connection administration flow is App-initiated for desktop/localhost use. Remote/headless consent and gateway hardening are outside this design. Do not expose connection administration or raw credentials through new server endpoints as an add-on shortcut.

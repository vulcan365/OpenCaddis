# OpenCaddis connection add-on templates

Copy one provider module template into an add-on assembly, replace the example IDs and endpoints,
and add the consuming FabrCore plugin. An assembly must expose exactly one public,
parameterless `IOpenCaddisAddonModule`. Provider, requirement, and add-on IDs are stable runtime
contracts; tools request a declared requirement ID and never request arbitrary scopes.

- `DelegatedOAuthModule.cs.txt`: standards-based OAuth authorization code with PKCE.
- `ClientCredentialsModule.cs.txt`: service-to-service OAuth.
- `ApiKeyModule.cs.txt`: securely stored header credential.
- `CustomProvider.cs.txt`: proprietary authentication escape hatch.
- `AuthorizedHttpPlugin.cs.txt`: principal-bound FabrCore tool consumption.

Build the project, copy its publish output into the configured AddOns directory, and restart
OpenCaddis Server. The provider and its schema then appear on the Connections page.

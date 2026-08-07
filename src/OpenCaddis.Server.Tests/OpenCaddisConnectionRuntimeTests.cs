using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCaddis.Sdk.Addons;
using OpenCaddis.Sdk.Connections;
using OpenCaddis.Server.Connections;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class OpenCaddisConnectionRuntimeTests
{
    [TestMethod]
    public async Task Api_key_is_secret_principal_bound_and_applied_only_to_requests()
    {
        await using var fixture = new RuntimeFixture();
        var registration = CreateApiKeyRegistration();
        using var addon = fixture.Runtime.RegisterAddon(registration, fixture.Services);

        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-api-key",
            PrincipalHandle = "alice@example.test",
            DisplayName = "Weather",
            Configuration = new Dictionary<string, string> { ["apiKey"] = "super-secret-key" }
        });
        Assert.IsTrue(saved.Success);
        Assert.AreEqual("••••••", saved.Connection!.Configuration["apiKey"]);
        Assert.DoesNotContain("super-secret-key", await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.Contains("super-secret-key", fixture.SecretStore.Values.Values);

        var connected = await fixture.Runtime.ConnectAsync(
            saved.Connection.Id,
            ["test.weather.read"]);
        Assert.IsTrue(connected.Success);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://weather.example.test/");
        var client = fixture.Runtime.ForPrincipal("alice@example.test", "test.weather");
        var credentialResult = await client.AuthorizeHttpRequestAsync(
            "test.weather.read",
            saved.Connection.Id,
            request);
        Assert.IsTrue(credentialResult.Success);
        Assert.AreEqual("super-secret-key", request.Headers.GetValues("X-Test-Key").Single());
        Assert.DoesNotContain("super-secret-key", credentialResult.Credential!.ToString());
        Assert.DoesNotContain("super-secret-key", JsonSerializer.Serialize(credentialResult));

        var otherPrincipal = await fixture.Runtime.ForPrincipal("bob", "test.weather")
            .GetCredentialAsync("test.weather.read", saved.Connection.Id);
        Assert.IsFalse(otherPrincipal.Success);
        Assert.AreEqual(ConnectionStatus.NotConnected, otherPrincipal.Status);

        var otherAddon = await fixture.Runtime.ForPrincipal("alice@example.test", "different.addon")
            .GetCredentialAsync("test.weather.read", saved.Connection.Id);
        Assert.IsFalse(otherAddon.Success);
        Assert.AreEqual(ConnectionStatus.ForbiddenPrincipal, otherAddon.Status);
    }

    [TestMethod]
    public async Task Client_credentials_refresh_is_coalesced_and_request_parameters_are_declared()
    {
        var handler = new TokenEndpointHandler((requestNumber, form) =>
        {
            Assert.AreEqual("client_credentials", form["grant_type"]);
            Assert.AreEqual("records.read", form["scope"]);
            Assert.AreEqual("https://api.example.test", form["audience"]);
            Assert.AreEqual("secret-value", form["client_secret"]);
            return Token(requestNumber == 1 ? "short-token" : "long-token", requestNumber == 1 ? 60 : 3600);
        });
        await using var fixture = new RuntimeFixture(handler);
        using var addon = fixture.Runtime.RegisterAddon(CreateClientCredentialsRegistration(), fixture.Services);
        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-client-credentials",
            PrincipalHandle = "alice",
            DisplayName = "Records service",
            Configuration = new Dictionary<string, string>
            {
                ["clientId"] = "client-id",
                ["clientSecret"] = "secret-value"
            }
        });
        Assert.IsTrue(saved.Success);
        Assert.IsTrue((await fixture.Runtime.ConnectAsync(saved.Connection!.Id, ["test.records.read"])).Success);

        var client = fixture.Runtime.ForPrincipal("alice", "test.records");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            client.GetCredentialAsync("test.records.read", saved.Connection.Id)));

        Assert.IsTrue(results.All(result => result.Success));
        Assert.IsTrue(results.All(result => result.Credential!.Value == "long-token"));
        Assert.AreEqual(2, handler.RequestCount, "Concurrent refreshes should collapse into one token request.");
        Assert.DoesNotContain("secret-value", await File.ReadAllTextAsync(fixture.MetadataPath));
    }

    [TestMethod]
    public async Task Delegated_oauth_validates_pkce_state_rotates_refresh_token_and_revokes()
    {
        var handler = new TokenEndpointHandler((requestNumber, form) =>
        {
            if (form["grant_type"] == "authorization_code")
            {
                Assert.AreEqual("authorization-code", form["code"]);
                Assert.IsTrue(form.ContainsKey("code_verifier"));
                return Token("delegated-short", 60, "refresh-one");
            }

            Assert.AreEqual("refresh_token", form["grant_type"]);
            Assert.AreEqual("refresh-one", form["refresh_token"]);
            return Token("delegated-long", 3600, "refresh-two");
        });
        var browser = new CallbackBrowser(CallbackBehavior.Success);
        await using var fixture = new RuntimeFixture(handler, browser);
        using var addon = fixture.Runtime.RegisterAddon(CreateDelegatedRegistration(), fixture.Services);
        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-delegated",
            PrincipalHandle = "alice",
            DisplayName = "Mail",
            Configuration = new Dictionary<string, string> { ["clientId"] = "public-client" }
        });

        var connected = await fixture.Runtime.ConnectAsync(saved.Connection!.Id, ["test.mail.read"]);
        Assert.IsTrue(connected.Success);
        Assert.IsNotNull(browser.LastAuthorizationUri);
        var authorizationQuery = ParseForm(browser.LastAuthorizationUri!.Query);
        Assert.AreEqual("S256", authorizationQuery["code_challenge_method"]);
        Assert.IsTrue(authorizationQuery["scope"].Contains("mail.read", StringComparison.Ordinal));

        var token = await fixture.Runtime.ForPrincipal("alice", "test.mail")
            .GetCredentialAsync("test.mail.read", saved.Connection.Id);
        Assert.IsTrue(token.Success);
        Assert.AreEqual("delegated-long", token.Credential!.Value);
        Assert.Contains("refresh-two", fixture.SecretStore.Values.Values);

        var disconnected = await fixture.Runtime.DisconnectAsync(saved.Connection.Id);
        Assert.IsTrue(disconnected.Success);
        Assert.AreEqual(1, handler.RevocationCount);
        Assert.DoesNotContain("refresh-two", fixture.SecretStore.Values.Values);
    }

    [TestMethod]
    public async Task Delegated_oauth_rejects_state_mismatch()
    {
        var browser = new CallbackBrowser(CallbackBehavior.StateMismatch);
        await using var fixture = new RuntimeFixture(new TokenEndpointHandler((_, _) => Token("unused", 3600)), browser);
        using var addon = fixture.Runtime.RegisterAddon(CreateDelegatedRegistration(), fixture.Services);
        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-delegated",
            PrincipalHandle = "alice",
            DisplayName = "Mail",
            Configuration = new Dictionary<string, string> { ["clientId"] = "public-client" }
        });

        var result = await fixture.Runtime.ConnectAsync(saved.Connection!.Id, ["test.mail.read"]);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(ConnectionStatus.Error, result.Status);
        Assert.Contains("state", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Cancellation_propagates_from_token_endpoint()
    {
        var handler = new TokenEndpointHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Token("never", 3600);
        });
        await using var fixture = new RuntimeFixture(handler);
        using var addon = fixture.Runtime.RegisterAddon(CreateClientCredentialsRegistration(), fixture.Services);
        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-client-credentials",
            PrincipalHandle = "alice",
            DisplayName = "Records service",
            Configuration = new Dictionary<string, string>
            {
                ["clientId"] = "client-id",
                ["clientSecret"] = "secret-value"
            }
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            fixture.Runtime.ConnectAsync(saved.Connection!.Id, ["test.records.read"], cancellation.Token));
    }

    [TestMethod]
    public async Task Token_exchange_retries_throttling_without_replaying_application_requests()
    {
        var handler = new TokenEndpointHandler((requestNumber, _) =>
        {
            if (requestNumber == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":\"temporarily_unavailable\"}")
                };
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromMilliseconds(1));
                return throttled;
            }

            return Token("retried-token", 3600);
        });
        using var client = new HttpClient(handler);

        var token = await OAuthProtocol.ExchangeAsync(
            client,
            new Uri("https://identity.example.test/token"),
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" },
            CancellationToken.None);

        Assert.AreEqual("retried-token", token.AccessToken);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task Provider_registration_can_unload_reload_and_preserve_metadata()
    {
        await using var fixture = new RuntimeFixture();
        var registration = CreateApiKeyRegistration();
        var addon = fixture.Runtime.RegisterAddon(registration, fixture.Services);
        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-api-key",
            PrincipalHandle = "alice",
            DisplayName = "Weather",
            Configuration = new Dictionary<string, string> { ["apiKey"] = "key" }
        });
        Assert.IsTrue((await fixture.Runtime.ConnectAsync(saved.Connection!.Id, ["test.weather.read"])).Success);

        addon.Dispose();
        var unavailable = (await fixture.Runtime.GetConnectionsAsync()).Single();
        Assert.AreEqual(ConnectionStatus.ProviderUnavailable, unavailable.Status);
        Assert.AreEqual(0, fixture.Runtime.GetRequirements().Count);

        using var reloaded = fixture.Runtime.RegisterAddon(registration, fixture.Services);
        var restored = (await fixture.Runtime.GetConnectionsAsync()).Single();
        Assert.AreEqual(ConnectionStatus.Connected, restored.Status);
        Assert.IsTrue((await fixture.Runtime.ForPrincipal("alice", "test.weather")
            .GetCredentialAsync("test.weather.read", restored.Id)).Success);
    }

    [TestMethod]
    public async Task Custom_provider_actions_and_disable_use_the_standard_catalog_and_binding()
    {
        await using var fixture = new RuntimeFixture();
        var provider = new TestCustomProvider();
        using var builtIn = fixture.Runtime.RegisterBuiltInProvider(provider, fixture.Services, "custom-owner");
        using var addon = fixture.Runtime.RegisterAddon(Build(builder =>
            builder.SetIdentity("test.custom", "Custom test", "1.0.0")
                .AddConnectionRequirement(new ConnectionRequirement
                {
                    Id = "test.custom.invoke",
                    AddonId = "test.custom",
                    ProviderId = "test-custom-provider",
                    DisplayName = "Invoke",
                    Purpose = "Invoke custom API",
                    CredentialKind = ConnectionCredentialKind.Custom
                })), fixture.Services);
        var saved = await fixture.Runtime.SaveAsync(new ConnectionSaveRequest
        {
            ProviderId = "test-custom-provider",
            PrincipalHandle = "alice",
            DisplayName = "Signed service"
        });
        Assert.IsTrue((await fixture.Runtime.ConnectAsync(
            saved.Connection!.Id,
            ["test.custom.invoke"])).Success);

        var action = await fixture.Runtime.ExecuteActionAsync(saved.Connection.Id, "rotate-certificate");
        Assert.IsTrue(action.Success);
        Assert.AreEqual(1, provider.ActionCalls);

        var disabled = await fixture.Runtime.SetEnabledAsync(saved.Connection.Id, enabled: false);
        Assert.AreEqual(ConnectionStatus.Disabled, disabled.Status);
        var credential = await fixture.Runtime.ForPrincipal("alice", "test.custom")
            .GetCredentialAsync("test.custom.invoke", saved.Connection.Id);
        Assert.IsFalse(credential.Success);
        Assert.AreEqual(ConnectionStatus.Disabled, credential.Status);
    }

    [TestMethod]
    public void Validation_rejects_duplicate_ids_and_malformed_endpoints()
    {
        var descriptor = CreateApiKeyRegistration().Providers.Single().Descriptor;
        var registration = new OpenCaddisAddonRegistration(
            "duplicate.test",
            "Duplicate",
            "1.0.0",
            [new(descriptor, null), new(descriptor, null)],
            []);
        Assert.ThrowsExactly<InvalidOperationException>(() => ConnectionValidation.Validate(registration));

        Assert.ThrowsExactly<InvalidOperationException>(() => ConnectionValidation.Validate(new ConnectionProviderDescriptor
        {
            Id = "bad-oauth",
            DisplayName = "Bad OAuth",
            AuthenticationKind = ConnectionAuthenticationKind.OAuthAuthorizationCodePkce,
            TokenEndpoint = new Uri("https://identity.example.test/token")
        }));
    }

    private static OpenCaddisAddonRegistration CreateApiKeyRegistration() => Build(builder =>
        builder.SetIdentity("test.weather", "Weather test", "1.0.0")
            .AddConnectionProvider(new ConnectionProviderDescriptor
            {
                Id = "test-api-key",
                DisplayName = "Test API key",
                AuthenticationKind = ConnectionAuthenticationKind.ApiKey,
                ApiKeyHeaderName = "X-Test-Key",
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
                Id = "test.weather.read",
                AddonId = "test.weather",
                ProviderId = "test-api-key",
                DisplayName = "Weather",
                Purpose = "Read weather",
                CredentialKind = ConnectionCredentialKind.ApiKey
            }));

    private static OpenCaddisAddonRegistration CreateClientCredentialsRegistration() => Build(builder =>
        builder.SetIdentity("test.records", "Records test", "1.0.0")
            .AddConnectionProvider(new ConnectionProviderDescriptor
            {
                Id = "test-client-credentials",
                DisplayName = "Test client credentials",
                AuthenticationKind = ConnectionAuthenticationKind.OAuthClientCredentials,
                TokenEndpoint = new Uri("https://identity.example.test/token"),
                ConfigurationFields =
                [
                    new ConnectionConfigurationField { Name = "clientId", Label = "Client ID", Required = true },
                    new ConnectionConfigurationField
                    {
                        Name = "clientSecret", Label = "Client secret", Required = true,
                        Kind = ConnectionConfigurationFieldKind.Secret, Sensitive = true
                    }
                ]
            })
            .AddConnectionRequirement(new ConnectionRequirement
            {
                Id = "test.records.read",
                AddonId = "test.records",
                ProviderId = "test-client-credentials",
                DisplayName = "Records",
                Purpose = "Read records",
                Scopes = ["records.read"],
                Audience = "https://api.example.test"
            }));

    private static OpenCaddisAddonRegistration CreateDelegatedRegistration() => Build(builder =>
        builder.SetIdentity("test.mail", "Mail test", "1.0.0")
            .AddConnectionProvider(new ConnectionProviderDescriptor
            {
                Id = "test-delegated",
                DisplayName = "Test delegated OAuth",
                AuthenticationKind = ConnectionAuthenticationKind.OAuthAuthorizationCodePkce,
                AuthorizationEndpoint = new Uri("https://identity.example.test/authorize"),
                TokenEndpoint = new Uri("https://identity.example.test/token"),
                RevocationEndpoint = new Uri("https://identity.example.test/revoke"),
                ConfigurationFields =
                [
                    new ConnectionConfigurationField { Name = "clientId", Label = "Client ID", Required = true }
                ]
            })
            .AddConnectionRequirement(new ConnectionRequirement
            {
                Id = "test.mail.read",
                AddonId = "test.mail",
                ProviderId = "test-delegated",
                DisplayName = "Mail",
                Purpose = "Read mail",
                Scopes = ["mail.read"]
            }));

    private static OpenCaddisAddonRegistration Build(Action<OpenCaddisAddonBuilder> configure)
    {
        var builder = new OpenCaddisAddonBuilder(new ServiceCollection());
        configure(builder);
        return builder.Build();
    }

    private static HttpResponseMessage Token(string accessToken, int expiresIn, string? refreshToken = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = expiresIn,
            refresh_token = refreshToken
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, string> ParseForm(string value) => value
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(
            part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
            part => Uri.UnescapeDataString((part.Length > 1 ? part[1] : string.Empty).Replace('+', ' ')),
            StringComparer.OrdinalIgnoreCase);

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "OpenCaddis.Connection.Tests", Guid.NewGuid().ToString("N"));

        public RuntimeFixture(HttpMessageHandler? handler = null, IOpenCaddisInteractiveBrowser? browser = null)
        {
            Directory.CreateDirectory(directory);
            Services = new ServiceCollection().BuildServiceProvider();
            SecretStore = new MemorySecretStore();
            var client = handler is null ? new HttpClient() : new HttpClient(handler);
            Runtime = new OpenCaddisConnectionRuntime(
                directory,
                SecretStore,
                browser ?? new NoopBrowser(),
                Services,
                client);
        }

        public ServiceProvider Services { get; }
        public MemorySecretStore SecretStore { get; }
        public OpenCaddisConnectionRuntime Runtime { get; }
        public string MetadataPath => Path.Combine(directory, "connections.json");

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Services.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class MemorySecretStore : IOpenCaddisSecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryRemove(key, out _));
    }

    private sealed class NoopBrowser : IOpenCaddisInteractiveBrowser
    {
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private enum CallbackBehavior
    {
        Success,
        StateMismatch
    }

    private sealed class CallbackBrowser(CallbackBehavior behavior) : IOpenCaddisInteractiveBrowser
    {
        public Uri? LastAuthorizationUri { get; private set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            LastAuthorizationUri = uri;
            var query = ParseForm(uri.Query);
            var redirect = query["redirect_uri"];
            var state = behavior == CallbackBehavior.StateMismatch ? "wrong-state" : query["state"];
            var callback = $"{redirect}?code=authorization-code&state={Uri.EscapeDataString(state)}";
            _ = Task.Run(async () =>
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(callback, cancellationToken);
            }, cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        private readonly Func<int, Dictionary<string, string>, HttpResponseMessage>? responseFactory;
        private readonly Func<CancellationToken, Task<HttpResponseMessage>>? asyncFactory;
        private int requestCount;
        private int revocationCount;

        public TokenEndpointHandler(Func<int, Dictionary<string, string>, HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        public TokenEndpointHandler(Func<CancellationToken, Task<HttpResponseMessage>> asyncFactory)
        {
            this.asyncFactory = asyncFactory;
        }

        public int RequestCount => requestCount;
        public int RevocationCount => revocationCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref revocationCount);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var current = Interlocked.Increment(ref requestCount);
            if (asyncFactory is not null)
            {
                return await asyncFactory(cancellationToken);
            }

            var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responseFactory!(current, form);
        }
    }

    private sealed class TestCustomProvider : IOpenCaddisConnectionProvider
    {
        public ConnectionProviderDescriptor Descriptor { get; } = new()
        {
            Id = "test-custom-provider",
            DisplayName = "Test custom provider",
            AuthenticationKind = ConnectionAuthenticationKind.Custom,
            Actions =
            [
                new ConnectionProviderActionDescriptor
                {
                    Id = "rotate-certificate",
                    Label = "Rotate certificate",
                    IsDestructive = true
                }
            ]
        };

        public int ActionCalls { get; private set; }

        public Task<IReadOnlyList<string>> ValidateConfigurationAsync(
            ConnectionProviderContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ConnectionProviderResult> ConnectAsync(
            ConnectionProviderContext context,
            IReadOnlyList<ConnectionRequirement> requirements,
            CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionProviderResult
        {
            Status = ConnectionStatus.Connected,
            GrantedRequirements = requirements.Select(requirement => requirement.Id).ToArray()
        });

        public Task<ConnectionCredentialResult> GetCredentialAsync(
            ConnectionProviderContext context,
            ConnectionRequirement requirement,
            CancellationToken cancellationToken = default) => Task.FromResult(
            ConnectionCredentialResult.Granted(new ConnectionCredential(
                ConnectionCredentialKind.Custom,
                "signed-request-material")));

        public Task DisconnectAsync(
            ConnectionProviderContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ConnectionProviderResult> ExecuteActionAsync(
            ConnectionProviderContext context,
            string actionId,
            CancellationToken cancellationToken = default)
        {
            ActionCalls++;
            return Task.FromResult(new ConnectionProviderResult
            {
                Status = ConnectionStatus.Connected,
                Message = "Certificate rotated."
            });
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCaddis.Sdk.Connections;

namespace OpenCaddis.Server.Connections;

internal static class StandardConnectionProviderFactory
{
    public static IOpenCaddisConnectionProvider Create(
        ConnectionProviderDescriptor descriptor,
        HttpClient httpClient) => descriptor.AuthenticationKind switch
        {
            ConnectionAuthenticationKind.OAuthAuthorizationCodePkce =>
                new AuthorizationCodePkceConnectionProvider(descriptor, httpClient),
            ConnectionAuthenticationKind.OAuthClientCredentials =>
                new ClientCredentialsConnectionProvider(descriptor, httpClient),
            ConnectionAuthenticationKind.ApiKey => new ApiKeyConnectionProvider(descriptor),
            _ => throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' requires a custom provider implementation.")
        };
}

internal abstract class StandardConnectionProvider(ConnectionProviderDescriptor descriptor)
    : IOpenCaddisConnectionProvider
{
    public ConnectionProviderDescriptor Descriptor { get; } = descriptor;

    public virtual Task<IReadOnlyList<string>> ValidateConfigurationAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (var field in Descriptor.ConfigurationFields)
        {
            context.Configuration.TryGetValue(field.Name, out var value);
            if (field.Required && string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{field.Label} is required.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value) &&
                field.Kind == ConnectionConfigurationFieldKind.Integer &&
                !int.TryParse(value, out _))
            {
                errors.Add($"{field.Label} must be an integer.");
            }

            if (!string.IsNullOrWhiteSpace(value) &&
                field.Kind == ConnectionConfigurationFieldKind.Boolean &&
                !bool.TryParse(value, out _))
            {
                errors.Add($"{field.Label} must be true or false.");
            }

            if (!string.IsNullOrWhiteSpace(value) &&
                field.AllowedValues.Count > 0 &&
                !field.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{field.Label} is not one of the allowed values.");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    public abstract Task<ConnectionProviderResult> ConnectAsync(
        ConnectionProviderContext context,
        IReadOnlyList<ConnectionRequirement> requirements,
        CancellationToken cancellationToken = default);

    public abstract Task<ConnectionCredentialResult> GetCredentialAsync(
        ConnectionProviderContext context,
        ConnectionRequirement requirement,
        CancellationToken cancellationToken = default);

    public abstract Task DisconnectAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default);

    protected static IReadOnlyList<string> ResolveScopes(
        ConnectionProviderDescriptor descriptor,
        IEnumerable<ConnectionRequirement> requirements) =>
        descriptor.DefaultScopes
            .Concat(requirements.SelectMany(requirement => requirement.Scopes))
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed class ApiKeyConnectionProvider(ConnectionProviderDescriptor descriptor)
    : StandardConnectionProvider(descriptor)
{
    public override Task<ConnectionProviderResult> ConnectAsync(
        ConnectionProviderContext context,
        IReadOnlyList<ConnectionRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        var value = context.GetRequiredSetting(Descriptor.ApiKeyConfigurationField);
        return Task.FromResult(new ConnectionProviderResult
        {
            Status = string.IsNullOrWhiteSpace(value)
                ? ConnectionStatus.ConfigurationInvalid
                : ConnectionStatus.Connected,
            Message = string.IsNullOrWhiteSpace(value) ? "API key is required." : "API key configured.",
            GrantedRequirements = requirements.Select(requirement => requirement.Id).ToArray()
        });
    }

    public override Task<ConnectionCredentialResult> GetCredentialAsync(
        ConnectionProviderContext context,
        ConnectionRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        var value = context.GetRequiredSetting(Descriptor.ApiKeyConfigurationField);
        return Task.FromResult(ConnectionCredentialResult.Granted(new ConnectionCredential(
            ConnectionCredentialKind.ApiKey,
            value,
            headerName: Descriptor.ApiKeyHeaderName)));
    }

    public override Task DisconnectAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class ClientCredentialsConnectionProvider(
    ConnectionProviderDescriptor descriptor,
    HttpClient httpClient) : StandardConnectionProvider(descriptor)
{
    private readonly ConcurrentDictionary<string, OAuthAccessToken> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    public override async Task<ConnectionProviderResult> ConnectAsync(
        ConnectionProviderContext context,
        IReadOnlyList<ConnectionRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        var requirement = requirements.FirstOrDefault() ?? new ConnectionRequirement
        {
            Id = "validation",
            AddonId = "opencaddis",
            ProviderId = Descriptor.Id,
            DisplayName = "Validate",
            Purpose = "Validate client credentials",
            Scopes = ResolveScopes(Descriptor, requirements),
            Audience = requirements.Select(item => item.Audience).FirstOrDefault(value => value is not null),
            Resource = requirements.Select(item => item.Resource).FirstOrDefault(value => value is not null)
        };
        var result = await GetCredentialAsync(context, requirement, cancellationToken);
        return new ConnectionProviderResult
        {
            Status = result.Status,
            Message = result.Success ? "Client credentials validated." : result.Message,
            AccountIdentifier = context.Configuration.GetValueOrDefault(Descriptor.ClientIdConfigurationField),
            GrantedRequirements = result.Success
                ? requirements.Select(item => item.Id).ToArray()
                : []
        };
    }

    public override async Task<ConnectionCredentialResult> GetCredentialAsync(
        ConnectionProviderContext context,
        ConnectionRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        var scopes = ResolveScopes(Descriptor, [requirement]);
        var cacheKey = $"{context.Connection.Id}|{string.Join(' ', scopes)}|{requirement.Audience}|{requirement.Resource}";
        if (cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return ConnectionCredentialResult.Granted(cached.ToCredential());
        }

        var semaphore = locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return ConnectionCredentialResult.Granted(cached.ToCredential());
            }

            var form = new Dictionary<string, string>(Descriptor.TokenParameters, StringComparer.OrdinalIgnoreCase)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = context.GetRequiredSetting(Descriptor.ClientIdConfigurationField),
                ["client_secret"] = context.GetRequiredSetting(Descriptor.ClientSecretConfigurationField)
            };
            if (scopes.Count > 0)
            {
                form["scope"] = string.Join(' ', scopes);
            }
            if (!string.IsNullOrWhiteSpace(requirement.Audience))
            {
                form["audience"] = requirement.Audience;
            }
            if (!string.IsNullOrWhiteSpace(requirement.Resource))
            {
                form["resource"] = requirement.Resource;
            }

            var token = await OAuthProtocol.ExchangeAsync(
                httpClient,
                Descriptor.TokenEndpoint!,
                form,
                cancellationToken);
            cache[cacheKey] = token;
            return ConnectionCredentialResult.Granted(token.ToCredential());
        }
        catch (OAuthProtocolException exception)
        {
            return ConnectionCredentialResult.Denied(
                ConnectionStatus.ReauthenticationRequired,
                exception.Message);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override Task DisconnectAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var key in cache.Keys.Where(key => key.StartsWith($"{context.Connection.Id}|", StringComparison.OrdinalIgnoreCase)))
        {
            cache.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }
}

internal sealed class AuthorizationCodePkceConnectionProvider(
    ConnectionProviderDescriptor descriptor,
    HttpClient httpClient) : StandardConnectionProvider(descriptor)
{
    private readonly ConcurrentDictionary<string, OAuthAccessToken> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    public override async Task<ConnectionProviderResult> ConnectAsync(
        ConnectionProviderContext context,
        IReadOnlyList<ConnectionRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        var scopes = ResolveScopes(Descriptor, requirements);
        var verifier = OAuthProtocol.CreateUrlSafeSecret(64);
        var challenge = OAuthProtocol.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = OAuthProtocol.CreateUrlSafeSecret(32);
        var port = OAuthProtocol.FindAvailablePort();
        var redirectUri = new Uri($"http://localhost:{port}/callback/");
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.AbsoluteUri);
        listener.Start();

        var query = new Dictionary<string, string>(Descriptor.AuthorizationParameters, StringComparer.OrdinalIgnoreCase)
        {
            ["response_type"] = "code",
            ["client_id"] = context.GetRequiredSetting(Descriptor.ClientIdConfigurationField),
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var authorizationUri = OAuthProtocol.AddQuery(Descriptor.AuthorizationEndpoint!, query);
        await context.InteractiveBrowser.OpenAsync(authorizationUri, cancellationToken);

        HttpListenerContext callback;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            callback = await listener.GetContextAsync().WaitAsync(timeout.Token);
        }

        var callbackState = callback.Request.QueryString["state"];
        var error = callback.Request.QueryString["error"];
        var code = callback.Request.QueryString["code"];
        var responseText = error is null && code is not null && callbackState == state
            ? "OpenCaddis connection completed. You can close this window."
            : "OpenCaddis could not complete the connection. Return to the app for details.";
        var responseBytes = Encoding.UTF8.GetBytes($"<!doctype html><title>OpenCaddis</title><p>{WebUtility.HtmlEncode(responseText)}</p>");
        callback.Response.ContentType = "text/html; charset=utf-8";
        callback.Response.ContentLength64 = responseBytes.Length;
        await callback.Response.OutputStream.WriteAsync(responseBytes, cancellationToken);
        callback.Response.Close();

        if (callbackState != state)
        {
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.Error,
                Message = "OAuth state validation failed."
            };
        }
        if (error is not null)
        {
            return new ConnectionProviderResult
            {
                Status = error.Contains("admin", StringComparison.OrdinalIgnoreCase)
                    ? ConnectionStatus.AdminApprovalRequired
                    : ConnectionStatus.ConsentRequired,
                Message = callback.Request.QueryString["error_description"] ?? error
            };
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.Error,
                Message = "The OAuth callback did not contain an authorization code."
            };
        }

        var form = new Dictionary<string, string>(Descriptor.TokenParameters, StringComparer.OrdinalIgnoreCase)
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = context.GetRequiredSetting(Descriptor.ClientIdConfigurationField),
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        };
        if (context.Configuration.TryGetValue(Descriptor.ClientSecretConfigurationField, out var clientSecret) &&
            !string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        try
        {
            var token = await OAuthProtocol.ExchangeAsync(
                httpClient,
                Descriptor.TokenEndpoint!,
                form,
                cancellationToken);
            cache[context.Connection.Id] = token;
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                await context.SecretStore.SetAsync(RefreshKey(context.Connection.Id), token.RefreshToken, cancellationToken);
            }

            var account = await ResolveAccountIdentifierAsync(token.AccessToken, cancellationToken);
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.Connected,
                Message = "Authorization completed.",
                AccountIdentifier = account,
                GrantedRequirements = requirements.Select(requirement => requirement.Id).ToArray()
            };
        }
        catch (OAuthProtocolException exception)
        {
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.Error,
                Message = exception.Message
            };
        }
    }

    public override async Task<ConnectionCredentialResult> GetCredentialAsync(
        ConnectionProviderContext context,
        ConnectionRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(context.Connection.Id, out var cached) &&
            cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return ConnectionCredentialResult.Granted(cached.ToCredential());
        }

        var semaphore = locks.GetOrAdd(context.Connection.Id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(context.Connection.Id, out cached) &&
                cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return ConnectionCredentialResult.Granted(cached.ToCredential());
            }

            var refreshToken = await context.SecretStore.GetAsync(RefreshKey(context.Connection.Id), cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return ConnectionCredentialResult.Denied(
                    ConnectionStatus.ReauthenticationRequired,
                    "Reconnect this account from the OpenCaddis Connections page.");
            }

            var scopes = ResolveScopes(Descriptor, [requirement]);
            var form = new Dictionary<string, string>(Descriptor.TokenParameters, StringComparer.OrdinalIgnoreCase)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = context.GetRequiredSetting(Descriptor.ClientIdConfigurationField),
                ["refresh_token"] = refreshToken
            };
            if (scopes.Count > 0)
            {
                form["scope"] = string.Join(' ', scopes);
            }
            if (context.Configuration.TryGetValue(Descriptor.ClientSecretConfigurationField, out var clientSecret) &&
                !string.IsNullOrWhiteSpace(clientSecret))
            {
                form["client_secret"] = clientSecret;
            }

            var token = await OAuthProtocol.ExchangeAsync(httpClient, Descriptor.TokenEndpoint!, form, cancellationToken);
            cache[context.Connection.Id] = token;
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                await context.SecretStore.SetAsync(RefreshKey(context.Connection.Id), token.RefreshToken, cancellationToken);
            }
            return ConnectionCredentialResult.Granted(token.ToCredential());
        }
        catch (OAuthProtocolException exception)
        {
            return ConnectionCredentialResult.Denied(
                ConnectionStatus.ReauthenticationRequired,
                exception.Message,
                requirement.Scopes);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override async Task DisconnectAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default)
    {
        cache.TryRemove(context.Connection.Id, out var token);
        var refreshToken = await context.SecretStore.GetAsync(RefreshKey(context.Connection.Id), cancellationToken);
        if (Descriptor.RevocationEndpoint is not null && !string.IsNullOrWhiteSpace(refreshToken))
        {
            try
            {
                using var response = await httpClient.PostAsync(
                    Descriptor.RevocationEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }),
                    cancellationToken);
            }
            catch
            {
                // Local disconnect must still remove the cached credential.
            }
        }
        await context.SecretStore.RemoveAsync(RefreshKey(context.Connection.Id), cancellationToken);
    }

    private async Task<string?> ResolveAccountIdentifierAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (Descriptor.UserInfoEndpoint is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Descriptor.UserInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        foreach (var property in new[] { "email", "preferred_username", "name", "sub" })
        {
            if (json.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static string RefreshKey(string connectionId) =>
        $"opencaddis.connection.{connectionId}.oauth-refresh";
}

internal static class OAuthProtocol
{
    public static async Task<OAuthAccessToken> ExchangeAsync(
        HttpClient httpClient,
        Uri tokenEndpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(form),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (attempt < 2 && response.StatusCode is HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or
                    HttpStatusCode.GatewayTimeout)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(100 * (attempt + 1));
                    await Task.Delay(
                        retryAfter > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : retryAfter,
                        cancellationToken);
                    continue;
                }

                var detail = TryReadError(body);
                throw new OAuthProtocolException(
                    $"Token endpoint returned {(int)response.StatusCode}: {detail ?? response.ReasonPhrase}.");
            }

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("access_token", out var accessTokenElement) ||
                string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
            {
                throw new OAuthProtocolException("Token endpoint response did not contain access_token.");
            }

            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiresElement) &&
                            expiresElement.TryGetInt32(out var seconds)
                ? seconds
                : 3600;
            return new OAuthAccessToken(
                accessTokenElement.GetString()!,
                json.RootElement.TryGetProperty("refresh_token", out var refreshElement)
                    ? refreshElement.GetString()
                    : null,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)),
                json.RootElement.TryGetProperty("token_type", out var typeElement)
                    ? typeElement.GetString() ?? "Bearer"
                    : "Bearer");
        }
    }

    public static string CreateUrlSafeSecret(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static Uri AddQuery(Uri uri, IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var builder = new UriBuilder(uri)
        {
            Query = string.IsNullOrEmpty(uri.Query)
                ? query
                : $"{uri.Query.TrimStart('?')}&{query}"
        };
        return builder.Uri;
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error_description", out var description))
            {
                return description.GetString();
            }
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}

internal sealed record OAuthAccessToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresOn,
    string Scheme)
{
    public ConnectionCredential ToCredential() => new(
        ConnectionCredentialKind.BearerToken,
        AccessToken,
        Scheme,
        expiresOn: ExpiresOn);
}

internal sealed class OAuthProtocolException(string message) : Exception(message);

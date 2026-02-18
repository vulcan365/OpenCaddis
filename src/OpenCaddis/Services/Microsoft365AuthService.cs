using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace OpenCaddis.Services;

public record DeviceCodeInfo(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);

public sealed class Microsoft365AuthService : IDisposable
{
    private readonly IDataProtector _protector;
    private readonly OpenCaddisConfigService _configService;
    private readonly ILogger<Microsoft365AuthService> _logger;
    private readonly HttpClient _httpClient = new();

    private string _clientId = string.Empty;
    private const string TenantId = "common";
    private const string Scopes = "Mail.Read Mail.ReadWrite User.Read offline_access";

    private TokenResponse? _tokens;
    private DateTimeOffset _accessTokenExpiry;
    private CancellationTokenSource? _pollCts;

    // Connection state
    public bool IsConnected => _tokens is not null && !string.IsNullOrEmpty(_tokens.AccessToken);
    public string? UserDisplayName { get; private set; }
    public string? UserEmail { get; private set; }
    public string[]? ConsentedScopes { get; private set; }

    // Device code state
    public DeviceCodeInfo? CurrentDeviceCode { get; private set; }
    public bool IsPolling => _pollCts is not null && !_pollCts.IsCancellationRequested;
    public string? LastError { get; private set; }

    public event Action? OnConnectionChanged;

    public Microsoft365AuthService(
        IDataProtectionProvider dataProtection,
        OpenCaddisConfigService configService,
        ILogger<Microsoft365AuthService> logger)
    {
        _protector = dataProtection.CreateProtector("OpenCaddis.Microsoft365");
        _configService = configService;
        _logger = logger;
    }

    public void Configure(string clientId)
    {
        _clientId = clientId;
    }

    private static string TokenEndpoint =>
        $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";

    private static string DeviceCodeEndpoint =>
        $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/devicecode";

    // --- Device Code Flow ---

    public async Task<DeviceCodeInfo> StartDeviceCodeFlowAsync()
    {
        LastError = null;
        CurrentDeviceCode = null;

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = Scopes
        });

        var response = await _httpClient.PostAsync(DeviceCodeEndpoint, content);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            LastError = $"Failed to start device code flow: {json}";
            OnConnectionChanged?.Invoke();
            throw new InvalidOperationException(LastError);
        }

        var dcResponse = JsonSerializer.Deserialize<DeviceCodeResponse>(json)!;

        CurrentDeviceCode = new DeviceCodeInfo(
            dcResponse.UserCode,
            dcResponse.VerificationUri,
            DateTimeOffset.UtcNow.AddSeconds(dcResponse.ExpiresIn));

        // Start background polling
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var deviceCode = dcResponse.DeviceCode;
        var interval = dcResponse.Interval;
        _ = PollForTokenAsync(deviceCode, interval, _pollCts.Token);

        OnConnectionChanged?.Invoke();
        return CurrentDeviceCode;
    }

    public void CancelDeviceCodeFlow()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        CurrentDeviceCode = null;
        OnConnectionChanged?.Invoke();
    }

    private async Task PollForTokenAsync(string deviceCode, int intervalSeconds, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            if (ct.IsCancellationRequested) break;

            try
            {
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = _clientId,
                    ["device_code"] = deviceCode
                });

                var response = await _httpClient.PostAsync(TokenEndpoint, content, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json)!;
                    await HandleTokenResponseAsync(tokenResponse);
                    CurrentDeviceCode = null;
                    _pollCts = null;
                    OnConnectionChanged?.Invoke();
                    return;
                }

                var error = JsonSerializer.Deserialize<OAuthError>(json);
                switch (error?.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "expired_token":
                        LastError = "The device code has expired. Please try connecting again.";
                        CurrentDeviceCode = null;
                        _pollCts = null;
                        OnConnectionChanged?.Invoke();
                        return;
                    case "authorization_declined":
                        LastError = "Authorization was declined. Please try again and accept the permissions.";
                        CurrentDeviceCode = null;
                        _pollCts = null;
                        OnConnectionChanged?.Invoke();
                        return;
                    default:
                        LastError = $"Authentication error: {error?.ErrorDescription ?? json}";
                        CurrentDeviceCode = null;
                        _pollCts = null;
                        OnConnectionChanged?.Invoke();
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during device code polling");
                LastError = $"Polling error: {ex.Message}";
                CurrentDeviceCode = null;
                _pollCts = null;
                OnConnectionChanged?.Invoke();
                return;
            }
        }
    }

    // --- Token Management ---

    public async Task<string> GetAccessTokenAsync()
    {
        if (_tokens is null)
            throw new InvalidOperationException("Not connected to Microsoft 365. Please connect in Settings > Connections.");

        if (DateTimeOffset.UtcNow >= _accessTokenExpiry.AddMinutes(-5))
        {
            await RefreshTokenAsync();
        }

        return _tokens.AccessToken;
    }

    public async Task<GraphServiceClient> GetGraphClientAsync()
    {
        await GetAccessTokenAsync(); // ensure token is fresh
        var tokenProvider = new TokenAccessProvider(this);
        var authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
        return new GraphServiceClient(new HttpClient(), authProvider);
    }

    private async Task RefreshTokenAsync()
    {
        if (_tokens?.RefreshToken is null)
            throw new InvalidOperationException("No refresh token available. Please reconnect.");

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId,
            ["refresh_token"] = _tokens.RefreshToken,
            ["scope"] = Scopes
        });

        var response = await _httpClient.PostAsync(TokenEndpoint, content);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Token refresh failed: {Response}", json);
            _tokens = null;
            UserDisplayName = null;
            UserEmail = null;
            ConsentedScopes = null;
            OnConnectionChanged?.Invoke();
            throw new InvalidOperationException("Token refresh failed. Please reconnect in Settings > Connections.");
        }

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json)!;
        await HandleTokenResponseAsync(tokenResponse);
    }

    private async Task HandleTokenResponseAsync(TokenResponse tokenResponse)
    {
        _tokens = tokenResponse;
        _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
        ConsentedScopes = tokenResponse.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        await PersistTokensAsync();
        await FetchUserProfileAsync();
    }

    private async Task FetchUserProfileAsync()
    {
        try
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);

            var response = await _httpClient.GetAsync("https://graph.microsoft.com/v1.0/me?$select=displayName,mail,userPrincipalName");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var profile = JsonSerializer.Deserialize<UserProfile>(json);
                UserDisplayName = profile?.DisplayName;
                UserEmail = profile?.Mail ?? profile?.UserPrincipalName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch user profile");
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }
    }

    // --- Token Persistence (via opencaddis.json) ---

    private async Task PersistTokensAsync()
    {
        try
        {
            if (!_configService.ConfigurationExists()) return;

            var config = await _configService.LoadConfigurationAsync();
            config.Microsoft365 ??= new Microsoft365ConfigurationDto
            {
                ClientId = _clientId
            };

            var tokenJson = JsonSerializer.Serialize(_tokens);
            config.Microsoft365.EncryptedTokens = _protector.Protect(tokenJson);
            config.Microsoft365.UserDisplayName = UserDisplayName;
            config.Microsoft365.UserEmail = UserEmail;

            await _configService.SaveConfigurationAsync(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist tokens");
        }
    }

    private async Task ClearPersistedTokensAsync()
    {
        try
        {
            if (!_configService.ConfigurationExists()) return;

            var config = await _configService.LoadConfigurationAsync();
            if (config.Microsoft365 is not null)
            {
                config.Microsoft365.EncryptedTokens = null;
                config.Microsoft365.UserDisplayName = null;
                config.Microsoft365.UserEmail = null;
                await _configService.SaveConfigurationAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear persisted tokens");
        }
    }

    public async Task TryRestoreSessionAsync()
    {
        if (string.IsNullOrEmpty(_clientId))
            return;

        if (!_configService.ConfigurationExists())
            return;

        try
        {
            var config = await _configService.LoadConfigurationAsync();
            var encrypted = config.Microsoft365?.EncryptedTokens;
            if (string.IsNullOrEmpty(encrypted))
                return;

            var json = _protector.Unprotect(encrypted);
            _tokens = JsonSerializer.Deserialize<TokenResponse>(json);

            // Restore cached display info while we refresh
            UserDisplayName = config.Microsoft365?.UserDisplayName;
            UserEmail = config.Microsoft365?.UserEmail;

            if (_tokens?.RefreshToken is not null)
            {
                await RefreshTokenAsync();
                _logger.LogInformation("Microsoft 365 session restored for {User}", UserEmail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore Microsoft 365 session");
            _tokens = null;
            UserDisplayName = null;
            UserEmail = null;
        }
    }

    public async Task DisconnectAsync()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        _tokens = null;
        CurrentDeviceCode = null;
        UserDisplayName = null;
        UserEmail = null;
        ConsentedScopes = null;
        LastError = null;

        await ClearPersistedTokensAsync();
        OnConnectionChanged?.Invoke();
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _httpClient.Dispose();
    }

    // --- Inner Types ---

    private sealed class TokenAccessProvider : IAccessTokenProvider
    {
        private readonly Microsoft365AuthService _authService;

        public TokenAccessProvider(Microsoft365AuthService authService)
        {
            _authService = authService;
        }

        public AllowedHostsValidator AllowedHostsValidator { get; } = new();

        public async Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            return await _authService.GetAccessTokenAsync();
        }
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; } = 5;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    private sealed class OAuthError
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private sealed class UserProfile
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("mail")]
        public string? Mail { get; set; }

        [JsonPropertyName("userPrincipalName")]
        public string? UserPrincipalName { get; set; }
    }
}

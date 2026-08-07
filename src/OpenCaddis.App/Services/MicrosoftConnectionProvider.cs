using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using OpenCaddis.Sdk.Connections;

namespace OpenCaddis.App.Services;

public sealed class MicrosoftConnectionProvider : IOpenCaddisConnectionProvider
{
    public const string ProviderId = "microsoft";
    private static readonly string[] BaseScopes = ["User.Read"];
    private readonly string cacheDirectory;
    private readonly ConcurrentDictionary<string, Lazy<Task<IPublicClientApplication>>> applications =
        new(StringComparer.OrdinalIgnoreCase);

    public MicrosoftConnectionProvider()
    {
        cacheDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "Connections", "Microsoft");
        Directory.CreateDirectory(cacheDirectory);
    }

    public ConnectionProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Microsoft",
        Description = "A Microsoft work or school account using a BYO Entra public-client registration.",
        AuthenticationKind = ConnectionAuthenticationKind.Custom,
        DefaultScopes = BaseScopes,
        ConfigurationFields =
        [
            new ConnectionConfigurationField
            {
                Name = "tenant",
                Label = "Tenant",
                Required = true,
                DefaultValue = "organizations",
                Placeholder = "organizations, tenant GUID, or verified domain",
                HelpText = "Use organizations for a multi-tenant app, or your tenant ID/domain for a single tenant."
            },
            new ConnectionConfigurationField
            {
                Name = "clientId",
                Label = "Application (client) ID",
                Required = true,
                Placeholder = "00000000-0000-0000-0000-000000000000",
                HelpText = "Register a Mobile and desktop public client with http://localhost as a redirect URI."
            }
        ]
    };

    public Task<IReadOnlyList<string>> ValidateConfigurationAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (!context.Configuration.TryGetValue("clientId", out var clientId) || !Guid.TryParse(clientId, out _))
        {
            errors.Add("Application (client) ID must be a GUID.");
        }

        if (!context.Configuration.TryGetValue("tenant", out var tenant) || !IsValidTenant(tenant))
        {
            errors.Add("Tenant must be organizations, common, consumers, a tenant GUID, or a verified domain.");
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    public async Task<ConnectionProviderResult> ConnectAsync(
        ConnectionProviderContext context,
        IReadOnlyList<ConnectionRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(context);
        try
        {
            var result = await application
                .AcquireTokenInteractive(ResolveScopes(requirements))
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.Connected,
                Message = $"Connected as {result.Account.Username}.",
                AccountIdentifier = result.Account.HomeAccountId.Identifier,
                GrantedRequirements = requirements.Select(requirement => requirement.Id).ToArray()
            };
        }
        catch (MsalServiceException exception) when (
            exception.ErrorCode.Contains("consent", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("admin approval", StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.AdminApprovalRequired,
                Message = "The tenant requires administrator approval for one or more requested permissions."
            };
        }
        catch (MsalClientException exception) when (
            exception.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            return new ConnectionProviderResult
            {
                Status = ConnectionStatus.ConsentRequired,
                Message = "Microsoft sign-in was canceled."
            };
        }
    }

    public async Task<ConnectionCredentialResult> GetCredentialAsync(
        ConnectionProviderContext context,
        ConnectionRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(context);
        var accounts = await application.GetAccountsAsync();
        var account = accounts.FirstOrDefault(candidate => string.Equals(
            candidate.HomeAccountId.Identifier,
            context.Connection.AccountIdentifier,
            StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return ConnectionCredentialResult.Denied(
                ConnectionStatus.ReauthenticationRequired,
                "Reconnect this Microsoft account from the Connections page.");
        }

        try
        {
            var result = await application
                .AcquireTokenSilent(ResolveScopes([requirement]), account)
                .ExecuteAsync(cancellationToken);
            return ConnectionCredentialResult.Granted(new ConnectionCredential(
                ConnectionCredentialKind.BearerToken,
                result.AccessToken,
                "Bearer",
                expiresOn: result.ExpiresOn));
        }
        catch (MsalUiRequiredException exception)
        {
            var status = exception.ErrorCode.Contains("consent", StringComparison.OrdinalIgnoreCase)
                ? ConnectionStatus.ConsentRequired
                : ConnectionStatus.ReauthenticationRequired;
            return ConnectionCredentialResult.Denied(
                status,
                "Open the Connections page to grant permissions or reconnect the Microsoft account.",
                requirement.Scopes);
        }
    }

    public async Task DisconnectAsync(
        ConnectionProviderContext context,
        CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(context);
        var accounts = await application.GetAccountsAsync();
        var account = accounts.FirstOrDefault(candidate => string.Equals(
            candidate.HomeAccountId.Identifier,
            context.Connection.AccountIdentifier,
            StringComparison.OrdinalIgnoreCase));
        if (account is not null)
        {
            await application.RemoveAsync(account);
        }
    }

    private Task<IPublicClientApplication> GetApplicationAsync(ConnectionProviderContext context)
    {
        var clientId = context.GetRequiredSetting("clientId");
        var tenant = context.GetRequiredSetting("tenant");
        var key = $"{clientId}:{tenant}";
        return applications.GetOrAdd(
            key,
            _ => new Lazy<Task<IPublicClientApplication>>(
                () => CreateApplicationAsync(clientId, tenant),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private async Task<IPublicClientApplication> CreateApplicationAsync(string clientId, string tenant)
    {
        var application = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenant.Trim()}")
            .WithRedirectUri("http://localhost")
            .Build();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}:{tenant}")))[..24]
            .ToLowerInvariant();
        var storage = new StorageCreationPropertiesBuilder($"msal-{hash}.cache", cacheDirectory).Build();
        var helper = await MsalCacheHelper.CreateAsync(storage);
        helper.VerifyPersistence();
        helper.RegisterCache(application.UserTokenCache);
        return application;
    }

    private static string[] ResolveScopes(IEnumerable<ConnectionRequirement> requirements) =>
        BaseScopes
            .Concat(requirements.SelectMany(requirement => requirement.Scopes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsValidTenant(string? tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return false;
        }
        if (tenant is "organizations" or "common" or "consumers" || Guid.TryParse(tenant, out _))
        {
            return true;
        }
        return Uri.CheckHostName(tenant) == UriHostNameType.Dns;
    }
}

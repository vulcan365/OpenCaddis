using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenCaddis.Server;

public sealed class OpenCaddisAgentCreationOptions
{
    public const string DefaultPrincipalHandle = "local-user";

    public string PrincipalHandle { get; set; } = DefaultPrincipalHandle;

    public string AgentHandle { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string ModelName { get; set; } = "default";

    public string? Description { get; set; }

    public string? SystemPrompt { get; set; }

    public IReadOnlyCollection<string> Plugins { get; set; } = [];

    public IReadOnlyCollection<string> Tools { get; set; } = [];

    public bool AddToSurface { get; set; } = true;
}

public sealed class OpenCaddisAgentRegistryService
{
    private readonly HttpClient httpClient;
    private readonly ILoggerFactory loggerFactory;

    public OpenCaddisAgentRegistryService(
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        this.httpClient = httpClient;
        this.loggerFactory = loggerFactory;
    }

    public Task<DiscoveryResponse> GetRegistryAsync(
        Uri hostUri,
        CancellationToken cancellationToken = default) =>
        CreateApiClient(hostUri).GetDiscoveryAsync(cancellationToken);

    public async Task<AgentHealthStatus> CreateAgentAsync(
        Uri hostUri,
        OpenCaddisAgentCreationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var principalHandle = NormalizeRequired(options.PrincipalHandle, "Principal handle");
        var agentHandle = NormalizeRequired(options.AgentHandle, "Agent handle");
        var agentType = NormalizeRequired(options.AgentType, "Agent type");
        var modelName = NormalizeRequired(options.ModelName, "Model name");
        if (principalHandle.Contains(':'))
        {
            throw new ArgumentException(
                "The principal handle cannot contain ':'.",
                nameof(options));
        }

        if (agentHandle.Contains(':'))
        {
            throw new ArgumentException(
                "Enter a bare agent handle without a principal prefix.",
                nameof(options));
        }

        var configuration = new AgentConfiguration
        {
            Handle = $"{principalHandle}:{agentHandle}",
            AgentType = agentType,
            Models = modelName,
            Description = NormalizeOptional(options.Description),
            SystemPrompt = NormalizeOptional(options.SystemPrompt),
            Plugins = NormalizeAliases(options.Plugins),
            Tools = NormalizeAliases(options.Tools),
            ForceReconfigure = false
        };
        var result = await CreateApiClient(hostUri).CreateAgentsAsync(
            [configuration],
            HealthDetailLevel.Detailed,
            cancellationToken);
        var health = result.Results.SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The FabrCore Host returned no agent health result.");
        if (result.FailureCount > 0)
        {
            throw new InvalidOperationException(
                health.Message ?? $"The FabrCore Host could not create '{configuration.Handle}'.");
        }

        if (options.AddToSurface)
        {
            try
            {
                await AddToSurfaceAsync(
                    hostUri,
                    principalHandle,
                    health.Handle,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Agent '{health.Handle}' was created, but it could not be added to Surface: {exception.Message}",
                    exception);
            }
        }

        return health;
    }

    private async Task AddToSurfaceAsync(
        Uri hostUri,
        string principalHandle,
        string agentHandle,
        CancellationToken cancellationToken)
    {
        var surfaceOptions = new SurfaceOptions
        {
            FabrCoreHostUrl = NormalizeHostUrl(hostUri)
        };
        var preferencesClient = new SurfacePreferencesClient(
            httpClient,
            Options.Create(surfaceOptions),
            loggerFactory.CreateLogger<SurfacePreferencesClient>());
        var preferences = await preferencesClient.GetAsync(
            principalHandle,
            surfaceOptions,
            cancellationToken);
        if (preferences.SurfaceAgentHandles.Add(agentHandle))
        {
            await preferencesClient.SaveAsync(
                principalHandle,
                preferences,
                cancellationToken);
        }
    }

    private FabrCoreHostApiClient CreateApiClient(Uri hostUri)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCore:HostUrl"] = NormalizeHostUrl(hostUri)
            })
            .Build();
        return new FabrCoreHostApiClient(
            httpClient,
            configuration,
            loggerFactory.CreateLogger<FabrCoreHostApiClient>());
    }

    private static string NormalizeHostUrl(Uri hostUri)
    {
        ArgumentNullException.ThrowIfNull(hostUri);
        if (!hostUri.IsAbsoluteUri ||
            hostUri.Scheme != Uri.UriSchemeHttp ||
            !hostUri.IsLoopback)
        {
            throw new ArgumentException(
                "The FabrCore Host URL must be an absolute loopback HTTP URL.",
                nameof(hostUri));
        }

        return hostUri.AbsoluteUri.TrimEnd('/');
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> NormalizeAliases(IReadOnlyCollection<string>? aliases) =>
        aliases?
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
}

using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenCaddis.Server.Builder;

public sealed class AddonBuilderAgentProvisioner
{
    private readonly ILoggerFactory loggerFactory;

    public AddonBuilderAgentProvisioner(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
    }

    public async Task<AgentHealthStatus> CreateAgentAsync(
        Uri hostUri,
        BuilderProjectInfo project,
        string solutionFilePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostUri);
        var agentConfiguration = AddonBuilderAgentDefinition.CreateConfiguration(
            project,
            solutionFilePath,
            outputPath);

        using var httpClient = new HttpClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FabrCore:HostUrl"] = NormalizeHostUrl(hostUri)
            })
            .Build();
        var apiClient = new FabrCoreHostApiClient(
            httpClient,
            configuration,
            loggerFactory.CreateLogger<FabrCoreHostApiClient>());
        var result = await apiClient.CreateAgentsAsync(
            [agentConfiguration],
            HealthDetailLevel.Detailed,
            cancellationToken);
        var health = result.Results.SingleOrDefault()
            ?? throw new InvalidOperationException("The FabrCore Host returned no agent health result.");
        if (result.FailureCount > 0)
        {
            throw new InvalidOperationException(
                health.Message ?? $"The FabrCore Host could not create {agentConfiguration.Handle}.");
        }

        try
        {
            await AddToSurfaceAsync(
                httpClient,
                loggerFactory,
                hostUri,
                AddonBuilderAgentDefinition.DefaultPrincipalHandle,
                health.Handle,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Agent '{health.Handle}' was created, but it could not be added to Surface: {exception.Message}",
                exception);
        }

        return health;
    }

    internal static async Task AddToSurfaceAsync(
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        Uri hostUri,
        string principalHandle,
        string agentHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(hostUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);

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

    internal static string NormalizeHostUrl(Uri hostUri)
    {
        ArgumentNullException.ThrowIfNull(hostUri);
        return hostUri.AbsoluteUri.TrimEnd('/');
    }
}

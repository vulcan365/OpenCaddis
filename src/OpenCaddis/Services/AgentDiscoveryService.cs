using Fabr.Client;
using Fabr.Core;

namespace OpenCaddis.Services;

public class AgentDiscoveryService
{
    private const string UserHandle = "opencaddis-user";
    private const string HandlePrefix = UserHandle + ":";

    private readonly IClientContextFactory _clientContextFactory;
    private readonly IFabrHostApiClient _apiClient;
    private readonly OpenCaddisConfigService _configService;
    private readonly ILogger<AgentDiscoveryService> _logger;

    public AgentDiscoveryService(
        IClientContextFactory clientContextFactory,
        IFabrHostApiClient apiClient,
        OpenCaddisConfigService configService,
        ILogger<AgentDiscoveryService> logger)
    {
        _clientContextFactory = clientContextFactory;
        _apiClient = apiClient;
        _configService = configService;
        _logger = logger;
    }

    public async Task<List<ChatAgent>> GetAllAgentsAsync()
    {
        // 1. Load configured handles from opencaddis.json
        var configuredHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_configService.ConfigurationExists())
        {
            try
            {
                var config = await _configService.LoadConfigurationAsync();
                foreach (var agent in config.Agents)
                {
                    configuredHandles.Add($"{HandlePrefix}{agent.Handle}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load opencaddis.json for agent discovery");
            }
        }

        // 2. Merge agents from both sources, keyed by handle
        var agentMap = new Dictionary<string, ChatAgent>(StringComparer.OrdinalIgnoreCase);

        // Source A: tracked agents from the client grain
        try
        {
            var context = await _clientContextFactory.GetOrCreateAsync(UserHandle);
            var tracked = await context.GetTrackedAgents();
            foreach (var t in tracked)
            {
                agentMap[t.Handle] = new ChatAgent
                {
                    Handle = t.Handle,
                    AgentType = t.AgentType,
                    IsConfigured = configuredHandles.Contains(t.Handle),
                    Status = "active"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get tracked agents");
        }

        // Source B: diagnostics API (catches agents created via HTTP that aren't tracked)
        // Note: AgentInfo.Key is the fully-qualified handle (e.g. "opencaddis-user:MemAgent")
        // while AgentInfo.Handle is just the short name (e.g. "MemAgent")
        try
        {
            var response = await _apiClient.GetAgentsAsync("active");
            _logger.LogDebug("Diagnostics API returned {Count} agents", response.Agents.Count);
            foreach (var info in response.Agents)
            {
                if (info.EntityType != EntityType.Agent) continue;
                if (!info.Key.StartsWith(HandlePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                // Only add if not already present from tracked agents
                if (!agentMap.ContainsKey(info.Key))
                {
                    agentMap[info.Key] = new ChatAgent
                    {
                        Handle = info.Key,
                        AgentType = info.AgentType,
                        IsConfigured = configuredHandles.Contains(info.Key),
                        Status = info.Status.ToString().ToLowerInvariant()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get agents from diagnostics API");
        }

        // 3. Sort: configured first, then alphabetical by handle
        return agentMap.Values
            .OrderByDescending(a => a.IsConfigured)
            .ThenBy(a => a.Handle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

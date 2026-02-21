using FabrCore.Client;
using FabrCore.Core;

namespace OpenCaddis.Services;

public class AgentManagerService
{
    private const string UserHandle = "opencaddis-user";

    private readonly OpenCaddisConfigService _configService;
    private readonly IClientContextFactory _clientContextFactory;
    private readonly ILogger<AgentManagerService> _logger;

    public AgentManagerService(
        OpenCaddisConfigService configService,
        IClientContextFactory clientContextFactory,
        ILogger<AgentManagerService> logger)
    {
        _configService = configService;
        _clientContextFactory = clientContextFactory;
        _logger = logger;
    }

    public Task BootstrapAsync() => ApplyConfigurationAsync(forceReconfigure: false);

    public Task ReloadAsync() => ApplyConfigurationAsync(forceReconfigure: true);

    private async Task ApplyConfigurationAsync(bool forceReconfigure)
    {
        var action = forceReconfigure ? "Reloading" : "Bootstrapping";

        if (!_configService.ConfigurationExists())
        {
            _logger.LogInformation("No opencaddis.json found — skipping agent {Action}", action.ToLowerInvariant());
            return;
        }

        OpenCaddisConfigurationDto config;
        try
        {
            config = await _configService.LoadConfigurationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load opencaddis.json");
            return;
        }

        if (config.Agents.Count == 0)
        {
            _logger.LogInformation("opencaddis.json has no agents — skipping {Action}", action.ToLowerInvariant());
            return;
        }

        _logger.LogInformation("{Action} {Count} agent(s)...", action, config.Agents.Count);

        var context = await _clientContextFactory.GetOrCreateAsync(UserHandle);
        var configuredHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var agentDto in config.Agents)
        {
            try
            {
                var agentConfig = new AgentConfiguration
                {
                    Handle = agentDto.Handle,
                    AgentType = agentDto.AgentType,
                    Models = string.IsNullOrEmpty(agentDto.Models) ? null : agentDto.Models,
                    SystemPrompt = string.IsNullOrEmpty(agentDto.SystemPrompt) ? null : agentDto.SystemPrompt,
                    Description = string.IsNullOrEmpty(agentDto.Description) ? null : agentDto.Description,
                    Args = agentDto.Args,
                    Plugins = agentDto.Plugins,
                    Tools = agentDto.Tools,
                    ForceReconfigure = forceReconfigure
                };

                await context.CreateAgent(agentConfig);
                configuredHandles.Add($"{UserHandle}:{agentDto.Handle}");

                var verb = forceReconfigure ? "Reconfigured" : "Created";
                _logger.LogInformation("{Verb} agent '{Handle}' (type: {AgentType})", verb, agentDto.Handle, agentDto.AgentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure agent '{Handle}'", agentDto.Handle);
            }
        }

        if (forceReconfigure)
        {
            try
            {
                var trackedAgents = await context.GetTrackedAgents();
                foreach (var tracked in trackedAgents)
                {
                    if (!configuredHandles.Contains(tracked.Handle))
                    {
                        _logger.LogWarning(
                            "Agent '{Handle}' is tracked by FabrCore but no longer in opencaddis.json — it will idle-deactivate",
                            tracked.Handle);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for removed agents");
            }
        }

        _logger.LogInformation("Agent {Action} complete", action.ToLowerInvariant());
    }
}

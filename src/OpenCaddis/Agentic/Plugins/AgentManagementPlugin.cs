using System.ComponentModel;
using System.Text;
using Fabr.Client;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("AgentManagement")]
public sealed class AgentManagementPlugin : IFabrPlugin
{
    private IFabrAgentHost? _host;
    private IFabrHostApiClient? _apiClient;
    private IFabrRegistry? _registry;
    private ILogger<AgentManagementPlugin> _logger = null!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrAgentHost>();
        _apiClient = serviceProvider.GetService<IFabrHostApiClient>();
        _registry = serviceProvider.GetService<IFabrRegistry>();
        _logger = serviceProvider.GetRequiredService<ILogger<AgentManagementPlugin>>();

        _logger.LogInformation("AgentManagementPlugin initialized (host: {HasHost}, apiClient: {HasApi}, registry: {HasRegistry})",
            _host is not null, _apiClient is not null, _registry is not null);
        return Task.CompletedTask;
    }

    private string GetUserId()
    {
        var handle = _host!.GetHandle();
        return handle.Split(':')[0];
    }

    // --- Agent API Tools (require userId) ---

    [Description("Create and configure a new agent on behalf of the user. Returns the new agent's health status.")]
    public async Task<string> CreateAgent(
        [Description("The handle/name for the new agent (e.g. 'my-agent')")] string handle,
        [Description("The agent type alias (e.g. 'assistant', 'thinking')")] string agentType,
        [Description("Optional model configuration name to use")] string? models = null,
        [Description("Optional system prompt for the agent")] string? systemPrompt = null,
        [Description("Optional comma-separated list of plugin aliases to enable")] string? plugins = null,
        [Description("Optional comma-separated list of tool names to enable")] string? tools = null,
        [Description("Whether to force reconfiguration if the agent already exists (default false)")] bool forceReconfigure = false)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Creating agent '{handle}'...");

        if (_host is null)
            return "Error: Agent host not available — cannot determine user context.";
        if (_apiClient is null)
            return "Error: Fabr API client not available.";

        try
        {
            var userId = GetUserId();
            var agentConfig = new AgentConfiguration
            {
                Handle = handle,
                AgentType = agentType,
                ForceReconfigure = forceReconfigure
            };

            if (!string.IsNullOrWhiteSpace(models))
                agentConfig.Models = models;
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                agentConfig.SystemPrompt = systemPrompt;
            if (!string.IsNullOrWhiteSpace(plugins))
                agentConfig.Plugins = plugins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!string.IsNullOrWhiteSpace(tools))
                agentConfig.Tools = tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var response = await _apiClient.CreateAgentsAsync(userId, [agentConfig]);

            var sb = new StringBuilder();
            sb.AppendLine($"Create agent result: {response.SuccessCount} succeeded, {response.FailureCount} failed (of {response.TotalRequested} requested)");

            foreach (var result in response.Results)
            {
                sb.AppendLine($"  Handle: {result.Handle}");
                sb.AppendLine($"  State: {result.State}");
                if (!string.IsNullOrWhiteSpace(result.Message))
                    sb.AppendLine($"  Message: {result.Message}");
            }

            _logger.LogInformation("Created agent '{Handle}' (type: {AgentType}): {Success}/{Total} succeeded",
                handle, agentType, response.SuccessCount, response.TotalRequested);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent '{Handle}'", handle);
            return $"Error creating agent '{handle}': {ex.Message}";
        }
    }

    [Description("Get the health status of a specific agent. Returns state, uptime, and message counts depending on detail level.")]
    public async Task<string> GetAgentHealth(
        [Description("The agent handle to check (e.g. 'assistant')")] string handle,
        [Description("Detail level: 'basic', 'detailed', or 'full' (default 'basic')")] string? detailLevel = "basic")
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Checking health of '{handle}'...");

        if (_host is null)
            return "Error: Agent host not available — cannot determine user context.";
        if (_apiClient is null)
            return "Error: Fabr API client not available.";

        try
        {
            var userId = GetUserId();
            var level = detailLevel?.ToLowerInvariant() switch
            {
                "detailed" => HealthDetailLevel.Detailed,
                "full" => HealthDetailLevel.Full,
                _ => HealthDetailLevel.Basic
            };

            var health = await _apiClient.GetAgentHealthAsync(userId, handle, level);

            var sb = new StringBuilder();
            sb.AppendLine($"Agent: {health.Handle}");
            sb.AppendLine($"State: {health.State}");
            sb.AppendLine($"Configured: {health.IsConfigured}");
            sb.AppendLine($"Timestamp: {health.Timestamp:u}");
            if (!string.IsNullOrWhiteSpace(health.Message))
                sb.AppendLine($"Message: {health.Message}");

            // Detailed level fields
            if (health.AgentType is not null)
                sb.AppendLine($"Agent Type: {health.AgentType}");
            if (health.Uptime.HasValue)
                sb.AppendLine($"Uptime: {health.Uptime.Value}");
            if (health.MessagesProcessed.HasValue)
                sb.AppendLine($"Messages Processed: {health.MessagesProcessed.Value}");
            if (health.ActiveTimerCount.HasValue)
                sb.AppendLine($"Active Timers: {health.ActiveTimerCount.Value}");
            if (health.ActiveReminderCount.HasValue)
                sb.AppendLine($"Active Reminders: {health.ActiveReminderCount.Value}");
            if (health.StreamCount.HasValue)
                sb.AppendLine($"Streams: {health.StreamCount.Value}");

            // Full level fields
            if (health.ActiveStreams is { Count: > 0 })
                sb.AppendLine($"Active Streams: {string.Join(", ", health.ActiveStreams)}");
            if (health.Diagnostics is { Count: > 0 })
            {
                sb.AppendLine("Diagnostics:");
                foreach (var (key, value) in health.Diagnostics)
                    sb.AppendLine($"  {key}: {value}");
            }

            _logger.LogDebug("Health check for '{Handle}': {State}", handle, health.State);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get health for agent '{Handle}'", handle);
            return $"Error getting health for '{handle}': {ex.Message}";
        }
    }

    // --- Discovery API Tools (no userId needed) ---

    [Description("Discover all registered capabilities: agent types, plugins, and tools available in the system.")]
    public async Task<string> DiscoverCapabilities()
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Discovering capabilities...");

        if (_registry is null)
            return "Error: Fabr registry not available — capability discovery is unavailable.";

        try
        {
            var agentTypes = _registry.GetAgentTypes();
            var pluginList = _registry.GetPlugins();
            var toolList = _registry.GetTools();

            var sb = new StringBuilder();

            sb.AppendLine($"Agent Types ({agentTypes.Count}):");
            foreach (var entry in agentTypes)
            {
                var aliases = entry.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", entry.Aliases)})" : "";
                sb.AppendLine($"  {entry.TypeName}{aliases}");
            }

            sb.AppendLine();
            sb.AppendLine($"Plugins ({pluginList.Count}):");
            foreach (var entry in pluginList)
            {
                var aliases = entry.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", entry.Aliases)})" : "";
                sb.AppendLine($"  {entry.TypeName}{aliases}");
            }

            sb.AppendLine();
            sb.AppendLine($"Tools ({toolList.Count}):");
            foreach (var entry in toolList)
            {
                var aliases = entry.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", entry.Aliases)})" : "";
                sb.AppendLine($"  {entry.TypeName}{aliases}");
            }

            _logger.LogDebug("Discovered {AgentTypes} agent types, {Plugins} plugins, {Tools} tools",
                agentTypes.Count, pluginList.Count, toolList.Count);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover capabilities");
            return $"Error discovering capabilities: {ex.Message}";
        }
    }

    // --- Diagnostics API Tools (no userId needed) ---

    [Description("List all agents in the system, optionally filtered by status ('active' or 'deactivated').")]
    public async Task<string> ListAgents(
        [Description("Optional status filter: 'active' or 'deactivated'. Omit to list all agents.")] string? status = null)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Listing agents...");

        if (_apiClient is null)
            return "Error: Fabr API client not available.";

        try
        {
            var response = await _apiClient.GetAgentsAsync(status);

            if (response.Agents.Count == 0)
            {
                var filterMsg = string.IsNullOrWhiteSpace(status) ? "" : $" with status '{status}'";
                return $"No agents found{filterMsg}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Agents ({response.Count}):");

            foreach (var agent in response.Agents)
            {
                sb.AppendLine($"  [{agent.Status}] {agent.Handle} (type: {agent.AgentType}, key: {agent.Key})");
                sb.AppendLine($"         Activated: {agent.ActivatedAt:u}");
                if (agent.DeactivatedAt.HasValue)
                    sb.AppendLine($"         Deactivated: {agent.DeactivatedAt.Value:u} — {agent.DeactivationReason}");
            }

            _logger.LogDebug("Listed {Count} agents (filter: {Status})", response.Count, status ?? "all");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list agents");
            return $"Error listing agents: {ex.Message}";
        }
    }

    [Description("Get detailed information about a specific agent by its key (e.g. 'opencaddis-user:assistant').")]
    public async Task<string> GetAgentDetails(
        [Description("The agent key (e.g. 'opencaddis-user:assistant')")] string key)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Getting details for '{key}'...");

        if (_apiClient is null)
            return "Error: Fabr API client not available.";

        try
        {
            var agent = await _apiClient.GetAgentAsync(key);

            if (agent is null)
                return $"Agent not found: '{key}'";

            var sb = new StringBuilder();
            sb.AppendLine($"Key: {agent.Key}");
            sb.AppendLine($"Handle: {agent.Handle}");
            sb.AppendLine($"Agent Type: {agent.AgentType}");
            sb.AppendLine($"Status: {agent.Status}");
            sb.AppendLine($"Entity Type: {agent.EntityType}");
            sb.AppendLine($"Activated: {agent.ActivatedAt:u}");
            if (agent.DeactivatedAt.HasValue)
                sb.AppendLine($"Deactivated: {agent.DeactivatedAt.Value:u}");
            if (!string.IsNullOrWhiteSpace(agent.DeactivationReason))
                sb.AppendLine($"Deactivation Reason: {agent.DeactivationReason}");

            _logger.LogDebug("Got details for agent '{Key}': {Status}", key, agent.Status);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent details for '{Key}'", key);
            return $"Error getting details for '{key}': {ex.Message}";
        }
    }

    [Description("Get aggregate statistics about agents: total, active, and deactivated counts.")]
    public async Task<string> GetAgentStatistics()
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Getting agent statistics...");

        if (_apiClient is null)
            return "Error: Fabr API client not available.";

        try
        {
            var stats = await _apiClient.GetAgentStatisticsAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Agent Statistics:");
            foreach (var (key, value) in stats)
            {
                sb.AppendLine($"  {key}: {value}");
            }

            _logger.LogDebug("Retrieved agent statistics");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent statistics");
            return $"Error getting agent statistics: {ex.Message}";
        }
    }

    [Description("Purge deactivated agents older than the specified number of hours. Returns the count of purged agents.")]
    public async Task<string> PurgeDeactivatedAgents(
        [Description("Purge agents deactivated more than this many hours ago (default 24)")] int? olderThanHours = 24)
    {
        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Purging old agents...");

        if (_apiClient is null)
            return "Error: Fabr API client not available.";

        try
        {
            var hours = olderThanHours ?? 24;
            var response = await _apiClient.PurgeOldAgentsAsync(hours);

            _logger.LogInformation("Purged {Count} deactivated agent(s) older than {Hours}h", response.PurgedCount, hours);
            return $"Purged {response.PurgedCount} agent(s). {response.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge deactivated agents");
            return $"Error purging agents: {ex.Message}";
        }
    }
}

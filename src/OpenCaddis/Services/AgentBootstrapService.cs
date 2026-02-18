namespace OpenCaddis.Services;

public class AgentBootstrapService : BackgroundService
{
    private const string UserHandle = "opencaddis-user";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly AgentManagerService _agentManager;
    private readonly OpenCaddisConfigService _configService;
    private readonly AgentEventLoggerProvider? _loggerProvider;
    private readonly ILogger<AgentBootstrapService> _logger;

    public AgentBootstrapService(
        IHostApplicationLifetime lifetime,
        AgentManagerService agentManager,
        OpenCaddisConfigService configService,
        ILogger<AgentBootstrapService> logger,
        AgentEventLoggerProvider? loggerProvider = null)
    {
        _lifetime = lifetime;
        _agentManager = agentManager;
        _configService = configService;
        _logger = logger;
        _loggerProvider = loggerProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        using var registration = _lifetime.ApplicationStarted.Register(() => tcs.SetResult());
        await tcs.Task;

        try
        {
            await _agentManager.BootstrapAsync();
            await SignalEventLoggerReadyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent bootstrap failed");
        }
    }

    private async Task SignalEventLoggerReadyAsync()
    {
        if (_loggerProvider is null)
            return;

        if (!_configService.ConfigurationExists())
            return;

        try
        {
            var config = await _configService.LoadConfigurationAsync();
            var eventLogAgent = config.Agents.FirstOrDefault(
                a => string.Equals(a.AgentType, "eventlog", StringComparison.OrdinalIgnoreCase));

            if (eventLogAgent is null)
                return;

            var fullHandle = $"{UserHandle}:{eventLogAgent.Handle}";
            _loggerProvider.SignalReady(fullHandle);
            _logger.LogInformation("Event log forwarding enabled for agent '{Handle}'", fullHandle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize event log forwarding — continuing without it");
        }
    }
}

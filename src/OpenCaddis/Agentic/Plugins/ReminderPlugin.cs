using System.ComponentModel;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("Reminders")]
public sealed class ReminderPlugin : IFabrPlugin
{
    private IFabrAgentHost? _host;
    private ILogger<ReminderPlugin> _logger = null!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<ReminderPlugin>>();

        _logger.LogInformation("ReminderPlugin initialized");
        return Task.CompletedTask;
    }

    [Description("Register a persistent reminder that sends a message to the agent on a recurring schedule. Reminders survive restarts.")]
    public async Task<string> RegisterReminder(
        [Description("Unique name for the reminder")] string reminderName,
        [Description("The message type to send when the reminder fires")] string messageType,
        [Description("Optional message content to include when the reminder fires")] string? message,
        [Description("Minutes to wait before the first tick")] double dueTimeMinutes,
        [Description("Minutes between subsequent ticks (minimum 1)")] double periodMinutes)
    {
        if (_host is null)
        {
            _logger.LogWarning("Cannot register reminder '{ReminderName}' — agent host not available", reminderName);
            return "Error: Reminder service is not available.";
        }

        await ThinkingNotifier.SendThinkingAsync(_host, $"Setting up reminder '{reminderName}'...");

        var dueTime = TimeSpan.FromMinutes(Math.Max(0, dueTimeMinutes));
        var period = TimeSpan.FromMinutes(Math.Max(1, periodMinutes));

        await _host.RegisterReminder(reminderName, messageType, message, dueTime, period);
        _logger.LogInformation("Registered reminder '{ReminderName}' (due: {DueMinutes}m, period: {PeriodMinutes}m, type: {MessageType})", reminderName, dueTime.TotalMinutes, period.TotalMinutes, messageType);
        return $"Reminder '{reminderName}' registered successfully. First tick in {dueTime.TotalMinutes:F0} minute(s), then every {period.TotalMinutes:F0} minute(s).";
    }

    [Description("Unregister a previously registered reminder by its name.")]
    public async Task<string> UnregisterReminder(
        [Description("The name of the reminder to unregister")] string reminderName)
    {
        if (_host is null)
        {
            _logger.LogWarning("Cannot unregister reminder '{ReminderName}' — agent host not available", reminderName);
            return "Error: Reminder service is not available.";
        }

        await ThinkingNotifier.SendThinkingAsync(_host, $"Removing reminder '{reminderName}'...");

        await _host.UnregisterReminder(reminderName);
        _logger.LogInformation("Unregistered reminder '{ReminderName}'", reminderName);
        return $"Reminder '{reminderName}' has been unregistered.";
    }
}

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

    [Description("Register a reminder. By default fires once and auto-removes itself. Set recurring=true with a periodMinutes for repeating reminders.")]
    public async Task<string> RegisterReminder(
        [Description("Unique name for the reminder")] string reminderName,
        [Description("Type of reminder: 'notify' to alert the user, or 'action' to autonomously execute a task")] string reminderType,
        [Description("The reminder message or task instructions")] string message,
        [Description("Minutes to wait before firing")] double dueTimeMinutes,
        [Description("Whether this reminder repeats. Defaults to false (one-shot).")] bool recurring = false,
        [Description("Minutes between subsequent ticks when recurring (minimum 1). Ignored for one-shot reminders.")] double periodMinutes = 1)
    {
        if (_host is null)
        {
            _logger.LogWarning("Cannot register reminder '{ReminderName}' — agent host not available", reminderName);
            return "Error: Reminder service is not available.";
        }

        await ThinkingNotifier.SendThinkingAsync(_host, $"Setting up reminder '{reminderName}'...");

        var normalizedType = reminderType?.ToLowerInvariant() == "action" ? "action" : "notify";
        var oneshotTag = recurring ? "" : ":oneshot";
        var messageType = $"reminder:{normalizedType}{oneshotTag}";

        var dueTime = TimeSpan.FromMinutes(Math.Max(0, dueTimeMinutes));
        // Orleans requires period >= 1 minute; for one-shot we still set 1m but auto-unregister after first tick
        var period = TimeSpan.FromMinutes(Math.Max(1, periodMinutes));

        await _host.RegisterReminder(reminderName, messageType, message, dueTime, period);
        _logger.LogInformation("Registered reminder '{ReminderName}' (due: {DueMinutes}m, period: {PeriodMinutes}m, type: {MessageType}, recurring: {Recurring})",
            reminderName, dueTime.TotalMinutes, period.TotalMinutes, messageType, recurring);

        if (recurring)
            return $"Recurring reminder '{reminderName}' registered. First tick in {dueTime.TotalMinutes:F0} minute(s), then every {period.TotalMinutes:F0} minute(s).";
        else
            return $"Reminder '{reminderName}' registered. Will fire in {dueTime.TotalMinutes:F0} minute(s).";
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

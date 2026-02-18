using System.ComponentModel;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Extensions.Logging;

namespace OpenCaddis.Agentic.Tools;

public class NotificationTool
{
    private readonly IFabrAgentHost _agentHost;
    private readonly Func<string?> _getClientHandle;
    private readonly ILogger<NotificationTool> _logger;

    public NotificationTool(IFabrAgentHost agentHost, Func<string?> getClientHandle, ILogger<NotificationTool> logger)
    {
        _agentHost = agentHost;
        _getClientHandle = getClientHandle;
        _logger = logger;
    }

    [Description("Send a one-way notification message to the user. Use this to proactively inform the user about something.")]
    public async Task<string> SendNotification(
        [Description("The message to send to the user")] string message)
    {
        var clientHandle = _getClientHandle();
        if (clientHandle is null)
        {
            _logger.LogWarning("Cannot send notification — no client handle available");
            return "No client handle available — cannot send notification.";
        }

        _logger.LogDebug("Sending notification to client {ClientHandle}", clientHandle);

        await _agentHost.SendMessage(new AgentMessage
        {
            ToHandle = clientHandle,
            FromHandle = _agentHost.GetHandle(),
            Kind = MessageKind.OneWay,
            Channel = "notification",
            Message = message
        });

        _logger.LogInformation("Notification sent to {ClientHandle}", clientHandle);
        return "Notification sent.";
    }
}

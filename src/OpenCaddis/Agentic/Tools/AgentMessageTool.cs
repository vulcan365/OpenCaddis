using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;

namespace OpenCaddis.Agentic.Tools;

public class AgentMessageTool
{
    private readonly IFabrCoreAgentHost _agentHost;
    private readonly ILogger<AgentMessageTool> _logger;

    public AgentMessageTool(IFabrCoreAgentHost agentHost, ILogger<AgentMessageTool> logger)
    {
        _agentHost = agentHost;
        _logger = logger;
    }

    [Description("Send a one-way message to another agent. Use this to notify or instruct another agent without waiting for a response.")]
    public async Task<string> SendAgentMessage(
        [Description("The handle of the target agent")] string toHandle,
        [Description("The message to send")] string message)
    {
        _logger.LogDebug("Sending message to {ToHandle} from {FromHandle}", toHandle, _agentHost.GetHandle());

        await _agentHost.SendMessage(new AgentMessage
        {
            ToHandle = toHandle,
            FromHandle = _agentHost.GetHandle(),
            Kind = MessageKind.OneWay,
            Message = message
        });

        _logger.LogInformation("Message sent to {ToHandle}", toHandle);
        return $"Message sent to {toHandle}.";
    }
}

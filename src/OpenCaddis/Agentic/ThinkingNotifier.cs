using System.Collections.Concurrent;
using FabrCore.Core;
using FabrCore.Sdk;

namespace OpenCaddis.Agentic;

/// <summary>
/// Static helper that lets plugins send "thinking" messages to the user.
/// The agent registers its client handle; plugins look it up by agent handle.
/// </summary>
public static class ThinkingNotifier
{
    private static readonly ConcurrentDictionary<string, string> _clientHandles = new();

    public static void SetClientHandle(string agentHandle, string clientHandle)
        => _clientHandles[agentHandle] = clientHandle;

    public static void RemoveClientHandle(string agentHandle)
        => _clientHandles.TryRemove(agentHandle, out _);

    public static bool TryGetClientHandle(string agentHandle, out string clientHandle)
        => _clientHandles.TryGetValue(agentHandle, out clientHandle!);

    public static async Task SendThinkingAsync(IFabrCoreAgentHost host, string message)
    {
        var agentHandle = host.GetHandle();
        if (!_clientHandles.TryGetValue(agentHandle, out var clientHandle))
            return;

        await host.SendMessage(new AgentMessage
        {
            ToHandle = clientHandle,
            FromHandle = agentHandle,
            Kind = MessageKind.OneWay,
            MessageType = "thinking",
            Message = message
        });
    }
}

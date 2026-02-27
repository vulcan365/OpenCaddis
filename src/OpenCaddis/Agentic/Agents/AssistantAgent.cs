using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("assistant")]
public class AssistantAgent : FabrCoreAgentProxy
{
    private AIAgent? agent;
    private AgentSession? session;

    public AssistantAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        var modelConfigName = config.Models
            ?? config.Args?.GetValueOrDefault("ModelConfig")
            ?? "default";
        var tools = await ResolveConfiguredToolsAsync();

        var result = await CreateChatClientAgent(
            modelConfigName,
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools
        );

        agent = result.Agent;
        session = result.Session;

        logger.LogInformation(
            "AssistantAgent '{Handle}' initialized with model config '{ModelConfig}' and {ToolCount} tools",
            config.Handle, modelConfigName, tools.Count);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var myHandle = fabrcoreAgentHost.GetHandle();
        if (message.FromHandle is not null && message.FromHandle != myHandle)
        {
            ThinkingNotifier.SetClientHandle(myHandle, message.FromHandle);
        }

        var response = message.Response();

        logger.LogInformation(
            "AssistantAgent '{Handle}' received message from {From} on channel '{Channel}'",
            config.Handle, message.FromHandle, message.Channel);
        logger.LogDebug(
            "AssistantAgent '{Handle}' message content: {Message}",
            config.Handle, Truncate(message.Message ?? "", 200));

        // Send a thinking indicator to the client
        await ThinkingNotifier.SendThinkingAsync(fabrcoreAgentHost, "Thinking...");

        // Run compaction if needed before invoking the model
        var compaction = await TryCompactAsync(
            onCompacting: () => ThinkingNotifier.SendThinkingAsync(fabrcoreAgentHost, "Compacting history..."));
        if (compaction?.WasCompacted == true)
        {
            await ThinkingNotifier.SendThinkingAsync(fabrcoreAgentHost,
                $"Compacted history: {compaction.OriginalMessageCount} → {compaction.CompactedMessageCount} messages");
        }

        var isReminder = message.MessageType?.StartsWith("reminder:") == true;

        try
        {
            var inputText = FormatReminderMessage(message) ?? message.Message ?? string.Empty;
            var result = await agent!.RunAsync(inputText, session);
            response.Message = result.Text ?? "No response";

            logger.LogInformation(
                "AssistantAgent '{Handle}' completed response ({Length} chars)",
                config.Handle, response.Message.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AssistantAgent '{Handle}' error processing message", config.Handle);
            response.Message = $"Error: {ex.Message}";
        }

        if (isReminder)
        {
            // Reminder messages are self-sent (FromHandle == ToHandle), so the OnMessage
            // return value goes back to the agent, not the user. Send the response to the
            // client explicitly so it appears in the UI.
            if (ThinkingNotifier.TryGetClientHandle(myHandle, out var clientHandle))
            {
                await fabrcoreAgentHost.SendMessage(new AgentMessage
                {
                    ToHandle = clientHandle,
                    FromHandle = myHandle,
                    Kind = MessageKind.OneWay,
                    Message = response.Message
                });
            }

            // Auto-unregister one-shot reminders after the first tick
            if (message.MessageType?.Contains(":oneshot") == true)
            {
                var reminderName = message.Args?.GetValueOrDefault("reminderName");
                if (reminderName is not null)
                {
                    try
                    {
                        await fabrcoreAgentHost.UnregisterReminder(reminderName);
                        logger.LogInformation("Auto-unregistered one-shot reminder '{ReminderName}'", reminderName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to auto-unregister one-shot reminder '{ReminderName}'", reminderName);
                    }
                }
            }
        }

        return response;
    }

    private static string? FormatReminderMessage(AgentMessage message)
    {
        if (message.MessageType is null || !message.MessageType.StartsWith("reminder:"))
            return null;

        var content = message.Message ?? string.Empty;

        // messageType is "reminder:action", "reminder:action:oneshot", "reminder:notify", "reminder:notify:oneshot"
        if (message.MessageType.Contains(":action"))
            return $"[REMINDER TRIGGERED] You have a scheduled task to execute autonomously. Use your available tools to carry out the following instructions:\n{content}";

        return $"[REMINDER TRIGGERED] You have a scheduled reminder. Notify the user with the following message using the SendNotification tool:\n\"{content}\"";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}

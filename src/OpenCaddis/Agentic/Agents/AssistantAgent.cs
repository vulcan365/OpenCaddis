using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("assistant")]
public class AssistantAgent : FabrAgentProxy
{
    private AIAgent? agent;
    private AgentSession? session;

    public AssistantAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrAgentHost fabrAgentHost)
        : base(config, serviceProvider, fabrAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        var modelConfigName = config.Args?.GetValueOrDefault("ModelConfig") ?? "default";
        var tools = await ResolveConfiguredToolsAsync();

        var result = await CreateChatClientAgent(
            modelConfigName,
            threadId: config.Handle ?? fabrAgentHost.GetHandle(),
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
        var myHandle = fabrAgentHost.GetHandle();
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
        await ThinkingNotifier.SendThinkingAsync(fabrAgentHost, "Thinking...");

        // Run compaction if needed before invoking the model
        var compaction = await TryCompactAsync(
            onCompacting: () => ThinkingNotifier.SendThinkingAsync(fabrAgentHost, "Compacting history..."));
        if (compaction?.WasCompacted == true)
        {
            await ThinkingNotifier.SendThinkingAsync(fabrAgentHost,
                $"Compacted history: {compaction.OriginalMessageCount} → {compaction.CompactedMessageCount} messages");
        }

        try
        {
            var result = await agent!.RunAsync(message.Message ?? string.Empty, session);
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

        return response;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}

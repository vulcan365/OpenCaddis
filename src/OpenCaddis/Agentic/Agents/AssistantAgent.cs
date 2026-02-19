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
        var modelConfig = config.Args?.GetValueOrDefault("ModelConfig") ?? "default";
        var tools = await ResolveConfiguredToolsAsync();

        (agent, session) = await CreateChatClientAgent(
            modelConfig,
            threadId: config.Handle ?? fabrAgentHost.GetHandle(),
            tools: tools
        );

        logger.LogInformation(
            "AssistantAgent '{Handle}' initialized with model config '{ModelConfig}' and {ToolCount} tools",
            config.Handle, modelConfig, tools.Count);
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

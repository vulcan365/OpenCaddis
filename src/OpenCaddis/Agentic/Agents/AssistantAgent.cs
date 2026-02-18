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
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var myHandle = fabrAgentHost.GetHandle();
        if (message.FromHandle is not null && message.FromHandle != myHandle)
        {
            ThinkingNotifier.SetClientHandle(myHandle, message.FromHandle);
        }

        var response = message.Response();

        // Send a thinking indicator to the client
        await ThinkingNotifier.SendThinkingAsync(fabrAgentHost, "Thinking...");

        try
        {
            var result = await agent!.RunAsync(message.Message ?? string.Empty, session);
            response.Message = result.Text ?? "No response";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message");
            response.Message = $"Error: {ex.Message}";
        }

        return response;
    }
}

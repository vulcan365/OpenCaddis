using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Agents.AI;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("sip")]
public class SipAgent : FabrAgentProxy
{
    private AIAgent? agent;
    private AgentSession? session;

    public SipAgent(
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
            "SipAgent '{Handle}' initialized with model config '{ModelConfig}'",
            config.Handle, modelConfigName);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        logger.LogInformation(
            "SipAgent '{Handle}' received transcription from {From}: {Text}",
            config.Handle, message.FromHandle, Truncate(message.Message ?? "", 200));

        try
        {
            var result = await agent!.RunAsync(message.Message ?? string.Empty, session);
            response.Message = result.Text ?? "No response";

            logger.LogInformation(
                "SipAgent '{Handle}' generated response ({Length} chars): {Text}",
                config.Handle, response.Message.Length, Truncate(response.Message, 200));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SipAgent '{Handle}' error processing transcription", config.Handle);
            response.Message = $"Error: {ex.Message}";
        }

        return response;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}

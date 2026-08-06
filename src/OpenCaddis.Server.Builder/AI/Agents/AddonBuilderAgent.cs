using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenCaddis.Server.Builder.AI.Agents;

[AgentAlias(Alias)]
[Description("Project-scoped .NET coding agent for OpenCaddis add-ons")]
[FabrCoreCapabilities("Uses Roslyn semantic analysis, scoped filesystem editing, and controlled .NET CLI operations to implement, verify, and publish changes for one configured project.")]
[FabrCoreNote("Each instance is restricted to one project folder and publishes only that project's primary DLL to the configured add-on path.")]
public sealed class AddonBuilderAgent : FabrCoreAgentProxy
{
    public const string Alias = "addon-builder-agent";
    private FabrCoreHarnessResult? harness;

    public AddonBuilderAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();
        harness = await CreateFabrCoreHarnessAgent(
            config.Models ?? "default",
            config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        if (harness is null)
        {
            response.Message = "The coding agent has not initialized successfully.";
            return response;
        }

        SetStatusMessage("Inspecting project and planning changes...");
        try
        {
            var prompt = new ChatMessage(ChatRole.User, message.Message ?? string.Empty);
            await foreach (var update in harness.RunStreamingAsync([prompt]))
            {
                response.Message += update.Text;
            }

            if (harness.DescribeLostDelegations() is { } lostDelegations)
            {
                response.Message += $"{Environment.NewLine}{Environment.NewLine}{lostDelegations}";
            }

            var remaining = await harness.GetRemainingTodosAsync();
            if (remaining.Count > 0)
            {
                response.Message += $"{Environment.NewLine}{Environment.NewLine}" +
                    $"Not completed within the iteration budget:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, remaining.Select(item => $"- {item.Title}"));
            }

            return response;
        }
        finally
        {
            SetStatusMessage(null);
        }
    }
}

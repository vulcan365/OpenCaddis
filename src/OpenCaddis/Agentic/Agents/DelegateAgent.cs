using System.Text.Json;
using System.Text.Json.Serialization;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("delegate")]
public class DelegateAgent : FabrAgentProxy
{
    private const string UserHandle = "opencaddis-user";

    private IChatClient? _routingClient;
    private AIAgent? _agent;
    private AgentSession? _session;
    private string? _lastClientHandle;

    private List<AvailableAgentInfo> _availableAgents = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public DelegateAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrAgentHost fabrAgentHost)
        : base(config, serviceProvider, fabrAgentHost)
    {
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Initialization
    // ═══════════════════════════════════════════════════════════════════════

    public override async Task OnInitialize()
    {
        var modelConfigName = config.Args?.GetValueOrDefault("ModelConfig") ?? "default";

        _routingClient = await GetChatClient(modelConfigName);

        var result = await CreateChatClientAgent(
            modelConfigName,
            threadId: config.Handle ?? fabrAgentHost.GetHandle(),
            tools: []
        );

        _agent = result.Agent;
        _session = result.Session;

        await DiscoverAvailableAgents();

        logger.LogInformation(
            "DelegateAgent initialized with {AgentCount} available agents: {Agents}",
            _availableAgents.Count,
            string.Join(", ", _availableAgents.Select(a => a.AgentName)));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  OnMessage — Entry Point
    // ═══════════════════════════════════════════════════════════════════════

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var myHandle = fabrAgentHost.GetHandle();

        // Only process genuine user requests on the default channel.
        //
        // Ignore:
        //  - "agent" channel: delegation responses already handled inline
        //    via SendAndReceiveMessage.
        //  - "thinking"/"status" MessageType: notifications from delegated
        //    agents whose ThinkingNotifier targets us (Fabr delivers OneWay
        //    SendMessage calls through the chat stream → OnMessage).
        //  - OneWay Kind: any fire-and-forget notification, not a user request.
        if (!string.IsNullOrEmpty(message.Channel)
            || message.MessageType is "thinking" or "status"
            || message.Kind == MessageKind.OneWay)
        {
            logger.LogDebug(
                "DelegateAgent ignoring message: Kind={Kind}, Channel='{Channel}', " +
                "MessageType='{MessageType}', From={From}",
                message.Kind, message.Channel, message.MessageType, message.FromHandle);
            return message.Response();
        }

        if (message.FromHandle is not null && message.FromHandle != myHandle)
        {
            _lastClientHandle = message.FromHandle;
            ThinkingNotifier.SetClientHandle(myHandle, _lastClientHandle);
        }

        var response = message.Response();

        logger.LogInformation(
            "DelegateAgent received message from {From} on channel '{Channel}'",
            message.FromHandle, message.Channel);
        logger.LogDebug(
            "DelegateAgent message content: {Message}",
            Truncate(message.Message ?? "", 200));

        // Run compaction if needed before invoking the model
        var compaction = await TryCompactAsync(
            onCompacting: () => SendThinkingAsync("Compacting history..."));
        if (compaction?.WasCompacted == true)
        {
            await SendThinkingAsync(
                $"Compacted history: {compaction.OriginalMessageCount} → {compaction.CompactedMessageCount} messages");
        }

        try
        {
            var userMessage = message.Message ?? string.Empty;

            if (_availableAgents.Count == 0)
            {
                logger.LogInformation("DelegateAgent has no cached agents, re-discovering");
                await DiscoverAvailableAgents();
            }

            if (_availableAgents.Count == 0)
            {
                logger.LogWarning("DelegateAgent has no available agents after discovery");
                response.Message = "No managed agents are available. Please check the agent configuration.";
                return response;
            }

            // Step 1: Select the best agent
            await SendThinkingAsync("Selecting the best agent...");
            var selection = await SelectAgent(userMessage);

            var selectedAgent = _availableAgents.FirstOrDefault(a =>
                string.Equals(a.AgentName, selection.SelectedAgentName, StringComparison.OrdinalIgnoreCase));

            if (selectedAgent is null)
            {
                logger.LogWarning(
                    "LLM selected unknown agent '{Selected}', falling back to first available",
                    selection.SelectedAgentName);
                selectedAgent = _availableAgents[0];
            }

            logger.LogInformation(
                "Selected agent '{Agent}' for delegation. Reasoning: {Reasoning}",
                selectedAgent.AgentName, selection.Reasoning);

            // Step 2: Formulate the delegation message
            await SendThinkingAsync("Formulating request...");
            var formulationPrompt = $"""
                The user sent the following request:
                ---
                {userMessage}
                ---

                You are delegating this to the **{selectedAgent.AgentName}** agent ({selectedAgent.Description}).

                Formulate the best possible message to send to this agent so it can fulfill the user's request effectively.
                Be clear, specific, and include all relevant context from the user's message.
                Output ONLY the message to send — no preamble, no explanation.
                """;

            var formulationResult = await _agent!.RunAsync(formulationPrompt, _session);
            var delegationMessage = formulationResult.Text ?? userMessage;

            logger.LogDebug(
                "DelegateAgent formulated delegation message for {Agent}: {Message}",
                selectedAgent.AgentName, Truncate(delegationMessage, 200));

            // Step 3: Delegate to the selected agent (with timeout)
            await SendThinkingAsync($"Delegating to {selectedAgent.AgentName}...");

            var taskMessage = new AgentMessage
            {
                ToHandle = selectedAgent.Handle,
                FromHandle = myHandle,
                Channel = "agent",
                Kind = MessageKind.Request,
                MessageType = "task",
                Message = delegationMessage
            };

            var timeoutSeconds = int.TryParse(
                config.Args?.GetValueOrDefault("DelegationTimeoutSeconds"), out var ts) ? ts : 180;

            var responseText = await DelegateWithTimeoutAsync(
                taskMessage, selectedAgent.AgentName, timeoutSeconds);

            // Step 4: Analyze and formulate final response
            await SendThinkingAsync("Analyzing response...");
            var analysisPrompt = $"""
                The user's original request was:
                ---
                {userMessage}
                ---

                You delegated to the **{selectedAgent.AgentName}** agent and received this response:
                ---
                {responseText}
                ---

                Analyze the response and formulate a clear, helpful reply for the user.
                If the agent's response fully answers the request, present it cleanly.
                If it's partial or unclear, note what was accomplished and what may still be needed.
                If the agent timed out or returned an error, let the user know clearly.
                Output ONLY the final response for the user.
                """;

            var analysisResult = await _agent!.RunAsync(analysisPrompt, _session);
            response.Message = analysisResult.Text ?? responseText;

            logger.LogInformation(
                "DelegateAgent completed delegation cycle via {Agent}, final response ({Length} chars)",
                selectedAgent.AgentName, response.Message.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in DelegateAgent.OnMessage");
            response.Message = $"An error occurred while processing your request: {ex.Message}";
        }

        return response;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Agent Selection
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<AgentSelectionOutput> SelectAgent(string userMessage)
    {
        var agentCatalog = BuildAgentCatalog();

        var systemPrompt = $"""
            You are an intelligent message router. Given a user's request and a catalog of available agents,
            select the single best agent to handle the request.

            ## Available Agents
            {agentCatalog}

            ## Rules
            - Select exactly ONE agent by name (the AgentName field).
            - Choose the agent whose capabilities best match the user's request.
            - Provide brief reasoning for your selection.
            - If no agent is a clear match, choose the most general-purpose agent.
            """;

        return await ExtractJsonAsync<AgentSelectionOutput>(systemPrompt, userMessage);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Agent Discovery (reused from WorkflowAgent pattern)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task DiscoverAvailableAgents()
    {
        _availableAgents = [];

        var managedAgentsCsv = config.Args?.GetValueOrDefault("ManagedAgents") ?? "";
        var agentNames = managedAgentsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (agentNames.Count == 0)
        {
            logger.LogWarning("DelegateAgent has no ManagedAgents configured");
            return;
        }

        foreach (var name in agentNames)
        {
            var handle = $"{UserHandle}:{name}";
            try
            {
                var health = await fabrAgentHost.GetAgentHealth(handle, HealthDetailLevel.Detailed);
                if (health.State == HealthState.Healthy && health.IsConfigured)
                {
                    var desc = health.Configuration?.Description is { Length: > 0 } d
                        ? d
                        : health.Configuration?.SystemPrompt is { Length: > 0 } sp
                            ? sp[..Math.Min(200, sp.Length)]
                            : $"Agent '{name}' (type: {health.AgentType})";

                    _availableAgents.Add(new AvailableAgentInfo
                    {
                        AgentName = name,
                        Handle = handle,
                        AgentType = health.AgentType ?? "unknown",
                        Description = desc
                    });
                }
                else
                {
                    logger.LogWarning("Agent '{Name}' is not healthy/configured — skipping", name);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get health for agent '{Name}' — skipping", name);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<T> ExtractJsonAsync<T>(string systemPrompt, string userPrompt)
        where T : class, new()
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(T));
        var chatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema: schema,
                schemaName: typeof(T).Name,
                schemaDescription: $"Structured {typeof(T).Name} response")
        };

        var response = await _routingClient!.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, userPrompt)], chatOptions);

        var text = response.Text?.Trim() ?? "{}";
        var json = TryExtractJsonObject(text) ?? "{}";

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts) ?? new T();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize {Type} from LLM response: {Json}",
                typeof(T).Name, Truncate(json, 200));
            return new T();
        }
    }

    private static string? TryExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];
        return null;
    }

    private string BuildAgentCatalog()
    {
        if (_availableAgents.Count == 0)
            return "(No agents available)";

        return string.Join("\n", _availableAgents.Select(a =>
            $"- **{a.AgentName}** (type: {a.AgentType}): {a.Description}"));
    }

    private async Task<string> DelegateWithTimeoutAsync(
        AgentMessage taskMessage, string agentName, int timeoutSeconds)
    {
        try
        {
            var delegationTask = fabrAgentHost.SendAndReceiveMessage(taskMessage);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

            var completed = await Task.WhenAny(delegationTask, timeoutTask);

            if (completed == delegationTask)
            {
                var agentResponse = await delegationTask;
                var responseText = agentResponse.Message ?? "";

                logger.LogInformation(
                    "Received response from {Agent} ({Length} chars)",
                    agentName, responseText.Length);

                return responseText;
            }

            // Timeout — the delegated agent is still processing, but we can't wait
            // any longer. Log and produce a meaningful fallback.
            logger.LogWarning(
                "Delegation to {Agent} timed out after {Timeout}s",
                agentName, timeoutSeconds);

            // Observe the late result to prevent unobserved task exceptions
            _ = delegationTask.ContinueWith(
                t => logger.LogWarning(t.Exception,
                    "Late delegation response from {Agent} faulted", agentName),
                TaskContinuationOptions.OnlyOnFaulted);

            return $"[Timeout] The {agentName} agent did not respond within " +
                   $"{timeoutSeconds} seconds. The request may still be processing.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delegation to {Agent} failed", agentName);
            return $"[Error] The {agentName} agent encountered an error: {ex.Message}";
        }
    }

    private async Task SendThinkingAsync(string message)
    {
        if (_lastClientHandle is null) return;

        var myHandle = fabrAgentHost.GetHandle();
        await fabrAgentHost.SendMessage(new AgentMessage
        {
            ToHandle = _lastClientHandle,
            FromHandle = myHandle,
            Kind = MessageKind.OneWay,
            MessageType = "thinking",
            Message = message
        });
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}

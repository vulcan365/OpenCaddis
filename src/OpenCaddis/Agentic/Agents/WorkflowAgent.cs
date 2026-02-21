using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("workflow")]
public class WorkflowAgent : FabrCoreAgentProxy
{
    private const string UserHandle = "opencaddis-user";
    private const string PlanStateKey = "workflow-plan";
    private const string EventsStateKey = "workflow-events";

    private IChatClient? _planningClient;
    private IChatClient? _executionClient;
    private string? _lastClientHandle;

    private List<AvailableAgentInfo> _availableAgents = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Initialization
    // ═══════════════════════════════════════════════════════════════════════

    public override async Task OnInitialize()
    {
        var modelConfig = config.Args?.GetValueOrDefault("ModelConfig") ?? "default";
        _planningClient = await GetChatClient(modelConfig);
        _executionClient = await GetChatClient(modelConfig);

        await DiscoverAvailableAgents();

        logger.LogInformation(
            "WorkflowAgent initialized with {AgentCount} available agents: {Agents}",
            _availableAgents.Count,
            string.Join(", ", _availableAgents.Select(a => a.AgentName)));
    }

    private async Task DiscoverAvailableAgents()
    {
        _availableAgents = [];

        var managedAgentsCsv = config.Args?.GetValueOrDefault("ManagedAgents") ?? "";
        var agentNames = managedAgentsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (agentNames.Count == 0)
        {
            logger.LogWarning("WorkflowAgent has no ManagedAgents configured");
            return;
        }

        foreach (var name in agentNames)
        {
            var handle = $"{UserHandle}:{name}";
            try
            {
                var health = await fabrcoreAgentHost.GetAgentHealth(handle, HealthDetailLevel.Detailed);
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
    //  OnMessage — Entry Point
    // ═══════════════════════════════════════════════════════════════════════

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var myHandle = fabrcoreAgentHost.GetHandle();

        if (message.FromHandle is not null && message.FromHandle != myHandle)
        {
            _lastClientHandle = message.FromHandle;
            ThinkingNotifier.SetClientHandle(myHandle, _lastClientHandle);
        }

        var response = message.Response();

        logger.LogInformation(
            "WorkflowAgent received message from {From} on channel '{Channel}'",
            message.FromHandle, message.Channel);
        logger.LogDebug(
            "WorkflowAgent message content: {Message}",
            Truncate(message.Message ?? "", 200));

        // ── Agent channel: delegated agent response ──
        if (string.Equals(message.Channel, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAgentResponse(message);
            response.Message = ""; // agent channel responses are processed internally
            return response;
        }

        // ── User channel: planning / status / control ──
        try
        {
            var plan = await GetStateAsync<WorkflowPlan>(PlanStateKey);
            var userMessage = message.Message ?? string.Empty;

            logger.LogInformation(
                "WorkflowAgent routing user message, current plan status: {Status}",
                plan?.Status.ToString() ?? "none");

            response.Message = plan?.Status switch
            {
                PlanStatus.Executing => await HandleUserMessageDuringExecution(userMessage, plan),
                PlanStatus.AwaitingApproval => await HandleUserApproval(userMessage, plan),
                PlanStatus.Paused => await HandlePausedState(userMessage, plan),
                _ => await HandlePlanningMessage(userMessage, plan)
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in WorkflowAgent.OnMessage");
            response.Message = $"An error occurred: {ex.Message}";
        }

        return response;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  User Channel Handlers
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<string> HandlePlanningMessage(string userMessage, WorkflowPlan? existingPlan)
    {
        await SendThinkingAsync("Planning...");

        if (_availableAgents.Count == 0)
            await DiscoverAvailableAgents();

        var agentCatalog = BuildAgentCatalog();
        var planOutput = await GeneratePlan(userMessage, agentCatalog);

        logger.LogInformation(
            "WorkflowAgent generated plan: goal='{Goal}', tasks={TaskCount}, missingInfo={HasMissingInfo}",
            Truncate(planOutput.Goal, 100), planOutput.Tasks.Count, !string.IsNullOrEmpty(planOutput.MissingInfo));

        if (!string.IsNullOrEmpty(planOutput.MissingInfo))
        {
            return $"I need some clarification before I can create a plan:\n\n{planOutput.MissingInfo}";
        }

        var plan = BuildPlanFromOutput(planOutput, userMessage);

        // Read max attempts from config
        if (int.TryParse(config.Args?.GetValueOrDefault("MaxTaskAttempts"), out var maxAttempts))
            plan.RunConfig.MaxAttemptsPerTask = maxAttempts;

        plan.Status = PlanStatus.AwaitingApproval;
        plan.UpdatedAt = DateTimeOffset.UtcNow;

        await PersistPlanAsync(plan);
        await RecordEvent(ExecutionEventType.UserApproved, plan.PlanId, message: "Plan created, awaiting approval");

        return FormatPlanForUser(plan);
    }

    private async Task<string> HandleUserApproval(string userMessage, WorkflowPlan plan)
    {
        var lower = userMessage.Trim().ToLowerInvariant();

        if (lower is "go" or "run" or "run it" or "execute" or "start" or "yes" or "approve" or "ok" or "okay" or "y")
        {
            logger.LogInformation("WorkflowAgent plan approved by user, starting execution");
            return await StartExecution(plan);
        }

        if (lower is "cancel" or "no" or "n" or "discard")
        {
            logger.LogInformation("WorkflowAgent plan cancelled by user");
            plan.Status = PlanStatus.Failed;
            plan.Notes = "Cancelled by user";
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistPlanAsync(plan);
            return "Plan cancelled. Send a new message to start fresh.";
        }

        // Treat as scope change — replan
        return await HandlePlanningMessage(userMessage, plan);
    }

    private async Task<string> HandleUserMessageDuringExecution(string userMessage, WorkflowPlan plan)
    {
        var lower = userMessage.Trim().ToLowerInvariant();

        if (lower is "status" or "status?" or "progress" or "progress?")
        {
            return FormatExecutionStatus(plan);
        }

        if (lower is "pause" or "stop")
        {
            plan.Status = PlanStatus.Paused;
            plan.UpdatedAt = DateTimeOffset.UtcNow;

            await PersistPlanAsync(plan);
            await RecordEvent(ExecutionEventType.UserPaused, plan.PlanId);
            return "Execution paused. Say **resume** to continue or send a new request to replan.";
        }

        if (lower is "cancel" or "abort")
        {
            plan.Status = PlanStatus.Failed;
            plan.Notes = "Cancelled by user during execution";
            plan.UpdatedAt = DateTimeOffset.UtcNow;

            await PersistPlanAsync(plan);
            return "Execution cancelled. Send a new message to start fresh.";
        }

        // Scope change during execution → trigger replan
        await SendThinkingAsync("Replanning...");
        return await TriggerReplan(plan, $"User changed requirements: {userMessage}");
    }

    private async Task<string> HandlePausedState(string userMessage, WorkflowPlan plan)
    {
        var lower = userMessage.Trim().ToLowerInvariant();

        if (lower is "resume" or "continue" or "go")
        {
            return await StartExecution(plan);
        }

        // New request — treat as replan
        return await HandlePlanningMessage(userMessage, plan);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Agent Channel Handler
    // ═══════════════════════════════════════════════════════════════════════

    private async Task HandleAgentResponse(AgentMessage message)
    {
        var plan = await GetStateAsync<WorkflowPlan>(PlanStateKey);
        if (plan is null || plan.Status != PlanStatus.Executing)
        {
            logger.LogWarning("Received agent response but no active plan");
            return;
        }

        var currentTask = plan.Tasks.FirstOrDefault(t => t.TaskId == plan.CurrentTaskId);
        if (currentTask is null)
        {
            logger.LogWarning("Received agent response but no current task");
            return;
        }

        var agentResponse = message.Message ?? "";
        logger.LogInformation("Received response from {Agent} for task '{Task}'",
            message.FromHandle, currentTask.Title);

        await RecordEvent(ExecutionEventType.TaskMessageSent, plan.PlanId, currentTask.TaskId,
            $"Response from {message.FromHandle}");

        // Evaluate acceptance criteria
        await SendThinkingAsync($"Evaluating: {currentTask.Title}...");
        var evaluation = await EvaluateAcceptanceCriteria(currentTask, agentResponse, plan.Goal);

        if (evaluation.Satisfied)
        {
            currentTask.Status = WorkflowTaskStatus.Completed;
            currentTask.ResolutionSummary = agentResponse;
            plan.UpdatedAt = DateTimeOffset.UtcNow;

            await RecordEvent(ExecutionEventType.TaskCompleted, plan.PlanId, currentTask.TaskId,
                $"Completed: {evaluation.Reasoning}");

            await PersistPlanAsync(plan);

            // Continue to next task
            await RunExecutionTick(plan);
        }
        else
        {
            await HandleTaskFailure(plan, currentTask, agentResponse, evaluation);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Execution Loop
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<string> StartExecution(WorkflowPlan plan)
    {
        // Validate plan
        var validation = ValidatePlan(plan);
        if (validation is not null)
        {
            logger.LogWarning("WorkflowAgent plan validation failed: {Validation}", validation);
            return validation;
        }

        logger.LogInformation(
            "WorkflowAgent starting execution of plan '{Goal}' with {TaskCount} tasks",
            Truncate(plan.Goal, 100), plan.Tasks.Count);

        plan.Status = PlanStatus.Executing;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistPlanAsync(plan);
        await RecordEvent(ExecutionEventType.ExecutionStarted, plan.PlanId);

        // Execute inline within the grain turn. Orleans grains are single-threaded;
        // Task.Run would escape the activation context and crash on state access.
        // SendAndReceiveMessage yields back to Orleans while waiting for delegated
        // agents, and thinking/status OneWay messages keep the user informed.
        try
        {
            await RunExecutionTick(plan);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Execution loop failed");
            plan = await GetStateAsync<WorkflowPlan>(PlanStateKey) ?? plan;
            plan.Status = PlanStatus.Failed;
            plan.Notes = $"Execution error: {ex.Message}";
            await PersistPlanAsync(plan);
            return $"Execution failed: {ex.Message}";
        }

        // Reload final state — RunExecutionTick updates status as it goes
        plan = await GetStateAsync<WorkflowPlan>(PlanStateKey) ?? plan;

        logger.LogInformation(
            "WorkflowAgent execution finished with status {Status}",
            plan.Status);

        return plan.Status switch
        {
            PlanStatus.Completed => FormatCompletionSummary(plan),
            PlanStatus.Stalled => FormatExecutionStatus(plan),
            PlanStatus.Failed => $"Workflow failed: {plan.Notes}",
            _ => FormatExecutionStatus(plan)
        };
    }

    private async Task RunExecutionTick(WorkflowPlan plan)
    {
        // Reload plan from state in case it was updated
        plan = await GetStateAsync<WorkflowPlan>(PlanStateKey) ?? plan;

        if (plan.Status != PlanStatus.Executing)
            return;

        // Find next runnable task
        var nextTask = FindNextRunnableTask(plan);

        if (nextTask is null)
        {
            // Check if all tasks done
            if (plan.Tasks.All(t =>
                t.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Skipped))
            {
                plan.Status = PlanStatus.Completed;
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                await PersistPlanAsync(plan);
                await RecordEvent(ExecutionEventType.ExecutionCompleted, plan.PlanId);
                return;
            }

            // Check for stall
            var hasInProgress = plan.Tasks.Any(t => t.Status == WorkflowTaskStatus.InProgress);
            if (!hasInProgress)
            {
                plan.Status = PlanStatus.Stalled;
                plan.Notes = "No runnable tasks and none in progress — possible dependency deadlock";
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                await PersistPlanAsync(plan);
                await RecordEvent(ExecutionEventType.StalledDetected, plan.PlanId);
            }
            return;
        }

        // Start the task
        await ExecuteTask(plan, nextTask);
    }

    private async Task ExecuteTask(WorkflowPlan plan, WorkflowTaskItem task)
    {
        task.Status = WorkflowTaskStatus.InProgress;
        task.AttemptCount++;
        plan.CurrentTaskId = task.TaskId;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistPlanAsync(plan);

        await RecordEvent(ExecutionEventType.TaskStarted, plan.PlanId, task.TaskId,
            $"Starting task '{task.Title}' (attempt {task.AttemptCount}), assigned to {task.AssignedAgentName}");

        await SendThinkingAsync($"Running: {task.Title}...");

        // Build task instruction message
        var instruction = BuildTaskInstruction(plan, task);

        // Resolve the agent handle
        var agentInfo = _availableAgents.FirstOrDefault(a =>
            string.Equals(a.AgentName, task.AssignedAgentName, StringComparison.OrdinalIgnoreCase));

        if (agentInfo is null)
        {
            task.Status = WorkflowTaskStatus.Failed;
            task.LastError = $"Agent '{task.AssignedAgentName}' not found in available agents";
            await PersistPlanAsync(plan);
            await HandleTaskFailure(plan, task, "", new AcceptanceEvaluation
            {
                Satisfied = false,
                Reasoning = task.LastError
            });
            return;
        }

        var myHandle = fabrcoreAgentHost.GetHandle();

        try
        {
            // Send as request to the delegated agent on the "agent" channel
            var taskMessage = new AgentMessage
            {
                ToHandle = agentInfo.Handle,
                FromHandle = myHandle,
                Channel = "agent",
                Kind = MessageKind.Request,
                MessageType = "task",
                Message = instruction
            };

            await RecordEvent(ExecutionEventType.TaskMessageSent, plan.PlanId, task.TaskId,
                $"Sent task to {agentInfo.Handle}");

            var agentResponse = await fabrcoreAgentHost.SendAndReceiveMessage(taskMessage);

            // Process the response inline (since SendAndReceiveMessage is synchronous)
            var responseText = agentResponse.Message ?? "";
            logger.LogInformation("Received response from {Agent} for task '{Task}'",
                agentInfo.Handle, task.Title);

            // Evaluate acceptance criteria
            await SendThinkingAsync($"Evaluating: {task.Title}...");
            var evaluation = await EvaluateAcceptanceCriteria(task, responseText, plan.Goal);

            logger.LogInformation(
                "WorkflowAgent acceptance evaluation for task '{Task}': satisfied={Satisfied}, reasoning={Reasoning}",
                task.Title, evaluation.Satisfied, Truncate(evaluation.Reasoning, 200));

            if (evaluation.Satisfied)
            {
                task.Status = WorkflowTaskStatus.Completed;
                task.ResolutionSummary = responseText;
                plan.UpdatedAt = DateTimeOffset.UtcNow;

                await RecordEvent(ExecutionEventType.TaskCompleted, plan.PlanId, task.TaskId,
                    $"Completed: {evaluation.Reasoning}");

                await PersistPlanAsync(plan);

                // Continue to next task
                await RunExecutionTick(plan);
            }
            else
            {
                await HandleTaskFailure(plan, task, responseText, evaluation);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute task '{Task}' on agent '{Agent}'",
                task.Title, agentInfo.Handle);

            task.LastError = ex.Message;
            await HandleTaskFailure(plan, task, "", new AcceptanceEvaluation
            {
                Satisfied = false,
                Reasoning = $"Exception: {ex.Message}"
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Task Failure & Retry
    // ═══════════════════════════════════════════════════════════════════════

    private async Task HandleTaskFailure(
        WorkflowPlan plan, WorkflowTaskItem task,
        string agentResponse, AcceptanceEvaluation evaluation)
    {
        task.LastError = evaluation.Reasoning;

        await RecordEvent(ExecutionEventType.TaskFailed, plan.PlanId, task.TaskId,
            $"Failed (attempt {task.AttemptCount}): {evaluation.Reasoning}");

        // Resolve the issue
        var resolution = await ResolveTaskIssue(plan, task, agentResponse, evaluation);

        switch (resolution.Decision)
        {
            case "retry" when task.AttemptCount < plan.RunConfig.MaxAttemptsPerTask:
                logger.LogInformation("Retrying task '{Task}' with improved instruction", task.Title);
                task.Status = WorkflowTaskStatus.Pending;
                if (!string.IsNullOrEmpty(resolution.ImprovedInstruction))
                    task.Description = resolution.ImprovedInstruction;
                await PersistPlanAsync(plan);
                await RunExecutionTick(plan);
                break;

            case "blocked":
                task.Status = WorkflowTaskStatus.Blocked;
                await PersistPlanAsync(plan);
                await NotifyUser($"Task **{task.Title}** is blocked: {resolution.Reasoning}\n\nSend instructions to help unblock, or say **cancel** to abort.");
                break;

            case "replan":
                await TriggerReplan(plan, $"Task '{task.Title}' failed: {resolution.Reasoning}");
                break;

            default: // stall or exceeded retries
                task.Status = WorkflowTaskStatus.Failed;
                plan.Status = PlanStatus.Stalled;
                plan.Notes = $"Task '{task.Title}' failed after {task.AttemptCount} attempts: {resolution.Reasoning}";
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                await PersistPlanAsync(plan);
                await RecordEvent(ExecutionEventType.StalledDetected, plan.PlanId, task.TaskId);
                await NotifyUser($"Execution stalled on task **{task.Title}** after {task.AttemptCount} attempt(s).\n\nReason: {resolution.Reasoning}\n\nSend a new message to replan, or **cancel** to stop.");
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Replanning
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<string> TriggerReplan(WorkflowPlan plan, string reason)
    {
        logger.LogInformation(
            "WorkflowAgent triggering replan (v{Version}): {Reason}",
            plan.PlanVersion, Truncate(reason, 200));

        if (plan.PlanVersion >= plan.RunConfig.MaxTotalReplans + 1)
        {
            plan.Status = PlanStatus.Failed;
            plan.Notes = $"Exceeded maximum replans ({plan.RunConfig.MaxTotalReplans})";
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            await PersistPlanAsync(plan);
            return $"Unable to complete the workflow — exceeded maximum replan attempts.\n\nLast issue: {reason}";
        }

        await RecordEvent(ExecutionEventType.ReplanTriggered, plan.PlanId, message: reason);

        var agentCatalog = BuildAgentCatalog();
        var taskStatusSummary = string.Join("\n", plan.Tasks.Select(t =>
            $"- {t.Title}: {t.Status}" + (t.LastError is not null ? $" (error: {t.LastError})" : "")));

        var replanOutput = await Replan(plan, reason, agentCatalog, taskStatusSummary);

        // Build updated plan preserving completed tasks
        var completedTasks = plan.Tasks
            .Where(t => t.Status == WorkflowTaskStatus.Completed)
            .ToList();

        var newTasks = replanOutput.Tasks.Select(t => new WorkflowTaskItem
        {
            Title = t.Title,
            Description = t.Description,
            AssignedAgentName = t.AssignedAgentName,
            Inputs = t.Inputs,
            Dependencies = t.Dependencies,
            AcceptanceCriteria = t.AcceptanceCriteria
        }).ToList();

        plan.Goal = replanOutput.Goal;
        plan.Tasks = [.. completedTasks, .. newTasks];
        plan.PlanVersion++;
        plan.CurrentTaskId = null;
        plan.Notes = replanOutput.Notes;
        plan.UpdatedAt = DateTimeOffset.UtcNow;

        await RecordEvent(ExecutionEventType.Replanned, plan.PlanId,
            message: replanOutput.ChangeSummary);

        if (replanOutput.RequiresUserApproval)
        {
            plan.Status = PlanStatus.AwaitingApproval;
            await PersistPlanAsync(plan);
            return $"I've revised the plan (v{plan.PlanVersion}):\n\n{FormatPlanForUser(plan)}\n\n**Changes:** {replanOutput.ChangeSummary}";
        }

        plan.Status = PlanStatus.Executing;
        await PersistPlanAsync(plan);

        // Continue execution inline within the grain turn
        await RunExecutionTick(plan);

        plan = await GetStateAsync<WorkflowPlan>(PlanStateKey) ?? plan;

        var statusSuffix = plan.Status == PlanStatus.Completed
            ? "\n\n" + FormatCompletionSummary(plan)
            : "\n\n" + FormatExecutionStatus(plan);

        return $"Plan revised (v{plan.PlanVersion}). {replanOutput.ChangeSummary}{statusSuffix}";
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LLM Calls — Planning Session
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<PlanGenerationOutput> GeneratePlan(string userMessage, string agentCatalog)
    {
        var systemPrompt = $"""
            You are a workflow planner. Given a user request and a catalog of available agents,
            produce a structured execution plan.

            ## Available Agents
            {agentCatalog}

            ## Rules
            - Set a clear, concise goal summarizing the user's intent.
            - Break the request into discrete tasks, each assignable to one agent.
            - Each task MUST have acceptance criteria — specific, verifiable checks.
            - Tasks may have dependencies (by title reference) indicating execution order.
            - Only assign tasks to agents from the catalog above.
            - If the request is ambiguous or missing critical info, set missingInfo instead of tasks.
            - Keep tasks focused — one agent, one objective per task.
            - Order tasks logically with dependencies.
            """;

        return await ExtractJsonAsync<PlanGenerationOutput>(systemPrompt, userMessage);
    }

    private async Task<ReplanOutput> Replan(
        WorkflowPlan plan, string reason, string agentCatalog, string taskStatusSummary)
    {
        var systemPrompt = $"""
            You are a workflow replanner. A plan is being revised because of issues during execution.

            ## Available Agents
            {agentCatalog}

            ## Rules
            - Preserve tasks that are already completed — do NOT re-include them.
            - Only output NEW or MODIFIED tasks that still need to be done.
            - Each task MUST have acceptance criteria.
            - If the changes are major (new goal, many new tasks), set requiresUserApproval to true.
            - If the changes are minor (retry, reassignment), set requiresUserApproval to false.
            - Provide a changeSummary explaining what changed and why.
            """;

        var userPrompt = $"""
            ## Original Goal
            {plan.Goal}

            ## Current Task Status
            {taskStatusSummary}

            ## Reason for Replan
            {reason}

            ## Plan Version
            {plan.PlanVersion}
            """;

        return await ExtractJsonAsync<ReplanOutput>(systemPrompt, userPrompt);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LLM Calls — Execution Session
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<AcceptanceEvaluation> EvaluateAcceptanceCriteria(
        WorkflowTaskItem task, string agentResponse, string planGoal)
    {
        var systemPrompt = """
            You are an acceptance criteria evaluator. Given a task's criteria and the agent's response,
            determine whether the criteria are satisfied.

            ## Rules
            - Set satisfied to true ONLY if ALL criteria are met.
            - Be pragmatic: imperfect but substantially correct results should pass.
            - List any unmet criteria specifically.
            - Provide clear reasoning.
            """;

        var userPrompt = $"""
            ## Plan Goal
            {planGoal}

            ## Task
            Title: {task.Title}
            Description: {task.Description}

            ## Acceptance Criteria
            {string.Join("\n", task.AcceptanceCriteria.Select(c => $"- {c}"))}

            ## Agent Response
            {Truncate(agentResponse, 4000)}
            """;

        return await ExtractJsonAsync<AcceptanceEvaluation>(systemPrompt, userPrompt);
    }

    private async Task<TaskIssueResolution> ResolveTaskIssue(
        WorkflowPlan plan, WorkflowTaskItem task,
        string agentResponse, AcceptanceEvaluation evaluation)
    {
        var systemPrompt = $"""
            You are a task issue resolver. A task failed to meet its acceptance criteria.
            Decide the best course of action.

            ## Rules
            - "retry": The task can succeed with improved instructions. Provide improvedInstruction.
            - "blocked": The task needs external input or permissions we don't have.
            - "replan": The task itself is wrong, dependencies are wrong, or the assignment is wrong.
            - "stall": No progress is possible.

            ## Constraints
            - This task has been attempted {task.AttemptCount} time(s).
            - Maximum attempts allowed: {plan.RunConfig.MaxAttemptsPerTask}.
            - If at max attempts, prefer "replan" or "stall" over "retry".
            """;

        var userPrompt = $"""
            ## Plan Goal
            {plan.Goal}

            ## Failed Task
            Title: {task.Title}
            Description: {task.Description}
            Assigned Agent: {task.AssignedAgentName}
            Attempt: {task.AttemptCount}
            Last Error: {task.LastError}

            ## Unmet Criteria
            {string.Join("\n", evaluation.UnmetCriteria.Select(c => $"- {c}"))}

            ## Agent Response
            {Truncate(agentResponse, 2000)}

            ## Evaluator Reasoning
            {evaluation.Reasoning}
            """;

        return await ExtractJsonAsync<TaskIssueResolution>(systemPrompt, userPrompt);
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

        var response = await _executionClient!.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userPrompt)], chatOptions);

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

    private static string BuildTaskInstruction(WorkflowPlan plan, WorkflowTaskItem task)
    {
        var inputsBlock = task.Inputs.Count > 0
            ? "\n\n## Inputs\n" + string.Join("\n", task.Inputs.Select(kv => $"- {kv.Key}: {kv.Value}"))
            : "";

        var criteriaBlock = task.AcceptanceCriteria.Count > 0
            ? "\n\n## Acceptance Criteria\n" + string.Join("\n", task.AcceptanceCriteria.Select(c => $"- {c}"))
            : "";

        // Gather completed dependency results as context
        var dependencyContext = "";
        if (task.Dependencies.Count > 0)
        {
            var depResults = plan.Tasks
                .Where(t => task.Dependencies.Contains(t.Title)
                    && t.Status == WorkflowTaskStatus.Completed
                    && t.ResolutionSummary is not null)
                .Select(t => $"### {t.Title}\n{Truncate(t.ResolutionSummary!, 1000)}")
                .ToList();

            if (depResults.Count > 0)
                dependencyContext = "\n\n## Results from Previous Tasks\n" + string.Join("\n\n", depResults);
        }

        return $"""
            ## Goal
            {plan.Goal}

            ## Your Task
            **{task.Title}**
            {task.Description}
            {inputsBlock}{criteriaBlock}{dependencyContext}

            ## Instructions
            - Complete this task thoroughly.
            - Return your results clearly.
            - If you are blocked or cannot complete the task, explain what is missing and what you tried.
            """;
    }

    private WorkflowPlan BuildPlanFromOutput(PlanGenerationOutput output, string userMessage)
    {
        var plan = new WorkflowPlan
        {
            Goal = output.Goal,
            UserHandle = _lastClientHandle ?? UserHandle,
            Notes = output.Notes
        };

        foreach (var taskOutput in output.Tasks)
        {
            plan.Tasks.Add(new WorkflowTaskItem
            {
                Title = taskOutput.Title,
                Description = taskOutput.Description,
                AssignedAgentName = taskOutput.AssignedAgentName,
                Inputs = taskOutput.Inputs,
                Dependencies = taskOutput.Dependencies,
                AcceptanceCriteria = taskOutput.AcceptanceCriteria
            });
        }

        return plan;
    }

    private static WorkflowTaskItem? FindNextRunnableTask(WorkflowPlan plan)
    {
        return plan.Tasks.FirstOrDefault(t =>
            t.Status == WorkflowTaskStatus.Pending
            && t.Dependencies.All(dep =>
                plan.Tasks.Any(d =>
                    string.Equals(d.Title, dep, StringComparison.OrdinalIgnoreCase)
                    && d.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Skipped)));
    }

    private static string? ValidatePlan(WorkflowPlan plan)
    {
        if (plan.Tasks.Count == 0)
            return "Cannot execute — the plan has no tasks.";

        var unassigned = plan.Tasks
            .Where(t => string.IsNullOrEmpty(t.AssignedAgentName)
                && t.Status is WorkflowTaskStatus.Pending)
            .ToList();
        if (unassigned.Count > 0)
            return $"Cannot execute — these tasks have no agent assigned: {string.Join(", ", unassigned.Select(t => t.Title))}";

        var noCriteria = plan.Tasks
            .Where(t => t.AcceptanceCriteria.Count == 0
                && t.Status is WorkflowTaskStatus.Pending)
            .ToList();
        if (noCriteria.Count > 0)
            return $"Cannot execute — these tasks have no acceptance criteria: {string.Join(", ", noCriteria.Select(t => t.Title))}";

        return null;
    }

    private static string FormatPlanForUser(WorkflowPlan plan)
    {
        var taskLines = plan.Tasks.Select((t, i) =>
        {
            var deps = t.Dependencies.Count > 0
                ? $" (after: {string.Join(", ", t.Dependencies)})"
                : "";
            var criteria = string.Join("; ", t.AcceptanceCriteria);
            return $"{i + 1}. **{t.Title}** → _{t.AssignedAgentName}_{deps}\n   Criteria: {criteria}";
        });

        return $"""
            **Goal:** {plan.Goal}

            **Tasks:**
            {string.Join("\n", taskLines)}

            Say **go** to execute, or describe changes to revise the plan.
            """;
    }

    private static string FormatExecutionStatus(WorkflowPlan plan)
    {
        var taskLines = plan.Tasks.Select((t, i) =>
        {
            var status = t.Status switch
            {
                WorkflowTaskStatus.Completed => "[done]",
                WorkflowTaskStatus.InProgress => "[running]",
                WorkflowTaskStatus.Failed => "[failed]",
                WorkflowTaskStatus.Blocked => "[blocked]",
                WorkflowTaskStatus.Skipped => "[skipped]",
                _ => "[pending]"
            };
            return $"{i + 1}. {status} {t.Title} → {t.AssignedAgentName}";
        });

        return $"""
            **Goal:** {plan.Goal} (v{plan.PlanVersion})
            **Status:** {plan.Status}

            {string.Join("\n", taskLines)}
            """;
    }

    private static string FormatCompletionSummary(WorkflowPlan plan)
    {
        var results = plan.Tasks
            .Where(t => t.Status == WorkflowTaskStatus.Completed && t.ResolutionSummary is not null)
            .Select(t => $"### {t.Title}\n{t.ResolutionSummary}")
            .ToList();

        var summary = results.Count > 0
            ? string.Join("\n\n", results)
            : "All tasks completed.";

        return $"**Workflow complete!** Goal: {plan.Goal}\n\n{summary}";
    }

    private async Task PersistPlanAsync(WorkflowPlan plan)
    {
        SetState(PlanStateKey, plan);
        await FlushStateAsync();
    }

    private async Task RecordEvent(
        ExecutionEventType type, string planId,
        string? taskId = null, string? message = null)
    {
        var events = await GetStateAsync<List<ExecutionEvent>>(EventsStateKey) ?? [];
        events.Add(new ExecutionEvent
        {
            Type = type,
            PlanId = planId,
            TaskId = taskId,
            Message = message ?? type.ToString()
        });

        // Keep last 200 events
        if (events.Count > 200)
            events = events[^200..];

        SetState(EventsStateKey, events);
        await FlushStateAsync();
    }

    private async Task SendThinkingAsync(string message)
    {
        if (_lastClientHandle is null) return;

        var myHandle = fabrcoreAgentHost.GetHandle();
        await fabrcoreAgentHost.SendMessage(new AgentMessage
        {
            ToHandle = _lastClientHandle,
            FromHandle = myHandle,
            Kind = MessageKind.OneWay,
            MessageType = "thinking",
            Message = message
        });
    }

    private async Task NotifyUser(string message)
    {
        if (_lastClientHandle is null) return;

        var myHandle = fabrcoreAgentHost.GetHandle();
        await fabrcoreAgentHost.SendMessage(new AgentMessage
        {
            ToHandle = _lastClientHandle,
            FromHandle = myHandle,
            Kind = MessageKind.OneWay,
            MessageType = "status",
            Message = message
        });
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}

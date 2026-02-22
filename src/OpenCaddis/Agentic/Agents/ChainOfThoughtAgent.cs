using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenCaddis.Agentic.Agents;

[AgentAlias("chainofthought")]
public class ChainOfThoughtAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;
    private IChatClient? _chatClient;
    private List<AITool> _tools = [];
    private string? _lastClientHandle;

    // Own logger so Debug output is categorized under OpenCaddis.Agentic.Agents
    // (base class logger is FabrCore.Sdk.FabrCoreAgentProxy)
    private readonly ILogger<ChainOfThoughtAgent> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public ChainOfThoughtAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
        _log = serviceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ChainOfThoughtAgent>();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Initialization
    // ═══════════════════════════════════════════════════════════════════════

    public override async Task OnInitialize()
    {
        var modelConfigName = config.Args?.GetValueOrDefault("ModelConfig") ?? "default";
        var networkTimeout = GetConfigInt("NetworkTimeoutSeconds", 180);
        _tools = await ResolveConfiguredToolsAsync();

        // Main agent uses higher timeout — CoT prompts accumulate context and are larger
        _chatClient = await GetChatClient(modelConfigName, networkTimeout);

        var result = await CreateChatClientAgent(
            modelConfigName,
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: _tools
        );

        _agent = result.Agent;
        _session = result.Session;

        _log.LogInformation(
            "ChainOfThoughtAgent '{Handle}' initialized with model config '{ModelConfig}', {ToolCount} tools, {Timeout}s timeout",
            config.Handle, modelConfigName, _tools.Count, networkTimeout);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  OnMessage — Entry Point
    // ═══════════════════════════════════════════════════════════════════════

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var myHandle = fabrcoreAgentHost.GetHandle();

        // Filter out non-user messages (same pattern as DelegateAgent)
        if (!string.IsNullOrEmpty(message.Channel)
            || message.MessageType is "thinking" or "status"
            || message.Kind == MessageKind.OneWay)
        {
            _log.LogDebug(
                "ChainOfThoughtAgent ignoring message: Kind={Kind}, Channel='{Channel}', " +
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

        _log.LogInformation(
            "ChainOfThoughtAgent received message from {From}",
            message.FromHandle);
        _log.LogDebug(
            "ChainOfThoughtAgent message content: {Message}",
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
            response.Message = await RunChainOfThought(userMessage);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ChainOfThoughtAgent error processing message");
            response.Message = $"Error: {ex.Message}";
        }

        return response;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Core Loop
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<string> RunChainOfThought(string userMessage)
    {
        var totalSw = Stopwatch.StartNew();
        var phaseSw = new Stopwatch();
        var llmCalls = new LlmCallCounter();

        var maxLoops = GetConfigInt("MaxLoops", 8);
        var maxSteps = GetConfigInt("MaxSteps", 5);
        var initialThreshold = GetConfigDouble("InitialConfidenceThreshold", 0.75);
        var minThreshold = GetConfigDouble("MinConfidenceThreshold", 0.40);

        _log.LogDebug(
            "CoT START — config: maxLoops={MaxLoops}, maxSteps={MaxSteps}, " +
            "initialThreshold={InitialThreshold:F2}, minThreshold={MinThreshold:F2}, " +
            "tools={ToolCount}, input={InputLength}chars",
            maxLoops, maxSteps, initialThreshold, minThreshold, _tools.Count, userMessage.Length);

        var state = new CoTState
        {
            OriginalRequest = userMessage,
            MaxLoops = maxLoops
        };

        // ── Assess ──────────────────────────────────────────────────────
        await SendThinkingAsync("Assessing complexity...");
        phaseSw.Restart();
        var assessment = await Assess(userMessage);
        llmCalls.Increment();
        phaseSw.Stop();

        _log.LogDebug(
            "CoT ASSESS [{Elapsed}ms] — complexity={Complexity}, confidence={Confidence:F2}, " +
            "requiresPlanning={RequiresPlanning}, hasDirectAnswer={HasDirectAnswer}, " +
            "reasoning={Reasoning}",
            phaseSw.ElapsedMilliseconds, assessment.Complexity, assessment.Confidence,
            assessment.RequiresPlanning, assessment.DirectAnswer is { Length: > 0 },
            Truncate(assessment.Reasoning, 200));

        state.WorkingMemory.Add(new ThoughtEntry
        {
            Type = ThoughtType.Assess,
            Content = $"Complexity: {assessment.Complexity}, Confidence: {assessment.Confidence:F2}, RequiresPlanning: {assessment.RequiresPlanning}",
            LoopIteration = 0,
            ConfidenceSnapshot = assessment.Confidence
        });

        // ── Fast path ───────────────────────────────────────────────────
        if (!assessment.RequiresPlanning
            && assessment.Confidence >= initialThreshold
            && assessment.DirectAnswer is { Length: > 0 })
        {
            _log.LogDebug(
                "CoT FAST PATH — complexity={Complexity}, confidence={Confidence:F2} >= threshold={Threshold:F2}",
                assessment.Complexity, assessment.Confidence, initialThreshold);

            phaseSw.Restart();
            var fastResult = await _agent!.RunAsync(userMessage, _session);
            llmCalls.Increment();
            phaseSw.Stop();
            totalSw.Stop();

            _log.LogDebug(
                "CoT FAST PATH COMPLETE [{PhaseMs}ms] — totalTime={TotalMs}ms, llmCalls={LlmCalls}, " +
                "responseLength={ResponseLength}chars",
                phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, llmCalls.Count,
                (fastResult.Text ?? assessment.DirectAnswer).Length);

            return fastResult.Text ?? assessment.DirectAnswer;
        }

        _log.LogDebug(
            "CoT ENTERING LOOP — complexity={Complexity}, confidence={Confidence:F2} < threshold={Threshold:F2}, " +
            "requiresPlanning={RequiresPlanning}",
            assessment.Complexity, assessment.Confidence, initialThreshold, assessment.RequiresPlanning);

        // ── Main loop ───────────────────────────────────────────────────
        for (var loop = 0; loop < maxLoops; loop++)
        {
            var loopSw = Stopwatch.StartNew();
            state.LoopCount = loop + 1;
            var threshold = ComputeThreshold(loop, maxLoops, initialThreshold, minThreshold);

            _log.LogDebug("CoT LOOP {Loop}/{MaxLoops} START — threshold={Threshold:F2}",
                loop + 1, maxLoops, threshold);

            // Plan or replan when no actionable steps remain
            var needsPlan = state.Plan.Count == 0
                || state.Plan.All(s => s.Status is StepStatus.Completed or StepStatus.Failed or StepStatus.Skipped);

            if (needsPlan)
            {
                if (loop == 0)
                {
                    // ── Plan ────────────────────────────────────────────
                    await SendThinkingAsync("Planning reasoning steps...");
                    phaseSw.Restart();
                    var planOutput = await Plan(userMessage, assessment, state);
                    llmCalls.Increment();
                    phaseSw.Stop();

                    state.Plan = planOutput.Steps.Select(s => new PlanStep
                    {
                        Id = s.Id,
                        Description = s.Description,
                        RequiresTools = s.RequiresTools,
                        CanParallelize = s.CanParallelize,
                        DependsOn = s.DependsOn,
                        PromptToExecute = s.PromptToExecute
                    }).ToList();

                    ComputeExecutionLayers(state.Plan);

                    _log.LogDebug("CoT PLAN [{Elapsed}ms] — {StepCount} steps, rationale={Rationale}",
                        phaseSw.ElapsedMilliseconds, state.Plan.Count, Truncate(planOutput.PlanRationale, 200));

                    foreach (var s in state.Plan)
                    {
                        _log.LogDebug(
                            "CoT PLAN STEP [{Id}] layer={Layer}, parallel={Parallel}, tools={Tools}, " +
                            "dependsOn=[{DependsOn}], desc={Desc}",
                            s.Id, s.ExecutionLayer, s.CanParallelize, s.RequiresTools,
                            string.Join(",", s.DependsOn), Truncate(s.Description, 120));
                    }

                    state.WorkingMemory.Add(new ThoughtEntry
                    {
                        Type = ThoughtType.Plan,
                        Content = $"Created {state.Plan.Count} steps",
                        LoopIteration = loop,
                        ConfidenceSnapshot = state.ConfidenceScore
                    });
                }
                else
                {
                    // ── Replan ──────────────────────────────────────────
                    await SendThinkingAsync($"Replanning (loop {loop + 1}/{maxLoops})...");
                    phaseSw.Restart();
                    var replanOutput = await Replan(state);
                    llmCalls.Increment();
                    phaseSw.Stop();

                    _log.LogDebug(
                        "CoT REPLAN [{Elapsed}ms] — keeping=[{Keeping}], newSteps={NewCount}, " +
                        "rationale={Rationale}",
                        phaseSw.ElapsedMilliseconds,
                        string.Join(",", replanOutput.StepsToKeep),
                        replanOutput.RevisedSteps.Count,
                        Truncate(replanOutput.ReplanRationale, 200));

                    // Keep completed steps the replan says to keep
                    var keptIds = new HashSet<string>(replanOutput.StepsToKeep);
                    state.Plan = state.Plan
                        .Where(s => s.Status == StepStatus.Completed && keptIds.Contains(s.Id))
                        .ToList();

                    state.Plan.AddRange(replanOutput.RevisedSteps.Select(s => new PlanStep
                    {
                        Id = s.Id,
                        Description = s.Description,
                        RequiresTools = s.RequiresTools,
                        CanParallelize = s.CanParallelize,
                        DependsOn = s.DependsOn,
                        PromptToExecute = s.PromptToExecute
                    }));

                    ComputeExecutionLayers(state.Plan);

                    foreach (var s in state.Plan.Where(s => s.Status == StepStatus.Pending))
                    {
                        _log.LogDebug(
                            "CoT REPLAN STEP [{Id}] layer={Layer}, parallel={Parallel}, tools={Tools}, desc={Desc}",
                            s.Id, s.ExecutionLayer, s.CanParallelize, s.RequiresTools,
                            Truncate(s.Description, 120));
                    }

                    state.WorkingMemory.Add(new ThoughtEntry
                    {
                        Type = ThoughtType.Replan,
                        Content = $"Replanned: {replanOutput.ReplanRationale}",
                        LoopIteration = loop,
                        ConfidenceSnapshot = state.ConfidenceScore
                    });
                }
            }
            else
            {
                _log.LogDebug("CoT LOOP {Loop} — skipping plan, {PendingCount} pending steps remain",
                    loop + 1, state.Plan.Count(s => s.Status == StepStatus.Pending));
            }

            // ── Execute ─────────────────────────────────────────────────
            var pendingCount = state.Plan.Count(s => s.Status == StepStatus.Pending);
            await SendThinkingAsync($"Executing {pendingCount} step(s) (loop {loop + 1}/{maxLoops})...");
            phaseSw.Restart();
            var preExecLlmCalls = llmCalls.Count;
            await ExecuteSteps(userMessage, state, llmCalls);
            phaseSw.Stop();

            var stepSummary = string.Join(", ", state.Plan.Select(s => $"{s.Id}:{s.Status}"));
            _log.LogDebug(
                "CoT EXECUTE [{Elapsed}ms] — llmCalls={StepCalls}, stepStatuses=[{StepSummary}]",
                phaseSw.ElapsedMilliseconds, llmCalls.Count - preExecLlmCalls, stepSummary);

            // ── Synthesize ──────────────────────────────────────────────
            await SendThinkingAsync("Synthesizing answer...");
            phaseSw.Restart();
            var synthesis = await Synthesize(userMessage, state);
            llmCalls.Increment();
            phaseSw.Stop();

            state.CurrentAnswer = synthesis.Answer;
            state.ConfidenceScore = synthesis.Confidence;

            _log.LogDebug(
                "CoT SYNTHESIZE [{Elapsed}ms] — confidence={Confidence:F2}, threshold={Threshold:F2}, " +
                "gaps=[{Gaps}], answerLength={AnswerLength}chars, reasoning={Reasoning}",
                phaseSw.ElapsedMilliseconds, synthesis.Confidence, threshold,
                string.Join(", ", synthesis.GapsIdentified),
                synthesis.Answer.Length,
                Truncate(synthesis.Reasoning, 200));

            state.WorkingMemory.Add(new ThoughtEntry
            {
                Type = ThoughtType.Synthesize,
                Content = $"Confidence: {synthesis.Confidence:F2}" +
                    (synthesis.GapsIdentified.Count > 0
                        ? $", Gaps: [{string.Join(", ", synthesis.GapsIdentified)}]"
                        : ""),
                LoopIteration = loop,
                ConfidenceSnapshot = synthesis.Confidence
            });

            loopSw.Stop();

            // Exit loop if confidence meets the decaying threshold
            if (synthesis.Confidence >= threshold)
            {
                _log.LogDebug(
                    "CoT LOOP {Loop} DONE [{Elapsed}ms] — confidence {Confidence:F2} >= threshold {Threshold:F2}, EXITING",
                    loop + 1, loopSw.ElapsedMilliseconds, synthesis.Confidence, threshold);
                break;
            }

            _log.LogDebug(
                "CoT LOOP {Loop} DONE [{Elapsed}ms] — confidence {Confidence:F2} < threshold {Threshold:F2}, CONTINUING",
                loop + 1, loopSw.ElapsedMilliseconds, synthesis.Confidence, threshold);

            // Mark remaining pending steps as skipped so the next iteration replans
            foreach (var step in state.Plan.Where(s => s.Status == StepStatus.Pending))
                step.Status = StepStatus.Skipped;
        }

        state.CompletedAt = DateTimeOffset.UtcNow;

        // ── Finalize ────────────────────────────────────────────────────
        await SendThinkingAsync("Finalizing answer...");
        phaseSw.Restart();
        var finalPrompt = BuildFinalPrompt(userMessage, state);
        var result = await _agent!.RunAsync(finalPrompt, _session);
        llmCalls.Increment();
        phaseSw.Stop();
        totalSw.Stop();

        var finalAnswer = result.Text ?? state.CurrentAnswer ?? "Unable to generate an answer.";

        _log.LogDebug(
            "CoT FINALIZE [{Elapsed}ms] — promptLength={PromptLength}chars, responseLength={ResponseLength}chars",
            phaseSw.ElapsedMilliseconds, finalPrompt.Length, finalAnswer.Length);

        _log.LogDebug(
            "CoT COMPLETE — totalTime={TotalMs}ms ({TotalSec:F1}s), loops={Loops}, " +
            "llmCalls={LlmCalls}, stepsExecuted={StepsExecuted}, " +
            "finalConfidence={Confidence:F2}, workingMemoryEntries={MemEntries}",
            totalSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds / 1000.0,
            state.LoopCount, llmCalls.Count, state.StepResults.Count,
            state.ConfidenceScore, state.WorkingMemory.Count);

        // Dump full working memory trace at debug level
        foreach (var entry in state.WorkingMemory)
        {
            _log.LogDebug("CoT TRACE [{Type}@loop{Loop}] confidence={Confidence:F2} — {Content}",
                entry.Type, entry.LoopIteration, entry.ConfidenceSnapshot, entry.Content);
        }

        return finalAnswer;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase: Assess
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<AssessmentOutput> Assess(string userMessage)
    {
        var systemPrompt = """
            You are a complexity assessor. Given a user question, assess its complexity
            and determine if it requires multi-step reasoning.

            ## Rules
            - Set complexity to one of: trivial, simple, medium, complex.
            - Set confidence (0.0 to 1.0) — how confident are you that you can answer directly?
            - If the question is trivial/simple AND you are highly confident (>=0.9), provide a directAnswer.
            - Set requiresPlanning to false ONLY if you can answer directly with high confidence.
            - For anything that needs research, tool use, multi-step reasoning, or composition, set requiresPlanning to true.

            ## Complexity Guide
            - trivial: Simple factual recall, greetings, basic math
            - simple: Single-concept questions, straightforward lookups
            - medium: Multi-step reasoning, requires combining information
            - complex: Research-heavy, needs tools, multi-faceted analysis
            """;

        return await RunStructuredAsync<AssessmentOutput>(systemPrompt, userMessage);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase: Plan
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<PlanOutput> Plan(string userMessage, AssessmentOutput assessment, CoTState state)
    {
        var maxSteps = GetConfigInt("MaxSteps", 5);
        var toolList = _tools.Count > 0
            ? "Available tools: " + string.Join(", ", _tools.Select(t => t.Name))
            : "No tools available.";

        var systemPrompt = $"""
            You are a reasoning planner. Given a user question and a complexity assessment,
            create a step-by-step execution plan.

            ## Assessment
            Complexity: {assessment.Complexity}
            Reasoning: {assessment.Reasoning}

            ## {toolList}

            ## Rules
            - MAXIMUM {maxSteps} steps. Fewer is better. Each step costs an LLM call (~30s).
            - Prefer 2-3 broad steps over many narrow ones. Combine related analysis into a single step.
            - Each step must have a unique id (e.g., "step-1", "step-2").
            - Each step must have a clear description and a promptToExecute (the actual prompt to run).
            - The promptToExecute should be a comprehensive prompt that produces a thorough result — don't split what one prompt can handle.
            - Set requiresTools=true if the step needs tool invocation.
            - Set canParallelize=true if the step has no side effects and can run concurrently with siblings.
            - Use dependsOn to reference step ids that must complete first.
            - The plan should fully address the user's question when all steps are complete.
            """;

        var planOutput = await RunStructuredAsync<PlanOutput>(systemPrompt, userMessage);

        // Enforce hard cap — truncate excess steps
        if (planOutput.Steps.Count > maxSteps)
        {
            _log.LogWarning("Plan had {Count} steps, truncating to {Max}", planOutput.Steps.Count, maxSteps);
            planOutput.Steps = planOutput.Steps.Take(maxSteps).ToList();
        }

        return planOutput;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase: Execute
    // ═══════════════════════════════════════════════════════════════════════

    private async Task ExecuteSteps(string userMessage, CoTState state, LlmCallCounter llmCalls)
    {
        var maxLayer = state.Plan
            .Where(s => s.Status == StepStatus.Pending)
            .Select(s => s.ExecutionLayer)
            .DefaultIfEmpty(-1)
            .Max();

        _log.LogDebug("CoT EXECUTE — {LayerCount} layers to process (0..{MaxLayer})",
            maxLayer + 1, maxLayer);

        for (var layer = 0; layer <= maxLayer; layer++)
        {
            var layerSw = Stopwatch.StartNew();
            var layerSteps = state.Plan
                .Where(s => s.ExecutionLayer == layer && s.Status == StepStatus.Pending)
                .ToList();

            if (layerSteps.Count == 0)
                continue;

            var parallelSteps = layerSteps.Where(s => s.CanParallelize).ToList();
            var sequentialSteps = layerSteps.Where(s => !s.CanParallelize).ToList();

            _log.LogDebug(
                "CoT EXECUTE LAYER {Layer} — {Total} steps ({Parallel} parallel, {Sequential} sequential)",
                layer, layerSteps.Count, parallelSteps.Count, sequentialSteps.Count);

            // Snapshot completed context before parallel execution
            var completedContext = BuildCompletedContext(state);

            // Run parallel steps via Task.WhenAll — results applied sequentially after
            if (parallelSteps.Count > 0)
            {
                var tasks = parallelSteps.Select(step =>
                    ExecuteStepCore(userMessage, step, completedContext, state.LoopCount - 1)).ToArray();
                var results = await Task.WhenAll(tasks);
                llmCalls.Increment(parallelSteps.Count);

                foreach (var (stepResult, thought) in results)
                {
                    if (stepResult != null) state.StepResults.Add(stepResult);
                    if (thought != null) state.WorkingMemory.Add(thought);
                }
            }

            // Run sequential steps one by one
            foreach (var step in sequentialSteps)
            {
                var context = BuildCompletedContext(state);
                var (stepResult, thought) = await ExecuteStepCore(
                    userMessage, step, context, state.LoopCount - 1);
                llmCalls.Increment();
                if (stepResult != null) state.StepResults.Add(stepResult);
                if (thought != null) state.WorkingMemory.Add(thought);
            }

            layerSw.Stop();
            _log.LogDebug("CoT EXECUTE LAYER {Layer} DONE [{Elapsed}ms]",
                layer, layerSw.ElapsedMilliseconds);
        }
    }

    private async Task<(StepResult? Result, ThoughtEntry? Thought)> ExecuteStepCore(
        string userMessage, PlanStep step, string completedContext, int loopIteration)
    {
        var stepSw = Stopwatch.StartNew();
        step.Status = StepStatus.Running;
        await SendThinkingAsync($"Step [{step.Id}]: {Truncate(step.Description, 80)}");

        _log.LogDebug(
            "CoT STEP [{StepId}] START — tools={Tools}, promptLength={PromptLength}chars, " +
            "contextLength={ContextLength}chars",
            step.Id, step.RequiresTools, step.PromptToExecute.Length, completedContext.Length);

        var systemPrompt = $"""
            You are executing a reasoning step as part of a chain-of-thought analysis.

            ## Original Question
            {userMessage}

            ## Your Step
            {step.Description}

            ## Previous Results
            {(string.IsNullOrEmpty(completedContext) ? "(none)" : completedContext)}

            ## Rules
            - Execute the step thoroughly and provide a clear result.
            - Set confidence (0.0 to 1.0) for how well this step was completed.
            - If you cannot complete the step or discover the plan is flawed, set needsReplan=true and explain why.
            """;

        try
        {
            var tools = step.RequiresTools ? _tools : null;
            var output = await RunStructuredAsync<StepExecutionOutput>(
                systemPrompt, step.PromptToExecute, tools);

            stepSw.Stop();
            step.Status = StepStatus.Completed;
            step.Result = output.Result;

            _log.LogDebug(
                "CoT STEP [{StepId}] DONE [{Elapsed}ms] — confidence={Confidence:F2}, " +
                "needsReplan={NeedsReplan}, outputLength={OutputLength}chars",
                step.Id, stepSw.ElapsedMilliseconds, output.Confidence,
                output.NeedsReplan, output.Result.Length);

            if (output.NeedsReplan)
            {
                _log.LogDebug("CoT STEP [{StepId}] REPLAN REQUESTED — reason={Reason}",
                    step.Id, output.ReplanReason);
            }

            var result = new StepResult
            {
                StepId = step.Id,
                Success = !output.NeedsReplan,
                Output = output.Result,
                Confidence = output.Confidence
            };

            var thought = new ThoughtEntry
            {
                Type = ThoughtType.Execute,
                Content = $"Step '{step.Id}': confidence={output.Confidence:F2}" +
                    (output.NeedsReplan ? $" [REPLAN: {output.ReplanReason}]" : ""),
                LoopIteration = loopIteration,
                ConfidenceSnapshot = output.Confidence
            };

            return (result, thought);
        }
        catch (Exception ex)
        {
            stepSw.Stop();
            _log.LogWarning(ex,
                "CoT STEP [{StepId}] FAILED [{Elapsed}ms] — error={Error}",
                step.Id, stepSw.ElapsedMilliseconds, ex.Message);
            step.Status = StepStatus.Failed;
            step.Error = ex.Message;

            var result = new StepResult
            {
                StepId = step.Id,
                Success = false,
                Output = "",
                ErrorMessage = ex.Message
            };

            return (result, null);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase: Synthesize
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<SynthesisOutput> Synthesize(string userMessage, CoTState state)
    {
        var resultsBlock = string.Join("\n\n", state.StepResults
            .Select(r => $"### Step: {r.StepId}\n" +
                $"Success: {r.Success}\n" +
                $"Confidence: {r.Confidence:F2}\n" +
                $"Output: {Truncate(r.Output, 800)}" +
                (r.ErrorMessage is not null ? $"\nError: {r.ErrorMessage}" : "")));

        var systemPrompt = $"""
            You are synthesizing results from multiple reasoning steps into a coherent answer.

            ## Original Question
            {userMessage}

            ## Step Results
            {resultsBlock}

            ## Rules
            - Combine all step results into a clear, comprehensive answer.
            - Set confidence (0.0 to 1.0) for the overall answer quality.
            - Identify any gaps — information that is missing or uncertain.
            - The answer should directly address the user's original question.
            - Be honest about limitations or uncertainties.
            """;

        return await RunStructuredAsync<SynthesisOutput>(systemPrompt,
            $"Synthesize an answer to: {userMessage}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Phase: Replan
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<CoTReplanOutput> Replan(CoTState state)
    {
        var maxSteps = GetConfigInt("MaxSteps", 5);
        // Replan should only add a few targeted steps, not rebuild from scratch
        var maxNewSteps = Math.Max(2, maxSteps / 2);

        var currentPlanSummary = string.Join("\n", state.Plan.Select(s =>
            $"- [{s.Id}] {s.Description} — {s.Status}" +
            (s.Result is not null ? $" → {Truncate(s.Result, 200)}" : "") +
            (s.Error is not null ? $" (error: {s.Error})" : "")));

        var lastSynthesis = state.WorkingMemory
            .Where(t => t.Type == ThoughtType.Synthesize)
            .LastOrDefault()?.Content ?? "No synthesis yet";

        var systemPrompt = $"""
            You are revising a reasoning plan because the current results are insufficient.

            ## Original Question
            {state.OriginalRequest}

            ## Current Plan & Results
            {currentPlanSummary}

            ## Last Synthesis
            {lastSynthesis}

            ## Current Confidence
            {state.ConfidenceScore:F2}

            ## Rules
            - MAXIMUM {maxNewSteps} new steps. Each step costs an LLM call (~30s). Be surgical.
            - Identify which completed steps should be kept (by id).
            - Only add steps that address specific gaps — do NOT repeat or rephrase completed work.
            - Combine related gaps into a single step where possible.
            - Each new step needs a unique id, description, and promptToExecute.
            - Explain your rationale for the revised plan.
            """;

        var replanOutput = await RunStructuredAsync<CoTReplanOutput>(systemPrompt,
            $"Revise the plan to better answer: {state.OriginalRequest}");

        // Enforce hard cap on new steps
        if (replanOutput.RevisedSteps.Count > maxNewSteps)
        {
            _log.LogWarning("Replan had {Count} new steps, truncating to {Max}",
                replanOutput.RevisedSteps.Count, maxNewSteps);
            replanOutput.RevisedSteps = replanOutput.RevisedSteps.Take(maxNewSteps).ToList();
        }

        return replanOutput;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  RunStructuredAsync — Core LLM Helper (TaskWorkingAgent pattern)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<T> RunStructuredAsync<T>(
        string systemPrompt, string userPrompt, IList<AITool>? tools = null)
        where T : class, new()
    {
        var sw = Stopwatch.StartNew();
        var typeName = typeof(T).Name;

        _log.LogDebug(
            "CoT LLM CALL [{Type}] START — systemPrompt={SysLen}chars, userPrompt={UserLen}chars, tools={ToolCount}",
            typeName, systemPrompt.Length, userPrompt.Length, tools?.Count ?? 0);

        var schema = AIJsonUtilities.CreateJsonSchema(typeof(T));
        var chatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema: schema,
                schemaName: typeName,
                schemaDescription: $"Structured {typeName} response"),
            Tools = tools,
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.None,
                Output = ReasoningOutput.None
            }
        };

        var agentOptions = new ChatClientAgentOptions
        {
            ChatOptions = chatOptions
        };

        var tempAgent = new ChatClientAgent(_chatClient!, agentOptions);
        var tempSession = await tempAgent.CreateSessionAsync();

        var response = await tempAgent.RunAsync(
            new ChatMessage(ChatRole.User, userPrompt), tempSession);

        sw.Stop();

        // Extract JSON text from assistant messages (TaskWorkingAgent pattern)
        var jsonText = string.Join("", response.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(t => t.Text));

        _log.LogDebug(
            "CoT LLM CALL [{Type}] RESPONSE [{Elapsed}ms] — " +
            "messageCount={MsgCount}, jsonLength={JsonLength}chars",
            typeName, sw.ElapsedMilliseconds,
            response.Messages.Count, jsonText.Length);

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            _log.LogWarning("CoT LLM CALL [{Type}] — EMPTY RESPONSE, returning default", typeName);
            return new T();
        }

        var json = TryExtractJsonObject(jsonText) ?? jsonText;

        try
        {
            var result = JsonSerializer.Deserialize<T>(json, JsonOpts) ?? new T();
            _log.LogDebug("CoT LLM CALL [{Type}] — parsed OK", typeName);
            return result;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex,
                "CoT LLM CALL [{Type}] — PARSE FAILED, json={Json}",
                typeName, Truncate(json, 300));
            return new T();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string BuildFinalPrompt(string userMessage, CoTState state)
    {
        var reasoningTrace = string.Join("\n", state.WorkingMemory
            .Select(t => $"[{t.Type}@loop{t.LoopIteration}] {t.Content}"));

        return $"""
            You performed a chain-of-thought analysis to answer the user's question.
            Present the final answer clearly and directly. Do not mention the internal reasoning process.

            ## User's Question
            {userMessage}

            ## Your Analysis Result
            {state.CurrentAnswer}

            ## Reasoning Trace (for your reference only — do not expose this)
            {reasoningTrace}

            Respond with a clear, well-structured answer to the user's question.
            """;
    }

    private static double ComputeThreshold(int loop, int maxLoops, double initial, double floor)
    {
        if (maxLoops <= 1) return floor;
        var decay = (initial - floor) * loop / (maxLoops - 1);
        return initial - decay;
    }

    private static void ComputeExecutionLayers(List<PlanStep> steps)
    {
        var layerMap = new Dictionary<string, int>();
        var maxIterations = steps.Count + 1;

        // Initialize: steps with no dependencies start at layer 0
        foreach (var step in steps)
        {
            if (step.Status != StepStatus.Pending)
            {
                layerMap[step.Id] = 0;
                continue;
            }
            layerMap[step.Id] = step.DependsOn.Count == 0 ? 0 : -1;
        }

        // Iteratively resolve layers via topological ordering
        for (var i = 0; i < maxIterations; i++)
        {
            var changed = false;
            foreach (var step in steps.Where(s =>
                s.Status == StepStatus.Pending && layerMap.GetValueOrDefault(s.Id, -1) == -1))
            {
                var depLayers = step.DependsOn
                    .Select(d => layerMap.GetValueOrDefault(d, -1))
                    .ToList();

                if (depLayers.All(l => l >= 0))
                {
                    layerMap[step.Id] = depLayers.Max() + 1;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        // Apply computed layers to steps
        foreach (var step in steps)
        {
            step.ExecutionLayer = Math.Max(0, layerMap.GetValueOrDefault(step.Id, 0));
        }
    }

    private string BuildCompletedContext(CoTState state)
    {
        return string.Join("\n", state.StepResults
            .Where(r => r.Success)
            .Select(r => $"[{r.StepId}]: {Truncate(r.Output, 500)}"));
    }

    private int GetConfigInt(string key, int defaultValue)
    {
        return int.TryParse(config.Args?.GetValueOrDefault(key), out var value) ? value : defaultValue;
    }

    private double GetConfigDouble(string key, double defaultValue)
    {
        return double.TryParse(config.Args?.GetValueOrDefault(key), out var value) ? value : defaultValue;
    }

    private static string? TryExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];
        return null;
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

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>Simple mutable counter — avoids ref-in-async limitation.</summary>
    private class LlmCallCounter
    {
        public int Count;
        public void Increment(int n = 1) => Count += n;
    }
}

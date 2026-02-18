using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCaddis.Agentic.CaddisFly;

namespace OpenCaddis.Services;

/// <summary>
/// Orchestrates CaddisFly pipeline execution, approval gates, and run state management.
/// </summary>
public sealed class CaddisFlyRuntimeService
{
    private readonly ILogger<CaddisFlyRuntimeService> _logger;
    private readonly CaddisFlyRunStore _store;
    private readonly ConcurrentDictionary<string, CaddisFlyRunState> _runs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CaddisFlyEnvelope>> _approvalWaiters = new();
    private readonly int _maxSteps;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CaddisFlyRuntimeService(ILogger<CaddisFlyRuntimeService> logger, CaddisFlyRunStore store)
    {
        _logger = logger;
        _store = store;
        _maxSteps = 50;
    }

    /// <summary>
    /// Restores paused runs from disk into the in-memory dictionary.
    /// </summary>
    public async Task RestorePausedRunsAsync()
    {
        _store.CleanupExpired();
        var pausedRuns = await _store.LoadPausedRunsAsync();
        foreach (var run in pausedRuns)
        {
            _runs[run.RunId] = run;
            _logger.LogInformation("Restored paused CaddisFly run {RunId}", run.RunId);
        }
        if (pausedRuns.Count > 0)
            _logger.LogInformation("Restored {Count} paused CaddisFly run(s)", pausedRuns.Count);
    }

    /// <summary>
    /// Executes a pipeline to completion or until an approval gate is hit.
    /// </summary>
    public async Task<CaddisFlyEnvelope> RunPipelineAsync(
        CaddisFlyPipeline pipeline,
        CommandExecutor executor,
        ICaddisFlyProgressReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        if (pipeline.Steps.Count == 0)
        {
            return new CaddisFlyEnvelope
            {
                RunId = Guid.NewGuid().ToString("N"),
                Status = CaddisFlyStatus.Error,
                Error = "Pipeline has no steps."
            };
        }

        if (pipeline.Steps.Count > _maxSteps)
        {
            return new CaddisFlyEnvelope
            {
                RunId = Guid.NewGuid().ToString("N"),
                Status = CaddisFlyStatus.Error,
                Error = $"Pipeline exceeds maximum step count ({_maxSteps})."
            };
        }

        var runId = Guid.NewGuid().ToString("N");
        var state = new CaddisFlyRunState
        {
            RunId = runId,
            Pipeline = pipeline
        };
        _runs[runId] = state;

        _logger.LogInformation("Starting CaddisFly run {RunId} with {StepCount} steps (pipeline: {PipelineName})",
            runId, pipeline.Steps.Count, pipeline.Name);

        return await ExecuteFromCurrentStepAsync(state, executor, reporter ?? NullProgressReporter.Instance, cancellationToken);
    }

    /// <summary>
    /// Resumes a paused run after an approval gate.
    /// </summary>
    public async Task<CaddisFlyEnvelope> ResumeRunAsync(
        string resumeToken,
        bool approved,
        CommandExecutor executor,
        ICaddisFlyProgressReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(resumeToken, out var state))
        {
            return new CaddisFlyEnvelope
            {
                RunId = resumeToken,
                Status = CaddisFlyStatus.Error,
                Error = "Invalid or expired resume token."
            };
        }

        if (state.Status != CaddisFlyStatus.NeedsApproval)
        {
            return new CaddisFlyEnvelope
            {
                RunId = state.RunId,
                Status = CaddisFlyStatus.Error,
                Error = $"Run is not awaiting approval (current status: {state.Status})."
            };
        }

        if (!approved)
        {
            _logger.LogInformation("CaddisFly run {RunId} denied at approval gate", state.RunId);
            state.Status = CaddisFlyStatus.Cancelled;
            await _store.SaveAsync(state);
            var deniedEnvelope = BuildEnvelope(state);
            NotifyApprovalWaiter(state.RunId, deniedEnvelope);
            return deniedEnvelope;
        }

        _logger.LogInformation("CaddisFly run {RunId} approved, resuming from step {Step}",
            state.RunId, state.NextStepIndex);

        state.Status = CaddisFlyStatus.Ok;
        var resumedEnvelope = await ExecuteFromCurrentStepAsync(state, executor, reporter ?? NullProgressReporter.Instance, cancellationToken);
        NotifyApprovalWaiter(state.RunId, resumedEnvelope);
        return resumedEnvelope;
    }

    /// <summary>
    /// Gets the current status of a run.
    /// </summary>
    public CaddisFlyEnvelope? GetRunStatus(string runId)
    {
        return _runs.TryGetValue(runId, out var state) ? BuildEnvelope(state) : null;
    }

    /// <summary>
    /// Cancels a running pipeline.
    /// </summary>
    public bool CancelRun(string runId)
    {
        if (!_runs.TryGetValue(runId, out var state))
            return false;

        _logger.LogInformation("Cancelling CaddisFly run {RunId}", runId);
        state.Status = CaddisFlyStatus.Cancelled;
        state.Cts.Cancel();
        return true;
    }

    /// <summary>
    /// Registers a TaskCompletionSource that the plugin can await while an approval gate is pending.
    /// </summary>
    public TaskCompletionSource<CaddisFlyEnvelope> RegisterApprovalWaiter(string runId)
    {
        var tcs = new TaskCompletionSource<CaddisFlyEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalWaiters[runId] = tcs;
        return tcs;
    }

    /// <summary>
    /// Removes an approval waiter (e.g. on timeout) without signaling it.
    /// </summary>
    public void RemoveApprovalWaiter(string runId)
        => _approvalWaiters.TryRemove(runId, out _);

    /// <summary>
    /// Signals a waiting plugin with the resolved envelope after approval/denial.
    /// </summary>
    private void NotifyApprovalWaiter(string runId, CaddisFlyEnvelope envelope)
    {
        if (_approvalWaiters.TryRemove(runId, out var tcs))
        {
            tcs.TrySetResult(envelope);
        }
    }

    private async Task<CaddisFlyEnvelope> ExecuteFromCurrentStepAsync(
        CaddisFlyRunState state,
        CommandExecutor executor,
        ICaddisFlyProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var totalSteps = state.Pipeline.Steps.Count;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Cts.Token);

        while (state.NextStepIndex < totalSteps)
        {
            var step = state.Pipeline.Steps[state.NextStepIndex];

            // Handle approve step — pause execution
            if (step.Command.Equals("approve", StringComparison.OrdinalIgnoreCase))
            {
                state.ApprovalPrompt = step.ApprovalPrompt ?? "Approval required to continue.";
                state.Status = CaddisFlyStatus.NeedsApproval;
                state.NextStepIndex++;

                _logger.LogInformation("CaddisFly run {RunId} paused at approval gate: {Prompt}",
                    state.RunId, state.ApprovalPrompt);

                await _store.SaveAsync(state);

                var envelope = BuildEnvelope(state);
                envelope.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
                return envelope;
            }

            // Handle parallel group
            if (step.IsParallelGroup)
            {
                await reporter.ReportStepStartingAsync(state.NextStepIndex, totalSteps, step.Name);

                var parallelResults = await ExecuteParallelGroupAsync(step, state.LastOutput, executor, linkedCts.Token);
                var allSucceeded = true;

                for (var pi = 0; pi < parallelResults.Count; pi++)
                {
                    var pr = parallelResults[pi];
                    state.CompletedSteps.Add(pr);
                    if (pr.ExitCode != 0 && !string.IsNullOrEmpty(pr.Error) && string.IsNullOrEmpty(pr.Output))
                        allSucceeded = false;

                    // Write step log for each parallel child
                    var parallelStep = step.ParallelSteps![pi];
                    await _store.WriteStepLogAsync(state.RunId, state.CompletedSteps.Count - 1, pr, parallelStep);
                }

                await reporter.ReportStepCompletedAsync(state.NextStepIndex, totalSteps, step.Name, allSucceeded);

                if (!allSucceeded)
                {
                    state.Status = CaddisFlyStatus.Error;
                    state.LastOutput = string.Join("\n---\n", parallelResults.Where(r => !string.IsNullOrEmpty(r.Error)).Select(r => r.Error));
                    state.NextStepIndex++;

                    _logger.LogWarning("CaddisFly run {RunId} failed at parallel group '{StepName}'", state.RunId, step.Name);

                    var errorEnv = BuildEnvelope(state);
                    errorEnv.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
                    return errorEnv;
                }

                // Merge outputs with separator
                state.LastOutput = string.Join("\n---\n", parallelResults.Select(r => r.Output).Where(o => !string.IsNullOrEmpty(o)));
                state.NextStepIndex++;
                continue;
            }

            await reporter.ReportStepStartingAsync(state.NextStepIndex, totalSteps, step.Name);

            // Execute step with retry logic
            var maxAttempts = 1 + step.Retries;
            CaddisFlyStepResult result = null!;
            bool stepSucceeded = false;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    result = await executor.ExecuteStepAsync(step, state.LastOutput, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    state.Status = state.Cts.IsCancellationRequested
                        ? CaddisFlyStatus.Cancelled
                        : CaddisFlyStatus.TimedOut;

                    var cancelEnv = BuildEnvelope(state);
                    cancelEnv.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
                    return cancelEnv;
                }

                // Tag the result with the attempt number
                result = result with { Attempt = attempt };

                stepSucceeded = !(result.ExitCode != 0 && !string.IsNullOrEmpty(result.Error) && string.IsNullOrEmpty(result.Output));

                if (stepSucceeded)
                    break;

                // If we have retries left, delay and report
                if (attempt < maxAttempts)
                {
                    _logger.LogInformation("CaddisFly run {RunId} step '{StepName}' failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                        state.RunId, step.Name, attempt, maxAttempts, step.RetryDelaySeconds);
                    await reporter.ReportStepRetryingAsync(state.NextStepIndex, totalSteps, step.Name, attempt + 1, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(step.RetryDelaySeconds), linkedCts.Token);
                }
            }

            state.CompletedSteps.Add(result);
            await _store.WriteStepLogAsync(state.RunId, state.CompletedSteps.Count - 1, result, step);
            await reporter.ReportStepCompletedAsync(state.NextStepIndex, totalSteps, step.Name, stepSucceeded);

            // Check for step failure after all attempts exhausted
            if (!stepSucceeded)
            {
                state.Status = CaddisFlyStatus.Error;
                state.LastOutput = result.Error;
                state.NextStepIndex++;

                _logger.LogWarning("CaddisFly run {RunId} failed at step '{StepName}' after {Attempts} attempt(s): {Error}",
                    state.RunId, step.Name, result.Attempt, result.Error);

                var errorEnv = BuildEnvelope(state);
                errorEnv.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
                return errorEnv;
            }

            // Pipe output to next step
            state.LastOutput = result.Output;
            state.NextStepIndex++;
        }

        // All steps completed
        state.Status = CaddisFlyStatus.Ok;
        _logger.LogInformation("CaddisFly run {RunId} completed successfully", state.RunId);

        await _store.SaveAsync(state);

        var finalEnvelope = BuildEnvelope(state);
        finalEnvelope.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
        return finalEnvelope;
    }

    private static async Task<List<CaddisFlyStepResult>> ExecuteParallelGroupAsync(
        PipelineStep group,
        string previousOutput,
        CommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var tasks = group.ParallelSteps!.Select(step =>
            ExecuteWithRetryAsync(step, previousOutput, executor, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return [.. results];
    }

    private static async Task<CaddisFlyStepResult> ExecuteWithRetryAsync(
        PipelineStep step,
        string previousOutput,
        CommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var maxAttempts = 1 + step.Retries;
        CaddisFlyStepResult result = null!;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            result = await executor.ExecuteStepAsync(step, previousOutput, cancellationToken);
            result = result with { Attempt = attempt };

            var succeeded = !(result.ExitCode != 0 && !string.IsNullOrEmpty(result.Error) && string.IsNullOrEmpty(result.Output));
            if (succeeded || attempt >= maxAttempts)
                break;

            await Task.Delay(TimeSpan.FromSeconds(step.RetryDelaySeconds), cancellationToken);
        }

        return result;
    }

    private static CaddisFlyEnvelope BuildEnvelope(CaddisFlyRunState state)
    {
        var envelope = new CaddisFlyEnvelope
        {
            RunId = state.RunId,
            Status = state.Status,
            Output = state.LastOutput,
            Steps = [.. state.CompletedSteps],
            TotalDurationMs = (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds
        };

        if (state.Status == CaddisFlyStatus.NeedsApproval)
        {
            envelope.ResumeToken = state.RunId;
            envelope.ApprovalPrompt = state.ApprovalPrompt;
        }

        if (state.Status == CaddisFlyStatus.Error)
        {
            envelope.Error = state.CompletedSteps.LastOrDefault()?.Error;
        }

        return envelope;
    }

    /// <summary>
    /// Serializes an envelope to JSON for agent responses.
    /// </summary>
    public static string ToJson(CaddisFlyEnvelope envelope)
        => JsonSerializer.Serialize(envelope, JsonOptions);
}

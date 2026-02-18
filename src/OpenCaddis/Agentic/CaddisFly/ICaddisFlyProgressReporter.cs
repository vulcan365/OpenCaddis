using Fabr.Sdk;
using OpenCaddis.Agentic;

namespace OpenCaddis.Agentic.CaddisFly;

/// <summary>
/// Reports pipeline execution progress to the user.
/// </summary>
public interface ICaddisFlyProgressReporter
{
    Task ReportStepStartingAsync(int stepIndex, int totalSteps, string stepName);
    Task ReportStepCompletedAsync(int stepIndex, int totalSteps, string stepName, bool success);
    Task ReportStepRetryingAsync(int stepIndex, int totalSteps, string stepName, int attempt, int maxAttempts);
}

/// <summary>
/// Sends progress as "thinking" messages via the agent host.
/// </summary>
public sealed class ThinkingProgressReporter : ICaddisFlyProgressReporter
{
    private readonly IFabrAgentHost? _host;

    public ThinkingProgressReporter(IFabrAgentHost? host) => _host = host;

    public async Task ReportStepStartingAsync(int stepIndex, int totalSteps, string stepName)
    {
        if (_host is null) return;
        await ThinkingNotifier.SendThinkingAsync(_host, $"[{stepIndex + 1}/{totalSteps}] Running: {stepName}...");
    }

    public async Task ReportStepCompletedAsync(int stepIndex, int totalSteps, string stepName, bool success)
    {
        if (_host is null) return;
        var status = success ? "completed" : "failed";
        await ThinkingNotifier.SendThinkingAsync(_host, $"[{stepIndex + 1}/{totalSteps}] Step {stepName} {status}");
    }

    public async Task ReportStepRetryingAsync(int stepIndex, int totalSteps, string stepName, int attempt, int maxAttempts)
    {
        if (_host is null) return;
        await ThinkingNotifier.SendThinkingAsync(_host, $"[{stepIndex + 1}/{totalSteps}] Retrying: {stepName} (attempt {attempt}/{maxAttempts})...");
    }
}

/// <summary>
/// No-op reporter for when progress reporting isn't needed.
/// </summary>
public sealed class NullProgressReporter : ICaddisFlyProgressReporter
{
    public static readonly NullProgressReporter Instance = new();
    public Task ReportStepStartingAsync(int stepIndex, int totalSteps, string stepName) => Task.CompletedTask;
    public Task ReportStepCompletedAsync(int stepIndex, int totalSteps, string stepName, bool success) => Task.CompletedTask;
    public Task ReportStepRetryingAsync(int stepIndex, int totalSteps, string stepName, int attempt, int maxAttempts) => Task.CompletedTask;
}

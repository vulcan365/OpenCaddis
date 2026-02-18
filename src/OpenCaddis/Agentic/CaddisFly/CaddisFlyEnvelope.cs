namespace OpenCaddis.Agentic.CaddisFly;

public enum CaddisFlyStatus
{
    Ok,
    NeedsApproval,
    Cancelled,
    Error,
    TimedOut
}

public sealed record CaddisFlyStepResult
{
    public required string StepName { get; init; }
    public required string Command { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public double DurationMs { get; init; }
    public int Attempt { get; init; } = 1;
    public bool Skipped { get; init; }
}

public sealed class CaddisFlyEnvelope
{
    public required string RunId { get; init; }
    public CaddisFlyStatus Status { get; set; }
    public string Output { get; set; } = "";
    public string? Error { get; set; }
    public string? ResumeToken { get; set; }
    public string? ApprovalPrompt { get; set; }
    public List<CaddisFlyStepResult> Steps { get; init; } = [];
    public double TotalDurationMs { get; set; }
}

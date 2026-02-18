namespace OpenCaddis.Agentic.CaddisFly;

/// <summary>
/// Serializable DTO for persisting CaddisFlyRunState to disk.
/// CancellationTokenSource is not serializable, so it's excluded.
/// </summary>
public sealed class CaddisFlyRunStateDto
{
    public required string RunId { get; init; }
    public required CaddisFlyPipeline Pipeline { get; init; }
    public int NextStepIndex { get; init; }
    public string LastOutput { get; init; } = "";
    public List<CaddisFlyStepResult> CompletedSteps { get; init; } = [];
    public CaddisFlyStatus Status { get; init; }
    public string? ApprovalPrompt { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset PersistedAt { get; init; }
}

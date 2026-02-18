namespace OpenCaddis.Agentic.CaddisFly;

public sealed class CaddisFlyRunState
{
    public required string RunId { get; init; }
    public required CaddisFlyPipeline Pipeline { get; init; }
    public int NextStepIndex { get; set; }
    public string LastOutput { get; set; } = "";
    public List<CaddisFlyStepResult> CompletedSteps { get; init; } = [];
    public CaddisFlyStatus Status { get; set; } = CaddisFlyStatus.Ok;
    public string? ApprovalPrompt { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; init; } = new();

    public CaddisFlyRunStateDto ToDto() => new()
    {
        RunId = RunId,
        Pipeline = Pipeline,
        NextStepIndex = NextStepIndex,
        LastOutput = LastOutput,
        CompletedSteps = [.. CompletedSteps],
        Status = Status,
        ApprovalPrompt = ApprovalPrompt,
        StartedAt = StartedAt,
        PersistedAt = DateTimeOffset.UtcNow
    };

    public static CaddisFlyRunState FromDto(CaddisFlyRunStateDto dto) => new()
    {
        RunId = dto.RunId,
        Pipeline = dto.Pipeline,
        NextStepIndex = dto.NextStepIndex,
        LastOutput = dto.LastOutput,
        CompletedSteps = [.. dto.CompletedSteps],
        Status = dto.Status,
        ApprovalPrompt = dto.ApprovalPrompt,
        StartedAt = dto.StartedAt
    };
}

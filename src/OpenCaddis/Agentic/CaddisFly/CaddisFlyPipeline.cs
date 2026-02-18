namespace OpenCaddis.Agentic.CaddisFly;

public sealed class PipelineStep
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public Dictionary<string, string> Args { get; init; } = [];
    public int? TimeoutSeconds { get; init; }
    public int Retries { get; init; }
    public int RetryDelaySeconds { get; init; } = 2;
    public string? ApprovalPrompt { get; init; }
    public List<PipelineStep>? ParallelSteps { get; init; }
    public bool IsParallelGroup => ParallelSteps is { Count: > 0 };
}

public sealed class CaddisFlyPipeline
{
    public string Name { get; init; } = "inline";
    public List<PipelineStep> Steps { get; init; } = [];
    public Dictionary<string, string> Variables { get; init; } = [];
}

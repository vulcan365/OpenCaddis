namespace OpenCaddis.Agentic.Agents;

// ─── Thought Type ───────────────────────────────────────────────────────

public enum ThoughtType
{
    Assess,
    Plan,
    Execute,
    Synthesize,
    Review,
    Replan
}

// ─── Step Status ────────────────────────────────────────────────────────

public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

// ─── Core State ─────────────────────────────────────────────────────────

public class CoTState
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalRequest { get; set; } = "";
    public List<ThoughtEntry> WorkingMemory { get; set; } = [];
    public List<PlanStep> Plan { get; set; } = [];
    public List<StepResult> StepResults { get; set; } = [];
    public int LoopCount { get; set; }
    public int MaxLoops { get; set; } = 8;
    public double ConfidenceScore { get; set; }
    public string? CurrentAnswer { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public class ThoughtEntry
{
    public ThoughtType Type { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public int LoopIteration { get; set; }
    public double ConfidenceSnapshot { get; set; }
}

public class PlanStep
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public bool RequiresTools { get; set; }
    public bool CanParallelize { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string PromptToExecute { get; set; } = "";
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int ExecutionLayer { get; set; }
}

public class StepResult
{
    public string StepId { get; set; } = "";
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public double Confidence { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ─── LLM Structured Output Types ───────────────────────────────────────

public class AssessmentOutput
{
    public string Complexity { get; set; } = "simple";
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
    public string? DirectAnswer { get; set; }
    public bool RequiresPlanning { get; set; }
}

public class PlanOutput
{
    public List<PlanStepOutput> Steps { get; set; } = [];
    public string PlanRationale { get; set; } = "";
}

public class PlanStepOutput
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public bool RequiresTools { get; set; }
    public bool CanParallelize { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public string PromptToExecute { get; set; } = "";
}

public class StepExecutionOutput
{
    public string Result { get; set; } = "";
    public double Confidence { get; set; }
    public bool NeedsReplan { get; set; }
    public string? ReplanReason { get; set; }
}

public class SynthesisOutput
{
    public string Answer { get; set; } = "";
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
    public List<string> GapsIdentified { get; set; } = [];
}

public class CoTReplanOutput
{
    public List<PlanStepOutput> RevisedSteps { get; set; } = [];
    public string ReplanRationale { get; set; } = "";
    public List<string> StepsToKeep { get; set; } = [];
}

namespace OpenCaddis.Agentic.Agents;

// ─── Plan Status ─────────────────────────────────────────────────────────

public enum PlanStatus
{
    Planning,
    AwaitingApproval,
    Executing,
    Paused,
    Completed,
    Failed,
    Stalled
}

// ─── Task Status ─────────────────────────────────────────────────────────

public enum WorkflowTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Blocked,
    Skipped
}

// ─── Execution Event Type ────────────────────────────────────────────────

public enum ExecutionEventType
{
    TaskStarted,
    TaskMessageSent,
    TaskCompleted,
    TaskFailed,
    Replanned,
    UserApproved,
    StalledDetected,
    ExecutionStarted,
    ExecutionCompleted,
    ReplanTriggered,
    UserPaused,
    UserResumed
}

// ─── Core Data Model ─────────────────────────────────────────────────────

public class WorkflowPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public string UserHandle { get; set; } = "";
    public string Goal { get; set; } = "";
    public PlanStatus Status { get; set; } = PlanStatus.Planning;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkflowTaskItem> Tasks { get; set; } = [];
    public string? CurrentTaskId { get; set; }
    public string? Notes { get; set; }
    public int PlanVersion { get; set; } = 1;
    public WorkflowRunConfig RunConfig { get; set; } = new();
}

public class WorkflowTaskItem
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssignedAgentName { get; set; } = "";
    public Dictionary<string, string> Inputs { get; set; } = new();
    public List<string> Dependencies { get; set; } = [];
    public List<string> AcceptanceCriteria { get; set; } = [];
    public WorkflowTaskStatus Status { get; set; } = WorkflowTaskStatus.Pending;
    public int AttemptCount { get; set; }
    public string? ResolutionSummary { get; set; }
    public List<string> Artifacts { get; set; } = [];
    public string? LastError { get; set; }
}

public class WorkflowRunConfig
{
    public int MaxAttemptsPerTask { get; set; } = 3;
    public int MaxTotalReplans { get; set; } = 3;
    public int StallTimeoutMinutes { get; set; } = 10;
}

// ─── Execution Events ────────────────────────────────────────────────────

public class ExecutionEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public ExecutionEventType Type { get; set; }
    public string PlanId { get; set; } = "";
    public string? TaskId { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, string>? Data { get; set; }
}

// ─── Available Agent Info ────────────────────────────────────────────────

public class AvailableAgentInfo
{
    public string AgentName { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Description { get; set; } = "";
    public string AgentType { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
}

// ─── LLM Structured Outputs ─────────────────────────────────────────────

public class PlanGenerationOutput
{
    public string Goal { get; set; } = "";
    public string? MissingInfo { get; set; }
    public List<PlanTaskOutput> Tasks { get; set; } = [];
    public string? Notes { get; set; }
}

public class PlanTaskOutput
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssignedAgentName { get; set; } = "";
    public Dictionary<string, string> Inputs { get; set; } = new();
    public List<string> Dependencies { get; set; } = [];
    public List<string> AcceptanceCriteria { get; set; } = [];
}

public class AcceptanceEvaluation
{
    public bool Satisfied { get; set; }
    public string Reasoning { get; set; } = "";
    public List<string> UnmetCriteria { get; set; } = [];
}

public class TaskIssueResolution
{
    public string Decision { get; set; } = "retry"; // retry | blocked | replan | stall
    public string? ImprovedInstruction { get; set; }
    public string Reasoning { get; set; } = "";
}

public class ReplanOutput
{
    public string Goal { get; set; } = "";
    public List<PlanTaskOutput> Tasks { get; set; } = [];
    public bool RequiresUserApproval { get; set; }
    public string? Notes { get; set; }
    public string ChangeSummary { get; set; } = "";
}

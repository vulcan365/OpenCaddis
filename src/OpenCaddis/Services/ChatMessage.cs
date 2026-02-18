namespace OpenCaddis.Services;

public class ChatMessage
{
    public required string Content { get; set; }
    public required bool IsFromUser { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public ChatMessageStatus Status { get; set; } = ChatMessageStatus.Complete;
    public CaddisFlyApprovalInfo? ApprovalInfo { get; set; }
}

public enum ChatMessageStatus { Complete, Thinking, Sending, Error, ApprovalPending }

public enum ApprovalResult { Pending, Approved, Denied }

public sealed class CaddisFlyApprovalInfo
{
    public required string ResumeToken { get; init; }
    public required string Prompt { get; init; }
    public required string PipelineName { get; init; }
    public required int StepIndex { get; init; }
    public required int TotalSteps { get; init; }
    public ApprovalResult Result { get; set; } = ApprovalResult.Pending;
}

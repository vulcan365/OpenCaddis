namespace OpenCaddis.Services;

public record ChatAgent
{
    public required string Handle { get; init; }
    public required string AgentType { get; init; }
    public bool IsConfigured { get; init; }
    public string? Status { get; init; }
}

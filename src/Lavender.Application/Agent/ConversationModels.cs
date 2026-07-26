namespace Lavender.Application.Agent;

public sealed class Conversation
{
    public required Guid Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ConversationTurn> Turns { get; init; } = [];
}

public sealed class ConversationTurn
{
    public required Guid Id { get; init; }
    public required string UserMessage { get; init; }
    public string? AssistantMessage { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public string? StopReason { get; set; }
    public List<AgentStep> Steps { get; init; } = [];
}

public sealed class AgentStep
{
    public required int Iteration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<ToolExecutionRecord> ToolCalls { get; init; } = [];
}

public sealed class ToolExecutionRecord
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public bool WasBlockedAsRepeat { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum AgentRunStatus
{
    Running,
    Completed,
    LimitReached,
    Failed,
    Cancelled
}

public sealed record AgentRunResult(
    string FinalAnswer,
    AgentRunStatus Status,
    int Iterations,
    int ToolCalls,
    string? StopReason = null);

public interface IConversationStore
{
    Task<Conversation> CreateAsync(CancellationToken cancellationToken = default);
    Task<Conversation?> LoadAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);
}

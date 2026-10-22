namespace Lavender.Application.Agent;

/// <summary>Saved discussion for a project: each turn contains model steps and their tool-call records.</summary>
public sealed class Conversation : System.ComponentModel.INotifyPropertyChanged
{
    private string? _generatedTitle;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string? GeneratedTitle
    {
        get => _generatedTitle;
        set
        {
            _generatedTitle = value;
            PropertyChanged?.Invoke(this, new(nameof(Title)));
        }
    }
    public bool TitleGenerationAttempted { get; set; }
    public required Guid Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ProjectPath { get; set; }
    public string? SolutionPath { get; set; }
    public List<string> ContextFiles { get; set; } = [];
    public string? Summary { get; set; }
    public int SummarizedTurnCount { get; set; }
    public string Title => !string.IsNullOrWhiteSpace(GeneratedTitle) ? GeneratedTitle : Turns.FirstOrDefault()?.UserMessage is string text
        ? text[..Math.Min(text.Length, 70)] : "New chat";
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
    public string? ProjectRevision { get; set; }
    public List<AgentStep> Steps { get; init; } = [];
}

public sealed class AgentStep
{
    public required int Iteration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<ToolExecutionRecord> ToolCalls { get; init; } = [];
}

/// <summary>Audit record of a requested tool call; a returned result alone does not prove an edit succeeded.</summary>
public sealed class ToolExecutionRecord
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public string? ResultJson { get; set; }
    public FileProgress? FileProgress { get; set; }
    public string? Error { get; set; }
    public string? Outcome { get; set; }
    public string? OutcomeMessage { get; set; }
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
    string? StopReason = null,
    IReadOnlyList<string>? ToolDiagnostics = null,
    bool ProjectFilesChanged = false);

public interface IConversationStore
{
    Task<Conversation> CreateAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> LoadAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Conversation>> ListAsync(string? projectPath, CancellationToken cancellationToken = default);
}

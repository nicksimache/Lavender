namespace Lavender.Infrastructure.Indexing.Relationships;

public enum CodeRelationshipType { InheritsFrom, Implements, Calls, UsesType }

public sealed class CodeRelationship
{
    public string SourceSymbolId { get; init; } = "";
    public string TargetSymbolId { get; init; } = "";
    public CodeRelationshipType RelationshipType { get; init; }
    public string FilePath { get; init; } = "";
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public string? SourceDisplayName { get; init; }
    public string? TargetDisplayName { get; init; }
    public bool IsTargetExternal { get; init; }
}

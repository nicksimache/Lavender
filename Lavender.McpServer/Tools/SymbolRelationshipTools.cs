using Lavender.Infrastructure.Indexing.Relationships;
using Lavender.Infrastructure.Knowledge;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class SymbolRelationshipTools
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly LavenderMcpState _state;

    public SymbolRelationshipTools(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_get_callers")]
    [Description("Gets symbols that call the given symbol in the currently indexed C# project. Call lavender_index_project first.")]
    public RelationshipToolResult GetCallers(
        [Description("Symbol ID returned by lavender_find_symbols.")]
        string symbolId,

        [Description("Maximum number of relationships to return. Defaults to 50 and is capped at 200.")]
        int limit = DefaultLimit)
    {
        return GetRelationships(symbolId, limit, RelationshipDirection.Callers);
    }

    [McpServerTool(Name = "lavender_get_callees")]
    [Description("Gets symbols called by the given symbol in the currently indexed C# project. Call lavender_index_project first.")]
    public RelationshipToolResult GetCallees(
        [Description("Symbol ID returned by lavender_find_symbols.")]
        string symbolId,

        [Description("Maximum number of relationships to return. Defaults to 50 and is capped at 200.")]
        int limit = DefaultLimit)
    {
        return GetRelationships(symbolId, limit, RelationshipDirection.Callees);
    }

    private RelationshipToolResult GetRelationships(
        string symbolId,
        int limit,
        RelationshipDirection direction)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            return RelationshipToolResult.Failed("The symbol ID was not provided.");
        }

        if (!_state.IsProjectIndexed)
        {
            return RelationshipToolResult.Failed(
                "No project is indexed. Call lavender_index_project first.");
        }

        try
        {
            IProjectKnowledgeService knowledge = _state.RequireKnowledgeService();
            int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
            IReadOnlyList<CodeRelationship> relationships = direction == RelationshipDirection.Callers
                ? knowledge.GetCallers(symbolId.Trim())
                : knowledge.GetCallees(symbolId.Trim());

            RelationshipSummary[] results = relationships
                .Take(boundedLimit)
                .Select(RelationshipSummary.FromCodeRelationship)
                .ToArray();

            return new RelationshipToolResult(
                Success: true,
                Message: $"Found {results.Length} relationship(s).",
                SymbolId: symbolId.Trim(),
                Direction: direction.ToString().ToLowerInvariant(),
                Relationships: results);
        }
        catch (Exception ex)
        {
            return RelationshipToolResult.Failed($"Getting relationships failed: {ex.Message}");
        }
    }
}

public enum RelationshipDirection
{
    Callers,
    Callees
}

public sealed record RelationshipToolResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("symbol_id")]
    string? SymbolId,

    [property: JsonPropertyName("direction")]
    string? Direction,

    [property: JsonPropertyName("relationships")]
    IReadOnlyList<RelationshipSummary> Relationships)
{
    public static RelationshipToolResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            SymbolId: null,
            Direction: null,
            Relationships: Array.Empty<RelationshipSummary>());
}

public sealed record RelationshipSummary(
    [property: JsonPropertyName("source_symbol_id")]
    string SourceSymbolId,

    [property: JsonPropertyName("target_symbol_id")]
    string TargetSymbolId,

    [property: JsonPropertyName("relationship_type")]
    string RelationshipType,

    [property: JsonPropertyName("file_path")]
    string FilePath,

    [property: JsonPropertyName("start_line")]
    int StartLine,

    [property: JsonPropertyName("start_column")]
    int StartColumn,

    [property: JsonPropertyName("source_display_name")]
    string? SourceDisplayName,

    [property: JsonPropertyName("target_display_name")]
    string? TargetDisplayName,

    [property: JsonPropertyName("is_target_external")]
    bool IsTargetExternal)
{
    public static RelationshipSummary FromCodeRelationship(CodeRelationship relationship) =>
        new(
            SourceSymbolId: relationship.SourceSymbolId,
            TargetSymbolId: relationship.TargetSymbolId,
            RelationshipType: relationship.RelationshipType.ToString(),
            FilePath: relationship.FilePath,
            StartLine: relationship.StartLine,
            StartColumn: relationship.StartColumn,
            SourceDisplayName: relationship.SourceDisplayName,
            TargetDisplayName: relationship.TargetDisplayName,
            IsTargetExternal: relationship.IsTargetExternal);
}

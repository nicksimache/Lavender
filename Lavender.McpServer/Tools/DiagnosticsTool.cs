using Lavender.Diagnostics;
using Lavender.Infrastructure.Knowledge;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class DiagnosticsTool
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly LavenderMcpState _state;

    public DiagnosticsTool(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_get_diagnostics")]
    [Description("Gets compiler diagnostics for the currently indexed C# project. Call lavender_index_project first.")]
    public async Task<DiagnosticsToolResult> GetDiagnosticsAsync(
        [Description("Maximum number of diagnostics to return. Defaults to 100 and is capped at 500.")]
        int limit = DefaultLimit,

        CancellationToken cancellationToken = default)
    {
        if (!_state.IsProjectIndexed)
        {
            return DiagnosticsToolResult.Failed(
                "No project is indexed. Call lavender_index_project first.");
        }

        try
        {
            IProjectKnowledgeService knowledge = _state.RequireKnowledgeService();
            int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
            IReadOnlyList<ProjectDiagnostic> diagnostics = await knowledge.GetDiagnosticsAsync(cancellationToken);

            DiagnosticSummary[] results = diagnostics
                .Take(boundedLimit)
                .Select(DiagnosticSummary.FromProjectDiagnostic)
                .ToArray();

            return new DiagnosticsToolResult(
                Success: true,
                Message: $"Found {results.Length} diagnostic(s).",
                Diagnostics: results);
        }
        catch (Exception ex)
        {
            return DiagnosticsToolResult.Failed($"Getting diagnostics failed: {ex.Message}");
        }
    }
}

public sealed record DiagnosticsToolResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("diagnostics")]
    IReadOnlyList<DiagnosticSummary> Diagnostics)
{
    public static DiagnosticsToolResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            Diagnostics: Array.Empty<DiagnosticSummary>());
}

public sealed record DiagnosticSummary(
    [property: JsonPropertyName("id")]
    string Id,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("category")]
    string Category,

    [property: JsonPropertyName("severity")]
    string Severity,

    [property: JsonPropertyName("file_path")]
    string? FilePath,

    [property: JsonPropertyName("start_line")]
    int? StartLine,

    [property: JsonPropertyName("start_column")]
    int? StartColumn,

    [property: JsonPropertyName("end_line")]
    int? EndLine,

    [property: JsonPropertyName("end_column")]
    int? EndColumn,

    [property: JsonPropertyName("symbol_id")]
    string? SymbolId,

    [property: JsonPropertyName("project_name")]
    string? ProjectName)
{
    public static DiagnosticSummary FromProjectDiagnostic(ProjectDiagnostic diagnostic) =>
        new(
            Id: diagnostic.Id,
            Message: diagnostic.Message,
            Category: diagnostic.Category,
            Severity: diagnostic.Severity,
            FilePath: diagnostic.FilePath,
            StartLine: diagnostic.StartLine,
            StartColumn: diagnostic.StartColumn,
            EndLine: diagnostic.EndLine,
            EndColumn: diagnostic.EndColumn,
            SymbolId: diagnostic.SymbolId,
            ProjectName: diagnostic.ProjectName);
}

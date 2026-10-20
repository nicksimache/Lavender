using Lavender.Diagnostics;
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
    [Description("Gets fresh compiler diagnostics independently of the semantic index. Refreshes after source/project changes and reuses results only for the same revision. Prefer one call after a coherent edit batch. Does not run the application.")]
    public async Task<DiagnosticsToolResult> GetDiagnosticsAsync(
        [Description("Maximum number of diagnostics to return. Defaults to 100 and is capped at 500.")]
        int limit = DefaultLimit,

        CancellationToken cancellationToken = default)
    {
        try
        {
            int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
            var snapshot = await _state.GetFreshDiagnosticsAsync(cancellationToken);
            IReadOnlyList<ProjectDiagnostic> diagnostics = snapshot.Diagnostics;

            DiagnosticSummary[] results = diagnostics
                .Take(boundedLimit)
                .Select(DiagnosticSummary.FromProjectDiagnostic)
                .ToArray();

            return new DiagnosticsToolResult(
                Success: true,
                Message: $"Showing {results.Length} of {diagnostics.Count} diagnostic(s). Compiler snapshot only; not a build or runtime test.",
                Diagnostics: results, Revision: snapshot.Revision, Cached: snapshot.Cached, Warnings: snapshot.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
    IReadOnlyList<DiagnosticSummary> Diagnostics,
    [property: JsonPropertyName("revision")] string? Revision = null,
    [property: JsonPropertyName("cached")] bool Cached = false,
    [property: JsonPropertyName("workspace_warnings")] IReadOnlyList<string>? Warnings = null)
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


using Lavender.Infrastructure.Knowledge;
using Lavender.Infrastructure.Source;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class SymbolSourceTool
{
    private const int MaxSourceCharacters = 12000;

    private readonly LavenderMcpState _state;

    public SymbolSourceTool(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_get_symbol_source")]
    [Description("Gets the source declaration for a symbol ID from the currently indexed C# project. Call lavender_index_project first.")]
    public async Task<SymbolSourceToolResult> GetSymbolSourceAsync(
        [Description("Symbol ID returned by lavender_find_symbols.")]
        string symbolId,

        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            return SymbolSourceToolResult.Failed("The symbol ID was not provided.");
        }

        if (!_state.IsProjectIndexed)
        {
            return SymbolSourceToolResult.Failed(
                "No project is indexed. Call lavender_index_project first.");
        }

        try
        {
            IProjectKnowledgeService knowledge = _state.RequireKnowledgeService();
            SymbolSourceResult? source = await knowledge.GetSymbolSourceAsync(symbolId.Trim(), cancellationToken);

            if (source is null)
            {
                return SymbolSourceToolResult.Failed($"No source declaration found for symbol ID: {symbolId.Trim()}");
            }

            return new SymbolSourceToolResult(
                Success: true,
                Message: "Found symbol source.",
                Source: SymbolSourceSummary.FromSymbolSourceResult(source));
        }
        catch (Exception ex)
        {
            return SymbolSourceToolResult.Failed($"Getting symbol source failed: {ex.Message}");
        }
    }

    public static string TruncateSource(string sourceCode) =>
        sourceCode.Length <= MaxSourceCharacters
            ? sourceCode
            : sourceCode[..MaxSourceCharacters] + "\n\n// Source truncated.";
}

public sealed record SymbolSourceToolResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("source")]
    SymbolSourceSummary? Source)
{
    public static SymbolSourceToolResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            Source: null);
}

public sealed record SymbolSourceSummary(
    [property: JsonPropertyName("symbol_id")]
    string SymbolId,

    [property: JsonPropertyName("file_path")]
    string FilePath,

    [property: JsonPropertyName("relative_path")]
    string RelativePath,

    [property: JsonPropertyName("start_line")]
    int StartLine,

    [property: JsonPropertyName("end_line")]
    int EndLine,

    [property: JsonPropertyName("display_name")]
    string DisplayName,

    [property: JsonPropertyName("signature")]
    string Signature,

    [property: JsonPropertyName("source_code")]
    string SourceCode)
{
    public static SymbolSourceSummary FromSymbolSourceResult(SymbolSourceResult source) =>
        new(
            SymbolId: source.SymbolId,
            FilePath: source.FilePath,
            RelativePath: source.RelativePath,
            StartLine: source.StartLine,
            EndLine: source.EndLine,
            DisplayName: source.DisplayName,
            Signature: source.Signature,
            SourceCode: SymbolSourceTool.TruncateSource(source.SourceCode));
}

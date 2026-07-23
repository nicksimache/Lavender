using Lavender.Core.DataTypes;
using Lavender.Infrastructure.Knowledge;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class FindSymbolsTool
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    private readonly LavenderMcpState _state;

    public FindSymbolsTool(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_find_symbols")]
    [Description("Finds symbols in the currently indexed C# project by name or fully qualified name. Call lavender_index_project first.")]
    public FindSymbolsResult FindSymbols(
        [Description("Text to search for in symbol names or fully qualified names.")]
        string query,

        [Description("Maximum number of symbols to return. Defaults to 25 and is capped at 100.")]
        int limit = DefaultLimit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return FindSymbolsResult.Failed("The symbol search query was not provided.");
        }

        if (!_state.IsProjectIndexed)
        {
            return FindSymbolsResult.Failed(
                "No project is indexed. Call lavender_index_project first.");
        }

        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        IProjectKnowledgeService knowledge = _state.RequireKnowledgeService();

        SymbolSummary[] symbols = knowledge
            .FindSymbols(query.Trim())
            .Take(boundedLimit)
            .Select(SymbolSummary.FromCodeSymbol)
            .ToArray();

        return new FindSymbolsResult(
            Success: true,
            Message: $"Found {symbols.Length} symbol(s).",
            Query: query.Trim(),
            Symbols: symbols);
    }
}

public sealed record FindSymbolsResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("query")]
    string? Query,

    [property: JsonPropertyName("symbols")]
    IReadOnlyList<SymbolSummary> Symbols)
{
    public static FindSymbolsResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            Query: null,
            Symbols: Array.Empty<SymbolSummary>());
}

public sealed record SymbolSummary(
    [property: JsonPropertyName("id")]
    string Id,

    [property: JsonPropertyName("name")]
    string Name,

    [property: JsonPropertyName("fully_qualified_name")]
    string FullyQualifiedName,

    [property: JsonPropertyName("symbol_type")]
    string SymbolType,

    [property: JsonPropertyName("relative_path")]
    string RelativePath,

    [property: JsonPropertyName("start_line")]
    int StartLine,

    [property: JsonPropertyName("end_line")]
    int EndLine)
{
    public static SymbolSummary FromCodeSymbol(CodeSymbol symbol) =>
        new(
            Id: symbol.Id,
            Name: symbol.Name,
            FullyQualifiedName: symbol.FullyQualifiedName,
            SymbolType: symbol.SymbolType.ToString(),
            RelativePath: symbol.RelativePath,
            StartLine: symbol.StartLine,
            EndLine: symbol.EndLine);
}

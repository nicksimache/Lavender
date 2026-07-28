using Lavender.Core.DataTypes;
using Lavender.Infrastructure.Backend;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class SemanticSearchTool
{
    private const int DefaultTopK = 5;
    private const int MaxTopK = 25;
    private const int MaxCodeCharacters = 4000;

    private readonly LavenderMcpState _state;

    public SemanticSearchTool(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_semantic_search")]
    [Description("Runs semantic search over the currently indexed C# project. Call lavender_index_project first.")]
    public async Task<SemanticSearchResult> SearchAsync(
        [Description("Natural-language query describing the code to find.")]
        string query,

        [Description("Number of code chunks to return. Defaults to 5 and is capped at 25.")]
        int topK = DefaultTopK)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SemanticSearchResult.Failed("The semantic search query was not provided.");
        }

        if (!_state.IsProjectIndexed)
        {
            await _state.WaitForProjectIndexed();
        }

        int boundedTopK = Math.Clamp(topK, 1, MaxTopK);

        try
        {
            VectorSearchCodeChunk_ObjectRecv response =
                await FastApiService.Instance.SearchProjectAsync(query.Trim(), boundedTopK);

            SemanticSearchChunk[] chunks = response.Results
                .Take(boundedTopK)
                .Select(SemanticSearchChunk.FromVectorSearchCodeChunk)
                .ToArray();

            return new SemanticSearchResult(
                Success: true,
                Message: $"Found {chunks.Length} chunk(s).",
                Query: query.Trim(),
                Results: chunks);
        }
        catch (Exception ex)
        {
            return SemanticSearchResult.Failed($"Semantic search failed: {ex.Message}");
        }
    }

    public static string TruncateCode(string code) =>
        code.Length <= MaxCodeCharacters
            ? code
            : code[..MaxCodeCharacters] + "\n\n// Result truncated.";
}

public sealed record SemanticSearchResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("query")]
    string? Query,

    [property: JsonPropertyName("results")]
    IReadOnlyList<SemanticSearchChunk> Results)
{
    public static SemanticSearchResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            Query: null,
            Results: Array.Empty<SemanticSearchChunk>());
}

public sealed record SemanticSearchChunk(
    [property: JsonPropertyName("file_path")]
    string FilePath,

    [property: JsonPropertyName("chunk_type")]
    string ChunkType,

    [property: JsonPropertyName("namespace")]
    string Namespace,

    [property: JsonPropertyName("class_name")]
    string ClassName,

    [property: JsonPropertyName("member_name")]
    string MemberName,

    [property: JsonPropertyName("signature")]
    string Signature,

    [property: JsonPropertyName("start_line")]
    int StartLine,

    [property: JsonPropertyName("end_line")]
    int EndLine,

    [property: JsonPropertyName("distance")]
    double Distance,

    [property: JsonPropertyName("code")]
    string Code)
{
    public static SemanticSearchChunk FromVectorSearchCodeChunk(VectorSearchCodeChunk chunk) =>
        new(
            FilePath: chunk.FilePath,
            ChunkType: chunk.ChunkType,
            Namespace: chunk.Namespace,
            ClassName: chunk.ClassName,
            MemberName: chunk.MemberName,
            Signature: chunk.Signature,
            StartLine: chunk.StartLine,
            EndLine: chunk.EndLine,
            Distance: chunk.Distance,
            Code: SemanticSearchTool.TruncateCode(chunk.Code));
}

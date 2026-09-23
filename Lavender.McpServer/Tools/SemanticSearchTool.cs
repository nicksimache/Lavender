using Lavender.Core.DataTypes;
using Lavender.Infrastructure.Backend;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class SemanticSearchTool
{
    private const int MaxCodeCharacters = 4000;

    private readonly LavenderMcpState _state;

    public SemanticSearchTool(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_semantic_search")]
    [Description("Selects the nearest one or two semantic groups by centroid and returns all their chunks, including during indexing. Groups use 25 initial chunk seeds and one assignment/averaging pass. Weak centroid matches wait for more batches. Partial or low-confidence results are marked; use source reading to verify them.")]
    public async Task<SemanticSearchResult> SearchAsync(
        [Description("Natural-language query describing the code to find.")]
        string query,

        [Description("Number of nearest groups to return: 1 or 2, default 2. Every chunk in the selected groups is returned.")]
        int groupCount = 2,
        [Description("Maximum centroid cosine distance for an early result during indexing. Lower is stricter; default 0.65. This is a heuristic, not a relevance guarantee.")]
        double maxDistance = 0.65,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SemanticSearchResult.Failed("The semantic search query was not provided.");
        }

        if (_state.ProjectPath is null)
            return SemanticSearchResult.Failed("Open a project first.");
        if (_state.RequiresReindex)
            return SemanticSearchResult.Failed(_state.NotReadyMessage);

        int boundedGroupCount = Math.Clamp(groupCount, 1, 2);

        try
        {
            VectorSearchCodeChunk_ObjectRecv response =
                await FastApiService.Instance.SearchProjectAsync(query.Trim(), boundedGroupCount, _state.ProjectPath, _state.IndexId, Math.Clamp(maxDistance, 0, 2), cancellationToken);

            SemanticSearchChunk[] chunks = response.Results
                .Select(SemanticSearchChunk.FromVectorSearchCodeChunk)
                .ToArray();

            return new SemanticSearchResult(
                Success: true,
                Message: $"Found {chunks.Length} chunk(s) in {chunks.Select(c => c.GroupId).Distinct().Count()} nearest group(s). Vector index: {response.IndexingStatus}; {response.IndexedChunks} chunks ready. " +
                    (response.IsPartial ? "Partial index; more chunks may change results. " : "") +
                    (response.LowConfidence ? "No close match: verify with source reading or try again after indexing. " : "") +
                    (response.IndexingError is not null ? $"Indexing error: {response.IndexingError}" : ""),
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
    [property: JsonPropertyName("group_id")]
    int GroupId,
    [property: JsonPropertyName("group_distance")]
    double GroupDistance,
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
            GroupId: chunk.GroupId,
            GroupDistance: chunk.GroupDistance,
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

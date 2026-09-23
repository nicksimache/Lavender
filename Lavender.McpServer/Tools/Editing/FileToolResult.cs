using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools.Editing;

public sealed record FileToolResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("changed")] bool Changed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("destination_path")] string? DestinationPath = null,
    [property: JsonPropertyName("start_line")] int? StartLine = null,
    [property: JsonPropertyName("end_line")] int? EndLine = null,
    [property: JsonPropertyName("content_hash")] string? ContentHash = null,
    [property: JsonPropertyName("backup_path")] string? BackupPath = null,
    [property: JsonPropertyName("line_count")] int? LineCount = null,
    [property: JsonPropertyName("encoding")] string? Encoding = null,
    [property: JsonPropertyName("content")] string? Content = null)
{
    public static FileToolResult NotImplemented(string operation, string path) =>
        new(false, false, "not_implemented", $"{operation} is a skeleton. No file was changed.", path);
}

using System.Text.Json;

namespace Lavender.Application.Agent;

/// <summary>Confirmed file changes, captured before tool output is truncated.</summary>
public sealed record FileProgress(string Path, string Status, int? Added, int? Removed, string Receipt)
{
    public static FileProgress? FromResult(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { using var doc = JsonDocument.Parse(json); return Read(doc.RootElement); }
        catch (JsonException) { return null; }
    }

    private static FileProgress? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("success", out var success))
        {
            if (success.ValueKind != JsonValueKind.True || !root.TryGetProperty("changed", out var changed) ||
                changed.ValueKind != JsonValueKind.True || !root.TryGetProperty("path", out var path)) return null;
            int? added = null, removed = null;
            if (root.TryGetProperty("line_changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                int a = 0, r = 0;
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("added_lines", out var lines) || !lines.TryGetInt32(out int count) ||
                        !change.TryGetProperty("line_delta", out var delta) || !delta.TryGetInt32(out int difference)) return null;
                    a += count; r += count - difference;
                }
                added = a; removed = r;
            }
            string status = root.TryGetProperty("status", out var s) ? s.ToString() : "changed";
            string receipt = path + "|" + status + "|" +
                (root.TryGetProperty("content_hash", out var hash) ? hash.ToString() : "") + "|" +
                (root.TryGetProperty("backup_path", out var backup) ? backup.ToString() : "");
            return new(path.ToString(), status, added, removed, receipt);
        }
        if (root.TryGetProperty("structuredContent", out var structured) && Read(structured) is { } found) return found;
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String && FromResult(text.GetString()) is { } nested) return nested;
        return null;
    }
}

using OpenAI.Chat;
using System.Text.Json.Nodes;

namespace Lavender.Application.Agent;

internal sealed record PlannedEditBatch(int Count, string Arguments);

/// <summary>Combines only adjacent, independent edits explicitly targeting the same original hash.</summary>
internal static class ToolEditBatchPlanner
{
    public static PlannedEditBatch? Plan(IReadOnlyList<ChatToolCall> calls, int first)
    {
        try
        {
            string? path = null, hash = null;
            JsonArray combined = [];
            int count = 0;
            for (int i = first; i < calls.Count; i++)
            {
                var call = calls[i];
                if (call.FunctionName is not ("lavender_write_lines" or "lavender_apply_edits")) break;
                var args = JsonNode.Parse(call.FunctionArguments.ToString());
                string? nextPath = args?["path"]?.GetValue<string>()?.Replace('\\', '/');
                string? nextHash = args?["expectedContentHash"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(nextPath) || string.IsNullOrWhiteSpace(nextHash)) break;
                path ??= nextPath; hash ??= nextHash;
                if (!path.Equals(nextPath, StringComparison.OrdinalIgnoreCase) || !hash.Equals(nextHash, StringComparison.OrdinalIgnoreCase)) break;
                var edits = call.FunctionName == "lavender_apply_edits" ? args!["edits"] as JsonArray
                    : new JsonArray(new JsonObject { ["startLine"] = args!["startLine"]?.DeepClone(),
                        ["endLine"] = args["endLine"]?.DeepClone(), ["replacementCode"] = args["replacementCode"]?.DeepClone() });
                if (edits is null || edits.Count == 0 || combined.Count + edits.Count > 100) break;
                var candidate = combined.Concat(edits).Select(e => (Start: e!["startLine"]!.GetValue<int>(),
                    End: e["endLine"]!.GetValue<int>(), Code: e["replacementCode"]!.GetValue<string>())).OrderBy(e => e.Start).ToArray();
                int previousEnd = 0;
                bool valid = true;
                foreach (var edit in candidate)
                {
                    // Keep insertion boundaries separate; combining these can alter newline semantics.
                    if (edit.Start <= previousEnd || edit.Start < 1 || edit.End < edit.Start) { valid = false; break; }
                    previousEnd = edit.End;
                }
                if (!valid) break;
                foreach (var edit in edits) combined.Add(edit!.DeepClone());
                count++;
            }
            return count < 2 ? null : new(count, new JsonObject
                { ["path"] = path, ["expectedContentHash"] = hash, ["edits"] = combined }.ToJsonString());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or FormatException or OverflowException or ArgumentException)
        { return null; } // Ordinary tool validation reports malformed calls; never guess their intent.
    }
}

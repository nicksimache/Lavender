using Lavender.Infrastructure.FileSystem;
using Lavender.Infrastructure.AI;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class ApplyEditsTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_apply_edits")]
    [Description("Atomically apply 1-100 non-overlapping line edits to one file. All inclusive one-based ranges refer to the ORIGINAL file. EndLine=StartLine-1 inserts. Requires the original content_hash; rejects overlaps/stale hashes. One backup and save for the entire batch. Read changed regions once afterward.")]
    public Task<FileToolResult> ExecuteAsync(string path, string expectedContentHash, LineEdit[] edits,
        CancellationToken cancellationToken = default) => FileToolExecution.RunAsync(path, async () =>
    {
        var result = await state.EditFileAsync(editor => editor.ApplyEditsAsync(path, edits, expectedContentHash, cancellationToken), cancellationToken);
        ApiUsageTracker.Write(new { event_type = "edit_batch", path, edit_count = edits.Length, changed = result.Changed });
        return FileToolExecution.Result(result, "written") with
        { Message = result.Changed
            ? $"Applied batch of {edits.Length} edits. All ranges used original coordinates. Prior readings are stale; verify changed regions once."
            : "Contents already match; no changes were made. Existing readings remain valid." };
    });
}

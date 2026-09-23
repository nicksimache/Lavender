using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class WriteLinesTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_write_lines")]
    [Description("Replaces inclusive one-based lines startLine through endLine in a text file after verifying its read hash. Empty replacementCode deletes the range. Set endLine=startLine-1 to insert before startLine; use line_count+1 to append, or 1,0 for an empty file. Preserves encoding/BOM and uses the file's first newline style. Saves a backup. Cannot run during indexing.")]
    public Task<FileToolResult> ExecuteAsync(string path, int startLine, int endLine, string replacementCode,
        [Description("Exact content_hash from lavender_read_file or the last successful write result.")] string expectedContentHash,
        CancellationToken cancellationToken = default) => FileToolExecution.RunAsync(path, async () =>
            FileToolExecution.Result(await state.EditFileAsync(
                editor => editor.WriteLinesAsync(path, startLine, endLine, replacementCode, expectedContentHash, cancellationToken),
                cancellationToken), "written") with { StartLine = startLine, EndLine = endLine });
}

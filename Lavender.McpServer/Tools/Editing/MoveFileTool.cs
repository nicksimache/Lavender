using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class MoveFileTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_move_file")]
    [Description("Moves or renames one project file. Both paths must be project-relative. Verifies the latest read hash, creates parent folders and saves a backup. Never overwrites the destination; no directory moves or case-only renames. Cannot run during indexing.")]
    public Task<FileToolResult> ExecuteAsync(
        string path, string destinationPath,
        [Description("Exact content_hash from the latest lavender_read_file result.")] string expectedContentHash,
        CancellationToken cancellationToken = default) => FileToolExecution.RunAsync(path, async () =>
            FileToolExecution.Result(await state.EditFileAsync(
                editor => editor.MoveAsync(path, destinationPath, expectedContentHash, cancellationToken), cancellationToken), "moved"));
}

using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class DeleteFileTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_delete_file")]
    [Description("Deletes one existing project file after verifying the hash from lavender_read_file. Saves a recoverable backup. Never deletes directories. Cannot run during indexing.")]
    public Task<FileToolResult> ExecuteAsync(
        [Description("Project-relative file path.")] string path,
        [Description("Exact content_hash from the latest lavender_read_file result.")] string expectedContentHash,
        CancellationToken cancellationToken = default) => FileToolExecution.RunAsync(path, async () =>
            FileToolExecution.Result(await state.EditFileAsync(
                editor => editor.DeleteAsync(path, expectedContentHash, cancellationToken), cancellationToken), "deleted"));
}

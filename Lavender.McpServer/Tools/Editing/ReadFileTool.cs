using ModelContextProtocol.Server;
using System.ComponentModel;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class ReadFileTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_read_file")]
    [Description("Reads a UTF-8 or BOM-marked UTF-16 text file in the selected project. Returns content, line_count, encoding and content_hash. Before editing, moving or deleting, use this reader and pass its exact hash to that tool. Maximum 4 MiB.")]
    public Task<FileToolResult> ExecuteAsync(
        [Description("Path relative to the selected project root.")] string path,
        CancellationToken cancellationToken = default) => FileToolExecution.RunAsync(path, async () =>
        {
            string root = state.ProjectPath ?? throw new InvalidOperationException("Open a project first.");
            var read = await new ProjectFileEditor(root).ReadAsync(path, cancellationToken);
            return new(true, false, "read", $"Read {read.Path}.", read.Path,
                ContentHash: read.ContentHash, LineCount: read.LineCount, Encoding: read.Encoding, Content: read.Content);
        });
}

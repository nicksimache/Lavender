using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class CreateFileTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_create_file")]
    [Description("Creates a new UTF-8 text file inside the selected project, creating missing parent folders. Never overwrites. Supply complete contents. Cannot run during indexing. After creation read source directly or reindex before using semantic/symbol tools.")]
    public async Task<FileToolResult> ExecuteAsync(
        [Description("Path relative to the selected project root, for example src/Utilities/Helper.cs.")] string path,
        [Description("Complete text contents; an empty string creates an empty file.")] string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string created = await state.CreateFileAsync(path, content, cancellationToken);
            return new(true, true, "created", $"Created {created}. The project index is now stale; read source directly or reindex.", created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new(false, false, "failed", $"Could not create file: {ex.Message}", path);
        }
    }
}

using ModelContextProtocol.Server;
using System.ComponentModel;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.McpServer.Tools.Editing;

[McpServerToolType]
public sealed class ReadFileTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_read_file")]
    [Description("Reads a text file or inclusive line range in the selected project. Prefer startLine/endLine to inspect or verify specific code instead of rereading whole files. Returns content, total line_count, encoding and whole-file content_hash. Before editing, moving or deleting, pass its exact hash to that tool. Maximum 4 MiB.")]
    public Task<FileToolResult> ExecuteAsync(
        [Description("Path relative to the selected project root.")] string path,
        [Description("First line to return (1-based). Omit for the beginning.")] int? startLine = null,
        [Description("Last line to return (inclusive). Omit for the end.")] int? endLine = null,
        CancellationToken cancellationToken = default) => FileToolExecution.RunAsync(path, async () =>
        {
            string root = state.ProjectPath ?? throw new InvalidOperationException("Open a project first.");
            var read = await new ProjectFileEditor(root).ReadAsync(path, cancellationToken);
            string content = read.Content;
            string message = $"Read {read.Path}.";
            int? returnedStart = null;
            int? returnedEnd = null;
            if (startLine.HasValue || endLine.HasValue)
            {
                string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                int start = startLine ?? 1;
                int end = endLine ?? read.LineCount;
                if (start < 1 || end < start || start > read.LineCount)
                    throw new ArgumentOutOfRangeException(nameof(startLine), "Specify a valid 1-based inclusive line range.");
                end = Math.Min(end, read.LineCount);
                returnedStart = start;
                returnedEnd = end;
                content = string.Join("\n", lines.Skip(start - 1).Take(end - start + 1));
                message = $"Read {read.Path}, lines {start}-{end}. Content hash covers the whole file.";
            }
            return new(true, false, "read", message, read.Path,
                StartLine: returnedStart, EndLine: returnedEnd,
                ContentHash: read.ContentHash, LineCount: read.LineCount, Encoding: read.Encoding, Content: content);
        });
}

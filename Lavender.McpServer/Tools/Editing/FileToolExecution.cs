using Lavender.Infrastructure.FileSystem;

namespace Lavender.McpServer.Tools.Editing;

internal static class FileToolExecution
{
    public static async Task<FileToolResult> RunAsync(string path, Func<Task<FileToolResult>> operation)
    {
        try { return await operation(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return new(false, false, "failed", ex.Message, path); }
    }

    public static FileToolResult Result(ProjectFileEdit edit, string status) =>
        new(true, edit.Changed, edit.Changed ? status : "unchanged",
            edit.Changed ? $"{status}: {edit.Path}. Read source directly or reindex before semantic/symbol queries."
                : "Contents already match; no change made.",
            edit.Path, edit.DestinationPath, ContentHash: edit.ContentHash, BackupPath: edit.BackupPath);
}

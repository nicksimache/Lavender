using Lavender.Infrastructure.FileSystem;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class ListFilesTool(LavenderMcpState state)
{
    [McpServerTool(Name = "lavender_list_files")]
    [Description("Lists project-relative source, configuration and asset paths without indexing. Query is a case-insensitive path substring (e.g. .cs). Paginated, sorted; excludes generated/dependency directories and links. Use this to discover code files, not the image-only asset catalog.")]
    public object Execute(string query = "", int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            string root = state.ProjectPath ?? throw new InvalidOperationException("Open a project first.");
            var paths = ProjectFileCatalog.Enumerate(root, cancellationToken)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .Where(p => p.Contains(query ?? "", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            offset = Math.Max(0, offset);
            var page = paths.Skip(offset).Take(Math.Clamp(limit, 1, 500)).ToArray();
            return new { success = true, root, total = paths.Length, files = page,
                next_offset = offset + page.Length < paths.Length ? (int?)(offset + page.Length) : null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new { success = false, message = ex.Message }; }
    }
}

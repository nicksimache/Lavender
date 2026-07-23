using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public static class ReadFileTool
{
    [McpServerTool(Name = "lavender_ping")]
    [Description("Checks whether the Lavender MCP server is running.")]
    public static string Ping()
    {
        return "Lavender MCP server is running.";
    }

    [McpServerTool(Name = "lavender_read_source_file")]
    [Description("Reads a source file located inside the selected project directory.")]
    public static async Task<string> ReadSourceFileAsync(
        [Description("Absolute path to the project root.")]
        string projectPath,

        [Description("Path to the file relative to the project root.")]
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return "The project path was not provided.";
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "The relative file path was not provided.";
        }

        string fullProjectPath = Path.GetFullPath(projectPath);
        string fullFilePath = Path.GetFullPath(
            Path.Combine(fullProjectPath, relativePath));

        string projectPrefix = fullProjectPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullFilePath.StartsWith(
                projectPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return "The requested file is outside the project directory.";
        }

        if (!File.Exists(fullFilePath))
        {
            return $"File not found: {relativePath}";
        }

        return await File.ReadAllTextAsync(fullFilePath);
    }
}

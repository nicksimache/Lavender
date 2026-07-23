using Lavender.McpServer;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class ProjectIndexTool
{
    private readonly LavenderMcpState _state;

    public ProjectIndexTool(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_index_project")]
    [Description("Indexes a C# solution or project so the Lavender MCP server can answer code-intelligence questions about it.")]
    public async Task<IndexProjectResult> IndexProjectAsync(
        [Description("Absolute path to the project root directory.")]
        string projectPath,

        [Description("Absolute path to the .sln or .csproj file to load with Roslyn.")]
        string solutionPath,

        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return IndexProjectResult.Failed("The project path was not provided.");
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return IndexProjectResult.Failed("The solution or project path was not provided.");
        }

        string fullProjectPath = Path.GetFullPath(projectPath);
        string fullSolutionPath = Path.GetFullPath(solutionPath);

        if (!Directory.Exists(fullProjectPath))
        {
            return IndexProjectResult.Failed($"Project directory not found: {fullProjectPath}");
        }

        if (!File.Exists(fullSolutionPath))
        {
            return IndexProjectResult.Failed($"Solution or project file not found: {fullSolutionPath}");
        }

        try
        {
            await _state.IndexProjectAsync(fullProjectPath, fullSolutionPath, cancellationToken);

            return new IndexProjectResult(
                Success: true,
                Message: "Project indexed successfully.",
                ProjectPath: _state.ProjectPath,
                SolutionPath: _state.SolutionPath);
        }
        catch (Exception ex)
        {
            return IndexProjectResult.Failed($"Project indexing failed: {ex.Message}");
        }
    }
}

public sealed record IndexProjectResult(
    bool Success,
    string Message,
    string? ProjectPath,
    string? SolutionPath)
{
    public static IndexProjectResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            ProjectPath: null,
            SolutionPath: null);
}

using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Knowledge;

namespace Lavender.McpServer;

public sealed class LavenderMcpState : IDisposable
{
    private const int PollInterval = 100;

    private readonly ProjectIndexer _projectIndexer = new();

    public string? ProjectPath { get; private set; }
    public string? SolutionPath { get; private set; }
    public IProjectKnowledgeService? KnowledgeService => _projectIndexer.KnowledgeService;

    public async Task IndexProjectAsync(
        string projectPath,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        await _projectIndexer.IndexProjectAsync(projectPath, solutionPath, cancellationToken);

        ProjectPath = Path.GetFullPath(projectPath);
        SolutionPath = Path.GetFullPath(solutionPath);
    }

    public bool IsProjectIndexed => KnowledgeService is not null;

    public IProjectKnowledgeService RequireKnowledgeService()
    {
        return KnowledgeService
            ?? throw new InvalidOperationException(
                "No project is indexed. Call lavender_index_project first.");
    }

    public async Task WaitForProjectIndexed(CancellationToken cancellationToken = default)
    {
        while (!IsProjectIndexed)
        {
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public void Dispose()
    {
        _projectIndexer.Dispose();
    }
}

using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Knowledge;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.McpServer;

public sealed class LavenderMcpState : IDisposable
{
    private const int PollInterval = 100;

    private readonly ProjectIndexer _projectIndexer = new();
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    public string IndexId { get; private set; } = "";
    public bool IsIndexing { get; private set; }
    public string? IndexingError { get; private set; }
    public bool RequiresReindex { get; private set; }
    public string NotReadyMessage => IsIndexing
        ? "Structural indexing is still running. Use semantic search or source reading in the meantime; do not start duplicate indexing."
        : RequiresReindex ? "Project files changed. Read source directly or call lavender_index_project to refresh the index."
        : IndexingError is not null ? $"Indexing failed: {IndexingError}. Source reading remains available."
        : "No project is indexed. Open a project first.";

    public string? ProjectPath { get; private set; }
    public string? SolutionPath { get; private set; }
    public IProjectKnowledgeService? KnowledgeService => RequiresReindex ? null : _projectIndexer.KnowledgeService;
    public string? TimingSummary => _projectIndexer.LastTimings?.Summary();

    public async Task IndexProjectAsync(
        string projectPath,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        if (!await _indexGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Project indexing is already running. Use source reading or semantic search while it finishes.");
        try
        {
            IsIndexing = true;
            IndexingError = null;
            ProjectPath = Path.GetFullPath(projectPath);
            SolutionPath = Path.GetFullPath(solutionPath);
            IndexId = Guid.NewGuid().ToString("N");
            RequiresReindex = false;
            await _projectIndexer.IndexProjectAsync(projectPath, solutionPath, IndexId, cancellationToken);
        }
        catch (Exception ex) { IndexingError = ex.Message; throw; }
        finally { IsIndexing = false; _indexGate.Release(); }
    }

    public bool IsProjectIndexed => KnowledgeService is not null;

    public async Task<string> CreateFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        if (!await _indexGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Indexing or another edit is running. Retry after it finishes.");
        try
        {
            string root = ProjectPath ?? throw new InvalidOperationException("Open a project first.");
            string created = await ProjectFileCreator.CreateAsync(root, path, content, cancellationToken);
            RequiresReindex = true;
            return created;
        }
        finally { _indexGate.Release(); }
    }

    public IProjectKnowledgeService RequireKnowledgeService()
    {
        return KnowledgeService
            ?? throw new InvalidOperationException(
                NotReadyMessage);
    }

    public async Task<ProjectFileEdit> EditFileAsync(Func<ProjectFileEditor, Task<ProjectFileEdit>> operation,
        CancellationToken cancellationToken = default)
    {
        if (!await _indexGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Indexing or another edit is running. Retry after it finishes.");
        try
        {
            string root = ProjectPath ?? throw new InvalidOperationException("Open a project first.");
            var result = await operation(new ProjectFileEditor(root));
            if (result.Changed) RequiresReindex = true;
            return result;
        }
        finally { _indexGate.Release(); }
    }

    public async Task WaitForProjectIndexed(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!IsProjectIndexed)
        {
            if (!IsIndexing) throw new InvalidOperationException(NotReadyMessage);
            await Task.Delay(PollInterval, timeout.Token);
        }
    }

    public void Dispose()
    {
        _projectIndexer.Dispose();
    }
}

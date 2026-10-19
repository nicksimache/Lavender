using System.Reflection;
using Lavender.Infrastructure.Build;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Lavender.Infrastructure.Editing;

/// <summary>
/// An editor-only Roslyn workspace. Unsaved buffers never alter the agent's index or disk.
/// Calls are serialized so a project switch cannot dispose a workspace in use.
/// </summary>
public sealed class EditorLanguageService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private Solution? _diskSolution;
    private HashSet<string> _bufferPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _projectPath;
    private int _generation;
    private int _loadedGeneration = -1;
    // Keep warmed compilations alive between startup and the first editor request.
    private readonly Dictionary<ProjectId, Compilation> _compilations = new();


    // Defer reloading until the next request; an in-flight request keeps its own generation.
    /// <summary>Marks the workspace for reloading on its next request.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>Serializes workspace access and runs an operation against the latest unsaved-buffer snapshot.</summary>
    public async Task<T?> WithDocumentAsync<T>(string projectPath, string filePath,
        IReadOnlyDictionary<string, string> buffers,
        Func<Document, CancellationToken, Task<T>> action, CancellationToken token) where T : class
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(projectPath).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            // Keep the latest snapshot so unchanged documents retain their syntax/semantic caches.
            Solution snapshot = _solution!;
            foreach (string closedPath in _bufferPaths.Where(path => !buffers.ContainsKey(path)))
                foreach (DocumentId id in snapshot.GetDocumentIdsWithFilePath(closedPath))
                    snapshot = snapshot.WithDocumentText(id,
                        await _diskSolution!.GetDocument(id)!.GetTextAsync(token).ConfigureAwait(false));
            foreach (var buffer in buffers)
                foreach (DocumentId id in snapshot.GetDocumentIdsWithFilePath(buffer.Key))
                {
                    var previous = await snapshot.GetDocument(id)!.GetTextAsync(token).ConfigureAwait(false);
                    var current = buffer.Value;
                    int start = 0;
                    while (start < previous.Length && start < current.Length && previous[start] == current[start])
                        start++;
                    if (start == previous.Length && start == current.Length)
                        continue;
                    int oldEnd = previous.Length, newEnd = current.Length;
                    while (oldEnd > start && newEnd > start && previous[oldEnd - 1] == current[newEnd - 1])
                    {
                        oldEnd--;
                        newEnd--;
                    }
                    snapshot = snapshot.WithDocumentText(id, previous.WithChanges(
                        new TextChange(TextSpan.FromBounds(start, oldEnd), current[start..newEnd])));
                }
            _solution = snapshot;
            _bufferPaths = new HashSet<string>(buffers.Keys, StringComparer.OrdinalIgnoreCase);

            DocumentId? selected = snapshot.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (selected is null)
                return null;
            var document = snapshot.GetDocument(selected)!;
            try
            {
                return await action(document, token).ConfigureAwait(false);
            }
            finally
            {
                if (document.Project.TryGetCompilation(out var compilation))
                    _compilations[document.Project.Id] = compilation;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the workspace and retains C# compilations in dependency order for editor reuse.</summary>
    public async Task WarmUpAsync(string projectPath, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(projectPath).ConfigureAwait(false);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            foreach (var id in _solution!.GetProjectDependencyGraph().GetTopologicallySortedProjects())
            {
                request.Token.ThrowIfCancellationRequested();
                if (_loadedGeneration != Volatile.Read(ref _generation))
                    return;
                var project = _solution.GetProject(id)!;
                if (project.Language != LanguageNames.CSharp || !project.SupportsCompilation || _compilations.ContainsKey(id))
                    continue;
                var compilation = await project.GetCompilationAsync(request.Token).ConfigureAwait(false);
                if (compilation is not null)
                    _compilations[id] = compilation;
            }
        }
        finally { _gate.Release(); }
    }

    // Called under the same gate by warmup, completion, and highlighting.
    /// <summary>Loads or reuses the requested project workspace while the service gate is held.</summary>
    private async Task EnsureLoadedAsync(string projectPath)
    {
        int generation = Volatile.Read(ref _generation);
        if (_loadedGeneration != generation ||
            !string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase))
        {
            _workspace?.Dispose();
            _compilations.Clear();
            _workspace = null;
            _solution = null;
            _projectPath = null;
            MSBuildRegistration.EnsureRegistered();
            // Completion providers are MEF exports from Features, not just the compiler assemblies.
            var assemblies = MefHostServices.DefaultAssemblies
                .Concat(new[] { typeof(CompletionService).Assembly,
                    Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features") }).Distinct();
            var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>(),
                MefHostServices.Create(assemblies));
            try
            {
                _solution = Path.GetExtension(projectPath).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    ? await workspace.OpenSolutionAsync(projectPath, cancellationToken: _lifetime.Token).ConfigureAwait(false)
                    : (await workspace.OpenProjectAsync(projectPath, cancellationToken: _lifetime.Token).ConfigureAwait(false)).Solution;
                _workspace = workspace;
                _diskSolution = _solution;
                _bufferPaths.Clear();
                _projectPath = projectPath;
                _loadedGeneration = generation;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

    }

    /// <summary>Cancels workspace loading and releases cached solutions, compilations, and workspace resources.</summary>
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
            _diskSolution = null;
            _compilations.Clear();
            _lifetime.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}

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
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _projectPath;
    private int _generation;
    private int _loadedGeneration = -1;

    // Defer reloading until the next request; an in-flight request keeps its own generation.
    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<T?> WithDocumentAsync<T>(string projectPath, string filePath,
        IReadOnlyDictionary<string, string> buffers,
        Func<Document, CancellationToken, Task<T>> action, CancellationToken token) where T : class
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int generation = Volatile.Read(ref _generation);
            if (_loadedGeneration != generation ||
                !string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            {
                _workspace?.Dispose();
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
                        ? await workspace.OpenSolutionAsync(projectPath, cancellationToken: token).ConfigureAwait(false)
                        : (await workspace.OpenProjectAsync(projectPath, cancellationToken: token).ConfigureAwait(false)).Solution;
                    _workspace = workspace;
                    _projectPath = projectPath;
                    _loadedGeneration = generation;
                }
                catch
                {
                    workspace.Dispose();
                    throw;
                }
            }

            // Apply every open buffer, including linked files, to an immutable request snapshot.
            Solution snapshot = _solution!;
            foreach (var buffer in buffers)
                foreach (DocumentId id in snapshot.GetDocumentIdsWithFilePath(buffer.Key))
                    snapshot = snapshot.WithDocumentText(id, SourceText.From(buffer.Value));

            DocumentId? selected = snapshot.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            return selected is null ? null
                : await action(snapshot.GetDocument(selected)!, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

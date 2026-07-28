using Lavender.Core.DataTypes;
using Lavender.Diagnostics;
using Lavender.Git;
using Lavender.Infrastructure.Backend;
using Lavender.Infrastructure.Indexing.Chunking;
using Lavender.Infrastructure.Indexing.Dependencies;
using Lavender.Infrastructure.Indexing.Relationships;
using Lavender.Infrastructure.Indexing.Symbol;
using Lavender.Infrastructure.Knowledge;
using Lavender.Infrastructure.Source;
using System.Diagnostics;

namespace Lavender.Infrastructure.Indexing;

/// <summary>
/// Coordinates one shared Roslyn load and preserves the existing vector chunk indexing flow.
/// </summary>
public sealed class ProjectIndexer : IDisposable
{
    private IndexedProjectContext? _context;
    public ProjectKnowledgeService? KnowledgeService { get; private set; }

    public async Task IndexProjectAsync(string projectPath, string solutionPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException(
                $"The selected project directory does not exist: {projectPath}");
        }

        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException(
                "The selected solution or project file does not exist.",
                solutionPath);
        }

        List<CodeChunk> chunks = CodeChunkService.GetCodeChunksFromFolder(projectPath);
        IndexedProjectContext newContext = await IndexedProjectContext.OpenAsync(solutionPath, cancellationToken);
        try
        {
            var identity = new SymbolIdentityService();
            SymbolIndex symbols = await new SymbolIndexingService(identity).IndexAsync(newContext, cancellationToken);

            CodeRelationshipGraph relationships = await new CodeRelationshipIndexer(identity).IndexAsync(newContext, symbols, cancellationToken);
            ProjectDependencyGraph dependencies = new ProjectDependencyIndexer().Index(newContext);

            KnowledgeService = new ProjectKnowledgeService(
                symbols,
                new SymbolSourceService(symbols, newContext),
                relationships,
                new RoslynDiagnosticsProvider(newContext, identity),
                new GitContextService(projectPath),
                dependencies);

            IndexedProjectContext? old = _context;
            _context = newContext;
            old?.Dispose();

            await FastApiService.Instance.StartServerAsync();
            await FastApiService.Instance.EmbedProjectAsync(chunks);
        }
        catch
        {
            newContext.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
        KnowledgeService = null;
    }
}

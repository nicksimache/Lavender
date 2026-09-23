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
    public IndexingTimings? LastTimings { get; private set; }

    public async Task IndexProjectAsync(string projectPath, string solutionPath, string indexId, CancellationToken cancellationToken = default)
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

        var timings = LastTimings = new IndexingTimings(projectPath);
        KnowledgeService = null;
        IndexedProjectContext? newContext = null;
        try
        {
            List<CodeChunk> chunks;
            using (timings.Stage("Scan and chunk files"))
                chunks = CodeChunkService.GetCodeChunksFromFolder(projectPath);
            timings.ChunkCount = chunks.Count;
            timings.FileCount = chunks.Select(c => c.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            // Publish vector batches before the structural index is ready, so chat can search them.
            using (timings.Stage("Check backend readiness"))
                await FastApiService.Instance.StartServerAsync(cancellationToken);
            using (timings.Stage("Embed HTTP round trip (includes backend work)"))
                timings.Backend = await FastApiService.Instance.EmbedProjectAsync(chunks, projectPath, indexId, cancellationToken);
            using (timings.Stage("Load Roslyn workspace"))
                newContext = await IndexedProjectContext.OpenAsync(solutionPath, cancellationToken);
            var identity = new SymbolIdentityService();
            SymbolIndex symbols;
            using (timings.Stage("Index symbols (including compilation)"))
                symbols = await new SymbolIndexingService(identity).IndexAsync(newContext, cancellationToken);
            timings.SymbolCount = symbols.Symbols.Count;

            CodeRelationshipGraph relationships;
            using (timings.Stage("Build relationship graph"))
                relationships = await new CodeRelationshipIndexer(identity).IndexAsync(newContext, symbols, cancellationToken);
            ProjectDependencyGraph dependencies;
            using (timings.Stage("Build dependency graph"))
                dependencies = new ProjectDependencyIndexer().Index(newContext);

            ProjectKnowledgeService knowledgeService;
            using (timings.Stage("Create query services"))
                knowledgeService = new ProjectKnowledgeService(
                symbols,
                new SymbolSourceService(symbols, newContext),
                relationships,
                new RoslynDiagnosticsProvider(newContext, identity),
                new GitContextService(projectPath),
                dependencies);

            // Publish the new index only after both code and vector indexing succeed.
            using (timings.Stage("Publish index and dispose previous workspace"))
            {
                IndexedProjectContext? old = _context;
                _context = newContext;
                KnowledgeService = knowledgeService;
                old?.Dispose();
            }
        }
        catch (Exception ex)
        {
            timings.Error = ex.Message;
            newContext?.Dispose();
            throw;
        }
        finally { timings.Finish(); }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
        KnowledgeService = null;
    }
}

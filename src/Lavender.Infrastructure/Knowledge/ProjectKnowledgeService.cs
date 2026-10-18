using Lavender.Core.DataTypes;
using Lavender.Diagnostics;
using Lavender.Git;
using Lavender.Infrastructure.Indexing.Dependencies;
using Lavender.Infrastructure.Indexing.Relationships;
using Lavender.Infrastructure.Indexing.Symbol;
using Lavender.Infrastructure.Source;

namespace Lavender.Infrastructure.Knowledge;

public interface IProjectKnowledgeService
{
    IReadOnlyList<CodeSymbol> FindSymbols(string query);

    Task<SymbolSourceResult?> GetSymbolSourceAsync(string symbolId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolSourceResult>> GetAllSymbolDeclarationsAsync(
        string symbolId,
        CancellationToken cancellationToken = default);

    IReadOnlyList<CodeRelationship> GetRelationships(string symbolId, CodeRelationshipType? type = null);

    IReadOnlyList<CodeRelationship> GetCallers(string symbolId);

    IReadOnlyList<CodeRelationship> GetCallees(string symbolId);

    Task<IReadOnlyList<ProjectDiagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default);

    Task<GitRepositoryStatus> GetGitStatusAsync(CancellationToken cancellationToken = default);

    Task<GitDiffResult> GetGitDiffAsync(
        string? relativeFilePath = null,
        bool staged = false,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ProjectDependencyEdge> GetProjectDependencies(string projectId);
}

/// <summary>Read-only query facade used by MCP tools; optional graphs are unavailable when indexing disables them.</summary>
public sealed class ProjectKnowledgeService : IProjectKnowledgeService
{
    private readonly SymbolIndex _symbols;
    private readonly ISymbolSourceService _source;
    private readonly CodeRelationshipGraph? _relationships;
    private readonly RoslynDiagnosticsProvider _diagnostics;
    private readonly GitContextService _git;
    private readonly ProjectDependencyGraph? _dependencies;

    public ProjectKnowledgeService(
        SymbolIndex symbols,
        ISymbolSourceService source,
        CodeRelationshipGraph? relationships,
        RoslynDiagnosticsProvider diagnostics,
        GitContextService git,
        ProjectDependencyGraph? dependencies)
    {
        _symbols = symbols;
        _source = source;
        _relationships = relationships;
        _diagnostics = diagnostics;
        _git = git;
        _dependencies = dependencies;
    }

    public IReadOnlyList<CodeSymbol> FindSymbols(string query) => _symbols.Find(query);

    public Task<SymbolSourceResult?> GetSymbolSourceAsync(string id, CancellationToken token = default) =>
        _source.GetSourceBySymbolIdAsync(id, token);

    public Task<IReadOnlyList<SymbolSourceResult>> GetAllSymbolDeclarationsAsync(
        string id,
        CancellationToken token = default) =>
        _source.GetAllDeclarationsAsync(id, token);

    private CodeRelationshipGraph Relationships => _relationships
        ?? throw new InvalidOperationException("Code relationship graph generation is disabled.");

    public IReadOnlyList<CodeRelationship> GetRelationships(string id, CodeRelationshipType? type = null) =>
        Relationships.GetOutgoingRelationships(id, type);

    public IReadOnlyList<CodeRelationship> GetCallers(string id) => Relationships.GetCallers(id);

    public IReadOnlyList<CodeRelationship> GetCallees(string id) => Relationships.GetCallees(id);

    public Task<IReadOnlyList<ProjectDiagnostic>> GetDiagnosticsAsync(CancellationToken token = default) =>
        _diagnostics.GetSolutionDiagnosticsAsync(token);

    public Task<GitRepositoryStatus> GetGitStatusAsync(CancellationToken token = default) => _git.GetStatusAsync(token);

    public Task<GitDiffResult> GetGitDiffAsync(
        string? path = null,
        bool staged = false,
        CancellationToken token = default) =>
        staged ? _git.GetStagedDiffAsync(path, token) : _git.GetWorkingTreeDiffAsync(path, token);

    public IReadOnlyList<ProjectDependencyEdge> GetProjectDependencies(string id) => (_dependencies
        ?? throw new InvalidOperationException("Project dependency graph generation is disabled.")).GetDependencies(id);
}

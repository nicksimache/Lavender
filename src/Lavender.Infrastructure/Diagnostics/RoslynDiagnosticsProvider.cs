using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Indexing.Symbol;
using Microsoft.CodeAnalysis;

namespace Lavender.Diagnostics;

public sealed class ProjectDiagnostic
{
    public string Id { get; init; } = "";
    public string Message { get; init; } = "";
    public string Category { get; init; } = "";
    public string Severity { get; init; } = "";
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? StartColumn { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? SymbolId { get; init; }
    public string? ProjectName { get; init; }
}

/// <summary>Provides syntax and compiler diagnostics from the shared Roslyn solution.</summary>
public sealed class RoslynDiagnosticsProvider
{
    private readonly IndexedProjectContext _context;
    private readonly SymbolIdentityService _identity;

    public RoslynDiagnosticsProvider(IndexedProjectContext context, SymbolIdentityService identity)
    {
        _context = context;
        _identity = identity;
    }

    public async Task<IReadOnlyList<ProjectDiagnostic>> GetSolutionDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ProjectDiagnostic>();

        foreach (Project project in _context.Solution.Projects)
        {
            result.AddRange(await GetProjectAsync(project, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<ProjectDiagnostic>> GetProjectDiagnosticsAsync(string projectIdOrName, CancellationToken cancellationToken = default)
    {
        Project? project = _context.Solution.Projects.FirstOrDefault(p => p.Id.Id.ToString() == projectIdOrName || p.Name.Equals(projectIdOrName, StringComparison.OrdinalIgnoreCase));
        return project is null ? Array.Empty<ProjectDiagnostic>() : await GetProjectAsync(project, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectDiagnostic>> GetDocumentDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(filePath);
        var all = await GetSolutionDiagnosticsAsync(cancellationToken);
        return all.Where(d => d.FilePath is not null && Path.GetFullPath(d.FilePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async Task<IReadOnlyList<ProjectDiagnostic>> GetProjectAsync(Project project, CancellationToken token)
    {
        Compilation? compilation = await project.GetCompilationAsync(token);
        if (compilation is null)
        {
            return Array.Empty<ProjectDiagnostic>();
        }

        var result = new List<ProjectDiagnostic>();
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics(token))
        {
            token.ThrowIfCancellationRequested();
            Location location = diagnostic.Location;
            FileLinePositionSpan? line = location.IsInSource ? location.GetLineSpan() : null;
            string? symbolId = null;
            if (location.IsInSource && location.SourceTree is not null)
            {
                SemanticModel model = compilation.GetSemanticModel(location.SourceTree);
                SyntaxNode root = await location.SourceTree.GetRootAsync(token);
                SyntaxNode node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
                ISymbol? symbol = null;
                foreach (SyntaxNode candidate in node.AncestorsAndSelf())
                {
                    symbol = model.GetDeclaredSymbol(candidate, token);
                    if (symbol is not null)
                    {
                        break;
                    }
                }

                symbol ??= model.GetEnclosingSymbol(location.SourceSpan.Start, token);
                if (symbol is not null)
                {
                    symbolId = _identity.GetId(symbol);
                }
            }

            result.Add(new ProjectDiagnostic
            {
                Id = diagnostic.Id,
                Message = diagnostic.GetMessage(),
                Category = diagnostic.Descriptor.Category,
                Severity = diagnostic.Severity.ToString(),
                FilePath = line?.Path,
                StartLine = line?.StartLinePosition.Line + 1,
                StartColumn = line?.StartLinePosition.Character + 1,
                EndLine = line?.EndLinePosition.Line + 1,
                EndColumn = line?.EndLinePosition.Character + 1,
                SymbolId = symbolId,
                ProjectName = project.Name
            });
        }

        return result;
    }
}

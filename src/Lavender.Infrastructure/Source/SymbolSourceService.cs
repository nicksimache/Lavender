using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Indexing.Symbol;
using Microsoft.CodeAnalysis;

namespace Lavender.Infrastructure.Source;

public sealed class SymbolSourceResult
{
    public string SymbolId { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string SourceCode { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Signature { get; init; } = "";
}

public interface ISymbolSourceService
{
    Task<SymbolSourceResult?> GetSourceBySymbolIdAsync(string symbolId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SymbolSourceResult>> GetAllDeclarationsAsync(string symbolId, CancellationToken cancellationToken = default);
}

/// <summary>Reads current declaration syntax directly from Roslyn syntax references.</summary>
public sealed class SymbolSourceService : ISymbolSourceService
{
    private readonly SymbolIndex _index;
    private readonly IndexedProjectContext _context;

    public SymbolSourceService(SymbolIndex index, IndexedProjectContext context)
    {
        _index = index;
        _context = context;
    }

    public async Task<SymbolSourceResult?> GetSourceBySymbolIdAsync(string symbolId, CancellationToken cancellationToken = default) =>
        (await GetAllDeclarationsAsync(symbolId, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<SymbolSourceResult>> GetAllDeclarationsAsync(string symbolId, CancellationToken cancellationToken = default)
    {
        ISymbol? symbol = _index.GetRoslynSymbol(symbolId);
        if (symbol is null || symbol.DeclaringSyntaxReferences.Length == 0)
        {
            return Array.Empty<SymbolSourceResult>();
        }

        var results = new List<SymbolSourceResult>();
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode node = await reference.GetSyntaxAsync(cancellationToken);
            FileLinePositionSpan span = node.SyntaxTree.GetLineSpan(node.Span, cancellationToken);
            string filePath = Path.GetFullPath(node.SyntaxTree.FilePath);
            results.Add(new SymbolSourceResult
            {
                SymbolId = symbolId,
                FilePath = filePath,
                RelativePath = Path.GetRelativePath(_context.RootDirectory, filePath).Replace('\\', '/'),
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                SourceCode = node.ToFullString(),
                DisplayName = symbol.ToDisplayString(SymbolIdentityService.IdentityFormat),
                Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            });
        }

        return results;
    }
}

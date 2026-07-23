using Lavender.Core.DataTypes;
using Microsoft.CodeAnalysis;

namespace Lavender.Infrastructure.Indexing.Symbol;

/// <summary>
/// Read-only symbol metadata plus Roslyn symbols for authoritative source resolution.
/// </summary>
public sealed class SymbolIndex
{
    private readonly Dictionary<string, ISymbol> _roslynSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeSymbol> _symbols = new(StringComparer.Ordinal);

    internal SymbolIndex(IEnumerable<(CodeSymbol Model, ISymbol Symbol)> entries)
    {
        foreach (var entry in entries)
        {
            _symbols.TryAdd(entry.Model.Id, entry.Model);
            _roslynSymbols.TryAdd(entry.Model.Id, entry.Symbol.OriginalDefinition);
        }
    }

    public IReadOnlyList<CodeSymbol> Symbols => _symbols.Values.ToArray();
    public CodeSymbol? Get(string id) => _symbols.GetValueOrDefault(id);
    public ISymbol? GetRoslynSymbol(string id) => _roslynSymbols.GetValueOrDefault(id);

    public IReadOnlyList<CodeSymbol> Find(string query) => _symbols.Values
        .Where(s =>
            s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || s.FullyQualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
        .ToArray();
}

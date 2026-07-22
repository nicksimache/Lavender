namespace Lavender.Infrastructure.Indexing.Relationships;

/// <summary>In-memory, immutable-after-construction semantic relationship graph.</summary>
public sealed class CodeRelationshipGraph
{
    private readonly IReadOnlyList<CodeRelationship> _all;
    private readonly Dictionary<string, IReadOnlyList<CodeRelationship>> _outgoing;
    private readonly Dictionary<string, IReadOnlyList<CodeRelationship>> _incoming;

    public CodeRelationshipGraph(IEnumerable<CodeRelationship> relationships)
    {
        _all = relationships.ToArray();
        _outgoing = _all.GroupBy(x => x.SourceSymbolId).ToDictionary(g => g.Key, g => (IReadOnlyList<CodeRelationship>)g.ToArray());
        _incoming = _all.GroupBy(x => x.TargetSymbolId).ToDictionary(g => g.Key, g => (IReadOnlyList<CodeRelationship>)g.ToArray());
    }

    public IReadOnlyList<CodeRelationship> GetOutgoingRelationships(string symbolId, CodeRelationshipType? type = null) =>
        Filter(_outgoing.GetValueOrDefault(symbolId) ?? Array.Empty<CodeRelationship>(), type);
    public IReadOnlyList<CodeRelationship> GetIncomingRelationships(string symbolId, CodeRelationshipType? type = null) =>
        Filter(_incoming.GetValueOrDefault(symbolId) ?? Array.Empty<CodeRelationship>(), type);
    public IReadOnlyList<CodeRelationship> GetCallers(string id) => GetIncomingRelationships(id, CodeRelationshipType.Calls);
    public IReadOnlyList<CodeRelationship> GetCallees(string id) => GetOutgoingRelationships(id, CodeRelationshipType.Calls);
    public IReadOnlyList<CodeRelationship> GetImplementations(string id) => GetIncomingRelationships(id, CodeRelationshipType.Implements);
    public IReadOnlyList<CodeRelationship> GetDerivedTypes(string id) => GetIncomingRelationships(id, CodeRelationshipType.InheritsFrom);
    public IReadOnlyList<CodeRelationship> GetUsedTypes(string id) => GetOutgoingRelationships(id, CodeRelationshipType.UsesType);
    private static IReadOnlyList<CodeRelationship> Filter(IReadOnlyList<CodeRelationship> values, CodeRelationshipType? type) =>
        type is null ? values.ToArray() : values.Where(x => x.RelationshipType == type).ToArray();
}

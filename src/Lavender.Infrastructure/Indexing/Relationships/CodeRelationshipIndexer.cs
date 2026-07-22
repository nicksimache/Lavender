using Lavender.Infrastructure.Indexing.Symbol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lavender.Infrastructure.Indexing.Relationships;

/// <summary>Collects semantic edges in one traversal per source document.</summary>
public sealed class CodeRelationshipIndexer
{
    private readonly SymbolIdentityService _identity;
    public CodeRelationshipIndexer(SymbolIdentityService identity) => _identity = identity;

    public async Task<CodeRelationshipGraph> IndexAsync(IndexedProjectContext context, SymbolIndex symbols, CancellationToken cancellationToken = default)
    {
        var result = new List<CodeRelationship>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var internalIds = symbols.Symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (Project project in context.Solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;
            foreach (Document document in project.Documents)
            {
                SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken);
                SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (root is null || tree is null) continue;
                SemanticModel model = compilation.GetSemanticModel(tree);
                string path = document.FilePath ?? document.Name;

                foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol source) continue;
                    if (source.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                        Add(source, baseType, CodeRelationshipType.InheritsFrom, declaration, path, result, seen, internalIds, false);
                    foreach (INamedTypeSymbol iface in source.Interfaces)
                        Add(source, iface, CodeRelationshipType.Implements, declaration, path, result, seen, internalIds, false);
                }

                foreach (SyntaxNode node in root.DescendantNodes())
                {
                    ISymbol? owner = GetOwner(model, node, cancellationToken);
                    if (owner is null || !internalIds.Contains(_identity.GetId(owner))) continue;
                    ISymbol? called = node switch
                    {
                        InvocationExpressionSyntax invocation => model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol,
                        ObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol,
                        ImplicitObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol,
                        ConstructorInitializerSyntax initializer => model.GetSymbolInfo(initializer, cancellationToken).Symbol as IMethodSymbol,
                        _ => null
                    };
                    if (called is not null) Add(owner, called, CodeRelationshipType.Calls, node, path, result, seen, internalIds, true);
                    if (node is TypeSyntax typeSyntax)
                    {
                        ITypeSymbol? type = model.GetTypeInfo(typeSyntax, cancellationToken).Type;
                        foreach (INamedTypeSymbol named in ExpandTypes(type))
                            Add(owner, named, CodeRelationshipType.UsesType, node, path, result, seen, internalIds, false);
                    }
                }
            }
        }
        return new CodeRelationshipGraph(result);
    }

    private static ISymbol? GetOwner(SemanticModel model, SyntaxNode node, CancellationToken token)
    {
        SyntaxNode? declaration = node.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseTypeDeclarationSyntax or VariableDeclaratorSyntax);
        return declaration switch
        {
            BaseMethodDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
            BasePropertyDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
            BaseTypeDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
            VariableDeclaratorSyntax n => model.GetDeclaredSymbol(n, token),
            _ => model.GetEnclosingSymbol(node.SpanStart, token)
        };
    }

    private static IEnumerable<INamedTypeSymbol> ExpandTypes(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol array) { foreach (var t in ExpandTypes(array.ElementType)) yield return t; yield break; }
        if (type is IPointerTypeSymbol pointer) { foreach (var t in ExpandTypes(pointer.PointedAtType)) yield return t; yield break; }
        if (type is not INamedTypeSymbol named) yield break;
        yield return named.OriginalDefinition;
        foreach (ITypeSymbol argument in named.TypeArguments)
            foreach (INamedTypeSymbol nested in ExpandTypes(argument)) yield return nested;
    }

    private void Add(ISymbol source, ISymbol target, CodeRelationshipType type, SyntaxNode location, string path,
        List<CodeRelationship> output, HashSet<string> seen, HashSet<string> internalIds, bool retainLocations)
    {
        string sourceId = _identity.GetId(source); string targetId = _identity.GetId(target);
        var position = location.SyntaxTree.GetLineSpan(location.Span).StartLinePosition;
        string key = retainLocations ? $"{sourceId}|{targetId}|{type}|{path}|{location.SpanStart}" : $"{sourceId}|{targetId}|{type}";
        if (!seen.Add(key) || sourceId == targetId && type == CodeRelationshipType.UsesType) return;
        output.Add(new CodeRelationship { SourceSymbolId = sourceId, TargetSymbolId = targetId, RelationshipType = type,
            FilePath = path, StartLine = position.Line + 1, StartColumn = position.Character + 1,
            SourceDisplayName = _identity.GetDisplayName(source), TargetDisplayName = _identity.GetDisplayName(target),
            IsTargetExternal = !internalIds.Contains(targetId) });
    }
}

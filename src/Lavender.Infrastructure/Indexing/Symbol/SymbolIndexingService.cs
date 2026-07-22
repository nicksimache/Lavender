using Lavender.Core.DataTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lavender.Infrastructure.Indexing.Symbol;

/// <summary>Builds symbol metadata from an already loaded Roslyn solution.</summary>
public sealed class SymbolIndexingService
{
    private readonly SymbolIdentityService _identity;
    public SymbolIndexingService(SymbolIdentityService identity) => _identity = identity;

    public async Task<SymbolIndex> IndexAsync(IndexedProjectContext context, CancellationToken cancellationToken = default)
    {
        var entries = new List<(CodeSymbol, ISymbol)>();
        foreach (Project project in context.Solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;
            string projectDirectory = Path.GetDirectoryName(project.FilePath ?? context.InputPath) ?? context.RootDirectory;
            foreach (Document document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken);
                SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (root is null || tree is null) continue;
                SemanticModel model = compilation.GetSemanticModel(tree);
                string absolutePath = Path.GetFullPath(document.FilePath ?? Path.Combine(projectDirectory, document.Name));
                string relativePath = NormalizePath(Path.GetRelativePath(context.RootDirectory, absolutePath));
                foreach (SyntaxNode declaration in GetDeclarations(root))
                {
                    ISymbol? symbol = GetDeclaredSymbol(model, declaration, cancellationToken);
                    if (symbol is null) continue;
                    entries.Add((CreateModel(symbol, declaration, absolutePath, relativePath), symbol));
                }
            }
        }
        return new SymbolIndex(entries);
    }

    private static IEnumerable<SyntaxNode> GetDeclarations(SyntaxNode root)
    {
        foreach (MemberDeclarationSyntax member in root.DescendantNodes().OfType<MemberDeclarationSyntax>()) yield return member;
        foreach (VariableDeclaratorSyntax variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            if (variable.Parent?.Parent is BaseFieldDeclarationSyntax) yield return variable;
        foreach (EnumMemberDeclarationSyntax member in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>()) yield return member;
    }

    private static ISymbol? GetDeclaredSymbol(SemanticModel model, SyntaxNode node, CancellationToken token) => node switch
    {
        BaseTypeDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
        DelegateDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
        BaseMethodDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
        EventDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
        BasePropertyDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
        VariableDeclaratorSyntax n => model.GetDeclaredSymbol(n, token),
        EnumMemberDeclarationSyntax n => model.GetDeclaredSymbol(n, token),
        _ => null
    };

    private CodeSymbol CreateModel(ISymbol symbol, SyntaxNode declaration, string filePath, string relativePath)
    {
        FileLinePositionSpan span = declaration.SyntaxTree.GetLineSpan(declaration.Span);
        return new CodeSymbol
        {
            Id = _identity.GetId(symbol), Name = symbol.Name,
            FullyQualifiedName = _identity.GetDisplayName(symbol),
            SymbolType = GetSymbolType(symbol),
            Namespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            ContainingType = symbol.ContainingType?.ToDisplayString() ?? "",
            Signature = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            FilePath = filePath, RelativePath = relativePath,
            StartLine = span.StartLinePosition.Line + 1, EndLine = span.EndLinePosition.Line + 1
        };
    }

    private static CodeSymbol.E_SymbolType GetSymbolType(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { IsRecord: true } => CodeSymbol.E_SymbolType.Record,
        INamedTypeSymbol { TypeKind: TypeKind.Class } => CodeSymbol.E_SymbolType.Class,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => CodeSymbol.E_SymbolType.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => CodeSymbol.E_SymbolType.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => CodeSymbol.E_SymbolType.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => CodeSymbol.E_SymbolType.Delegate,
        IMethodSymbol { MethodKind: MethodKind.Constructor } => CodeSymbol.E_SymbolType.Constructor,
        IMethodSymbol { MethodKind: MethodKind.Destructor } => CodeSymbol.E_SymbolType.Destructor,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => CodeSymbol.E_SymbolType.Operator,
        IMethodSymbol { MethodKind: MethodKind.Conversion } => CodeSymbol.E_SymbolType.ConversionOperator,
        IMethodSymbol => CodeSymbol.E_SymbolType.Method,
        IPropertySymbol { IsIndexer: true } => CodeSymbol.E_SymbolType.Indexer,
        IPropertySymbol => CodeSymbol.E_SymbolType.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => CodeSymbol.E_SymbolType.EnumMember,
        IFieldSymbol => CodeSymbol.E_SymbolType.Field,
        IEventSymbol => CodeSymbol.E_SymbolType.Event,
        _ => CodeSymbol.E_SymbolType.None
    };

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}

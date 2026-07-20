using Lavender.Core.DataTypes;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Lavender.Infrastructure.Indexing.Symbol;

public class SymbolIndexingService
{
    private static readonly SymbolDisplayFormat FullyQualifiedFormat =
        new(
            globalNamespaceStyle:
                SymbolDisplayGlobalNamespaceStyle.Omitted,

            typeQualificationStyle:
                SymbolDisplayTypeQualificationStyle
                    .NameAndContainingTypesAndNamespaces,

            genericsOptions:
                SymbolDisplayGenericsOptions.IncludeTypeParameters,

            memberOptions:
                SymbolDisplayMemberOptions.IncludeContainingType |
                SymbolDisplayMemberOptions.IncludeParameters |
                SymbolDisplayMemberOptions.IncludeType,

            parameterOptions:
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeParamsRefOut,

            miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                SymbolDisplayMiscellaneousOptions
                    .IncludeNullableReferenceTypeModifier
        );

    public static async Task<List<CodeSymbol>> IndexProjectAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        RegisterMSBuild();

        using var workspace = MSBuildWorkspace.Create();

        using var registration =
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                Console.WriteLine(
                    $"Roslyn workspace warning: {args.Diagnostic.Message}");
            });

        Solution solution = await workspace.OpenSolutionAsync(
            projectPath,
            cancellationToken: cancellationToken);

        var results = new List<CodeSymbol>();

        foreach (Project project in solution.Projects)
        {
            Compilation? compilation =
            await project.GetCompilationAsync(cancellationToken);

            if (compilation is null)
            {
                throw new InvalidOperationException(
                    $"Could not create compilation for {project.Name}.");
            }

            foreach (Document document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SyntaxNode? root =
                    await document.GetSyntaxRootAsync(cancellationToken);

                SyntaxTree? syntaxTree =
                    await document.GetSyntaxTreeAsync(cancellationToken);

                if (root is null || syntaxTree is null)
                {
                    continue;
                }

                SemanticModel semanticModel =
                    compilation.GetSemanticModel(syntaxTree);

                string filePath =
                    document.FilePath ?? document.Name;

                string projectDirectory =
                    Path.GetDirectoryName(project.FilePath ?? "")
                    ?? "";

                string relativePath =
                    Path.GetRelativePath(
                        projectDirectory,
                        filePath);

                results.AddRange(
                    GetDeclaredSymbols(
                        root,
                        semanticModel,
                        relativePath,
                        cancellationToken));
            }

        }

        return results;
    }

    private static IEnumerable<CodeSymbol> GetDeclaredSymbols(
        SyntaxNode root,
        SemanticModel semanticModel,
        string relativePath,
        CancellationToken cancellationToken)
    {
        foreach (MemberDeclarationSyntax declaration in
                 root.DescendantNodes()
                     .OfType<MemberDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ISymbol? symbol =
                GetDeclaredSymbol(
                    semanticModel,
                    declaration,
                    cancellationToken);

            if (symbol is null)
            {
                continue;
            }

            yield return CreateCodeSymbol(
                symbol,
                declaration,
                relativePath);
        }

        foreach (VariableDeclaratorSyntax variable in
                 root.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Parent?.Parent
                is not BaseFieldDeclarationSyntax)
            {
                continue;
            }

            IFieldSymbol? symbol =
                semanticModel.GetDeclaredSymbol(
                    variable,
                    cancellationToken) as IFieldSymbol;

            if (symbol is null)
            {
                continue;
            }

            yield return CreateCodeSymbol(
                symbol,
                variable,
                relativePath);
        }

        foreach (EnumMemberDeclarationSyntax enumMember in
                 root.DescendantNodes()
                     .OfType<EnumMemberDeclarationSyntax>())
        {
            IFieldSymbol? symbol =
                semanticModel.GetDeclaredSymbol(
                    enumMember,
                    cancellationToken) as IFieldSymbol;

            if (symbol is null)
            {
                continue;
            }

            yield return CreateCodeSymbol(
                symbol,
                enumMember,
                relativePath);
        }
    }

    private static ISymbol? GetDeclaredSymbol(
        SemanticModel semanticModel,
        MemberDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        return declaration switch
        {
            BaseTypeDeclarationSyntax type =>
                semanticModel.GetDeclaredSymbol(
                    type,
                    cancellationToken),

            DelegateDeclarationSyntax delegateDeclaration =>
                semanticModel.GetDeclaredSymbol(
                    delegateDeclaration,
                    cancellationToken),

            BaseMethodDeclarationSyntax method =>
                semanticModel.GetDeclaredSymbol(
                    method,
                    cancellationToken),

            EventDeclarationSyntax eventDeclaration =>
                semanticModel.GetDeclaredSymbol(
                    eventDeclaration,
                    cancellationToken),

            BasePropertyDeclarationSyntax property =>
                semanticModel.GetDeclaredSymbol(
                    property,
                    cancellationToken),

            _ => null
        };
    }

    private static CodeSymbol CreateCodeSymbol(
        ISymbol symbol,
        SyntaxNode declaration,
        string relativePath)
    {
        string fullyQualifiedName =
            symbol.ToDisplayString(FullyQualifiedFormat);

        string signature =
            GetSignature(symbol);

        CodeSymbol.E_SymbolType symbolType =
            GetSymbolType(symbol);

        string containingType =
            symbol.ContainingType?.ToDisplayString(
                SymbolDisplayFormat.MinimallyQualifiedFormat)
            ?? "";

        string namespaceName =
            symbol.ContainingNamespace is
            { IsGlobalNamespace: false }
                ? symbol.ContainingNamespace.ToDisplayString()
                : "";

        return new CodeSymbol
        {
            Id = GetId(
                relativePath,
                fullyQualifiedName,
                symbolType,
                signature),

            Name = symbol.Name,
            FullyQualifiedName = fullyQualifiedName,
            Namespace = namespaceName,
            ContainingType = containingType,
            Signature = signature,
            SymbolType = symbolType,
            FilePath = NormalizePath(relativePath),
            StartLine = GetStartLine(declaration)
        };
    }

    private static string GetSignature(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method =>
                method.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat),

            IPropertySymbol property =>
                property.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat),

            IFieldSymbol field =>
                field.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat),

            IEventSymbol eventSymbol =>
                eventSymbol.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat),

            INamedTypeSymbol namedType =>
                namedType.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat),

            _ => symbol.ToDisplayString(
                SymbolDisplayFormat.MinimallyQualifiedFormat)
        };
    }

    private static CodeSymbol.E_SymbolType GetSymbolType(
        ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol
            {
                TypeKind: TypeKind.Class,
                IsRecord: true
            } => CodeSymbol.E_SymbolType.Record,

            INamedTypeSymbol
            {
                TypeKind: TypeKind.Class
            } => CodeSymbol.E_SymbolType.Class,

            INamedTypeSymbol
            {
                TypeKind: TypeKind.Struct,
                IsRecord: true
            } => CodeSymbol.E_SymbolType.Record,

            INamedTypeSymbol
            {
                TypeKind: TypeKind.Struct
            } => CodeSymbol.E_SymbolType.Struct,

            INamedTypeSymbol
            {
                TypeKind: TypeKind.Interface
            } => CodeSymbol.E_SymbolType.Interface,

            INamedTypeSymbol
            {
                TypeKind: TypeKind.Enum
            } => CodeSymbol.E_SymbolType.Enum,

            INamedTypeSymbol
            {
                TypeKind: TypeKind.Delegate
            } => CodeSymbol.E_SymbolType.Delegate,

            IMethodSymbol
            {
                MethodKind: MethodKind.Constructor
            } => CodeSymbol.E_SymbolType.Constructor,

            IMethodSymbol
            {
                MethodKind: MethodKind.Destructor
            } => CodeSymbol.E_SymbolType.Destructor,

            IMethodSymbol
            {
                MethodKind: MethodKind.UserDefinedOperator
            } => CodeSymbol.E_SymbolType.Operator,

            IMethodSymbol
            {
                MethodKind: MethodKind.Conversion
            } => CodeSymbol.E_SymbolType.ConversionOperator,

            IMethodSymbol =>
                CodeSymbol.E_SymbolType.Method,

            IPropertySymbol
            {
                IsIndexer: true
            } => CodeSymbol.E_SymbolType.Indexer,

            IPropertySymbol =>
                CodeSymbol.E_SymbolType.Property,

            IFieldSymbol
            {
                ContainingType.TypeKind: TypeKind.Enum
            } => CodeSymbol.E_SymbolType.EnumMember,

            IFieldSymbol =>
                CodeSymbol.E_SymbolType.Field,

            IEventSymbol =>
                CodeSymbol.E_SymbolType.Event,

            _ => CodeSymbol.E_SymbolType.None
        };
    }

    private static int GetStartLine(SyntaxNode node)
    {
        return node.SyntaxTree
            .GetLineSpan(node.Span)
            .StartLinePosition
            .Line + 1;
    }

    private static string GetId(
        string relativePath,
        string fullyQualifiedName,
        CodeSymbol.E_SymbolType symbolType,
        string signature)
    {
        string source =
            $"{NormalizePath(relativePath)}:" +
            $"{fullyQualifiedName}:" +
            $"{symbolType}:" +
            $"{signature}";

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(source)));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static void RegisterMSBuild()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }
}

using Lavender.Core.DataTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lavender.Infrastructure.Indexing.Symbol
{
    public class SymbolIndexingService
    {
        public static List<CodeSymbol> GetCodeSymbolsFromFolder(string dir)
        {
                var codeSymbols = new List<CodeSymbol>();

                foreach (string file in Directory.GetFiles(
                             dir,
                             "*.cs",
                             SearchOption.AllDirectories))
                {
                    codeSymbols.AddRange(GetCodeSymbols(file));
                }

                return codeSymbols;
        }

        private static List<CodeSymbol> GetCodeSymbols(string filePath)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var fileText = File.ReadAllText(filePath);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(fileText);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            var codeSymbols = new List<CodeSymbol>();

            foreach (BaseTypeDeclarationSyntax typeNode in
                     root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                string namespaceName = GetNamespace(typeNode);
                string containingType = GetContainingType(typeNode);
                string typeName = typeNode.Identifier.Text;
                string fullyQualifiedTypeName = BuildFullyQualifiedName(
                    namespaceName,
                    containingType,
                    typeName);

                codeSymbols.Add(new CodeSymbol
                {
                    Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                    FilePath = relativePath,
                    Name = typeName,
                    FullyQualifiedName = fullyQualifiedTypeName,
                    Namespace = namespaceName,
                    ContainingType = containingType,
                    Signature = GetTypeSignature(typeNode),
                    SymbolType = GetTypeSymbolType(typeNode),
                    StartLine = GetStartLine(typeNode)
                });

                if (typeNode is not TypeDeclarationSyntax typeDeclaration)
                {
                    continue;
                }

                foreach (MemberDeclarationSyntax member in typeDeclaration.Members)
                {
                    switch (member)
                    {
                        case MethodDeclarationSyntax method:
                        {
                            string parameters = string.Join(
                                ", ",
                                method.ParameterList.Parameters.Select(parameter =>
                                    $"{parameter.Type} {parameter.Identifier.Text}"));

                            codeSymbols.Add(new CodeSymbol
                            {
                                Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                                FilePath = relativePath,
                                Name = method.Identifier.Text,
                                FullyQualifiedName =
                                    $"{fullyQualifiedTypeName}.{method.Identifier.Text}",
                                Namespace = namespaceName,
                                ContainingType = typeName,
                                Signature =
                                    $"{method.ReturnType} {method.Identifier.Text}({parameters})",
                                SymbolType = CodeSymbol.E_SymbolType.Method,
                                StartLine = GetStartLine(method)
                            });

                            break;
                        }

                        case ConstructorDeclarationSyntax constructor:
                        {
                            string parameters = string.Join(
                                ", ",
                                constructor.ParameterList.Parameters.Select(parameter =>
                                    $"{parameter.Type} {parameter.Identifier.Text}"));

                            codeSymbols.Add(new CodeSymbol
                            {
                                Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                                FilePath = relativePath,
                                Name = constructor.Identifier.Text,
                                FullyQualifiedName =
                                    $"{fullyQualifiedTypeName}.{constructor.Identifier.Text}",
                                Namespace = namespaceName,
                                ContainingType = typeName,
                                Signature =
                                    $"{constructor.Identifier.Text}({parameters})",
                                SymbolType = CodeSymbol.E_SymbolType.Constructor,
                                StartLine = GetStartLine(constructor)
                            });

                            break;
                        }

                        case PropertyDeclarationSyntax property:
                            codeSymbols.Add(new CodeSymbol
                            {
                                Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                                FilePath = relativePath,
                                Name = property.Identifier.Text,
                                FullyQualifiedName =
                                    $"{fullyQualifiedTypeName}.{property.Identifier.Text}",
                                Namespace = namespaceName,
                                ContainingType = typeName,
                                Signature =
                                    $"{property.Type} {property.Identifier.Text}",
                                SymbolType = CodeSymbol.E_SymbolType.Property,
                                StartLine = GetStartLine(property)
                            });

                            break;

                        case FieldDeclarationSyntax field:
                            foreach (VariableDeclaratorSyntax variable in
                                     field.Declaration.Variables)
                            {
                                codeSymbols.Add(new CodeSymbol
                                {
                                    Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                                    FilePath = relativePath,
                                    Name = variable.Identifier.Text,
                                    FullyQualifiedName =
                                        $"{fullyQualifiedTypeName}.{variable.Identifier.Text}",
                                    Namespace = namespaceName,
                                    ContainingType = typeName,
                                    Signature =
                                        $"{field.Declaration.Type} {variable.Identifier.Text}",
                                    SymbolType = CodeSymbol.E_SymbolType.Field,
                                    StartLine = GetStartLine(variable)
                                });
                            }

                            break;

                        case EventDeclarationSyntax eventDeclaration:
                            codeSymbols.Add(new CodeSymbol
                            {
                                Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                                FilePath = relativePath,
                                Name = eventDeclaration.Identifier.Text,
                                FullyQualifiedName =
                                    $"{fullyQualifiedTypeName}.{eventDeclaration.Identifier.Text}",
                                Namespace = namespaceName,
                                ContainingType = typeName,
                                Signature =
                                    $"{eventDeclaration.Type} {eventDeclaration.Identifier.Text}",
                                SymbolType = CodeSymbol.E_SymbolType.Event,
                                StartLine = GetStartLine(eventDeclaration)
                            });

                            break;

                        case EventFieldDeclarationSyntax eventField:
                            foreach (VariableDeclaratorSyntax variable in
                                     eventField.Declaration.Variables)
                            {
                                codeSymbols.Add(new CodeSymbol
                                {
                                    Id = GetID(relativePath, fullyQualifiedTypeName, GetTypeSymbolType(typeNode), GetTypeSignature(typeNode)),
                                    FilePath = relativePath,
                                    Name = variable.Identifier.Text,
                                    FullyQualifiedName =
                                        $"{fullyQualifiedTypeName}.{variable.Identifier.Text}",
                                    Namespace = namespaceName,
                                    ContainingType = typeName,
                                    Signature =
                                        $"{eventField.Declaration.Type} {variable.Identifier.Text}",
                                    SymbolType = CodeSymbol.E_SymbolType.Event,
                                    StartLine = GetStartLine(variable)
                                });
                            }

                            break;
                    }
                }
            }

            return codeSymbols;
        }

        private static string GetNamespace(SyntaxNode node)
        {
            BaseNamespaceDeclarationSyntax? namespaceNode =
                node.Ancestors()
                    .OfType<BaseNamespaceDeclarationSyntax>()
                    .FirstOrDefault();

            return namespaceNode?.Name.ToString() ?? "";
        }

        private static string GetContainingType(SyntaxNode node)
        {
            IEnumerable<string> containingTypes =
                node.Ancestors()
                    .OfType<TypeDeclarationSyntax>()
                    .Reverse()
                    .Select(type => type.Identifier.Text);

            return string.Join(".", containingTypes);
        }

        private static string BuildFullyQualifiedName(
            string namespaceName,
            string containingType,
            string name)
        {
            var parts = new[]
            {
                namespaceName,
                containingType,
                name
            }
            .Where(part => !string.IsNullOrWhiteSpace(part));

            return string.Join(".", parts);
        }

        private static string GetTypeSignature(BaseTypeDeclarationSyntax typeNode)
        {
            string keyword = typeNode.Kind() switch
            {
                SyntaxKind.ClassDeclaration => "class",
                SyntaxKind.StructDeclaration => "struct",
                SyntaxKind.InterfaceDeclaration => "interface",
                SyntaxKind.RecordDeclaration => "record",
                SyntaxKind.RecordStructDeclaration => "record struct",
                SyntaxKind.EnumDeclaration => "enum",
                _ => "type"
            };

            return $"{keyword} {typeNode.Identifier.Text}";
        }

        private static CodeSymbol.E_SymbolType GetTypeSymbolType(
            BaseTypeDeclarationSyntax typeNode)
        {
            return typeNode.Kind() switch
            {
                SyntaxKind.ClassDeclaration => CodeSymbol.E_SymbolType.Class,
                SyntaxKind.StructDeclaration => CodeSymbol.E_SymbolType.Struct,
                SyntaxKind.InterfaceDeclaration => CodeSymbol.E_SymbolType.Interface,
                SyntaxKind.RecordDeclaration => CodeSymbol.E_SymbolType.Record,
                SyntaxKind.RecordStructDeclaration => CodeSymbol.E_SymbolType.Record,
                SyntaxKind.EnumDeclaration => CodeSymbol.E_SymbolType.Enum,
                _ => CodeSymbol.E_SymbolType.None
            };
        }

        private static int GetStartLine(SyntaxNode node)
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        private static string GetID(string relativePath, string fullyQualifiedTypeName, CodeSymbol.E_SymbolType symbolType, string signature)
        {
            return Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{relativePath}:{fullyQualifiedTypeName}:{symbolType}:{signature}")));
        }
    }
}

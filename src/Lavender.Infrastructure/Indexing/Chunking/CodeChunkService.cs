using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lavender.Core.DataTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lavender.Infrastructure.Indexing.Chunking
{
    public class CodeChunkService
    {
        private const int GenericChunkLineLimit = 200;

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".xaml",
            ".py",
            ".csproj",
            ".sln",
            ".json",
            ".md",
            ".txt"
        };

        private static readonly HashSet<string> SupportedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gitignore",
            "requirements.txt"
        };

        private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".venv",
            "__pycache__",
            "bin",
            "obj",
            "lavender_vectors",
            "node_modules",
            "packages",
            "artifacts"
        };

        public static List<CodeChunk> GetCodeChunksFromFolder(string dir)
        {
            var codeChunks = new List<CodeChunk>();

            foreach (string file in EnumerateIndexableFiles(dir))
            {
                codeChunks.AddRange(
                    Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                        ? GetCodeChunks(file)
                        : GetGenericFileChunks(file));
            }

            return codeChunks;
        }

        private static IEnumerable<string> EnumerateIndexableFiles(string root)
        {
            foreach (string filePath in SafeEnumerateFiles(root))
            {
                if (IsIndexableFile(filePath))
                {
                    yield return filePath;
                }
            }

            foreach (string directoryPath in SafeEnumerateDirectories(root))
            {
                if (ShouldIgnoreDirectory(directoryPath))
                {
                    continue;
                }

                foreach (string filePath in EnumerateIndexableFiles(directoryPath))
                {
                    yield return filePath;
                }
            }
        }

        private static bool IsIndexableFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            string fileName = Path.GetFileName(filePath);

            return SupportedExtensions.Contains(extension)
                || SupportedFileNames.Contains(fileName);
        }

        private static bool ShouldIgnoreDirectory(string directoryPath)
        {
            string directoryName = Path.GetFileName(directoryPath);
            return IgnoredDirectoryNames.Contains(directoryName);
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directoryPath)
        {
            try
            {
                return Directory.EnumerateFiles(directoryPath).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string directoryPath)
        {
            try
            {
                return Directory.EnumerateDirectories(directoryPath).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }

        private static List<CodeChunk> GetGenericFileChunks(string filePath)
        {
            string fileText = File.ReadAllText(filePath);
            string[] lines = fileText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            if (lines.Length <= GenericChunkLineLimit)
            {
                return new List<CodeChunk> { CreateGenericFileChunk(filePath, fileText, 1, lines.Length, "WholeFile") };
            }

            var chunks = new List<CodeChunk>();
            for (int startIndex = 0; startIndex < lines.Length; startIndex += GenericChunkLineLimit)
            {
                string chunkText = string.Join(
                    "\n",
                    lines.Skip(startIndex).Take(GenericChunkLineLimit));

                int startLine = startIndex + 1;
                int endLine = Math.Min(lines.Length, startIndex + GenericChunkLineLimit);

                chunks.Add(CreateGenericFileChunk(
                    filePath,
                    chunkText,
                    startLine,
                    endLine,
                    $"Lines-{startLine}-{endLine}"));
            }

            return chunks;
        }

        private static CodeChunk CreateGenericFileChunk(
            string filePath,
            string code,
            int startLine,
            int endLine,
            string chunkKey)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var chunk = new CodeChunk
            {
                Id = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{relativePath}:{chunkKey}"))),

                FilePath = filePath,
                RelativePath = relativePath,
                ChunkType = CodeChunk.E_ChunkType.WholeFile,
                Signature = $"File type: {GetFileKind(filePath)}",
                StartLine = startLine,
                EndLine = endLine,
                Code = code
            };

            chunk.EmbeddingText = BuildEmbeddingText(chunk);
            return chunk;
        }

        private static string GetFileKind(string filePath)
        {
            string extension = Path.GetExtension(filePath).TrimStart('.');
            return string.IsNullOrWhiteSpace(extension)
                ? Path.GetFileName(filePath)
                : extension;
        }

        private static List<CodeChunk> GetCodeChunks(string filePath)
        {
            var codeChunks = new List<CodeChunk>();

            var fileText = File.ReadAllText(filePath);
            var lines = fileText.Split('\n');
            SyntaxTree tree = CSharpSyntaxTree.ParseText(fileText);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            if (lines.Length <= 150)
            {
                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                Path.GetRelativePath(
                                    Directory.GetCurrentDirectory(),
                                    $"{filePath}:WholeFile")))),

                    FilePath = filePath,
                    RelativePath = Path.GetRelativePath(
                        Directory.GetCurrentDirectory(),
                        filePath),

                    ChunkType = CodeChunk.E_ChunkType.WholeFile,

                    StartLine = 1,
                    EndLine = lines.Length,

                    Code = root.ToFullString()
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                codeChunks.Add(chunk);

                return codeChunks;
            }

            codeChunks.Add(CreateFileSummaryChunk(filePath, root));

            foreach (ClassDeclarationSyntax classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (classNode.Members.OfType<FieldDeclarationSyntax>().Any())
                {
                    codeChunks.Add(GetClassFieldsChunk(filePath, classNode));
                }
                codeChunks.AddRange(GetMethodChunks(filePath, classNode));
                codeChunks.AddRange(GetConstructorChunks(filePath, classNode));
                codeChunks.AddRange(GetPropertyDeclarationChunks(filePath, classNode));
            }

            codeChunks.AddRange(GetRecordChunks(filePath, root));
            codeChunks.AddRange(GetStructChunks(filePath, root));
            codeChunks.AddRange(GetInterfaceChunks(filePath, root));
            codeChunks.AddRange(GetEnumChunks(filePath, root));

            return codeChunks;
        }

        private static CodeChunk CreateFileSummaryChunk(string filePath, SyntaxNode root)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var usings = root
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();

            var classes = root
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Select(c => c.Identifier.Text)
                .ToList();

            var methods = root
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => m.Identifier.Text)
                .ToList();

            string summary = $"""
            File: {relativePath}

            Usings:
            {string.Join("\n", usings.Select(u => $"- {u}"))}

            Classes:
            {string.Join("\n", classes.Select(c => $"- {c}"))}

            Methods:
            {string.Join("\n", methods.Select(m => $"- {m}"))}
            """;

            var chunk = new CodeChunk
            {
                Id = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{relativePath}:FileSummary"))),

                FilePath = filePath,
                RelativePath = relativePath,

                ChunkType = CodeChunk.E_ChunkType.FileSummary,

                StartLine = 1,
                EndLine = root.GetLocation().GetLineSpan().EndLinePosition.Line + 1,

                Code = summary,
            };

            chunk.EmbeddingText = BuildEmbeddingText(chunk);
            return chunk;
        }

        private static CodeChunk GetClassFieldsChunk(
            string filePath,
            ClassDeclarationSyntax classNode)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var fields = classNode
                .Members
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                .ToList();

            string summary = $"""
            Class: {classNode.Identifier.Text}

            Fields:
            {string.Join("\n", fields.Select(f => $"- {f}"))}
            """;

            var chunk = new CodeChunk
            {
                Id = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            $"{relativePath}:{classNode.Identifier.Text}:ClassFields"))),

                FilePath = filePath,
                RelativePath = relativePath,

                ChunkType = CodeChunk.E_ChunkType.ClassFields,
                ClassName = classNode.Identifier.Text,

                StartLine = classNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EndLine = classNode.GetLocation().GetLineSpan().EndLinePosition.Line + 1,

                Code = summary,
            };

            chunk.EmbeddingText = BuildEmbeddingText(chunk);
            return chunk;
        }

        private static List<CodeChunk> GetMethodChunks(
            string filePath,
            ClassDeclarationSyntax classNode)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var methodChunks = new List<CodeChunk>();

            foreach (var methodNode in classNode.Members.OfType<MethodDeclarationSyntax>())
            {
                var lineSpan = methodNode.SyntaxTree.GetLineSpan(methodNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string code =
                    methodNode.GetLeadingTrivia().ToFullString() +
                    methodNode.ToFullString();

                string? ns = methodNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                $"{relativePath}:{classNode.Identifier.Text}:{methodNode.Identifier.Text}:Method-LineNumber:{startLine}"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Method,
                    ClassName = classNode.Identifier.Text,
                    MemberName = methodNode.Identifier.Text,

                    Signature =
                        $"{methodNode.Modifiers} " +
                        $"{methodNode.ReturnType} " +
                        $"{methodNode.Identifier.Text}(" +
                        $"{string.Join(", ",
                            methodNode.ParameterList.Parameters.Select(
                                p => $"{p.Type} {p.Identifier.Text}"))})",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = code,
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                methodChunks.Add(chunk);
            }

            return methodChunks;
        }

        private static List<CodeChunk> GetConstructorChunks(
            string filePath,
            ClassDeclarationSyntax classNode)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var conChunks = new List<CodeChunk>();

            foreach (var conNode in classNode.Members.OfType<ConstructorDeclarationSyntax>())
            {
                var lineSpan = conNode.SyntaxTree.GetLineSpan(conNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string code =
                    conNode.GetLeadingTrivia().ToFullString() +
                    conNode.ToFullString();

                string? ns = conNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                $"{relativePath}:{classNode.Identifier.Text}:{conNode.Identifier.Text}:Constructor"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Constructor,
                    ClassName = classNode.Identifier.Text,
                    MemberName = conNode.Identifier.Text,

                    Signature =
                        $"{conNode.Modifiers} " +
                        $"{conNode.Identifier.Text}(" +
                        $"{string.Join(", ",
                            conNode.ParameterList.Parameters.Select(
                                p => $"{p.Type} {p.Identifier.Text}"))})",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = code,
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                conChunks.Add(chunk);
            }

            return conChunks;
        }

        private static List<CodeChunk> GetPropertyDeclarationChunks(
            string filePath,
            ClassDeclarationSyntax classNode)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var pdChunks = new List<CodeChunk>();

            foreach (var pdNode in classNode.Members.OfType<PropertyDeclarationSyntax>())
            {
                var lineSpan = pdNode.SyntaxTree.GetLineSpan(pdNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string code =
                    pdNode.GetLeadingTrivia().ToFullString() +
                    pdNode.ToFullString();

                string? ns = pdNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                $"{relativePath}:{classNode.Identifier.Text}:{pdNode.Identifier.Text}:Property"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Property,
                    ClassName = classNode.Identifier.Text,
                    MemberName = pdNode.Identifier.Text,

                    Signature =
                        $"{pdNode.Modifiers} " +
                        $"{pdNode.Type} " +
                        $"{pdNode.Identifier.Text}",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = code,
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                pdChunks.Add(chunk);
            }

            return pdChunks;
        }

        private static List<CodeChunk> GetRecordChunks(
            string filePath,
            CompilationUnitSyntax root)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var recordChunks = new List<CodeChunk>();

            foreach (var recordNode in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                var lineSpan = recordNode.SyntaxTree.GetLineSpan(recordNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string? ns = recordNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{relativePath}:{recordNode.Identifier.Text}:Record"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Record,
                    ClassName = recordNode.Identifier.Text,
                    MemberName = recordNode.Identifier.Text,

                    Signature =
                        $"{recordNode.Modifiers} record {recordNode.Identifier.Text}",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = recordNode.GetLeadingTrivia().ToFullString() + recordNode.ToFullString()
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                recordChunks.Add(chunk);
            }

            return recordChunks;
        }

        private static List<CodeChunk> GetStructChunks(
            string filePath,
            CompilationUnitSyntax root)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var structChunks = new List<CodeChunk>();

            foreach (var structNode in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            {
                var lineSpan = structNode.SyntaxTree.GetLineSpan(structNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string? ns = structNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{relativePath}:{structNode.Identifier.Text}:Struct"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Struct,
                    ClassName = structNode.Identifier.Text,
                    MemberName = structNode.Identifier.Text,

                    Signature =
                        $"{structNode.Modifiers} struct {structNode.Identifier.Text}",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = structNode.GetLeadingTrivia().ToFullString() + structNode.ToFullString()
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                structChunks.Add(chunk);
            }

            return structChunks;
        }

        private static List<CodeChunk> GetInterfaceChunks(
            string filePath,
            CompilationUnitSyntax root)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var interfaceChunks = new List<CodeChunk>();

            foreach (var interfaceNode in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                var lineSpan = interfaceNode.SyntaxTree.GetLineSpan(interfaceNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string? ns = interfaceNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{relativePath}:{interfaceNode.Identifier.Text}:Interface"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Interface,
                    ClassName = interfaceNode.Identifier.Text,
                    MemberName = interfaceNode.Identifier.Text,

                    Signature =
                        $"{interfaceNode.Modifiers} interface {interfaceNode.Identifier.Text}",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = interfaceNode.GetLeadingTrivia().ToFullString() + interfaceNode.ToFullString()
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                interfaceChunks.Add(chunk);
            }

            return interfaceChunks;
        }

        private static List<CodeChunk> GetEnumChunks(
            string filePath,
            CompilationUnitSyntax root)
        {
            string relativePath = Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                filePath);

            var enumChunks = new List<CodeChunk>();

            foreach (var enumNode in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var lineSpan = enumNode.SyntaxTree.GetLineSpan(enumNode.Span);

                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                string? ns = enumNode
                .Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?
                .Name
                .ToString();

                var chunk = new CodeChunk
                {
                    Id = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{relativePath}:{enumNode.Identifier.Text}:Enum"))),

                    FilePath = filePath,
                    RelativePath = relativePath,
                    Namespace = ns ?? "",

                    ChunkType = CodeChunk.E_ChunkType.Enum,
                    ClassName = enumNode.Identifier.Text,
                    MemberName = enumNode.Identifier.Text,

                    Signature =
                        $"{enumNode.Modifiers} enum {enumNode.Identifier.Text}",

                    StartLine = startLine,
                    EndLine = endLine,

                    Code = enumNode.GetLeadingTrivia().ToFullString() + enumNode.ToFullString()
                };

                chunk.EmbeddingText = BuildEmbeddingText(chunk);
                enumChunks.Add(chunk);
            }

            return enumChunks;
        }

        public static string BuildEmbeddingText(CodeChunk chunk)
        {
            return $"""
                File: {chunk.RelativePath}
                Namespace: {chunk.Namespace}
                Chunk Type: {chunk.ChunkType.ToString()}
                Class: {chunk.ClassName}
                Member: {chunk.MemberName}
                Signature: {chunk.Signature}

                Code: {chunk.Code}
                """;
        }
    }
}

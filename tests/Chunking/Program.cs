using Lavender.Core.DataTypes;
using Lavender.Infrastructure.Indexing.Chunking;
using Microsoft.ML.Tokenizers;

var tokenizer = TiktokenTokenizer.CreateForModel("text-embedding-3-small");
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
foreach (var kind in Enum.GetValues<CodeChunk.E_ChunkType>())
foreach (string separator in new[] { "\n", "\r\n", "\r", "" })
{
    string code = string.Join(separator, Enumerable.Range(0, 1200).Select(i => $"value_{i} = \"你好 😀\";"));
    var source = new CodeChunk { Id = "parent", FilePath = "Test.cs", RelativePath = "Test.cs",
        ChunkType = kind, StartLine = 21, EndLine = separator == "" ? 21 : 1220,
        Code = code, MemberName = "Test", ClassName = "Example", Signature = "void Test()" };
    var parts = CodeChunkService.SplitChunk(source).ToList();
    Check(parts.Count > 1, "Expected splitting");
    Check(string.Concat(parts.Select(p => p.Code)) == code, "Lost or duplicated text");
    Check(parts.Select(p => p.Id).Distinct().Count() == parts.Count, "Duplicate IDs");
    Check(parts.All(p => tokenizer.CountTokens(p.EmbeddingText) <= 2000), "Token overflow");
    Check(parts.All(p => p.MemberName == "Test" && p.ChunkType == kind), "Lost metadata");
    Check(parts.Select(p => p.Id).SequenceEqual(CodeChunkService.SplitChunk(source).Select(p => p.Id)), "Unstable IDs");
    if (kind is not CodeChunk.E_ChunkType.FileSummary and not CodeChunk.E_ChunkType.ClassFields)
        Check(parts[0].StartLine == 21 && parts[^1].EndLine == source.EndLine, "Wrong source range");
}
var small = new CodeChunk { Id = "small", Code = "return 1;" };
Check(ReferenceEquals(CodeChunkService.SplitChunk(small).Single(), small), "Small chunk changed");
try
{
    CodeChunkService.SplitChunk(new CodeChunk { Signature = string.Join(" ", Enumerable.Repeat("signature", 4000)), Code = "x" }).ToList();
    throw new Exception("Oversized metadata accepted");
}
catch (InvalidOperationException) { checks++; }
// An optional source fixture exercises all actual GetXChunks routes without embeddings.
if (args.Length > 0)
{
    var chunks = CodeChunkService.GetCodeChunksFromFolder(args[0]);
    Check(chunks.Count > 0, "No fixture chunks");
    Check(chunks.All(c => tokenizer.CountTokens(c.EmbeddingText) <= 2000), "Fixture overflow");
    Check(chunks.Count(c => c.ChunkType == CodeChunk.E_ChunkType.Constructor) > 1, "Constructor was not split");
}
Console.WriteLine($"Passed {checks} chunking checks.");

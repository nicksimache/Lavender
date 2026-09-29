using Lavender.McpServer;
using Lavender.McpServer.Tools.Editing;

string root = Path.Combine(Path.GetTempPath(), "LavenderEditingTools-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var state = new LavenderMcpState();
// Seed project selection only: this fixture must never start indexing or call an API.
typeof(LavenderMcpState).GetProperty(nameof(state.ProjectPath))!.SetValue(state, root);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
try
{
    var created = await new CreateFileTool(state).ExecuteAsync("Scratch/Test.cs", "class Test {}\n");
    Check(created.Success && File.Exists(Path.Combine(root, "Scratch/Test.cs")), "Create failed");
    var reader = new ReadFileTool(state);
    var read = await reader.ExecuteAsync("Scratch/Test.cs");
    Check(read.Success && read.ContentHash is not null, "Read/hash failed");
    var written = await new WriteLinesTool(state).ExecuteAsync("Scratch/Test.cs", 1, 1,
        "class Test { public int Value => 42; }", read.ContentHash!);
    Check(written.Success && written.Changed, "Write failed: " + written.Message);
    var verified = await reader.ExecuteAsync("Scratch/Test.cs");
    Check(verified.Content!.Contains("Value => 42"), "Write did not reach disk");
    var stale = await new WriteLinesTool(state).ExecuteAsync("Scratch/Test.cs", 1, 1, "lost", read.ContentHash!);
    Check(!stale.Success, "Stale edit was accepted");
    var moved = await new MoveFileTool(state).ExecuteAsync("Scratch/Test.cs", "Scratch/Moved.cs", verified.ContentHash!);
    Check(moved.Success, "Move failed");
    var deleted = await new DeleteFileTool(state).ExecuteAsync("Scratch/Moved.cs", verified.ContentHash!);
    Check(deleted.Success && !File.Exists(Path.Combine(root, "Scratch/Moved.cs")), "Delete failed");
    Check(state.RequiresReindex, "Edits did not invalidate index");
    Console.WriteLine("PASS: create/read/write/read-back/stale-hash/move/delete/index invalidation through MCP tool classes.");
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(root).StartsWith("LavenderEditingTools-")) Directory.Delete(root, true);
}

using Lavender.Application.Agent;
using Lavender.Infrastructure.AI;
using Lavender.Infrastructure.FileSystem;

string temp = Path.Combine(Path.GetTempPath(), "LavenderMemoryTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
try
{
    string history = Path.Combine(temp, "history");
    var store = new JsonConversationStore(history);
    var a = await store.CreateAsync();
    a.ProjectPath = Path.Combine(temp, "ProjectA");
    a.ContextFiles.Add(Path.Combine(a.ProjectPath, "Code.cs"));
    a.Summary = "The user chose a local search index.";
    a.SummarizedTurnCount = 1;
    a.Turns.Add(new ConversationTurn { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow,
        UserMessage = "Find the indexer", AssistantMessage = "It is in Indexer.cs", Status = AgentRunStatus.Completed });
    await store.SaveAsync(a);
    var b = await store.CreateAsync();
    b.ProjectPath = Path.Combine(temp, "ProjectB");
    await store.SaveAsync(b);
    var reopened = new JsonConversationStore(history);
    Check((await reopened.ListAsync(a.ProjectPath)).Select(c => c.Id).SequenceEqual(new[] { a.Id }), "project isolation survives restart");
    var restored = (await reopened.LoadAsync(a.Id))!;
    Check(restored.Turns[0].AssistantMessage == "It is in Indexer.cs" && restored.ContextFiles.Count == 1, "messages and selected files restored");
    Check(restored.Summary == a.Summary && restored.SummarizedTurnCount == 1, "summary restored");
    Check(restored.Title == "Find the indexer", "chat title follows first message");
    await File.WriteAllTextAsync(Path.Combine(history, Guid.NewGuid().ToString("N") + ".json"), "broken json");
    Check((await reopened.ListAsync(a.ProjectPath)).Count == 1, "damaged history does not hide valid chats");
    var ephemeral = new JsonConversationStore(Path.Combine(temp, "ephemeral"), persist: false);
    var transient = await ephemeral.CreateAsync();
    transient.Turns.Add(a.Turns[0]);
    await ephemeral.SaveAsync(transient);
    Check((await ephemeral.LoadAsync(transient.Id))!.Turns.Count == 1 && !Directory.Exists(Path.Combine(temp, "ephemeral")), "nonpersistent conversations retain turns without disk writes");
    var step = new AgentStep { Iteration = 1, CreatedAt = DateTimeOffset.UtcNow };
    step.ToolCalls.Add(new ToolExecutionRecord { CallId = "call1", ToolName = "read_file", ArgumentsJson = "{\"path\":\"Indexer.cs\"}", ResultJson = new string('x', 20_000), StartedAt = DateTimeOffset.UtcNow });
    restored.Turns[0].Steps.Add(step);
    string evidence = ConversationEvidence.Build(restored.Turns[0], 1_000);
    Check(evidence.Length <= 1_000 && evidence.Contains("read_file") && evidence.Contains("truncated"), "tool evidence is bounded and attributed");
    Check(ConversationEvidence.Build(restored.Turns[0], 0) == "", "zero evidence budget respected");
    string project = Path.Combine(temp, "source");
    Directory.CreateDirectory(project);
    await File.WriteAllTextAsync(Path.Combine(project, "A.cs"), "class A {} ");
    string? before = ProjectSnapshot.Compute(project);
    Directory.CreateDirectory(Path.Combine(project, "bin"));
    await File.WriteAllTextAsync(Path.Combine(project, "bin", "Generated.cs"), "class Ignore {} ");
    Check(ProjectSnapshot.Compute(project) == before, "build output does not invalidate evidence");
    await File.AppendAllTextAsync(Path.Combine(project, "A.cs"), "// source changed");
    Check(ProjectSnapshot.Compute(project) != before, "source changes invalidate evidence");
    Check((await reopened.ListAsync(a.ProjectPath + Path.DirectorySeparatorChar)).Count == 1,
        "trailing separators share project history");
    Check((await reopened.ListAsync(a.ProjectPath!.ToUpperInvariant())).Count == 1,
        "Windows path casing shares project history");
    await reopened.ClearProjectAsync(a.ProjectPath);
    Check((await reopened.ListAsync(a.ProjectPath)).Count == 0, "clear removes current project chats from memory");
    var afterClear = new JsonConversationStore(history);
    Check((await afterClear.ListAsync(a.ProjectPath)).Count == 0 && (await afterClear.ListAsync(b.ProjectPath)).Count == 1,
        "clear persists across restart and preserves other projects");
    string wrapper = Path.Combine(temp, "wrapper");
    string inner = Path.Combine(wrapper, "ActualProject");
    Directory.CreateDirectory(inner);
    string solution = Path.Combine(inner, "App.sln");
    await File.WriteAllTextAsync(solution, "fixture");
    Check(ProjectFolderResolver.FindSolution(wrapper) == solution && ProjectFolderResolver.FindSolution(inner) == solution,
        "wrapper folder and project folder resolve to the same solution");
    string preferences = Path.Combine(temp, "preferences");
    var lastProject = new LastProjectStore(preferences);
    Check(await lastProject.LoadAsync() is null, "first launch has no previous project");
    await lastProject.SaveAsync(inner, solution);
    var previous = await new LastProjectStore(preferences).LoadAsync();
    Check(previous?.ProjectPath == inner && previous.SolutionPath == solution, "last project survives restart");
    await File.WriteAllTextAsync(Path.Combine(preferences, "last-project.json"), "invalid json");
    Check(await lastProject.LoadAsync() is null, "damaged startup preference is ignored");
    await lastProject.SaveAsync(inner, Path.Combine(inner, "Missing.sln"));
    Check(await lastProject.LoadAsync() is null, "missing previous project does not block startup");
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LavenderMemoryTests")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(temp).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected test cleanup path.");
    Directory.Delete(temp, recursive: true);
}

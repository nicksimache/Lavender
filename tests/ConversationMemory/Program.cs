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
    string? previousReportDirectory = Environment.GetEnvironmentVariable("LAVENDER_INDEX_REPORT_DIRECTORY");
    try
    {
        Environment.SetEnvironmentVariable("LAVENDER_INDEX_REPORT_DIRECTORY", Path.Combine(temp, "timings"));
        var timings = new Lavender.Infrastructure.Indexing.IndexingTimings(temp);
        using (var initial = System.Text.Json.JsonDocument.Parse(File.ReadAllText(timings.ReportPath)))
            Check(initial.RootElement.GetProperty("Status").GetString() == "Running", "timing report exists before first stage completes");
        using (timings.Stage("scan")) { }
        timings.Error = "Test failure";
        timings.Finish();
        using var finished = System.Text.Json.JsonDocument.Parse(File.ReadAllText(timings.ReportPath));
        Check(finished.RootElement.GetProperty("Status").GetString() == "Failed"
            && finished.RootElement.GetProperty("StageMilliseconds").TryGetProperty("scan", out _),
            "failed timing report retains completed stages in selected directory");
    }
    finally { Environment.SetEnvironmentVariable("LAVENDER_INDEX_REPORT_DIRECTORY", previousReportDirectory); }
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
    ToolExecutionRecord MakeRecord(string name) => new()
    {
        CallId = Guid.NewGuid().ToString(), ToolName = name, ArgumentsJson = "{\"relativePath\":\"File.cs\",\"startLine\":2,\"endLine\":4}",
        StartedAt = DateTimeOffset.UtcNow
    };
    var inspected = MakeRecord("git_diff");
    ToolUseSummary.RecordOutcome(inspected, "{\"structuredContent\":{\"Success\":true,\"Message\":\"Diff loaded\"}}");
    var created = MakeRecord("create_file");
    ToolUseSummary.RecordOutcome(created, "{\"content\":[{\"text\":\"{\\\"success\\\":true,\\\"message\\\":\\\"Created File.cs\\\"}\"}]}");
    var notWritten = MakeRecord("write_lines");
    ToolUseSummary.RecordOutcome(notWritten, "{\"structuredContent\":{\"success\":false,\"message\":\"Not implemented; no file changed\"}}");
    Check(inspected.Outcome == "succeeded" && created.Outcome == "succeeded" && notWritten.Outcome == "failed",
        "tool outcomes recognize structured and text results, including inactive skeletons");
    var errorRecord = MakeRecord("broken_tool");
    ToolUseSummary.RecordOutcome(errorRecord, "{\"isError\":true,\"content\":[{\"text\":\"failure\"}]}");
    Check(errorRecord.Outcome == "failed", "MCP protocol error is not classified as success");
    var plainRead = MakeRecord("read_file");
    ToolUseSummary.RecordOutcome(plainRead, "{\"content\":[{\"text\":\"ordinary source text\"}]}");
    Check(plainRead.Outcome == "returned", "plain read output is not treated as confirmed modification");
    string finalPrompt = ToolUseSummary.BuildFinalPrompt(new[] { inspected, created, notWritten }.Select(ToolUseSummary.FromRecord).ToArray());
    Check(finalPrompt.IndexOf("git_diff") < finalPrompt.IndexOf("create_file")
        && finalPrompt.IndexOf("create_file") < finalPrompt.IndexOf("write_lines")
        && finalPrompt.Contains("failed") && finalPrompt.Contains("File.cs"), "final prompt preserves ordered actions, paths and failures");
    var auditStep = new AgentStep { Iteration = 1, CreatedAt = DateTimeOffset.UtcNow };
    auditStep.ToolCalls.AddRange(new[] { inspected, created, notWritten });
    b.Turns.Add(new ConversationTurn { Id = Guid.NewGuid(), UserMessage = "fixture", CreatedAt = DateTimeOffset.UtcNow, Steps = [auditStep] });
    await store.SaveAsync(b);
    var persistedAudit = await new JsonConversationStore(history).LoadAsync(b.Id);
    Check(persistedAudit!.Turns.Last().Steps[0].ToolCalls[2].Outcome == "failed", "tool outcomes survive chat reload");

    string untouched = Path.Combine(temp, "untouched.cs");
    await File.WriteAllTextAsync(untouched, "original");

    string createRoot = Path.Combine(temp, "create-project");
    Directory.CreateDirectory(createRoot);
    string createdPath = await ProjectFileCreator.CreateAsync(createRoot, "src/Example.cs", "// café\r\nclass Example {}\r\n");
    Check(createdPath == "src/Example.cs" && await File.ReadAllTextAsync(Path.Combine(createRoot, createdPath)) == "// café\r\nclass Example {}\r\n",
        "create file makes parent directories and preserves Unicode and newlines");
    async Task<bool> Refuses(string path)
    {
        try { await ProjectFileCreator.CreateAsync(createRoot, path, "overwrite"); return false; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }
    Check(await Refuses("src/Example.cs") && (await File.ReadAllTextAsync(Path.Combine(createRoot, createdPath))).Contains("café"),
        "create never overwrites existing contents");
    Check(await Refuses("../escape.cs") && await Refuses(Path.Combine(temp, "absolute.cs"))
        && await Refuses(".git/config") && await Refuses("file.cs:stream") && await Refuses("NUL.txt")
        && await Refuses("name. ") && await Refuses("src"), "create rejects escaping, protected, device and directory paths");
    await ProjectFileCreator.CreateAsync(createRoot, "empty.txt", "");
    Check(new FileInfo(Path.Combine(createRoot, "empty.txt")).Length == 0, "create supports empty files");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        bool stopped = false;
        try { await ProjectFileCreator.CreateAsync(createRoot, "cancelled.txt", "x", cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && !File.Exists(Path.Combine(createRoot, "cancelled.txt")), "cancelled creation leaves no destination file");
    }
    bool encodingFailed = false;
    try { await ProjectFileCreator.CreateAsync(createRoot, "bad.txt", "\uD800"); }
    catch (System.Text.EncoderFallbackException) { encodingFailed = true; }
    Check(encodingFailed && !File.Exists(Path.Combine(createRoot, "bad.txt"))
        && Directory.GetFiles(createRoot, ".lavender-create-*.tmp", SearchOption.AllDirectories).Length == 0,
        "failed writes leave no partial destination or temporary file");
    async Task<bool> RaceCreate(string content)
    {
        try { await ProjectFileCreator.CreateAsync(createRoot, "race.txt", content); return true; }
        catch (IOException) { return false; }
    }
    bool[] race = await Task.WhenAll(RaceCreate("first"), RaceCreate("second"));
    Check(race.Count(x => x) == 1 && new[] { "first", "second" }.Contains(await File.ReadAllTextAsync(Path.Combine(createRoot, "race.txt"))),
        "concurrent creates publish one complete file without overwrite");
    var serializedFileResult = System.Text.Json.JsonSerializer.Serialize(new Lavender.McpServer.Tools.Editing.FileToolResult(true, true, "created", "Created src/Example.cs", "src/Example.cs"));
    var fileRecord = MakeRecord("lavender_create_file");
    ToolUseSummary.RecordOutcome(fileRecord, serializedFileResult);
    Check(fileRecord.Outcome == "succeeded" && serializedFileResult.Contains("\"status\":\"created\"")
        && serializedFileResult.Contains("\"path\":\"src/Example.cs\""), "create result serializes unique fields and confirms success to the ledger");

    var editor = new ProjectFileEditor(createRoot, Path.Combine(temp, "backups"));
    await ProjectFileCreator.CreateAsync(createRoot, "edit.cs", "one\r\ntwo\r\nthree\r\n");
    var readEdit = await editor.ReadAsync("edit.cs");
    Check(readEdit.LineCount == 3 && readEdit.ContentHash.Length == 64 && readEdit.Content.Contains("two"),
        "read returns contents, physical line count and content hash");
    var written = await editor.WriteLinesAsync("edit.cs", 2, 2, "two updated\nextra", readEdit.ContentHash);
    Check(await File.ReadAllTextAsync(Path.Combine(createRoot, "edit.cs")) == "one\r\ntwo updated\r\nextra\r\nthree\r\n"
        && await File.ReadAllTextAsync(written.BackupPath!) == readEdit.Content, "line replacement preserves CRLF and saves original bytes");
    bool staleRejected = false;
    try { await editor.DeleteAsync("edit.cs", readEdit.ContentHash); }
    catch (InvalidOperationException) { staleRejected = true; }
    Check(staleRejected && File.Exists(Path.Combine(createRoot, "edit.cs")), "stale hash prevents deletion after an edit");
    var inserted = await editor.WriteLinesAsync("edit.cs", 1, 0, "header", written.ContentHash!);
    var afterInsert = await editor.ReadAsync("edit.cs");
    Check(afterInsert.LineCount == 5 && afterInsert.Content.StartsWith("header\r\none"), "insertion before first line preserves following code");
    var removed = await editor.WriteLinesAsync("edit.cs", 2, 3, "", inserted.ContentHash!);
    Check((await editor.ReadAsync("edit.cs")).Content == "header\r\nextra\r\nthree\r\n", "empty replacement removes only the selected lines");
    var noChange = await editor.WriteLinesAsync("edit.cs", 2, 2, "extra", removed.ContentHash!);
    Check(!noChange.Changed && noChange.BackupPath is null, "identical line edit reports no change");
    bool invalidRange = false;
    try { await editor.WriteLinesAsync("edit.cs", 5, 7, "bad", removed.ContentHash!); }
    catch (ArgumentException) { invalidRange = true; }
    Check(invalidRange && (await editor.ReadAsync("edit.cs")).ContentHash == removed.ContentHash, "invalid line ranges leave the file unchanged");
    await ProjectFileCreator.CreateAsync(createRoot, "append.txt", "first");
    var appendRead = await editor.ReadAsync("append.txt");
    await editor.WriteLinesAsync("append.txt", 2, 1, "last", appendRead.ContentHash);
    Check((await editor.ReadAsync("append.txt")).Content == "first\nlast", "append separates lines when original has no final newline");
    await ProjectFileCreator.CreateAsync(createRoot, "append-newline.txt", "first\r\n");
    var terminatedRead = await editor.ReadAsync("append-newline.txt");
    await editor.WriteLinesAsync("append-newline.txt", 2, 1, "last", terminatedRead.ContentHash);
    Check((await editor.ReadAsync("append-newline.txt")).Content == "first\r\nlast\r\n", "append retains existing final newline convention");
    var emptyRead = await editor.ReadAsync("empty.txt");
    await editor.WriteLinesAsync("empty.txt", 1, 0, "first line", emptyRead.ContentHash);
    Check((await editor.ReadAsync("empty.txt")).Content == "first line", "insertion populates an empty file");
    var utf16 = new System.Text.UnicodeEncoding(false, true, true);
    await File.WriteAllBytesAsync(Path.Combine(createRoot, "unicode.txt"), utf16.GetPreamble().Concat(utf16.GetBytes("α\nbeta\n")).ToArray());
    var unicodeRead = await editor.ReadAsync("unicode.txt");
    await editor.WriteLinesAsync("unicode.txt", 2, 2, "β", unicodeRead.ContentHash);
    byte[] unicodeBytes = await File.ReadAllBytesAsync(Path.Combine(createRoot, "unicode.txt"));
    Check(unicodeBytes[0] == 0xFF && unicodeBytes[1] == 0xFE && utf16.GetString(unicodeBytes[2..]) == "α\nβ\n",
        "line writes preserve UTF-16 encoding and BOM");
    var movedEdit = await editor.MoveAsync("edit.cs", "nested/Renamed.cs", removed.ContentHash!);
    Check(!File.Exists(Path.Combine(createRoot, "edit.cs")) && File.Exists(Path.Combine(createRoot, "nested", "Renamed.cs"))
        && File.Exists(movedEdit.BackupPath!), "move creates destination folders and retains a backup");
    bool collision = false;
    try { await editor.MoveAsync("nested/Renamed.cs", "empty.txt", movedEdit.ContentHash!); }
    catch (IOException) { collision = true; }
    Check(collision && (await editor.ReadAsync("empty.txt")).Content == "first line", "move cannot overwrite another file");
    bool escape = false;
    try { await editor.MoveAsync("nested/Renamed.cs", "../escape.cs", movedEdit.ContentHash!); }
    catch (ArgumentException) { escape = true; }
    Check(escape && File.Exists(Path.Combine(createRoot, "nested", "Renamed.cs")), "move rejects an escaping destination");
    using (var stopEdit = new CancellationTokenSource())
    {
        stopEdit.Cancel();
        try { await editor.DeleteAsync("nested/Renamed.cs", movedEdit.ContentHash!, stopEdit.Token); }
        catch (OperationCanceledException) { }
        Check(File.Exists(Path.Combine(createRoot, "nested", "Renamed.cs")), "cancelled delete retains the file");
    }
    var deletedEdit = await editor.DeleteAsync("nested/Renamed.cs", movedEdit.ContentHash!);
    Check(!File.Exists(Path.Combine(createRoot, "nested", "Renamed.cs"))
        && await File.ReadAllTextAsync(deletedEdit.BackupPath!) == "header\r\nextra\r\nthree\r\n",
        "delete removes only the requested file and keeps recoverable original contents");
    Check(Directory.GetFiles(createRoot, ".lavender-edit-*.tmp", SearchOption.AllDirectories).Length == 0,
        "completed operations leave no temporary edit files");

    string gitFixture = Path.Combine(temp, "git");
    Directory.CreateDirectory(gitFixture);
    async Task Git(params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = gitFixture,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        if (process.ExitCode != 0) throw new Exception(await stderr);
        await stderr;
    }
    await Git("init", "--quiet");
    await File.WriteAllTextAsync(Path.Combine(gitFixture, "Code[1].cs"), "original\n");
    await Git("add", "--", "Code[1].cs");
    await File.WriteAllTextAsync(Path.Combine(gitFixture, "Code[1].cs"), "changed\n");
    await Git("config", "diff.external", "nonexistent-external-diff-fixture");
    string gitDiagnostics = Path.Combine(temp, "git-diagnostics");
    var gitService = new Lavender.Git.GitContextService(gitFixture, gitDiagnostics);
    var working = await gitService.GetWorkingTreeDiffAsync("Code[1].cs");
    var staged = await gitService.GetStagedDiffAsync("Code[1].cs");
    Check(working.Succeeded && working.UnifiedDiff.Contains("+changed") && working.UnifiedDiff.Contains("-original"),
        "working diff handles literal filenames and disables external diff helpers");
    Check(staged.Succeeded && staged.UnifiedDiff.Contains("+original") && !staged.UnifiedDiff.Contains("+changed"),
        "staged diff is distinct from working changes");
    bool rejected = false;
    try { await gitService.GetWorkingTreeDiffAsync("../untouched.cs"); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Git diff rejects paths outside the selected project");
    Directory.CreateDirectory(Path.Combine(gitFixture, "bin"));
    await File.WriteAllTextAsync(Path.Combine(gitFixture, "bin", "Generated.txt"), "before\n");
    await Git("add", "--", "bin/Generated.txt");
    await File.WriteAllTextAsync(Path.Combine(gitFixture, "bin", "Generated.txt"), "after\n");
    var sourceOnly = await gitService.GetWorkingTreeDiffAsync();
    var explicitGenerated = await gitService.GetWorkingTreeDiffAsync("bin/Generated.txt");
    Check(sourceOnly.Succeeded && sourceOnly.UnifiedDiff.Contains("Code[1].cs")
        && !sourceOnly.UnifiedDiff.Contains("Generated.txt") && explicitGenerated.UnifiedDiff.Contains("+after"),
        "whole-project diff excludes generated folders but explicit file requests work");
    Check(Directory.GetFiles(gitDiagnostics, "*.json").Length > 0
        && Directory.GetFiles(gitDiagnostics, "*.trace.jsonl").Length > 0,
        "Git command diagnostics and trace files are written");
    string fakeInstall = Path.Combine(temp, "git-install");
    Directory.CreateDirectory(Path.Combine(fakeInstall, "cmd"));
    Directory.CreateDirectory(Path.Combine(fakeInstall, "mingw64", "bin"));
    string launcher = Path.Combine(fakeInstall, "cmd", "git.exe");
    string binary = Path.Combine(fakeInstall, "mingw64", "bin", "git.exe");
    await File.WriteAllTextAsync(launcher, "fixture");
    await File.WriteAllTextAsync(binary, "fixture");
    Check(Lavender.Git.GitContextService.ResolveGitExecutable(Path.GetDirectoryName(launcher)) == binary,
        "Windows Git resolution bypasses the command launcher");
    File.Delete(binary);
    Check(Lavender.Git.GitContextService.ResolveGitExecutable(Path.GetDirectoryName(launcher)) == launcher,
        "portable Git without a sibling binary keeps its original executable");
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LavenderMemoryTests")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(temp).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected test cleanup path.");
    // Git object files can be read-only on Windows; this is the verified temporary fixture only.
    foreach (string file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(temp, recursive: true);
}

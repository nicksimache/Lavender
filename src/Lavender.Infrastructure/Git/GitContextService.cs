using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Lavender.Git;

public sealed class GitFileStatus
{
    public string Path { get; init; } = "";
    public string IndexStatus { get; init; } = " ";
    public string WorkingTreeStatus { get; init; } = " ";
    public string? OriginalPath { get; init; }
    public bool IsStaged => IndexStatus != " " && IndexStatus != "?";
    public bool IsConflict =>
        IndexStatus == "U"
        || WorkingTreeStatus == "U"
        || (IndexStatus == "A" && WorkingTreeStatus == "A")
        || (IndexStatus == "D" && WorkingTreeStatus == "D");
}

public sealed class GitRepositoryStatus
{
    public bool IsRepository { get; init; }
    public bool IsGitAvailable { get; init; } = true;
    public string RepositoryRoot { get; init; } = "";
    public string? Branch { get; init; }
    public IReadOnlyList<GitFileStatus> Files { get; init; } = Array.Empty<GitFileStatus>();
    public string? Error { get; init; }
}

public sealed class GitDiffResult
{
    public string RepositoryRoot { get; init; } = "";
    public string? RequestedFile { get; init; }
    public bool IsStaged { get; init; }
    public string UnifiedDiff { get; init; } = "";
    public int ExitCode { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => ExitCode == 0 && Error is null;
}

public sealed class GitCommitInfo
{
    public string Hash { get; init; } = "";
    public string ShortHash { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public string AuthorEmail { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string Subject { get; init; } = "";
}

/// <summary>Executes only read-only Git commands using argument-safe process invocation.</summary>
public sealed class GitContextService
{
    private readonly string _workingDirectory;
    private readonly string _diagnosticsDirectory;

    public GitContextService(string selectedPath, string? diagnosticsDirectory = null)
    {
        _workingDirectory = Directory.Exists(selectedPath)
            ? selectedPath
            : Path.GetDirectoryName(selectedPath) ?? selectedPath;
        _diagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lavender", "Diagnostics", "Git");
    }

    public async Task<GitRepositoryStatus> GetStatusAsync(CancellationToken token = default)
    {
        CommandResult root = await RunAsync(new[] { "rev-parse", "--show-toplevel" }, token);
        if (root.StartError is not null)
        {
            return new() { IsGitAvailable = false, Error = root.StartError };
        }

        if (root.ExitCode != 0)
        {
            return new() { IsRepository = false, Error = root.Error.Trim() };
        }

        string repo = root.Output.Trim();
        CommandResult branch = await RunAsync(new[] { "branch", "--show-current" }, token);
        CommandResult status = await RunAsync(new[] { "status", "--porcelain=v1", "-z" }, token);

        if (status.ExitCode != 0)
        {
            return new() { IsRepository = true, RepositoryRoot = repo, Error = status.Error.Trim() };
        }

        return new() { IsRepository = true, RepositoryRoot = repo, Branch = branch.Output.Trim(), Files = ParseStatus(status.Output) };
    }

    public Task<GitDiffResult> GetWorkingTreeDiffAsync(string? relativeFilePath = null, CancellationToken token = default) => GetDiffAsync(false, relativeFilePath, token);
    public Task<GitDiffResult> GetStagedDiffAsync(string? relativeFilePath = null, CancellationToken token = default) => GetDiffAsync(true, relativeFilePath, token);

    private async Task<GitDiffResult> GetDiffAsync(bool staged, string? file, CancellationToken token)
    {
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color" };

        if (staged)
        {
            args.Add("--cached");
        }

        if (file is not null)
        {
            string fullPath = Path.GetFullPath(Path.Combine(_workingDirectory, file));
            string relative = Path.GetRelativePath(Path.GetFullPath(_workingDirectory), fullPath);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new ArgumentException("The requested diff path is outside the selected project.");
            args.Add("--");
            args.Add(":(literal)" + relative.Replace('\\', '/'));
        }
        else
        {
            // These folders can be tracked despite .gitignore and churn while Lavender/VS run.
            args.Add("--");
            args.Add(".");
            foreach (string folder in new[] { "bin", "obj", ".vs", "artifacts", ".venv", "node_modules" })
                args.Add($":(exclude,glob)**/{folder}/**");
        }

        CommandResult command = await RunAsync(args, token);
        CommandResult root = await RunAsync(new[] { "rev-parse", "--show-toplevel" }, token);

        return new()
        {
            RepositoryRoot = root.Output.Trim(),
            RequestedFile = file,
            IsStaged = staged,
            UnifiedDiff = command.Output,
            ExitCode = command.ExitCode,
            Error = command.StartError ?? (command.ExitCode == 0 ? null : command.Error.Trim())
        };
    }

    public async Task<IReadOnlyList<GitCommitInfo>> GetRecentCommitsAsync(int count, CancellationToken token = default)
    {
        if (count <= 0)
        {
            return Array.Empty<GitCommitInfo>();
        }

        CommandResult r = await RunAsync(new[] { "log", $"-{count}", "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1e" }, token);

        if (r.ExitCode != 0)
        {
            return Array.Empty<GitCommitInfo>();
        }

        return r.Output
            .Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Trim('\r', '\n').Split('\x1f'))
            .Where(x => x.Length == 6)
            .Select(x => new GitCommitInfo
            {
                Hash = x[0],
                ShortHash = x[1],
                AuthorName = x[2],
                AuthorEmail = x[3],
                Date = DateTimeOffset.Parse(x[4], CultureInfo.InvariantCulture),
                Subject = x[5]
            })
            .ToArray();
    }

    public static IReadOnlyList<GitFileStatus> ParseStatus(string porcelain)
    {
        string[] entries = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<GitFileStatus>();

        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            string? original = null;
            string path = entry[3..];

            if (entry[0] is 'R' or 'C' && i + 1 < entries.Length)
            {
                original = entries[++i];
            }

            files.Add(new()
            {
                IndexStatus = entry[0].ToString(),
                WorkingTreeStatus = entry[1].ToString(),
                Path = path,
                OriginalPath = original
            });
        }

        return files;
    }

    private async Task<CommandResult> RunAsync(IEnumerable<string> args, CancellationToken token)
    {
        string[] arguments = args.ToArray();
        string executable = ResolveGitExecutable();
        string command = "git --no-pager " + string.Join(" ", arguments.Select(a => JsonSerializer.Serialize(a)));
        string? diagnosticPath = null;
        string? tracePath = null;
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            string id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            diagnosticPath = Path.Combine(_diagnosticsDirectory, id + ".json");
            tracePath = Path.Combine(_diagnosticsDirectory, id + ".trace.jsonl");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var stopwatch = Stopwatch.StartNew();
        void Report(string status, int? exitCode, string stdout, string stderr)
        {
            if (diagnosticPath is null) return;
            try
            {
                File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(new
                {
                    Command = command, Executable = executable, WorkingDirectory = _workingDirectory, Status = status,
                    ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds, ExitCode = exitCode,
                    OutputCharacters = stdout.Length, Error = stderr[..Math.Min(stderr.Length, 8000)],
                    TracePath = tracePath
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var psi = new ProcessStartInfo(executable) { WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--no-pager");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (tracePath is not null) psi.Environment["GIT_TRACE2_EVENT"] = tracePath.Replace('\\', '/');
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        Report("Starting", null, "", "");
        try
        {
            if (!process.Start())
            {
                return new(-1, "", "", "Git failed to start.");
            }
            // Git commands here never consume stdin. Do not inherit the live MCP protocol pipe.
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report("Start failed", null, "", ex.Message);
            return new(-1, "", "", ex.Message);
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            Report(token.IsCancellationRequested ? "Cancelled" : "Timed out", process.ExitCode, await output, await error);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"{command} exceeded 10 seconds in '{_workingDirectory}' and was terminated. " +
                (diagnosticPath is null ? "Could not create a diagnostics file." : $"Diagnostics: {diagnosticPath}"));
        }
        Report("Exited", process.ExitCode, await output, await error);
        return new(process.ExitCode, await output, await error, null);
    }

    internal static string ResolveGitExecutable(string? searchPath = null)
    {
        if (!OperatingSystem.IsWindows()) return "git";
        foreach (string entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (!Path.IsPathRooted(directory)) continue;
            string candidate = Path.Combine(directory, "git.exe");
            if (!File.Exists(candidate)) continue;
            // cmd/git.exe is Git for Windows' launcher. Invoke the real binary directly in
            // detached MCP processes; it can otherwise stall before Git initializes tracing.
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)).Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                string installation = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory))!;
                foreach (string architecture in new[] { "mingw64", "mingw32" })
                {
                    string binary = Path.Combine(installation, architecture, "bin", "git.exe");
                    if (File.Exists(binary)) return binary;
                }
            }
            return candidate;
        }
        return "git";
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error, string? StartError);
}

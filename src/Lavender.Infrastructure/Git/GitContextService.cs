using System.Diagnostics;
using System.Globalization;

namespace Lavender.Git;

public sealed class GitFileStatus { public string Path { get; init; } = ""; public string IndexStatus { get; init; } = " "; public string WorkingTreeStatus { get; init; } = " "; public string? OriginalPath { get; init; } public bool IsStaged => IndexStatus != " " && IndexStatus != "?"; public bool IsConflict => IndexStatus == "U" || WorkingTreeStatus == "U" || (IndexStatus == "A" && WorkingTreeStatus == "A") || (IndexStatus == "D" && WorkingTreeStatus == "D"); }
public sealed class GitRepositoryStatus { public bool IsRepository { get; init; } public bool IsGitAvailable { get; init; } = true; public string RepositoryRoot { get; init; } = ""; public string? Branch { get; init; } public IReadOnlyList<GitFileStatus> Files { get; init; } = Array.Empty<GitFileStatus>(); public string? Error { get; init; } }
public sealed class GitDiffResult { public string RepositoryRoot { get; init; } = ""; public string? RequestedFile { get; init; } public bool IsStaged { get; init; } public string UnifiedDiff { get; init; } = ""; public int ExitCode { get; init; } public string? Error { get; init; } public bool Succeeded => ExitCode == 0 && Error is null; }
public sealed class GitCommitInfo { public string Hash { get; init; } = ""; public string ShortHash { get; init; } = ""; public string AuthorName { get; init; } = ""; public string AuthorEmail { get; init; } = ""; public DateTimeOffset Date { get; init; } public string Subject { get; init; } = ""; }

/// <summary>Executes only read-only Git commands using argument-safe process invocation.</summary>
public sealed class GitContextService
{
    private readonly string _workingDirectory;
    public GitContextService(string selectedPath) => _workingDirectory = Directory.Exists(selectedPath) ? selectedPath : Path.GetDirectoryName(selectedPath) ?? selectedPath;

    public async Task<GitRepositoryStatus> GetStatusAsync(CancellationToken token = default)
    {
        CommandResult root = await RunAsync(new[] { "rev-parse", "--show-toplevel" }, token);
        if (root.StartError is not null) return new() { IsGitAvailable = false, Error = root.StartError };
        if (root.ExitCode != 0) return new() { IsRepository = false, Error = root.Error.Trim() };
        string repo = root.Output.Trim();
        CommandResult branch = await RunAsync(new[] { "branch", "--show-current" }, token);
        CommandResult status = await RunAsync(new[] { "status", "--porcelain=v1", "-z" }, token);
        if (status.ExitCode != 0) return new() { IsRepository = true, RepositoryRoot = repo, Error = status.Error.Trim() };
        return new() { IsRepository = true, RepositoryRoot = repo, Branch = branch.Output.Trim(), Files = ParseStatus(status.Output) };
    }

    public Task<GitDiffResult> GetWorkingTreeDiffAsync(string? relativeFilePath = null, CancellationToken token = default) => GetDiffAsync(false, relativeFilePath, token);
    public Task<GitDiffResult> GetStagedDiffAsync(string? relativeFilePath = null, CancellationToken token = default) => GetDiffAsync(true, relativeFilePath, token);
    private async Task<GitDiffResult> GetDiffAsync(bool staged, string? file, CancellationToken token)
    {
        var args = new List<string> { "diff" }; if (staged) args.Add("--cached"); if (file is not null) { args.Add("--"); args.Add(file); }
        CommandResult command = await RunAsync(args, token); CommandResult root = await RunAsync(new[] { "rev-parse", "--show-toplevel" }, token);
        return new() { RepositoryRoot = root.Output.Trim(), RequestedFile = file, IsStaged = staged, UnifiedDiff = command.Output, ExitCode = command.ExitCode, Error = command.StartError ?? (command.ExitCode == 0 ? null : command.Error.Trim()) };
    }

    public async Task<IReadOnlyList<GitCommitInfo>> GetRecentCommitsAsync(int count, CancellationToken token = default)
    {
        if (count <= 0) return Array.Empty<GitCommitInfo>();
        CommandResult r = await RunAsync(new[] { "log", $"-{count}", "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1e" }, token);
        if (r.ExitCode != 0) return Array.Empty<GitCommitInfo>();
        return r.Output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(record => record.Trim('\r', '\n').Split('\x1f')).Where(x => x.Length == 6)
            .Select(x => new GitCommitInfo { Hash = x[0], ShortHash = x[1], AuthorName = x[2], AuthorEmail = x[3], Date = DateTimeOffset.Parse(x[4], CultureInfo.InvariantCulture), Subject = x[5] }).ToArray();
    }

    public static IReadOnlyList<GitFileStatus> ParseStatus(string porcelain)
    {
        string[] entries = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries); var files = new List<GitFileStatus>();
        for (int i = 0; i < entries.Length; i++) { string e = entries[i]; if (e.Length < 4) continue; string? original = null; string path = e[3..]; if (e[0] is 'R' or 'C' && i + 1 < entries.Length) original = entries[++i]; files.Add(new() { IndexStatus = e[0].ToString(), WorkingTreeStatus = e[1].ToString(), Path = path, OriginalPath = original }); }
        return files;
    }

    private async Task<CommandResult> RunAsync(IEnumerable<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        try { if (!process.Start()) return new(-1, "", "", "Git failed to start."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(-1, "", "", ex.Message); }
        Task<string> output = process.StandardOutput.ReadToEndAsync(token); Task<string> error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); return new(process.ExitCode, await output, await error, null);
    }
    private sealed record CommandResult(int ExitCode, string Output, string Error, string? StartError);
}

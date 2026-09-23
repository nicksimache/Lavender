using Lavender.Git;
using System.Diagnostics;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lavender.McpServer.Tools;

[McpServerToolType]
public sealed class GitTools
{
    private const int MaxDiffCharacters = 20000;

    private readonly LavenderMcpState _state;

    public GitTools(LavenderMcpState state)
    {
        _state = state;
    }

    [McpServerTool(Name = "lavender_get_git_status")]
    [Description("Gets Git status for the selected project. Available while indexing; no semantic or Roslyn index is required.")]
    public async Task<GitStatusToolResult> GetGitStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (_state.ProjectPath is not string projectPath)
            return GitStatusToolResult.Failed("Open a project first.");

        try
        {
            GitRepositoryStatus status = await new GitContextService(projectPath).GetStatusAsync(cancellationToken);

            return new GitStatusToolResult(
                Success: status.Error is null,
                Message: status.Error ?? "Git status loaded.",
                Status: GitRepositoryStatusSummary.FromGitRepositoryStatus(status));
        }
        catch (Exception ex)
        {
            return GitStatusToolResult.Failed($"Getting git status failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "lavender_get_git_diff")]
    [Description("Gets a Git diff for the selected project, including while indexing. No semantic or Roslyn index is required. Untracked files do not appear in Git diff; use Git status and source reading for those.")]
    public async Task<GitDiffToolResult> GetGitDiffAsync(
        [Description("Optional relative file path to diff. Empty returns a project diff excluding generated folders (bin, obj, .vs, artifacts, .venv, node_modules). Specify a file explicitly to inspect it even in those folders.")]
        string? relativeFilePath = null,

        [Description("Whether to return the staged diff instead of the working tree diff.")]
        bool staged = false,

        CancellationToken cancellationToken = default)
    {
        if (_state.ProjectPath is not string projectPath)
            return GitDiffToolResult.Failed("Open a project first.");

        var elapsed = Stopwatch.StartNew();
        try
        {
            var git = new GitContextService(projectPath);
            string? normalizedPath = string.IsNullOrWhiteSpace(relativeFilePath)
                ? null
                : relativeFilePath.Trim();

            GitDiffResult diff = staged
                ? await git.GetStagedDiffAsync(normalizedPath, cancellationToken)
                : await git.GetWorkingTreeDiffAsync(normalizedPath, cancellationToken);

            return new GitDiffToolResult(
                Success: diff.Succeeded,
                Message: diff.Error ?? $"Git diff loaded in {elapsed.Elapsed.TotalSeconds:F2}s." +
                    (normalizedPath is null ? " Generated folders were excluded; specify a file path to inspect one explicitly." : ""),
                Diff: GitDiffSummary.FromGitDiffResult(diff));
        }
        catch (Exception ex)
        {
            return GitDiffToolResult.Failed($"Getting Git diff failed after {elapsed.Elapsed.TotalSeconds:F2}s: {ex.Message}");
        }
    }

    public static string TruncateDiff(string diff) =>
        diff.Length <= MaxDiffCharacters
            ? diff
            : diff[..MaxDiffCharacters] + "\n\n# Diff truncated.";
}

public sealed record GitStatusToolResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("status")]
    GitRepositoryStatusSummary? Status)
{
    public static GitStatusToolResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            Status: null);
}

public sealed record GitRepositoryStatusSummary(
    [property: JsonPropertyName("is_repository")]
    bool IsRepository,

    [property: JsonPropertyName("is_git_available")]
    bool IsGitAvailable,

    [property: JsonPropertyName("repository_root")]
    string RepositoryRoot,

    [property: JsonPropertyName("branch")]
    string? Branch,

    [property: JsonPropertyName("files")]
    IReadOnlyList<GitFileStatusSummary> Files,

    [property: JsonPropertyName("error")]
    string? Error)
{
    public static GitRepositoryStatusSummary FromGitRepositoryStatus(GitRepositoryStatus status) =>
        new(
            IsRepository: status.IsRepository,
            IsGitAvailable: status.IsGitAvailable,
            RepositoryRoot: status.RepositoryRoot,
            Branch: status.Branch,
            Files: status.Files.Select(GitFileStatusSummary.FromGitFileStatus).ToArray(),
            Error: status.Error);
}

public sealed record GitFileStatusSummary(
    [property: JsonPropertyName("path")]
    string Path,

    [property: JsonPropertyName("index_status")]
    string IndexStatus,

    [property: JsonPropertyName("working_tree_status")]
    string WorkingTreeStatus,

    [property: JsonPropertyName("original_path")]
    string? OriginalPath,

    [property: JsonPropertyName("is_staged")]
    bool IsStaged,

    [property: JsonPropertyName("is_conflict")]
    bool IsConflict)
{
    public static GitFileStatusSummary FromGitFileStatus(GitFileStatus status) =>
        new(
            Path: status.Path,
            IndexStatus: status.IndexStatus,
            WorkingTreeStatus: status.WorkingTreeStatus,
            OriginalPath: status.OriginalPath,
            IsStaged: status.IsStaged,
            IsConflict: status.IsConflict);
}

public sealed record GitDiffToolResult(
    [property: JsonPropertyName("success")]
    bool Success,

    [property: JsonPropertyName("message")]
    string Message,

    [property: JsonPropertyName("diff")]
    GitDiffSummary? Diff)
{
    public static GitDiffToolResult Failed(string message) =>
        new(
            Success: false,
            Message: message,
            Diff: null);
}

public sealed record GitDiffSummary(
    [property: JsonPropertyName("repository_root")]
    string RepositoryRoot,

    [property: JsonPropertyName("requested_file")]
    string? RequestedFile,

    [property: JsonPropertyName("is_staged")]
    bool IsStaged,

    [property: JsonPropertyName("unified_diff")]
    string UnifiedDiff,

    [property: JsonPropertyName("exit_code")]
    int ExitCode,

    [property: JsonPropertyName("error")]
    string? Error)
{
    public static GitDiffSummary FromGitDiffResult(GitDiffResult diff) =>
        new(
            RepositoryRoot: diff.RepositoryRoot,
            RequestedFile: diff.RequestedFile,
            IsStaged: diff.IsStaged,
            UnifiedDiff: GitTools.TruncateDiff(diff.UnifiedDiff),
            ExitCode: diff.ExitCode,
            Error: diff.Error);
}

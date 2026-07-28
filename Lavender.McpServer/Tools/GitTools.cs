using Lavender.Git;
using Lavender.Infrastructure.Knowledge;
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
    [Description("Gets git repository status for the currently indexed project. Call lavender_index_project first.")]
    public async Task<GitStatusToolResult> GetGitStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_state.IsProjectIndexed)
        {
            await _state.WaitForProjectIndexed();
        }

        try
        {
            IProjectKnowledgeService knowledge = _state.RequireKnowledgeService();
            GitRepositoryStatus status = await knowledge.GetGitStatusAsync(cancellationToken);

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
    [Description("Gets a git diff for the currently indexed project. Call lavender_index_project first.")]
    public async Task<GitDiffToolResult> GetGitDiffAsync(
        [Description("Optional relative file path to diff. Leave empty for the whole repository.")]
        string? relativeFilePath = null,

        [Description("Whether to return the staged diff instead of the working tree diff.")]
        bool staged = false,

        CancellationToken cancellationToken = default)
    {
        if (!_state.IsProjectIndexed)
        {
            return GitDiffToolResult.Failed(
                "No project is indexed. Call lavender_index_project first.");
        }

        try
        {
            IProjectKnowledgeService knowledge = _state.RequireKnowledgeService();
            string? normalizedPath = string.IsNullOrWhiteSpace(relativeFilePath)
                ? null
                : relativeFilePath.Trim();

            GitDiffResult diff = await knowledge.GetGitDiffAsync(normalizedPath, staged, cancellationToken);

            return new GitDiffToolResult(
                Success: diff.Succeeded,
                Message: diff.Error ?? "Git diff loaded.",
                Diff: GitDiffSummary.FromGitDiffResult(diff));
        }
        catch (Exception ex)
        {
            return GitDiffToolResult.Failed($"Getting git diff failed: {ex.Message}");
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

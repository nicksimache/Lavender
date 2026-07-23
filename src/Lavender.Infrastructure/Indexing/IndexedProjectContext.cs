using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lavender.Infrastructure.Indexing;

/// <summary>
/// Owns the single Roslyn workspace and solution shared by all index services.
/// </summary>
public sealed class IndexedProjectContext : IDisposable
{
    private readonly MSBuildWorkspace _workspace;
    private readonly IDisposable _warningRegistration;

    public Solution Solution { get; }
    public string InputPath { get; }
    public string RootDirectory { get; }
    public IReadOnlyList<string> WorkspaceWarnings { get; }

    private IndexedProjectContext(
        MSBuildWorkspace workspace,
        IDisposable warningRegistration,
        Solution solution,
        string inputPath,
        IReadOnlyList<string> warnings)
    {
        _workspace = workspace;
        _warningRegistration = warningRegistration;
        Solution = solution;
        InputPath = Path.GetFullPath(inputPath);
        RootDirectory = Path.GetDirectoryName(InputPath) ?? Directory.GetCurrentDirectory();
        WorkspaceWarnings = warnings;
    }

    public static async Task<IndexedProjectContext> OpenAsync(string solutionOrProjectPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionOrProjectPath);

        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        var warnings = new List<string>();
        var workspace = MSBuildWorkspace.Create();
        IDisposable warningRegistration = workspace.RegisterWorkspaceFailedHandler(e => warnings.Add(e.Diagnostic.Message));

        try
        {
            string fullPath = Path.GetFullPath(solutionOrProjectPath);
            Solution solution = string.Equals(Path.GetExtension(fullPath), ".sln", StringComparison.OrdinalIgnoreCase)
                ? await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken)
                : (await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken)).Solution;

            return new IndexedProjectContext(workspace, warningRegistration, solution, fullPath, warnings);
        }
        catch
        {
            warningRegistration.Dispose();
            workspace.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _warningRegistration.Dispose();
        _workspace.Dispose();
    }
}

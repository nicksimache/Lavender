using Lavender.Infrastructure.FileSystem;
using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Indexing.Symbol;
using Lavender.Infrastructure.AI;
using System.Security.Cryptography;
using System.Text;

namespace Lavender.Diagnostics;

public sealed record FreshDiagnosticsResult(string Revision, bool Cached,
    IReadOnlyList<ProjectDiagnostic> Diagnostics, IReadOnlyList<string> Warnings);

/// <summary>Compiler snapshots independent of embeddings. Caller serializes access with file edits.</summary>
public sealed class FreshDiagnosticsService
{
    private FreshDiagnosticsResult? _cached;
    private string[] _dependencies = [];
    private string[] _dependencyRoots = [];
    public void Reset() { _cached = null; _dependencies = []; _dependencyRoots = []; }

    public async Task<FreshDiagnosticsResult> GetAsync(string root, string project, CancellationToken token)
    {
        string before = await FingerprintAsync(root, project, token);
        if (_cached?.Revision == before)
        {
            ApiUsageTracker.Write(new { event_type = "diagnostics_snapshot", revision = before, cached = true });
            return _cached with { Cached = true };
        }
        string sourceBefore = await FingerprintAsync(root, project, token, includeDependencies: false);
        // Reloading also picks up added/deleted source files and changed project configuration.
        // This never builds an embedding index or invokes a model.
        using var context = await IndexedProjectContext.OpenAsync(project, token);
        _dependencyRoots = context.Solution.Projects.Select(p => p.FilePath).OfType<string>()
            .Select(p => Path.GetDirectoryName(p)!).Where(p => !p.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _dependencies = context.Solution.Projects.SelectMany(p =>
            p.Documents.Select(d => d.FilePath)
                .Concat(p.AdditionalDocuments.Select(d => d.FilePath))
                .Concat(p.AnalyzerConfigDocuments.Select(d => d.FilePath))
                .Concat(p.MetadataReferences.Select(r => r.Display))
                .Append(p.FilePath)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string snapshot = await FingerprintAsync(root, project, token);
        var diagnostics = await new RoslynDiagnosticsProvider(context, new SymbolIdentityService()).GetSolutionDiagnosticsAsync(token);
        string after = await FingerprintAsync(root, project, token);
        // Reopening on the next call is safer than caching a snapshot changed externally during analysis.
        if (snapshot != after || sourceBefore != await FingerprintAsync(root, project, token, includeDependencies: false))
        {
            _cached = null;
            throw new InvalidOperationException("Files changed during compiler analysis. Retry after edits finish.");
        }
        _cached = new(after, false, diagnostics, context.WorkspaceWarnings.ToArray());
        ApiUsageTracker.Write(new { event_type = "diagnostics_snapshot", revision = after, cached = false, count = diagnostics.Count });
        return _cached;
    }

    private async Task<string> FingerprintAsync(string root, string project, CancellationToken token, bool includeDependencies = true)
    {
        var projectFiles = new[] { root }.Concat(includeDependencies ? _dependencyRoots : [])
            .SelectMany(p => ProjectFileCatalog.Enumerate(p, token)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var paths = projectFiles
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".cs" or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".json" or ".config" or ".editorconfig" or ".resx" or ".razor" or ".cshtml" or ".xaml")
            .Append(project);
        paths = paths.Concat(AncestorConfiguration(project));
        // Restore outputs affect compilation even though general discovery excludes obj/.
        paths = paths.Concat(projectFiles.Where(p => Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => new[] { Path.Combine(Path.GetDirectoryName(p)!, "obj", "project.assets.json"),
                Path.Combine(Path.GetDirectoryName(p)!, "obj", Path.GetFileName(p) + ".nuget.g.props"),
                Path.Combine(Path.GetDirectoryName(p)!, "obj", Path.GetFileName(p) + ".nuget.g.targets") }));
        if (includeDependencies) paths = paths.Concat(_dependencies);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "\0"));
            if (!File.Exists(path)) { hash.AppendData(new byte[] { 0 }); continue; }
            using var stream = File.OpenRead(path);
            hash.AppendData(await SHA256.HashDataAsync(stream, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> AncestorConfiguration(string project)
    {
        for (var directory = Directory.GetParent(project); directory is not null; directory = directory.Parent)
            foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config" })
                yield return Path.Combine(directory.FullName, name);
    }
}

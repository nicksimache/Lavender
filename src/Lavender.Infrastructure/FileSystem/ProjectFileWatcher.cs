using System.IO;

namespace Lavender.Infrastructure.FileSystem;

/// <summary>Reports external workspace changes; callers marshal notifications to their UI thread.</summary>
internal sealed class ProjectFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _root;
    private readonly Action _changed;
    private bool _disposed;

    public ProjectFileWatcher(string root, Action changed)
    {
        _root = Path.GetFullPath(root);
        _changed = changed;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 32 * 1024
        };
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += (_, e) =>
        {
            if (IsRelevant(e.OldFullPath) || IsRelevant(e.FullPath)) Notify();
        };
        // An overflow means events were lost: request a full explorer reconciliation.
        _watcher.Error += (_, _) => Notify();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.FullPath)) Notify();
    }

    private bool IsRelevant(string path)
    {
        string relative = Path.GetRelativePath(_root, path);
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(ProjectScanner.ShouldIgnoreFolder) && !ProjectScanner.ShouldIgnoreFile(path);
    }

    private void Notify()
    {
        if (!_disposed) _changed();
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher.Dispose();
    }
}

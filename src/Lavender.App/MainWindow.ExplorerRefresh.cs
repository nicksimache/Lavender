using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.App;

public partial class MainWindow
{
    private ProjectFileWatcher? _projectFileWatcher;
    private DispatcherTimer? _explorerRefreshTimer;
    private bool _refreshingExplorer;
    private int _explorerRefreshQueued;

    private void WatchProjectFiles(string root)
    {
        _projectFileWatcher?.Dispose();
        _explorerRefreshTimer?.Stop();
        if (_explorerRefreshTimer is null)
        {
            _explorerRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _explorerRefreshTimer.Tick += (_, _) =>
            {
                _explorerRefreshTimer.Stop();
                RefreshExplorerFromDisk();
                foreach (var tab in _openFiles)
                {
                    try { tab.ReloadIfClean(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { AppendAgentActivity($"Could not refresh {tab.FileName}: {ex.Message}"); }
                }
            };
            // Reconcile on focus too, in case the OS watcher missed an event.
            Activated += (_, _) => QueueExplorerRefresh();
            Closed += (_, _) =>
            {
                _projectFileWatcher?.Dispose();
                _explorerRefreshTimer.Stop();
            };
        }
        try { _projectFileWatcher = new ProjectFileWatcher(root, QueueExplorerRefresh); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { AppendAgentActivity($"Automatic explorer refresh unavailable: {ex.Message}"); }
    }

    private void QueueExplorerRefresh()
    {
        if (_windowLifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _explorerRefreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _explorerRefreshQueued, 0);
            if (_windowLifetime.IsCancellationRequested) return;
            _explorerRefreshTimer?.Stop();
            _explorerRefreshTimer?.Start();
        }));
    }

    private void RefreshExplorerFromDisk()
    {
        if (_selectedProjectPath is null || _windowLifetime.IsCancellationRequested) return;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? selected = (FolderView.SelectedItem as TreeViewItem)?.Tag as string;
        foreach (var item in ExplorerItems(FolderView.Items))
            if (item.IsExpanded && item.Tag is string path) expanded.Add(path);
        var root = BuildDisplayableExplorerDirectory(_selectedProjectPath, includeIfEmpty: true);
        _refreshingExplorer = true;
        try
        {
            FolderView.Items.Clear();
            if (root is null) return;
            FolderView.Items.Add(root);
            foreach (var item in ExplorerItems(FolderView.Items))
            {
                if (item.Tag is not string path) continue;
                item.IsExpanded = expanded.Contains(path);
                item.IsSelected = string.Equals(path, selected, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { _refreshingExplorer = false; }
    }

    private static IEnumerable<TreeViewItem> ExplorerItems(ItemCollection items)
    {
        foreach (TreeViewItem item in items)
        {
            yield return item;
            foreach (var child in ExplorerItems(item.Items)) yield return child;
        }
    }
}

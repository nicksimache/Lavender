using System.IO;
using System.Windows;
using System.Windows.Threading;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.App;

public partial class MainWindow
{
    private ExternalOpenRequest? _externalOpen;
    private DispatcherTimer? _externalOpenTimer;
    private bool _openingExternal;

    public void QueueExternalOpen(ExternalOpenRequest? request)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (request is null) return;
        // Latest click wins while startup or another project operation is in progress.
        _externalOpen = request;
        if (_externalOpenTimer is null)
        {
            _externalOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _externalOpenTimer.Tick += async (_, _) => await ProcessExternalOpenAsync();
            Closed += (_, _) => _externalOpenTimer.Stop();
        }
        _externalOpenTimer.Start();
    }

    private async Task ProcessExternalOpenAsync()
    {
        if (!_ready || _openingExternal || _externalOpen is null || _windowLifetime.IsCancellationRequested) return;
        var request = _externalOpen;
        bool switchProject = request.ProjectPath is not null && !string.Equals(
            Path.TrimEndingDirectorySeparator(request.ProjectPath), _selectedProjectPath, StringComparison.OrdinalIgnoreCase);
        if (switchProject && (_busy || _indexing)) return;
        _openingExternal = true;
        _externalOpen = null;
        try
        {
            if (switchProject)
            {
                string solution = ProjectFolderResolver.FindSolution(request.ProjectPath!);
                await OpenProjectAsync(request.ProjectPath!, solution);
            }
            // A newer click or project change supersedes this pending navigation.
            if (_externalOpen is null && (request.ProjectPath is null || string.Equals(
                Path.TrimEndingDirectorySeparator(request.ProjectPath), _selectedProjectPath, StringComparison.OrdinalIgnoreCase)))
                await OpenCodeFileAtLineAsync(request.FilePath, request.Line, request.Column);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open source location", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _openingExternal = false;
            if (_externalOpen is null) _externalOpenTimer?.Stop();
        }
    }

    /// <summary>Opens a source file in the existing editor, preserving unsaved edits. Positions are one-based.</summary>
    public async Task OpenCodeFileAtLineAsync(string path, int line = 1, int column = 1)
    {
        Dispatcher.VerifyAccess();
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Source file no longer exists.", fullPath);
        var tab = _openFiles.FirstOrDefault(t => string.Equals(t.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            ?? new OpenFileTab(fullPath);
        if (tab.IsImage) throw new ArgumentException("Source navigation requires a text file.");
        tab.ReloadIfClean();
        await OpenPreparedFileAsync(tab);
        if (_activeFile != tab || _windowLifetime.IsCancellationRequested) return;
        int targetLine = Math.Clamp(line, 1, tab.Document.LineCount);
        var documentLine = tab.Document.GetLineByNumber(targetLine);
        int targetColumn = Math.Clamp(column, 1, documentLine.Length + 1);
        FilePreviewBox.Select(documentLine.Offset + targetColumn - 1, 0);
        FilePreviewBox.ScrollTo(targetLine, targetColumn);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        FilePreviewBox.Focus();
    }
}

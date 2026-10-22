using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Lavender.Infrastructure.Editing;
using Microsoft.CodeAnalysis.Classification;

namespace Lavender.App.Rendering;

/// <summary>Connects the editor to Roslyn without putting language-service logic in MainWindow.</summary>
public sealed partial class EditorAssistance : IAsyncDisposable
{
    private const int TypingDebounceMilliseconds = 50; // We don't use this anymore
    private const int InitParallelClassificationPasses = 2;

    private readonly TextEditor _editor;
    private readonly Func<IReadOnlyDictionary<string, string>> _readBuffers;
    private readonly Action<string> _report;
    private readonly Action<bool> _setLoading;
    private readonly EditorLanguageService _language;
    private readonly SemanticColorizer _colors = new();
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _colorRequest;
    private CancellationTokenSource? _completionRequest;
    private CompletionWindow? _popup;
    private string? _projectPath;
    private string? _filePath;
    private int _revision;
    private TextDocument? _trackedDocument;
    private bool _colorWorkerRunning;
    private bool _colorPending;
    private bool _debouncePending;
    private int _classificationPasses;

    public int Revision => _revision;

    private bool CanAnalyze => _projectPath is not null && _filePath is not null
        && Path.GetExtension(_filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Wires editor events, semantic colors, completion, and file preparation services.</summary>
    public EditorAssistance(TextEditor editor, Func<IReadOnlyDictionary<string, string>> readBuffers,
        Action<string> report, Action<bool>? setLoading = null)
    {
        _editor = editor;
        _readBuffers = readBuffers;
        _report = report;
        _setLoading = setLoading ?? (_ => { });
        _language = new EditorLanguageService();

        _editor.Foreground = EditorTheme.Brush(EditorTheme.Text);
        _editor.TextArea.TextView.LineTransformers.Add(_colors);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticLines);
        _editor.MouseHover += DiagnosticHover;
        _editor.MouseHoverStopped += DiagnosticHoverStopped;

        _editor.TextChanged += TextChanged;
        _editor.DocumentChanged += DocumentChanged;
        TrackDocument();
        _editor.TextArea.TextEntered += TextEntered;
        _editor.PreviewKeyDown += PreviewKeyDown;
    }

    /// <summary>Selects a file and applies prepared colors or schedules its initial classification.</summary>
    public void SelectFile(string? projectPath, string? filePath, bool reloadProject = false,
        ClassifiedSpan[]? preparedSpans = null)
    {
        bool projectChanged = _projectPath != projectPath;
        if(reloadProject)
        {
            _language.Invalidate();
        }

        if(_projectPath != projectPath || _filePath != filePath)
        {
            _colors.Spans = Array.Empty<ClassifiedSpan>();
        }

        _projectPath = projectPath;
        _filePath = filePath;

        if(projectPath is not null && (projectChanged || reloadProject))
        {
            WarmUp(projectPath);
        }

        _popup?.Close();
        _editor.SyntaxHighlighting = filePath is null ? null : EditorTheme.ForFile(filePath);

        if(preparedSpans is not null)
        {
            ++_revision;
            _colorRequest?.Cancel();
            _completionRequest?.Cancel();
            _colorPending = false;
            _colors.Spans = preparedSpans;
            _setLoading(false);
            _editor.TextArea.TextView.Redraw();
            QueueDiagnostics();
        }
        else
        {
            _setLoading(CanAnalyze);
            Refresh();
        }
    }

    /// <summary>Classifies a target file without replacing the currently visible editor document.</summary>
    public async Task<ClassifiedSpan[]> PrepareFileAsync(string? projectPath, string filePath,
        string text, CancellationToken token)
    {
        if(projectPath is null || !Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ClassifiedSpan>();
        }

        var buffers = new Dictionary<string, string>(_readBuffers(), StringComparer.OrdinalIgnoreCase)
        {
            [filePath] = text
        };

        string label = $"{Path.GetFileName(filePath)} [opening]";
        var spans = await Task.Run(() => _language.WithDocumentAsync(projectPath, filePath, buffers,
            (document, cancellation) => ClassifyDocumentAsync(document, text.Length, cancellation), token), token);
        token.ThrowIfCancellationRequested();

        if(spans is null)
        {
            _report($"Semantic highlighting unavailable: {label}; opening with basic highlighting.");
        }

        return spans ?? Array.Empty<ClassifiedSpan>();
    }

    /// <summary>Invalidates project state, warms the workspace, and refreshes the active file.</summary>
    public void ReloadProject()
    {
        _language.Invalidate();

        if(_projectPath is not null)
        {
            WarmUp(_projectPath);
        }

        _popup?.Close();
        Refresh();
    }

    /// <summary>Prepares the workspace and compilations in the background and reports failures.</summary>
    private async void WarmUp(string projectPath)
    {
        try
        {
            var token = _lifetime.Token;
            await Task.Run(() => _language.WarmUpAsync(projectPath, token), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _report($"Editor project loading: {ex.Message}");
        }
    }

    /// <summary>Queues a batched semantic refresh after an editor text change.</summary>
    private void TextChanged(object? sender, EventArgs e) => Refresh(debounce: true);

    /// <summary>Updates document event subscriptions when the editor switches buffers.</summary>
    private void DocumentChanged(object? sender, EventArgs e) => TrackDocument();

    /// <summary>Detaches the previous buffer and tracks edits to the current document.</summary>
    private void TrackDocument()
    {
        if(_trackedDocument is not null)
        {
            _trackedDocument.Changing -= DocumentChanging;
        }

        _trackedDocument = _editor.Document;

        if(_trackedDocument is not null)
        {
            _trackedDocument.Changing += DocumentChanging;
        }

        _colors.Spans = Array.Empty<ClassifiedSpan>();
    }

    /// <summary>Adjusts retained classification offsets before the document edit is applied.</summary>
    private void DocumentChanging(object? sender, DocumentChangeEventArgs e) =>
        _colors.ApplyChange(e.Offset, e.RemovalLength, e.InsertionLength);

    /// <summary>Marks the latest document state pending and starts a single classification worker.</summary>
    private void Refresh(bool debounce = false)
    {
        ++_revision;

        QueueDiagnostics();

        if(!debounce)
        {
            _colorRequest?.Cancel();
        }

        _completionRequest?.Cancel();
        _colorPending = true;
        _debouncePending = debounce;

        if(!_colorWorkerRunning)
        {
            RunColorWorker();
        }
    }

    /// <summary>Processes coalesced classification requests until no newer state is pending.</summary>
    private async void RunColorWorker()
    {
        _colorWorkerRunning = true;

        try
        {
            while (_colorPending && !_lifetime.IsCancellationRequested)
            {
                if(_debouncePending)
                {
                    await Task.Delay(TypingDebounceMilliseconds, _lifetime.Token);
                }
                if(!_colorPending)
                {
                    break;
                }

                _colorPending = false;
                await AnalyzeColorsAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _colorWorkerRunning = false;
        }
    }

    /// <summary>Classifies the active snapshot and redraws changed lines only if the result is current.</summary>
    private async Task AnalyzeColorsAsync()
    {
        int revision = _revision;

        if(!CanAnalyze || _lifetime.IsCancellationRequested)
        {
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _colorRequest = request;

        try
        {
            // Capture WPF documents on the UI thread before Roslyn runs in the background.
            string project = _projectPath!, path = _filePath!;
            string label = $"{Path.GetFileName(path)} [revision {revision}]";
            var buffers = _readBuffers();
            int length = _editor.Document.TextLength;
            var spans = await Task.Run(() => _language.WithDocumentAsync(project, path, buffers,
                (document, token) => ClassifyDocumentAsync(document, length, token), request.Token));

            if(request.IsCancellationRequested || revision != _revision)
            {
                return;
            }
            if(spans is null)
            {
                _report($"Semantic highlighting unavailable: {label}; file is outside the loaded project.");
                _setLoading(false);
                return;
            }

            var changed = _colors.ReplaceSpans(spans ?? Array.Empty<ClassifiedSpan>());

            // One UI continuation applies all results; adjacent changed ranges are merged.
            int firstLine = -1, lastLine = -1;
            var document = _editor.Document;

            foreach (var range in changed)
            {
                int start = document.GetLineByOffset(Math.Min(range.Start, document.TextLength)).LineNumber;
                int end = document.GetLineByOffset(
                    Math.Min(Math.Max(range.Start, range.End - 1), document.TextLength)).LineNumber;

                if(firstLine >= 0 && start > lastLine + 1)
                {
                    RedrawLines(firstLine, lastLine);
                    firstLine = -1;
                }

                if(firstLine < 0)
                {
                    firstLine = start;
                }

                lastLine = Math.Max(lastLine, end);
            }

            if(firstLine >= 0)
            {
                RedrawLines(firstLine, lastLine);
            }

            _setLoading(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _report($"Editor highlighting: {ex.Message}");
            if(revision == _revision)
            {
                _setLoading(false);
            }
        }
        finally
        {
            if(ReferenceEquals(_colorRequest, request))
            {
                _colorRequest = null;
            }
        }
    }

    /// <summary>Prepares a shared semantic model and selects sequential or parallel classification.</summary>
    private async Task<ClassifiedSpan[]> ClassifyDocumentAsync(Microsoft.CodeAnalysis.Document document,
        int length, CancellationToken token)
    {
        // Retain the shared semantic model while parallel ranges use it.
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        bool parallel = ++_classificationPasses <= InitParallelClassificationPasses
            || length >= EditorClassification.LargeFileLength;
        var result = await EditorClassification.GetAsync(document, parallel, token).ConfigureAwait(false);

        GC.KeepAlive(model);
        return result;
    }

    /// <summary>Invalidates the visual range covering the specified inclusive line numbers.</summary>
    private void RedrawLines(int first, int last)
    {
        var start = _editor.Document.GetLineByNumber(first);
        var end = _editor.Document.GetLineByNumber(last);
        _editor.TextArea.TextView.Redraw(start.Offset, end.EndOffset - start.Offset);
    }

    /// <summary>Requests member completion after a period is entered.</summary>
    private void TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if(e.Text == ".")
        {
            RequestCompletion();
        }
    }

    /// <summary>Handles the explicit completion keyboard shortcut.</summary>
    private void PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            RequestCompletion();
        }
    }

    /// <summary>Loads suggestions and displays them only when the document and caret still match.</summary>
    private async void RequestCompletion()
    {
        _completionRequest?.Cancel();
        _popup?.Close();
        if(!CanAnalyze || _lifetime.IsCancellationRequested)
        {
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _completionRequest = request;
        int revision = _revision, caret = _editor.CaretOffset;
        TextDocument currentDocument = _editor.Document;
        string project = _projectPath!, path = _filePath!;
        var buffers = _readBuffers();

        try
        {
            var suggestions = await Task.Run(() => _language.WithDocumentAsync(project, path, buffers,
                (document, token) => CSharpSuggestions.GetAsync(document, caret, token), request.Token));
            // Never open a popup for an old document, caret position, or text revision.
            if(request.IsCancellationRequested || revision != _revision || _editor.CaretOffset != caret
                || !ReferenceEquals(currentDocument, _editor.Document) || !_editor.IsKeyboardFocusWithin)
            {
                return;
            }

            if(suggestions is null)
            {
                _report("Editor completion: this file is not part of the selected C# solution/project.");
                return;
            }
            if(suggestions.Items.Count == 0)
            {
                return;
            }

            var popup = new CompletionWindow(_editor.TextArea)
            {
                StartOffset = suggestions.Start,
                Background = EditorTheme.Brush("#252331"),
                Foreground = EditorTheme.Brush(EditorTheme.Text),
                BorderBrush = EditorTheme.Brush("#57506D")
            };

            popup.CompletionList.ListBox.Background = popup.Background;
            popup.CompletionList.ListBox.Foreground = popup.Foreground;

            foreach (var suggestion in suggestions.Items)
            {
                popup.CompletionList.CompletionData.Add(new SuggestionData(suggestion));
            }

            popup.Closed += (_, _) =>
            {
                if(ReferenceEquals(_popup, popup))
                {
                    _popup = null;
                }
            };

            _popup = popup;
            popup.Show();
            popup.CompletionList.SelectItem(_editor.Document.GetText(suggestions.Start, caret - suggestions.Start));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _report($"Editor completion: {ex.Message}");
        }
        finally
        {
            if(ReferenceEquals(_completionRequest, request))
            {
                _completionRequest = null;
            }
        }
    }

    /// <summary>Cancels pending work, detaches editor events, and releases the language service.</summary>
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _diagnosticRequest?.Cancel();
        _editor.MouseHover -= DiagnosticHover;
        _editor.MouseHoverStopped -= DiagnosticHoverStopped;
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_diagnosticLines);
        _popup?.Close();
        _editor.TextChanged -= TextChanged;
        _editor.DocumentChanged -= DocumentChanged;

        if(_trackedDocument is not null)
        {
            _trackedDocument.Changing -= DocumentChanging;
        }

        _editor.TextArea.TextEntered -= TextEntered;
        _editor.PreviewKeyDown -= PreviewKeyDown;
        _editor.TextArea.TextView.LineTransformers.Remove(_colors);

        await _language.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed class SuggestionData(EditorSuggestion suggestion) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => suggestion.Name;
        public object Content => Text;
        public object Description => suggestion.Description;
        public double Priority => 0;

        /// <summary>Replaces the completion segment with the selected suggestion text.</summary>
        public void Complete(TextArea textArea, ISegment segment, EventArgs e) =>
            textArea.Document.Replace(segment, suggestion.InsertionText);
    }
}

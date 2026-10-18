using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Lavender.Infrastructure.Editing;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Lavender.App.Rendering;

/// <summary>Connects the editor to Roslyn without putting language-service logic in MainWindow.</summary>
public sealed class EditorAssistance : IAsyncDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<IReadOnlyDictionary<string, string>> _readBuffers;
    private readonly Action<string> _report;
    private readonly EditorLanguageService _language = new();
    private readonly SemanticColorizer _colors = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _colorRequest;
    private CancellationTokenSource? _completionRequest;
    private CompletionWindow? _popup;
    private string? _projectPath;
    private string? _filePath;
    private int _revision;

    public EditorAssistance(TextEditor editor, Func<IReadOnlyDictionary<string, string>> readBuffers,
        Action<string> report)
    {
        _editor = editor;
        _readBuffers = readBuffers;
        _report = report;
        _editor.Foreground = EditorTheme.Brush(EditorTheme.Text);
        _editor.TextArea.TextView.LineTransformers.Add(_colors);
        _editor.TextChanged += TextChanged;
        _editor.TextArea.TextEntered += TextEntered;
        _editor.PreviewKeyDown += PreviewKeyDown;
    }

    public void SelectFile(string? projectPath, string? filePath)
    {
        _projectPath = projectPath;
        _filePath = filePath;
        _popup?.Close();
        _editor.SyntaxHighlighting = filePath is null ? null : EditorTheme.ForFile(filePath);
        Refresh();
    }

    private bool CanAnalyze => _projectPath is not null && _filePath is not null
        && Path.GetExtension(_filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    public void ReloadProject()
    {
        _language.Invalidate();
        _popup?.Close();
        Refresh();
    }

    private void TextChanged(object? sender, EventArgs e) => Refresh();

    private async void Refresh()
    {
        // Offsets belong to a particular text revision. Drop old colors as soon as typing changes it.
        int revision = ++_revision;
        _colorRequest?.Cancel();
        _completionRequest?.Cancel();
        _colors.Spans = Array.Empty<ClassifiedSpan>();
        _editor.TextArea.TextView.Redraw();
        if (!CanAnalyze || _lifetime.IsCancellationRequested)
            return;

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _colorRequest = request;
        try
        {
            await Task.Delay(300, request.Token);
            // Capture WPF documents on the UI thread before Roslyn runs in the background.
            string project = _projectPath!, path = _filePath!;
            var buffers = _readBuffers();
            int length = _editor.Document.TextLength;
            var spans = await Task.Run(() => _language.WithDocumentAsync(project, path, buffers,
                async (document, token) => (await Classifier.GetClassifiedSpansAsync(
                    document, new TextSpan(0, length), token)).OrderBy(s => s.TextSpan.Start).ToArray(), request.Token));
            if (request.IsCancellationRequested || revision != _revision)
                return;
            _colors.Spans = spans ?? Array.Empty<ClassifiedSpan>();
            _editor.TextArea.TextView.Redraw();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _report($"Editor highlighting: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_colorRequest, request))
                _colorRequest = null;
        }
    }

    private void TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text == ".")
            RequestCompletion();
    }

    private void PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            RequestCompletion();
        }
    }

    private async void RequestCompletion()
    {
        _completionRequest?.Cancel();
        _popup?.Close();
        if (!CanAnalyze || _lifetime.IsCancellationRequested)
            return;
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
            if (request.IsCancellationRequested || revision != _revision || _editor.CaretOffset != caret
                || !ReferenceEquals(currentDocument, _editor.Document) || !_editor.IsKeyboardFocusWithin)
                return;
            if (suggestions is null)
            {
                _report("Editor completion: this file is not part of the selected C# solution/project.");
                return;
            }
            if (suggestions.Items.Count == 0)
                return;

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
                popup.CompletionList.CompletionData.Add(new SuggestionData(suggestion));
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_popup, popup))
                    _popup = null;
            };
            _popup = popup;
            popup.Show();
            popup.CompletionList.SelectItem(_editor.Document.GetText(suggestions.Start, caret - suggestions.Start));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _report($"Editor completion: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_completionRequest, request))
                _completionRequest = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _popup?.Close();
        _editor.TextChanged -= TextChanged;
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

        public void Complete(TextArea textArea, ISegment segment, EventArgs e) =>
            textArea.Document.Replace(segment, suggestion.InsertionText);
    }
}

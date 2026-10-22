using System.Windows.Input;
using Lavender.Infrastructure.Editing;

namespace Lavender.App.Rendering;

public sealed partial class EditorAssistance
{
    private readonly DiagnosticUnderlines _diagnosticLines = new();
    private CancellationTokenSource? _diagnosticRequest;
    public event Action<IReadOnlyList<EditorDiagnostic>, string>? DiagnosticsChanged;

    private async void QueueDiagnostics()
    {
        _diagnosticRequest?.Cancel();
        _diagnosticLines.Items = [];
        _editor.TextArea.TextView.InvalidateLayer(_diagnosticLines.Layer);
        _editor.ToolTip = null;
        DiagnosticsChanged?.Invoke([], CanAnalyze ? "Checking current file…" : "Select a C# file");
        if (!CanAnalyze || _lifetime.IsCancellationRequested) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnosticRequest = request;
        var document = _editor.Document;
        int revision = _revision;
        string project = _projectPath!, file = _filePath!;
        try
        {
            await Task.Delay(350, request.Token);
            var buffers = _readBuffers();
            var results = await Task.Run(() => _language.WithDocumentAsync(project, file, buffers,
                EditorDiagnostics.GetAsync, request.Token), request.Token);
            if (request.IsCancellationRequested || revision != _revision || document != _editor.Document) return;
            _diagnosticLines.Items = results ?? [];
            _editor.TextArea.TextView.InvalidateLayer(_diagnosticLines.Layer);
            DiagnosticsChanged?.Invoke(_diagnosticLines.Items, results is null
                ? "File is outside the loaded C# project" : $"Current file · {results.Length} problems · Roslyn");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested)
                DiagnosticsChanged?.Invoke([], "Analysis unavailable: " + ex.Message);
        }
        finally { if (ReferenceEquals(_diagnosticRequest, request)) _diagnosticRequest = null; }
    }

    private void DiagnosticHover(object sender, MouseEventArgs e)
    {
        var position = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (position is null) return;
        int offset = _editor.Document.GetOffset(position.Value.Location);
        var messages = _diagnosticLines.Items.Where(d => offset >= d.Offset && offset <= d.Offset + Math.Max(1, d.Length))
            .Select(d => $"{d.Code}: {d.Message}").ToArray();
        _editor.ToolTip = messages.Length == 0 ? null : string.Join("\n", messages);
    }
    private void DiagnosticHoverStopped(object sender, MouseEventArgs e) => _editor.ToolTip = null;
}

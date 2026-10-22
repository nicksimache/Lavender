using Microsoft.CodeAnalysis;

namespace Lavender.Infrastructure.Editing;

public sealed record EditorDiagnostic(string FilePath, int Line, int Column, int Offset, int Length,
    string Severity, string Code, string Message)
{
    public string Display => $"{Severity} {Code}: {Message} — {System.IO.Path.GetFileName(FilePath)}:{Line}";
}

public static class EditorDiagnostics
{
    public static async Task<EditorDiagnostic[]> GetAsync(Document document, CancellationToken token)
    {
        var tree = await document.GetSyntaxTreeAsync(token).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        var diagnostics = model is null ? tree!.GetDiagnostics(token) : model.GetDiagnostics(cancellationToken: token);
        return diagnostics.Where(d => !d.IsSuppressed && d.Location.IsInSource && d.Location.SourceTree == tree &&
                d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderBy(d => d.Location.SourceSpan.Start).Take(200).Select(d =>
            {
                var line = d.Location.GetLineSpan().StartLinePosition;
                return new EditorDiagnostic(document.FilePath ?? "", line.Line + 1, line.Character + 1,
                    d.Location.SourceSpan.Start, d.Location.SourceSpan.Length, d.Severity.ToString(), d.Id, d.GetMessage());
            }).ToArray();
    }
}

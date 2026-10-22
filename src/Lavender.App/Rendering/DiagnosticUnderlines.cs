using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Lavender.Infrastructure.Editing;

namespace Lavender.App.Rendering;

internal sealed class DiagnosticUnderlines : IBackgroundRenderer
{
    public IReadOnlyList<EditorDiagnostic> Items { get; set; } = [];
    public KnownLayer Layer => KnownLayer.Selection;
    public void Draw(TextView view, DrawingContext drawing)
    {
        if (!view.VisualLinesValid || view.Document is null) return;
        foreach (var diagnostic in Items)
        {
            if (diagnostic.Offset > view.Document.TextLength || view.Document.TextLength == 0) continue;
            int start = Math.Min(diagnostic.Offset, view.Document.TextLength - 1);
            var segment = new TextSegment { StartOffset = start,
                Length = Math.Min(Math.Max(1, diagnostic.Length), view.Document.TextLength - start) };
            var pen = new Pen(diagnostic.Severity == "Error" ? Brushes.IndianRed : Brushes.Goldenrod, 1.2);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, segment))
            {
                for (double x = rect.Left; x < rect.Right; x += 4)
                {
                    drawing.DrawLine(pen, new Point(x, rect.Bottom - 1), new Point(Math.Min(x + 2, rect.Right), rect.Bottom - 3));
                    drawing.DrawLine(pen, new Point(Math.Min(x + 2, rect.Right), rect.Bottom - 3), new Point(Math.Min(x + 4, rect.Right), rect.Bottom - 1));
                }
            }
        }
    }
}

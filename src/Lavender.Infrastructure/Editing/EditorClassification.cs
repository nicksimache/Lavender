using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Lavender.Infrastructure.Editing;

/// <summary>Coordinates sequential or parallel Roslyn classification for editor documents.</summary>
public static class EditorClassification
{
    public const int LargeFileLength = 20_000;

    /// <summary>Classifies a document using one range or up to three parallel ranges and merges their results.</summary>
    public static async Task<ClassifiedSpan[]> GetAsync(Document document, bool parallel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var text = await document.GetTextAsync(token).ConfigureAwait(false);

        if(!parallel || text.Length < 3)
        {
            return (await Classifier.GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), token).ConfigureAwait(false)).ToArray();
        }

        // Keep tokens and their trivia intact, including multiline strings/comments.
        // Each request still uses the full document and its project semantic context.
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var cuts = new SortedSet<int> { 0, text.Length };

        for (int i = 1; i < 3; i++)
        {
            int position = (int)((long)text.Length * i / 3);
            int boundary = root!.FindToken(position).FullSpan.End;

            if(boundary > 0 && boundary < text.Length)
            {
                cuts.Add(boundary);
            }
        }

        var boundaries = cuts.ToArray();
        var tasks = Enumerable.Range(0, boundaries.Length - 1)
            .Select(i => Task.Run(async () =>
            {
                var range = TextSpan.FromBounds(boundaries[i], boundaries[i + 1]);

                return (await Classifier.GetClassifiedSpansAsync(document, range, token)
                    .ConfigureAwait(false)).ToArray();
            }, token))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // Roslyn can return classifications extending across a requested range boundary.
        // Remove exact duplicates only; different classifications at the same span are valid.
        return results.SelectMany(spans => spans)
            .Distinct()
            .OrderBy(s => s.TextSpan.Start)
            .ToArray();
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;

namespace Lavender.Infrastructure.Editing;

public sealed record EditorSuggestion(string Name, string InsertionText, string Description);
public sealed record EditorSuggestions(int Start, IReadOnlyList<EditorSuggestion> Items);

public static class CSharpSuggestions
{
    public static async Task<EditorSuggestions> GetAsync(Document document, int position, CancellationToken token)
    {
        var service = CompletionService.GetService(document);
        if (service is null)
            return new(position, Array.Empty<EditorSuggestion>());
        var list = await service.GetCompletionsAsync(document, position, cancellationToken: token).ConfigureAwait(false);
        if (list is null)
            return new(position, Array.Empty<EditorSuggestion>());

        var suggestions = new List<EditorSuggestion>();
        foreach (var item in list.ItemsList)
        {
            token.ThrowIfCancellationRequested();
            var change = await service.GetChangeAsync(document, item, cancellationToken: token).ConfigureAwait(false);
            // The popup owns one identifier span. Multi-edit refactorings/snippets need a separate UI.
            if (change.TextChange.Span != list.Span)
                continue;
            suggestions.Add(new(item.DisplayText, change.TextChange.NewText ?? item.DisplayText,
                string.Join(", ", item.Tags) + " " + item.InlineDescription));
        }
        return new(list.Span.Start, suggestions);
    }
}

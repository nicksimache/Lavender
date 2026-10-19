using Lavender.App.Rendering;
using Lavender.Infrastructure.Editing;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using System.IO;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var theme = EditorTheme.ForFile("Test.cs")!;
        Check(theme.GetNamedColor("MethodCall").Foreground.GetColor(null) !=
            theme.GetNamedColor("Keywords").Foreground.GetColor(null), "Methods and keywords have different colors");
        Check(EditorTheme.ForFile("Test.unknown") is null, "Unknown files use plain text");
        Check(ReferenceEquals(theme, EditorTheme.ForFile("Second.cs")), "Theme can be reused");
        var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = "current tab" };
        var loadingStates = new List<bool>();
        var assistance = new EditorAssistance(editor, () => new Dictionary<string, string>(),
            _ => { }, loadingStates.Add);
        var preparedText = assistance.PrepareFileAsync(null, "next.txt", "next tab", CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(editor.Text == "current tab" && loadingStates.Count == 0,
            "Preparing a tab leaves the current editor visible and unchanged");
        editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument("next tab");
        assistance.SelectFile(null, "next.txt", preparedSpans: preparedText);
        Check(loadingStates.SequenceEqual(new[] { false }) && editor.Text == "next tab",
            "Activating prepared content does not enter the loading state");
        assistance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var colors = new SemanticColorizer
        {
            Spans = new[]
            {
                new ClassifiedSpan(ClassificationTypeNames.ClassName, new TextSpan(0, 3)),
                new ClassifiedSpan(ClassificationTypeNames.MethodName, new TextSpan(5, 4)),
                new ClassifiedSpan(ClassificationTypeNames.LocalName, new TextSpan(12, 3))
            }
        };
        colors.ApplyChange(6, 1, 3);
        Check(colors.Spans.Count == 2 && colors.Spans[0].TextSpan == new TextSpan(0, 3)
            && colors.Spans[1].TextSpan == new TextSpan(14, 3),
            "Editing a token preserves earlier colors and shifts later colors");
        colors.ApplyChange(0, 17, 0);
        Check(colors.Spans.Count == 0, "Deleting the document removes all old colors");
        Check(colors.FindFirstSpan(0) == 0, "Empty color index has no candidates");
        colors.Spans = new[]
        {
            new ClassifiedSpan(ClassificationTypeNames.Comment, new TextSpan(0, 100)),
            new ClassifiedSpan(ClassificationTypeNames.ClassName, new TextSpan(10, 3)),
            new ClassifiedSpan(ClassificationTypeNames.MethodName, new TextSpan(110, 5))
        };
        Check(colors.FindFirstSpan(50) == 0, "Lookup preserves overlapping multiline classifications");
        Check(colors.FindFirstSpan(100) == 2 && colors.FindFirstSpan(115) == 3,
            "Lookup skips classifications ending at or before the line");
        colors.Spans = Enumerable.Range(0, 100_000).Select(i =>
            new ClassifiedSpan(ClassificationTypeNames.LocalName, new TextSpan(i * 10, 3))).ToArray();
        Check(colors.FindFirstSpan(999_990) == 99_999,
            "Late-file rendering jumps directly to the relevant classification");
        Check(colors.ReplaceSpans(colors.Spans).Count == 0, "Identical semantic results require no redraw");
        colors.Spans = new[] { new ClassifiedSpan(ClassificationTypeNames.Comment, new TextSpan(0, 100)) };
        colors.ApplyChange(5, 1, 0);
        var removed = colors.ReplaceSpans(Array.Empty<ClassifiedSpan>());
        Check(removed.Count == 1 && removed[0] == new TextSpan(0, 99),
            "Removing a multiline classification invalidates its remaining painted range");
        colors.Spans = new[]
        {
            new ClassifiedSpan(ClassificationTypeNames.LocalName, new TextSpan(0, 3)),
            new ClassifiedSpan(ClassificationTypeNames.LocalName, new TextSpan(10, 3))
        };
        var recolored = colors.ReplaceSpans(new[]
        {
            new ClassifiedSpan(ClassificationTypeNames.LocalName, new TextSpan(0, 3)),
            new ClassifiedSpan(ClassificationTypeNames.FieldName, new TextSpan(10, 3))
        });
        Check(recolored.Count == 1 && recolored[0] == new TextSpan(10, 3),
            "A changed classification leaves unrelated ranges untouched");
        var fixtureSpans = Enumerable.Range(0, 10_000).Select(i =>
            new ClassifiedSpan(ClassificationTypeNames.LocalName, new TextSpan(i * 10, 3))).ToArray();
        colors.Spans = fixtureSpans;
        for (int i = 0; i < 500; i++)
        {
            colors.ApplyChange(50_005, 0, 1);
            colors.ApplyChange(50_005, 1, 0);
        }
        Check(colors.Spans.SequenceEqual(fixtureSpans), "Repeated insertion/deletion restores all span offsets");
        RunAsync(Path.GetFullPath(args[0])).GetAwaiter().GetResult();
        Console.WriteLine("PASS: editor themes, project-aware completion, unsaved buffers, semantic colors, cancellation.");
    }

    private static async Task RunAsync(string project)
    {
        await using var language = new EditorLanguageService();
        await Task.WhenAll(language.WarmUpAsync(project, CancellationToken.None),
            language.WarmUpAsync(project, CancellationToken.None));
        string directory = Path.GetDirectoryName(project)!;
        string caller = Path.Combine(directory, "Program.cs");
        string fixture = Path.Combine(directory, "CompletionFixture.cs");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await language.WithDocumentAsync(project, caller, new Dictionary<string, string>(),
            (document, _) =>
            {
                Check(document.Project.TryGetCompilation(out var cachedCompilation),
                    "Startup compilation remains cached before the first editor request, even after GC");
                return Task.FromResult(document);
            }, CancellationToken.None);
        var buffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [fixture] = "public class Sample { public int Count { get; set; } public void Run() {} " +
                "private void Hidden() {} public static void StaticOnly() {} public int @event; } " +
                "public static class Extensions { public static void Extra(this Sample item) {} }"
        };

        async Task<EditorSuggestions> Complete(string marked)
        {
            int caret = marked.IndexOf("$$", StringComparison.Ordinal);
            buffers[caller] = marked.Replace("$$", "");
            return await language.WithDocumentAsync(project, caller, buffers,
                (document, token) => CSharpSuggestions.GetAsync(document, caret, token), CancellationToken.None)
                ?? throw new Exception("Project document not found");
        }

        var instance = await Complete("class Caller { void M() { var obj = new Sample(); obj.$$ } }");
        Check(instance.Items.Any(i => i.Name == "Count"), "Unsaved property suggested");
        Check(instance.Items.Any(i => i.Name == "Run"), "Instance method suggested");
        Check(instance.Items.Any(i => i.Name == "Extra"), "Extension method suggested");
        Check(instance.Items.All(i => i.Name != "Hidden" && i.Name != "StaticOnly"), "Accessibility and instance context respected");
        Check(instance.Items.Any(i => i.InsertionText == "@event"), "Escaped identifiers insert correctly");

        var statics = await Complete("class Caller { void M() { Sample.$$ } }");
        Check(statics.Items.Any(i => i.Name == "StaticOnly") && statics.Items.All(i => i.Name != "Run"), "Static context respected");
        var framework = await Complete("class Caller { void M() { string text = \"hello\"; text.$$ } }");
        Check(framework.Items.Any(i => i.Name == "Substring"), "Framework members suggested");
        var prefix = await Complete("class Caller { void M() { string text = \"hello\"; text.Sub$$ } }");
        Check(buffers[caller][prefix.Start..].StartsWith("Sub"), "Replacement begins at the typed prefix");
        var comment = await Complete("class Caller { void M() { // text.$$\n } }");
        Check(comment.Items.Count == 0, "No completion inside comments");

        buffers[fixture] = "public class Sample { public int FreshProperty { get; set; } }";
        var changed = await Complete("class Caller { void M() { var obj = new Sample(); obj.$$ } }");
        Check(changed.Items.Any(i => i.Name == "FreshProperty") && changed.Items.All(i => i.Name != "Count"),
            "A second unsaved buffer replaces earlier semantic information");

        buffers[caller] = "class Caller { void Method() { var obj = new Sample(); obj.FreshProperty = 42; } }";
        var spans = await language.WithDocumentAsync(project, caller, buffers,
            async (document, token) => (await Classifier.GetClassifiedSpansAsync(document,
                new TextSpan(0, buffers[caller].Length), token)).ToArray(), CancellationToken.None);
        Check(spans is not null && spans.Any(s => s.ClassificationType == "class name") &&
            spans.Any(s => s.ClassificationType == "property name") &&
            spans.Any(s => s.ClassificationType == "method name"), "Roslyn distinguishes types, properties, and methods");

        foreach (string source in new[]
        {
            "", "class C {}",
            "/// <summary>Example <see cref=\"System.String\"/></summary>\n" +
            "class C { /* multiline\n comment\n here */ string S = @\"one\ntwo\nthree\"; " +
            "string M(int value) => $\"value: {value + 1}\"; }",
            "class C { string S = \"\"\"\nfirst\nsecond\nthird\n\"\"\"; " +
            "bool M(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @\"[a-z]+\\d\"); }",
            "class C {\n" + string.Join("\n", Enumerable.Range(0, 400).Select(i =>
                $"public static int Method{i}(int value) {{ var result = System.Math.Abs(value); return result + {i}; }}")) + "\n}",
            "class C { void M() { var unfinished = new System.Collections.Generic.List<"
        })
        {
            buffers[caller] = source;
            await language.WithDocumentAsync(project, caller, buffers, async (document, token) =>
            {
                var model = await document.GetSemanticModelAsync(token);
                var single = await EditorClassification.GetAsync(document, false, token);
                var parallel = await EditorClassification.GetAsync(document, true, token);
                Check(new HashSet<ClassifiedSpan>(single).SetEquals(parallel),
                    $"Parallel classification matches whole-file classification ({source.Length} characters)");
                GC.KeepAlive(model);
                return document;
            }, CancellationToken.None);
        }

        var firstDocument = await language.WithDocumentAsync(project, caller, buffers,
            (document, _) => Task.FromResult(document), CancellationToken.None);
        var sameDocument = await language.WithDocumentAsync(project, caller, buffers,
            (document, _) => Task.FromResult(document), CancellationToken.None);
        Check(ReferenceEquals(firstDocument, sameDocument), "Unchanged buffers reuse the Roslyn document and its caches");
        buffers[caller] += "\n// incremental edit";
        var editedDocument = await language.WithDocumentAsync(project, caller, buffers,
            (document, _) => Task.FromResult(document), CancellationToken.None);
        var edits = (await editedDocument!.GetTextChangesAsync(firstDocument!)).ToArray();
        Check(edits.Length == 1 && edits[0].Span.Length == 0,
            "A small buffer edit remains an incremental Roslyn text change");
        buffers.Remove(fixture);
        var closedDocument = await language.WithDocumentAsync(project, fixture, buffers,
            (document, _) => Task.FromResult(document), CancellationToken.None);
        Check((await closedDocument!.GetTextAsync()).ToString() == await File.ReadAllTextAsync(fixture),
            "Closing an unsaved buffer restores the loaded project text");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await EditorClassification.GetAsync(editedDocument!, true, cancelled.Token);
            throw new Exception("Cancelled parallel classification was accepted");
        }
        catch (OperationCanceledException) { }
        try
        {
            await language.WithDocumentAsync(project, caller, buffers,
                (document, token) => CSharpSuggestions.GetAsync(document, 0, token), cancelled.Token);
            throw new Exception("Cancelled request was accepted");
        }
        catch (OperationCanceledException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}

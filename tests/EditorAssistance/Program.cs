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
        RunAsync(Path.GetFullPath(args[0])).GetAwaiter().GetResult();
        Console.WriteLine("PASS: editor themes, project-aware completion, unsaved buffers, semantic colors, cancellation.");
    }

    private static async Task RunAsync(string project)
    {
        await using var language = new EditorLanguageService();
        string directory = Path.GetDirectoryName(project)!;
        string caller = Path.Combine(directory, "Program.cs");
        string fixture = Path.Combine(directory, "CompletionFixture.cs");
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

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
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

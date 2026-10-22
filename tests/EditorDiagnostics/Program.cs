using Lavender.Infrastructure.Editing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using var workspace = new AdhocWorkspace();
var project = workspace.AddProject("Fixture", LanguageNames.CSharp)
    .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
    .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
var document = project.AddDocument("Example.cs", SourceText.From("class Example { void Run() { Missing(); } }"), filePath: "Example.cs");
var errors = await EditorDiagnostics.GetAsync(document, default);
Check(errors.Any(e => e.Code == "CS0103" && e.Line == 1 && e.FilePath == "Example.cs" && e.Length > 0), "Semantic errors include navigation spans");
document = document.WithText(SourceText.From("class Example { void Run() { int value = ; } }"));
errors = await EditorDiagnostics.GetAsync(document, default);
Check(errors.Any(e => e.Severity == "Error"), "Syntax errors are reported");
document = document.WithText(SourceText.From("class Example { void Run() { } }"));
Check((await EditorDiagnostics.GetAsync(document, default)).Length == 0, "Correcting unsaved text clears errors");
var other = document.Project.AddDocument("Other.cs", "class Other { void Run() { Missing(); } }");
document = other.Project.GetDocument(document.Id)!;
Check((await EditorDiagnostics.GetAsync(document, default)).Length == 0, "Other files do not leak into current-file diagnostics");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await EditorDiagnostics.GetAsync(document.WithText(SourceText.From("class New {}")), cancelled.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { Console.WriteLine("PASS: Cancelled work stops"); }
static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS: " + label); }

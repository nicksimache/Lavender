using Lavender.App;
using Lavender.Application.Agent;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

internal static class Program
{
    [STAThread] static void Main()
    {
        const string edit = """{"success":true,"changed":true,"path":"PlayerController.cs","status":"updated","content_hash":"hash","line_changes":[{"added_lines":96,"line_delta":69}]}""";
        var wrapped = JsonSerializer.Serialize(new { content = new[] { new { text = edit } } });
        var change = FileProgress.FromResult(wrapped)!;
        Check(change.Added == 96 && change.Removed == 27, "MCP edit counts are extracted");
        Check(FileProgress.FromResult(edit.Replace("\"success\":true", "\"success\":false")) is null, "Failed edits cannot appear as changes");
        Check(FileProgress.FromResult(edit.Replace("\"changed\":true", "\"changed\":false")) is null, "No-op edits cannot appear as changes");
        Application app = new();
        MainWindow window = new();
        var panel = (StackPanel)window.FindName("ChatMessagesPanel");
        panel.Children.Clear();
        void Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        Call("StartProgressTurn");
        var thinking = (TextBlock)panel.Children[0];
        Check(thinking.Inlines.OfType<Run>().All(r => ((SolidColorBrush)r.Foreground).HasAnimatedProperties), "Each active letter animates");
        Call("ShowAgentProgress", "Using lavender_apply_edits…");
        Check(panel.Children.Count == 1 && ReferenceEquals(panel.Children[0], thinking) &&
            new TextRange(thinking.ContentStart, thinking.ContentEnd).Text == "Thinking…", "Tools keep the same Thinking label");
        var record = new ToolExecutionRecord { CallId = "1", ToolName = "lavender_apply_edits", ArgumentsJson = "{}", ResultJson = wrapped, Outcome = "succeeded", StartedAt = DateTimeOffset.UtcNow };
        Call("AppendToolActivity", record);
        Call("AppendToolActivity", record);
        Check(panel.Children.Count == 2, "Batched result appears only once");
        Check(!thinking.Inlines.OfType<Run>().Any(r => r.Foreground is SolidColorBrush b && b.HasAnimatedProperties), "Completed steps stop animating");
        Call("ShowAgentProgress", "Thinking…");
        Call("FinishProgressState");
        Call("AddMessageBubble", "I added the requested functionality.", false);
        Check(panel.Children.Count == 4, "Progress and final reply are ordered");
        Check(panel.Children[3] is RichTextBox reply && reply.Padding.Left == 0 && reply.Document.PagePadding.Left == 0,
            "Final reply has no extra left indentation");
        var history = (ListBox)window.FindName("ConversationPicker");
        history.IsEnabled = false;
        history.ApplyTemplate();
        var historyBackground = (Border)history.Template.FindName("HistoryBackground", history);
        Check(historyBackground.Background is SolidColorBrush background && background.Color.A == 0,
            "Disabled history retains transparent background");
        Check(window.FindName("AgentActivityLog") is null, "Old activity panel removed");
        panel.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        panel.Measure(new Size(460, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 460, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var renderedReply = (RichTextBox)panel.Children[3];
        var start = renderedReply.Document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward)!;
        var rect = start.GetCharacterRect(LogicalDirection.Forward);
        double replyX = renderedReply.TranslatePoint(rect.TopLeft, panel).X;
        foreach (var progress in panel.Children.OfType<TextBlock>())
        {
            double progressX = progress.TranslatePoint(progress.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft, panel).X;
            Check(Math.Abs(replyX - progressX) < 0.5, "Rendered progress and reply text share the same left edge");
        }
        var bitmap = new RenderTargetBitmap(460, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "progress-preview.png"))) encoder.Save(file);
        window.Close();
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}

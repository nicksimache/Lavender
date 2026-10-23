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
        Check(FileProgress.FromResult(wrapped) is { Added: 96, Removed: 27 }, "MCP edit counts are retained in evidence");
        Check(FileProgress.FromResult(edit.Replace("\"success\":true", "\"success\":false")) is null, "Failed edits cannot appear as changes");
        Application app = new();
        MainWindow window = new();
        var panel = (StackPanel)window.FindName("ChatMessagesPanel");
        panel.Children.Clear();
        object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        string Text(TextBlock row) => new TextRange(row.ContentStart, row.ContentEnd).Text;
        void Drain() { while ((bool)Call("RevealNextProgressCharacter")!) { } }
        var failure = new ToolExecutionRecord { CallId = "error", ToolName = "unity_get_editor_state",
            ArgumentsJson = "{}", Outcome = "failed", Error = "private connection exception",
            UserExplanation = "Old error explanation", StartedAt = DateTimeOffset.UtcNow };
        Call("StartProgressTurn");
        var thinking = (TextBlock)panel.Children[0];
        Check(Text(thinking) == "", "Thinking starts empty for typing");
        Call("RevealNextReplyCharacter", null, EventArgs.Empty);
        Check(Text(thinking) == "T", "Response timer reveals one progress letter");
        foreach (string outcome in new[] { "failed", "blocked", "timed_out", "cancelled" })
        {
            failure.Outcome = outcome;
            Call("AppendToolActivity", failure);
            Call("ShowAgentProgress", "Using tools");
        }
        Check(panel.Children.Count == 1 && ReferenceEquals(thinking, panel.Children[0]), "Errors never add messages or repeat Thinking");
        Drain();
        Check(Text(thinking) == "Thinking…", "Thinking finishes typing");
        var record = new ToolExecutionRecord { CallId = "1", ToolName = "lavender_apply_edits", ArgumentsJson = "{}", ResultJson = wrapped, Outcome = "succeeded", StartedAt = DateTimeOffset.UtcNow };
        Call("AppendToolActivity", record);
        Call("AppendToolActivity", record);
        Check(panel.Children.Count == 2, "Duplicate edit receipt is suppressed");
        var changed = (TextBlock)panel.Children[1];
        Check(Text(changed) == "", "Changed row starts empty");
        Call("RevealNextReplyCharacter", null, EventArgs.Empty);
        Check(Text(changed) == "C", "Changed row types using the response timer");
        Call("ShowAgentProgress", "Thinking…");
        Call("ShowAgentProgress", "Thinking…");
        Check(panel.Children.Count == 3, "Only one Thinking row follows the edit");
        Drain();
        Check(Text(changed) == "Changed PlayerController.cs  +96  −27", "Changed row includes line counts");
        var counts = changed.Inlines.OfType<Run>().ToArray();
        Check(((SolidColorBrush)counts[1].Foreground).Color == Color.FromRgb(98, 196, 139) &&
            ((SolidColorBrush)counts[2].Foreground).Color == Color.FromRgb(145, 96, 190),
            "Added lines are green and removed lines are purple after typing");
        Call("FinishProgressState");
        Call("AddMessageBubble", "I added the requested functionality.", false);
        Check(panel.Children.Count == 4 && panel.Children[3] is RichTextBox, "Progress and final reply are ordered");

        // Replay several failed/read-only steps followed by one successful edit.
        var chat = new Conversation { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        var turn = new ConversationTurn { Id = Guid.NewGuid(), UserMessage = "Update the player", CreatedAt = DateTimeOffset.UtcNow, AssistantMessage = "Done." };
        chat.Turns.Add(turn);
        for (int i = 0; i < 4; i++)
        {
            var step = new AgentStep { Iteration = i, CreatedAt = DateTimeOffset.UtcNow };
            step.ToolCalls.Add(i == 3 ? record : failure);
            turn.Steps.Add(step);
        }
        Call("ShowConversation", chat);
        var rows = panel.Children.OfType<TextBlock>().ToArray();
        Check(rows.Length == 2 && Text(rows[0]) == "Thinking…" && Text(rows[1]) == "Changed PlayerController.cs  +96  −27",
            "History merges consecutive Thinking steps and hides saved errors");
        Check(!(bool)Call("RevealNextProgressCharacter")!, "History does not leave queued animations");
        panel.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        panel.Measure(new Size(460, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 460, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(460, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "progress-preview.png"))) encoder.Save(file);
        window.Close();
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}

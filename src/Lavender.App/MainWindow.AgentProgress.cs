using Lavender.Application.Agent;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lavender.App;

public partial class MainWindow
{
    // Match the native RichTextBox text inset used by assistant replies.
    private static readonly Thickness ProgressTextPadding = new(5, 0, 0, 0);
    private TextBlock? _progressState;
    private readonly HashSet<string> _progressReceipts = [];
    private readonly Queue<(Run Run, string Text)> _progressRevealQueue = new();
    private bool _restoringProgress;

    private void StartProgressTurn()
    {
        FlushProgressReveals();
        FinishProgressState();
        _progressReceipts.Clear();
        ShowAgentProgress("Thinking…");
    }

    private void ShowAgentProgress(string status)
    {
        if (_progressState is not null) return;
        _progressState = new TextBlock { FontSize = 12, Padding = ProgressTextPadding,
            Foreground = new SolidColorBrush(Color.FromRgb(132, 126, 145)),
            Margin = new Thickness(2, 5, 0, 9) };
        AddProgressRow(_progressState, "Thinking…");
    }

    private void AddProgressRow(TextBlock row, string text, int? added = null, int? removed = null)
    {
        ChatMessagesPanel.Children.Add(row);
        void AddSegment(string value, Brush? foreground = null)
        {
            var run = new Run();
            if (foreground is not null) run.Foreground = foreground;
            row.Inlines.Add(run);
            if (_restoringProgress) run.Text = value;
            else _progressRevealQueue.Enqueue((run, value));
        }
        AddSegment(text);
        if (added is int plus && removed is int minus)
        {
            AddSegment($"  +{plus}", new SolidColorBrush(Color.FromRgb(98, 196, 139)));
            AddSegment($"  −{minus}", new SolidColorBrush(Color.FromRgb(145, 96, 190)));
            row.ToolTip = "Lines inserted/replaced and removed by this edit, not a net Git diff.";
        }
        if (!_restoringProgress) _replyRevealTimer.Start();
        ChatScrollViewer.ScrollToEnd();
    }

    private bool RevealNextProgressCharacter()
    {
        if (!_progressRevealQueue.TryPeek(out var pending)) return false;
        // Use the same timer and Unicode text-element stepping as the final response.
        pending.Run.Text += StringInfo.GetNextTextElement(pending.Text, pending.Run.Text.Length);
        if (pending.Run.Text.Length == pending.Text.Length) _progressRevealQueue.Dequeue();
        ChatScrollViewer.ScrollToEnd();
        return true;
    }

    private void FlushProgressReveals()
    {
        while (_progressRevealQueue.TryDequeue(out var pending)) pending.Run.Text = pending.Text;
    }

    private void FinishProgressState() => _progressState = null;

    private void AppendToolActivity(ToolExecutionRecord record)
    {
        // Errors and inspection results stay in the agent's evidence for the final answer.
        if (record.Outcome is "failed" or "blocked" or "timed_out" or "cancelled") return;
        var change = record.FileProgress ?? FileProgress.FromResult(record.ResultJson);
        if (change is null || !_progressReceipts.Add(change.Receipt)) return;
        ShowAgentProgress("Thinking…");
        FinishProgressState();
        var row = new TextBlock { FontSize = 12, Padding = ProgressTextPadding, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(196, 191, 204)), Margin = new Thickness(2, 3, 0, 9) };
        AddProgressRow(row, $"Changed {change.Path}", change.Added, change.Removed);
    }

    private void AppendAgentActivity(string message) => Trace.WriteLine(message);
}

using Lavender.Application.Agent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lavender.App;

public partial class MainWindow
{
    // RichTextBox reserves five DIPs inside its text view even with zero PagePadding.
    // Match that native inset so progress text and selectable replies share a left edge.
    private static readonly Thickness ProgressTextPadding = new(5, 0, 0, 0);
    private TextBlock? _progressState;
    private string? _progressLabel;
    private readonly List<SolidColorBrush> _pulseBrushes = [];
    private readonly HashSet<string> _progressReceipts = [];

    private void StartProgressTurn()
    {
        FinishProgressState();
        _progressReceipts.Clear();
        ShowAgentProgress("Thinking…");
    }

    private void ShowAgentProgress(string status)
    {
        const string label = "Thinking…";
        if (_progressLabel == label && _progressState is not null) return;
        StopProgressPulse();
        if (_progressState is null)
        {
            _progressState = new TextBlock { FontSize = 12, Padding = ProgressTextPadding, Margin = new Thickness(2, 5, 0, 9) };
            ChatMessagesPanel.Children.Add(_progressState);
        }
        _progressLabel = label;
        _progressState.Inlines.Clear();
        for (int i = 0; i < label.Length; i++)
        {
            var brush = new SolidColorBrush(Color.FromRgb(132, 126, 145));
            _pulseBrushes.Add(brush);
            _progressState.Inlines.Add(new Run(label[i].ToString()) { Foreground = brush });
            double start = i * 0.075;
            var pulse = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3), RepeatBehavior = RepeatBehavior.Forever };
            pulse.KeyFrames.Add(new DiscreteColorKeyFrame(brush.Color, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
            pulse.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(240, 226, 255), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.18))));
            pulse.KeyFrames.Add(new LinearColorKeyFrame(brush.Color, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.55))));
            brush.BeginAnimation(SolidColorBrush.ColorProperty, pulse);
        }
        ChatScrollViewer.ScrollToEnd();
    }

    private void StopProgressPulse()
    {
        foreach (var brush in _pulseBrushes) brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _pulseBrushes.Clear();
    }

    private void FinishProgressState()
    {
        StopProgressPulse();
        if (_progressState is not null && _progressLabel is not null)
            _progressState.Text = _progressLabel.TrimEnd('…');
        if (_progressState is not null) _progressState.Foreground = new SolidColorBrush(Color.FromRgb(132, 126, 145));
        _progressState = null;
        _progressLabel = null;
    }

    private void AppendToolActivity(ToolExecutionRecord record)
    {
        var change = record.FileProgress ?? FileProgress.FromResult(record.ResultJson);
        if (change is not null && _progressReceipts.Add(change.Receipt))
        {
            FinishProgressState();
            var row = new TextBlock { FontSize = 12, Padding = ProgressTextPadding, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(196, 191, 204)), Margin = new Thickness(2, 3, 0, 9),
                ToolTip = "Lines inserted/replaced and removed by this edit, not a net Git diff." };
            string verb = change.Status == "created" ? "Created" : change.Status == "deleted" ? "Deleted" : change.Status == "moved" ? "Moved" : "Changed";
            row.Inlines.Add(new Run($"{verb} {change.Path}  "));
            if (change.Added is int added && change.Removed is int removed)
            {
                row.Inlines.Add(new Run($"+{added}") { Foreground = new SolidColorBrush(Color.FromRgb(98, 196, 139)) });
                row.Inlines.Add(new Run($"  −{removed}") { Foreground = new SolidColorBrush(Color.FromRgb(145, 96, 190)) });
            }
            ChatMessagesPanel.Children.Add(row);
            ChatScrollViewer.ScrollToEnd();
        }
        else if (record.Outcome is "failed" or "blocked" or "timed_out" or "cancelled")
        {
            FinishProgressState();
            ChatMessagesPanel.Children.Add(new TextBlock { Text = record.OutcomeMessage ?? record.Error ?? "Action did not complete.",
                Foreground = Brushes.RosyBrown, FontSize = 12, Padding = ProgressTextPadding, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 3, 0, 9) });
        }
    }

    // Retain technical diagnostics for debugging without a separate activity panel.
    private void AppendAgentActivity(string message) => Trace.WriteLine(message);
}

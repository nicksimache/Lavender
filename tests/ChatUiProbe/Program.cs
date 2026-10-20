using Lavender.App;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
    Application app = new();
    MainWindow window = new();
    StackPanel panel = (StackPanel)window.FindName("ChatMessagesPanel")!;
    panel.Children.Clear();
    MethodInfo add = typeof(MainWindow).GetMethod("AddMessageBubble", BindingFlags.Instance | BindingFlags.NonPublic)!;
    add.Invoke(window, ["hello", true]);
    add.Invoke(window, ["This longer question should wrap inside the maximum user-message width when the chat pane is resized.", true]);
    add.Invoke(window, ["Assistant **bold** reply without a bubble.", false]);
    panel.Measure(new Size(350, 1000));
    panel.Arrange(new Rect(0, 0, 350, panel.DesiredSize.Height));
    panel.UpdateLayout();

    Border shortBubble = (Border)panel.Children[0];
    Border longBubble = (Border)panel.Children[1];
    if (shortBubble.ActualWidth >= 120 ||
        shortBubble.HorizontalAlignment != HorizontalAlignment.Right ||
        longBubble.ActualWidth > 350 * 0.7 + 1 ||
        shortBubble.BorderThickness != new Thickness(0) ||
        panel.Children[2] is not TextBlock assistant ||
        !assistant.Inlines.OfType<Bold>().Any())
        throw new Exception($"Chat layout incorrect: short={shortBubble.ActualWidth}, long={longBubble.ActualWidth}");

    Console.WriteLine($"Short user bubble: {shortBubble.ActualWidth:F1}px; long: {longBubble.ActualWidth:F1}px; assistant: rendered Markdown without a bubble.");
    typeof(MainWindow).GetMethod("AppendResponseDelta", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, ["abcdefghijklmnopqrstuvwxyz"]);
    DispatcherFrame frame = new();
    DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    delay.Tick += (_, _) => { delay.Stop(); frame.Continue = false; };
    delay.Start();
    Dispatcher.PushFrame(frame);
    StringBuilder visible = (StringBuilder)typeof(MainWindow)
        .GetField("_visibleReply", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    if (visible.Length == 0 || visible.Length >= 26)
        throw new Exception($"Reply did not reveal progressively: {visible.Length} characters after 180 ms.");
    Console.WriteLine($"Streaming reveal: {visible.Length} of 26 characters after 180 ms.");
    int height = (int)Math.Ceiling(panel.ActualHeight);
    RenderTargetBitmap bitmap = new(350, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(panel);
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    string output = Path.Combine(Path.GetTempPath(), "LavenderChatLayout.png");
    using FileStream stream = File.Create(output);
    encoder.Save(stream);
    window.Close();
    }
}

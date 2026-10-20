using Lavender.App;
using Lavender.Application.Agent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfMath.Controls;

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
    var models = (IReadOnlyList<AgentModelOption>)typeof(MainWindow)
        .GetField("_availableModels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    MethodInfo selectModel = typeof(MainWindow).GetMethod("SelectModelFromMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
    StackPanel modelMenu = (StackPanel)window.FindName("ModelMenu")!;
    StackPanel reasoningMenu = (StackPanel)window.FindName("ReasoningMenu")!;
    StackPanel reasoningChoices = (StackPanel)window.FindName("ReasoningListPanel")!;
    if (modelMenu.Children.Count != 1 || modelMenu.Children[0] is not ScrollViewer modelListScroll ||
        modelListScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Hidden)
        throw new Exception("The model list should have no heading or visible scrollbar.");
    selectModel.Invoke(window, [models.First(model => model.Id == "gpt-5")]);
    if (modelMenu.Visibility != Visibility.Collapsed || reasoningMenu.Visibility != Visibility.Visible ||
        reasoningChoices.Children.Count != 5 || reasoningMenu.Children.Count != 3)
        throw new Exception("Selecting GPT-5 did not show its stacked reasoning choices.");
    Border menuBorder = (Border)((System.Windows.Controls.Primitives.Popup)window.FindName("ModelMenuPopup")!).Child;
    menuBorder.Measure(new Size(300, double.PositiveInfinity));
    menuBorder.Arrange(new Rect(0, 0, menuBorder.DesiredSize.Width, menuBorder.DesiredSize.Height));
    menuBorder.UpdateLayout();
    RenderTargetBitmap menuImage = new(
        (int)Math.Ceiling(menuBorder.ActualWidth), (int)Math.Ceiling(menuBorder.ActualHeight),
        96, 96, PixelFormats.Pbgra32);
    menuImage.Render(menuBorder);
    PngBitmapEncoder menuEncoder = new();
    menuEncoder.Frames.Add(BitmapFrame.Create(menuImage));
    using (FileStream menuStream = File.Create(Path.Combine(AppContext.BaseDirectory, "ReasoningMenu.png")))
        menuEncoder.Save(menuStream);
    ((Button)reasoningChoices.Children[4]).RaiseEvent(
        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    if (models.First(model => model.Id == "gpt-5").ReasoningEffort != ReasoningEffort.High)
        throw new Exception("Choosing High reasoning did not update GPT-5.");
    TextBlock pickerReasoning = (TextBlock)window.FindName("ModelPickerReasoningLabel")!;
    TextBlock pickerModel = (TextBlock)window.FindName("ModelPickerLabel")!;
    if (pickerReasoning.Text != "High" || pickerReasoning.Visibility != Visibility.Visible ||
        pickerModel.Text != "GPT-5" ||
        ((SolidColorBrush)pickerModel.Foreground).Color != Color.FromRgb(240, 240, 240) ||
        ((SolidColorBrush)pickerReasoning.Foreground).Color != Color.FromRgb(160, 160, 160))
        throw new Exception("The selected reasoning level is missing beside the model name.");
    selectModel.Invoke(window, [models.First(model => model.Id == "gpt-4.1-mini")]);
    if (((AgentModelOption)typeof(MainWindow).GetField("_selectedModel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Id != "gpt-4.1-mini" ||
        pickerReasoning.Visibility != Visibility.Collapsed)
        throw new Exception("Selecting a model without adjustable reasoning did not update the picker.");
    selectModel.Invoke(window, [models.First(model => model.Id == "gpt-6-astra")]);
    if (reasoningChoices.Children.Count != 6)
        throw new Exception("GPT-6 Astra did not show its supported reasoning choices.");
    ((Button)reasoningChoices.Children[4]).RaiseEvent(
        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    if (pickerReasoning.Text != "XHigh")
        throw new Exception("GPT-6 Astra reasoning did not update the gray model label.");
    AgentModelOption sol = models.First(model => model.Id == "gpt-6-sol");
    selectModel.Invoke(window, [sol]);
    if (reasoningChoices.Children.Count != 7)
        throw new Exception("GPT-6 Sol did not show its supported reasoning choices.");
    ((Button)reasoningChoices.Children[1]).RaiseEvent(
        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    if (sol.ReasoningEffort != ReasoningEffort.None || pickerReasoning.Text != "" ||
        pickerReasoning.Visibility != Visibility.Collapsed)
        throw new Exception("None reasoning should remain selected but have no picker label.");
    Console.WriteLine("Model menu: adjustable models show stacked reasoning choices; fixed models select directly.");
    add.Invoke(window, [@"Inline $x^2$ and \(\frac{1}{2}\).", false]);
    add.Invoke(window, ["Display:\n$$\\frac{a}{b}$$\n\\[\\sum_{i=1}^{n} i\\]", false]);
    add.Invoke(window, ["Still typing $x^2 and `code $x$`.", false]);
    add.Invoke(window, [@"Invalid $\notARealMathCommand$ fallback.", false]);
    add.Invoke(window, ["Multiline:\n$$\n\\frac{2}{3}\n$$\n\\[\n\\int_0^1 x^2 dx\n\\]", false]);
    add.Invoke(window, [@"The index \(i\) is shown here.", false]);
    TextBlock inlineMath = (TextBlock)panel.Children[3];
    TextBlock displayMath = (TextBlock)panel.Children[4];
    TextBlock unfinished = (TextBlock)panel.Children[5];
    TextBlock invalid = (TextBlock)panel.Children[6];
    TextBlock multiline = (TextBlock)panel.Children[7];
    TextBlock simpleIndex = (TextBlock)panel.Children[8];
    if (inlineMath.Inlines.OfType<InlineUIContainer>().Count() != 2 ||
        displayMath.Inlines.OfType<InlineUIContainer>().Count() != 2 ||
        inlineMath.Inlines.OfType<InlineUIContainer>().Any(item => item.Child is not FormulaControl { HasError: false }) ||
        displayMath.Inlines.OfType<InlineUIContainer>().Any(item => item.Child is not FormulaControl { HasError: false }) ||
        unfinished.Inlines.OfType<InlineUIContainer>().Any() ||
        invalid.Inlines.OfType<InlineUIContainer>().Any() ||
        multiline.Inlines.OfType<InlineUIContainer>().Count() != 2 ||
        simpleIndex.Inlines.OfType<InlineUIContainer>().Count() != 1 ||
        !string.Concat(invalid.Inlines.OfType<Run>().Select(run => run.Text)).Contains(@"$\notARealMathCommand$"))
        throw new Exception("LaTeX rendering or fallback did not behave as expected.");
    Console.WriteLine("LaTeX: inline and display equations rendered; unfinished, code, and invalid input stayed readable.");
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
    panel.Measure(new Size(350, double.PositiveInfinity));
    panel.Arrange(new Rect(0, 0, 350, panel.DesiredSize.Height));
    panel.UpdateLayout();
    int height = (int)Math.Ceiling(panel.ActualHeight);
    RenderTargetBitmap bitmap = new(350, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(panel);
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    string output = Path.Combine(AppContext.BaseDirectory, "LavenderChatLayout.png");
    using FileStream stream = File.Create(output);
    encoder.Save(stream);
    window.Close();
    }
}

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lavender.App.Rendering;

/// <summary>Renders the Markdown used in chat replies without HTML or external resources.</summary>
internal static class AssistantMarkdown
{
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>.+?)\*\*|(?<!\*)\*(?!\*)(?<italic>.+?)\*|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<url>[^)]+)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex OrderedList = new(@"^\d+[.)]\s+", RegexOptions.Compiled);

    public static void Render(TextBlock block, string markdown)
    {
        block.Inlines.Clear();
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inCodeBlock = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (index > 0)
                block.Inlines.Add(new LineBreak());

            string line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                block.Inlines.Add(new Run(line)
                {
                    FontFamily = new FontFamily("Cascadia Mono"),
                    Background = new SolidColorBrush(Color.FromRgb(43, 43, 43))
                });
                continue;
            }

            int headingDepth = 0;
            while (headingDepth < line.Length && headingDepth < 6 && line[headingDepth] == '#')
                headingDepth++;
            if (headingDepth > 0 && headingDepth < line.Length && line[headingDepth] == ' ')
            {
                Span heading = new() { FontWeight = FontWeights.SemiBold,
                    FontSize = headingDepth == 1 ? 18 : headingDepth == 2 ? 16 : 14 };
                AddInline(heading.Inlines, line[(headingDepth + 1)..]);
                block.Inlines.Add(heading);
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("+ ", StringComparison.Ordinal))
            {
                block.Inlines.Add(new Run("•  "));
                line = line[2..];
            }
            else if (OrderedList.Match(line) is { Success: true } ordered)
            {
                block.Inlines.Add(new Run(line[..ordered.Length]));
                line = line[ordered.Length..];
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                block.Inlines.Add(new Run("│  ") { Foreground = Brushes.Gray });
                line = line[2..];
            }

            AddInline(block.Inlines, line);
        }
    }

    private static void AddInline(InlineCollection output, string text)
    {
        int position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
                output.Add(new Run(text[position..match.Index]));
            if (match.Groups["bold"].Success)
            {
                Bold bold = new();
                AddInline(bold.Inlines, match.Groups["bold"].Value);
                output.Add(bold);
            }
            else if (match.Groups["italic"].Success)
            {
                Italic italic = new();
                AddInline(italic.Inlines, match.Groups["italic"].Value);
                output.Add(italic);
            }
            else if (match.Groups["code"].Success)
                output.Add(new Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono"),
                    Background = new SolidColorBrush(Color.FromRgb(43, 43, 43))
                });
            else
            {
                // Show the label and URL as text; opening links requires a user action.
                string url = match.Groups["url"].Value;
                output.Add(new Run(match.Groups["label"].Value)
                {
                    TextDecorations = TextDecorations.Underline,
                    ToolTip = url
                });
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            output.Add(new Run(text[position..]));
    }
}

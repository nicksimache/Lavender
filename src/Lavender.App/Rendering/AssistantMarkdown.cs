using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfMath.Controls;

namespace Lavender.App.Rendering;

/// <summary>Renders the Markdown used in chat replies without HTML or external resources.</summary>
internal static class AssistantMarkdown
{
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>.+?)\*\*|(?<!\*)\*(?!\*)(?<italic>.+?)\*|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<url>[^)]+)\)|(?<!\\)\\\((?<mathParen>[^`]+?)\\\)|(?<![\\$])\$(?![\s$])(?<mathDollar>[^$`\r\n]*?\S)\$(?!\$)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex OrderedList = new(@"^\d+[.)]\s+", RegexOptions.Compiled);

    public static void Render(RichTextBox box, string markdown)
    {
        // Reuse the same inline renderer while allowing native text selection and Ctrl+C.
        TextBlock source = new();
        Render(source, markdown);
        Paragraph paragraph = new() { Margin = new Thickness(0) };
        while (source.Inlines.FirstInline is Inline inline)
        {
            source.Inlines.Remove(inline);
            paragraph.Inlines.Add(inline);
        }
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(paragraph);
        box.Tag = markdown;
    }

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

            if (TryReadDisplayMath(lines, index, out string math, out int endIndex, out string original))
            {
                AddMath(block.Inlines, math, original, display: true);
                index = endIndex;
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
            else if (match.Groups["mathParen"].Success || match.Groups["mathDollar"].Success)
            {
                string formula = match.Groups["mathParen"].Success
                    ? match.Groups["mathParen"].Value : match.Groups["mathDollar"].Value;
                AddMath(output, formula, match.Value, display: false);
            }
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

    private static bool TryReadDisplayMath(string[] lines, int start, out string math, out int end, out string original)
    {
        math = original = "";
        end = start;
        string trimmed = lines[start].Trim();
        string opening = trimmed.StartsWith("$$", StringComparison.Ordinal) ? "$$"
            : trimmed.StartsWith("\\[", StringComparison.Ordinal) ? "\\[" : "";
        if (opening.Length == 0)
            return false;

        string closing = opening == "$$" ? "$$" : "\\]";
        string firstLine = trimmed[opening.Length..];
        int sameLineEnd = firstLine.IndexOf(closing, StringComparison.Ordinal);
        if (sameLineEnd >= 0 && string.IsNullOrWhiteSpace(firstLine[(sameLineEnd + closing.Length)..]))
        {
            math = firstLine[..sameLineEnd];
            original = lines[start];
            return true;
        }

        for (int candidate = start + 1; candidate < lines.Length; candidate++)
        {
            string candidateLine = lines[candidate].Trim();
            if (!candidateLine.EndsWith(closing, StringComparison.Ordinal))
                continue;

            end = candidate;
            original = string.Join("\n", lines[start..(end + 1)]);
            math = string.Join("\n", lines[(start + 1)..end].Prepend(firstLine)
                .Append(candidateLine[..^closing.Length])).Trim();
            return true;
        }

        // Keep an unfinished expression visible during streaming.
        return false;
    }

    private static void AddMath(InlineCollection output, string formula, string original, bool display)
    {
        try
        {
            FormulaControl control = new()
            {
                Formula = formula,
                Scale = display ? 17 : 13,
                Foreground = new SolidColorBrush(Color.FromRgb(216, 216, 216)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (control.HasError)
            {
                output.Add(new Run(original));
                return;
            }

            output.Add(new InlineUIContainer(control)
            {
                BaselineAlignment = BaselineAlignment.Center
            });
        }
        catch (Exception)
        {
            output.Add(new Run(original));
        }
    }
}

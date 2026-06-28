using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lavender.Services
{
    internal static class RichTextBoxRenderer
    {
        public static void Render(
            RichTextBox richTextBox,
            List<SyntaxHighlighter.CodeSpan> spans)
        {
            richTextBox.Document.Blocks.Clear();

            var paragraph = new Paragraph
            {
                Margin = new System.Windows.Thickness(0)
            };

            foreach (var span in spans)
            {
                var run = new Run(span.Text)
                {
                    Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(span.Color))
                };

                paragraph.Inlines.Add(run);
            }

            richTextBox.Document.Blocks.Add(paragraph);
        }
    }
}
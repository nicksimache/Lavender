using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Lavender.App.Rendering;

/// <summary>Lavender Night: language rules come from AvalonEdit and Roslyn, colors live here.</summary>
public static class EditorTheme
{
    // TODO: change this color scheme its ug

    public const string Text = "#E5E5E5";
    public const string Keyword = "#C6A0F6";
    public const string Type = "#8DDEAB";
    public const string Method = "#E6D89E";
    public const string Variable = "#8CC8F4";
    public const string Property = "#DCE0E8";
    public const string String = "#DFA6B9";
    public const string Number = "#B5D78F";
    public const string Comment = "#55C75A";
    public const string Operator = "#D5D5DC";



    private static readonly HashSet<IHighlightingDefinition> Themed = new();
    private static readonly Dictionary<string, Brush> BrushesByColor = new();

    /// <summary>Returns a cached frozen brush for a palette color.</summary>
    public static Brush Brush(string hex)
    {
        if(!BrushesByColor.TryGetValue(hex, out Brush? brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            BrushesByColor[hex] = brush;
        }

        return brush;
    }

    /// <summary>Selects the built-in language definition and applies the editor palette.</summary>
    public static IHighlightingDefinition? ForFile(string path)
    {
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));

        if(definition is null || !Themed.Add(definition))
        {
            return definition;
        }

        foreach (var color in definition.NamedHighlightingColors)
        {
            string name = color.Name.ToLowerInvariant();
            string hex = name switch
            {
                "methodcall" => Method,
                "valuetypekeywords" or "referencetypekeywords" or "typekeywords" => Type,
                "modifiers" or "visibility" or "namespacekeywords" => Operator,
                "getsetaddremove" => Property,
                "thisorbasereference" or "truefalse" or "nullorvaluekeywords" => Number,
                "punctuation" => "#BAC2DE",
                _ when name.Contains("comment") => Comment,
                _ when name.Contains("string") || name.Contains("char") || name.Contains("attributevalue") => String,
                _ when name.Contains("number") || name.Contains("digit") => Number,
                _ when name.Contains("method") || name.Contains("function") => Method,
                _ when name.Contains("attribute") => Property,
                _ when name.Contains("tag") => Type,
                _ when name.Contains("keyword") => Keyword,
                _ when name.Contains("preprocessor") => "#B39DDB",
                _ => Text
            };

            color.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
            color.FontWeight = FontWeights.Normal;
        }

        return definition;
    }

    /// <summary>Maps a Roslyn classification name to its editor foreground color.</summary>
    public static string? ForClassification(string kind) => kind switch
    {
        ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword => Keyword,

        ClassificationTypeNames.ClassName or ClassificationTypeNames.StructName
            or ClassificationTypeNames.InterfaceName or ClassificationTypeNames.EnumName
            or ClassificationTypeNames.DelegateName or ClassificationTypeNames.TypeParameterName
            or ClassificationTypeNames.RecordClassName or ClassificationTypeNames.RecordStructName => Type,

        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName => Method,

        ClassificationTypeNames.LocalName or ClassificationTypeNames.ParameterName or ClassificationTypeNames.FieldName => Variable,

        ClassificationTypeNames.PropertyName or ClassificationTypeNames.EventName => Property,

        ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral
            or ClassificationTypeNames.StringEscapeCharacter => String,

        ClassificationTypeNames.NumericLiteral or ClassificationTypeNames.EnumMemberName or ClassificationTypeNames.ConstantName => Number,

        ClassificationTypeNames.Comment or ClassificationTypeNames.XmlDocCommentText => Comment,

        ClassificationTypeNames.Operator or ClassificationTypeNames.OperatorOverloaded => Operator,

        ClassificationTypeNames.Punctuation => "#BAC2DE",

        ClassificationTypeNames.NamespaceName => "#A6ADC8",

        _ => null
    };
}

/// <summary>Retains unchanged token colors while fresh semantic classifications are pending.</summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private List<ClassifiedSpan> _spans = new();
    private int[] _maximumEnds = Array.Empty<int>();
    private readonly List<TextSpan> _invalidated = new();

    // Callers supply spans ordered by start. Prefix maxima also handle overlapping spans.
    public IReadOnlyList<ClassifiedSpan> Spans
    {
        get => _spans;
        set
        {
            _invalidated.Clear();
            _spans = value.ToList();
            _maximumEnds = new int[value.Count];

            int maximum = 0;
            for (int i = 0; i < value.Count; i++)
            {
                _maximumEnds[i] = maximum = Math.Max(maximum, value[i].TextSpan.End);
            }
        }
    }

    /// <summary>Finds the first classification that can overlap the requested document offset.</summary>
    public int FindFirstSpan(int offset)
    {
        int low = 0;
        int high = _spans.Count;

        while (low < high)
        {
            int middle = low + (high - low) / 2;

            if(_maximumEnds[middle] <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Preserves unaffected classifications and updates offsets and invalidation ranges after an edit.</summary>
    public void ApplyChange(int offset, int removedLength, int insertedLength)
    {
        int end = offset + removedLength;
        int delta = insertedLength - removedLength;

        int Map(int position) => position <= offset
            ? position
            : position >= end
                ? position + delta
                : offset + insertedLength;

        for (int i = 0; i < _invalidated.Count; i++)
        {
            _invalidated[i] = TextSpan.FromBounds(Map(_invalidated[i].Start), Map(_invalidated[i].End));
        }

        int first = FindFirstSpan(offset);
        int write = first;

        for (int i = first; i < _spans.Count; i++)
        {
            var span = _spans[i];

            if(span.TextSpan.End <= offset)
            {
                _spans[write++] = span;
            }
            else if(span.TextSpan.Start >= end)
            {
                _spans[write++] = new ClassifiedSpan(span.ClassificationType,
                    new TextSpan(span.TextSpan.Start + delta, span.TextSpan.Length));
            }
            else
            {
                _invalidated.Add(TextSpan.FromBounds(Map(span.TextSpan.Start), Map(span.TextSpan.End)));
            }
            // Tokens intersecting the edit fall back to immediate AvalonEdit highlighting.
        }

        _spans.RemoveRange(write, _spans.Count - write);

        int maximum = first == 0 ? 0 : _maximumEnds[first - 1];
        for (int i = first; i < _spans.Count; i++)
        {
            _maximumEnds[i] = maximum = Math.Max(maximum, _spans[i].TextSpan.End);
        }
    }

    /// <summary>Replaces semantic classifications and returns merged ranges whose colors may have changed.</summary>
    public IReadOnlyList<TextSpan> ReplaceSpans(IReadOnlyList<ClassifiedSpan> next)
    {
        // Compare classified ranges, including removals, so semantic changes elsewhere redraw too.
        var previous = new HashSet<ClassifiedSpan>(_spans);
        var incoming = new HashSet<ClassifiedSpan>(next);
        var changed = previous.Except(incoming)
            .Concat(incoming.Except(previous))
            .Where(s => EditorTheme.ForClassification(s.ClassificationType) is not null)
            .Select(s => s.TextSpan)
            .Concat(_invalidated)
            .OrderBy(s => s.Start);

        var ranges = new List<TextSpan>();

        foreach (var span in changed)
        {
            if(ranges.Count > 0 && span.Start <= ranges[^1].End)
            {
                ranges[^1] = TextSpan.FromBounds(ranges[^1].Start, Math.Max(ranges[^1].End, span.End));
            }
            else
            {
                ranges.Add(span);
            }
        }

        Spans = next;
        return ranges;
    }

    /// <summary>Applies semantic foreground colors to classifications intersecting a rendered line.</summary>
    protected override void ColorizeLine(DocumentLine line)
    {
        for (int i = FindFirstSpan(line.Offset); i < Spans.Count; i++)
        {
            var span = Spans[i];

            if(span.TextSpan.Start >= line.EndOffset)
            {
                break;
            }
            if(span.TextSpan.End <= line.Offset)
            {
                continue;
            }

            string? color = EditorTheme.ForClassification(span.ClassificationType);

            if(color is null)
            {
                continue;
            }

            ChangeLinePart(Math.Max(line.Offset, span.TextSpan.Start),
                Math.Min(line.EndOffset, span.TextSpan.End),
                element => element.TextRunProperties.SetForegroundBrush(EditorTheme.Brush(color)));
        }
    }
}

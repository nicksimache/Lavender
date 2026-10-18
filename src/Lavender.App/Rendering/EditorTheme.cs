using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.CodeAnalysis.Classification;

namespace Lavender.App.Rendering;

/// <summary>Lavender Night: language rules come from AvalonEdit and Roslyn, colors live here.</summary>
public static class EditorTheme
{
    public const string Text = "#D8DEE9";
    public const string Keyword = "#C4A7E7";
    public const string Type = "#7BDAC6";
    public const string Method = "#EBCB8B";
    public const string Variable = "#9CDCFE";
    public const string Property = "#B8C0FF";
    public const string String = "#E9B38F";
    public const string Number = "#B5CEA8";
    public const string Comment = "#8FAF87";
    public const string Operator = "#89B4FA";
    private static readonly HashSet<IHighlightingDefinition> Themed = new();
    private static readonly Dictionary<string, Brush> BrushesByColor = new();

    public static Brush Brush(string hex)
    {
        if (!BrushesByColor.TryGetValue(hex, out Brush? brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            BrushesByColor[hex] = brush;
        }
        return brush;
    }

    public static IHighlightingDefinition? ForFile(string path)
    {
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
        if (definition is null || !Themed.Add(definition))
            return definition;
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

/// <summary>Paints Roslyn classifications only while they match the current editor text.</summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    public IReadOnlyList<ClassifiedSpan> Spans { get; set; } = Array.Empty<ClassifiedSpan>();

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var span in Spans)
        {
            if (span.TextSpan.Start >= line.EndOffset)
                break;
            if (span.TextSpan.End <= line.Offset)
                continue;
            string? color = EditorTheme.ForClassification(span.ClassificationType);
            if (color is null)
                continue;
            ChangeLinePart(Math.Max(line.Offset, span.TextSpan.Start),
                Math.Min(line.EndOffset, span.TextSpan.End),
                element => element.TextRunProperties.SetForegroundBrush(EditorTheme.Brush(color)));
        }
    }
}

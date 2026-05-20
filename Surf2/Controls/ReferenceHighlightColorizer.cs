using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Surf2.Models;

namespace Surf2.Controls;

public sealed class ReferenceHighlightColorizer : DocumentColorizingTransformer
{
    private static readonly Brush FallbackReferenceBrush = new SolidColorBrush(Color.FromRgb(180, 83, 9));

    private readonly Dictionary<string, HighlightRenderStyle> _highlightStyles;

    static ReferenceHighlightColorizer()
    {
        FallbackReferenceBrush.Freeze();
    }

    public ReferenceHighlightColorizer(IReadOnlyDictionary<string, ReferenceHighlightStyleSetting> highlightStyles)
    {
        _highlightStyles = new Dictionary<string, HighlightRenderStyle>(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, ReferenceHighlightStyleSetting style) in highlightStyles)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            _highlightStyles[name] = CreateRenderStyle(style);
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_highlightStyles.Count == 0 || CurrentContext.Document == null)
        {
            return;
        }

        string text = CurrentContext.Document.GetText(line);
        int index = 0;

        while (index < text.Length)
        {
            if (!IsIdentifierCharacter(text[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < text.Length && IsIdentifierCharacter(text[index]))
            {
                index++;
            }

            string token = text[start..index];
            if (!_highlightStyles.TryGetValue(token, out HighlightRenderStyle? highlightStyle))
            {
                continue;
            }

            int absoluteStartOffset = line.Offset + start;
            int absoluteEndOffset = line.Offset + index;
            ChangeLinePart(absoluteStartOffset, absoluteEndOffset, element =>
            {
                Typeface existingTypeface = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetForegroundBrush(highlightStyle.Foreground);
                element.TextRunProperties.SetTypeface(new Typeface(
                    existingTypeface.FontFamily,
                    highlightStyle.IsItalic ? FontStyles.Italic : FontStyles.Normal,
                    highlightStyle.IsBold ? FontWeights.Bold : FontWeights.Normal,
                    existingTypeface.Stretch));

                if (highlightStyle.IsUnderline)
                {
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                }
            });
        }
    }

    private static bool IsIdentifierCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value is '_' or '$' or '#' or '@';
    }

    private static HighlightRenderStyle CreateRenderStyle(ReferenceHighlightStyleSetting style)
    {
        Brush foreground = FallbackReferenceBrush;

        try
        {
            if (ColorConverter.ConvertFromString(style.Foreground) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                foreground = brush;
            }
        }
        catch (FormatException)
        {
        }

        return new HighlightRenderStyle(
            foreground,
            style.IsBold,
            style.IsItalic,
            style.IsUnderline);
    }

    private sealed record HighlightRenderStyle(
        Brush Foreground,
        bool IsBold,
        bool IsItalic,
        bool IsUnderline);
}

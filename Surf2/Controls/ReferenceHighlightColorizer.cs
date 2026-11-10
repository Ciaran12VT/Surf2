using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Surf2.Models;

namespace Surf2.Controls;

public sealed class ReferenceHighlightColorizer : DocumentColorizingTransformer
{
    private static readonly Brush FallbackReferenceBrush = new SolidColorBrush(Color.FromRgb(180, 83, 9));
    private static readonly Brush PendingReferenceBrush = new SolidColorBrush(Color.FromRgb(160, 160, 160));
    private static readonly HashSet<string> NonReferenceKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "foreach", "while", "switch", "catch", "using", "lock", "return",
        "typeof", "sizeof", "nameof", "checked", "unchecked", "default", "new", "throw", "function",
        "sub", "class", "struct", "interface", "enum", "delegate", "with", "select", "case"
    };

    private readonly Dictionary<string, HighlightRenderStyle> _highlightStyles;
    private readonly bool _referencesPending;
    private readonly IReadOnlySet<string>? _availableNames;

    static ReferenceHighlightColorizer()
    {
        FallbackReferenceBrush.Freeze();
        PendingReferenceBrush.Freeze();
    }

    public ReferenceHighlightColorizer(IReadOnlyDictionary<string, ReferenceHighlightStyleSetting> highlightStyles,
        bool referencesPending = false, IReadOnlySet<string>? availableNames = null)
    {
        _referencesPending = referencesPending;
        _availableNames = availableNames;
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
        if ((_highlightStyles.Count == 0 && !_referencesPending) || CurrentContext.Document == null)
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
                if (!_referencesPending || _availableNames?.Contains(token) == true || !IsPendingCandidate(text, start, index)) continue;
                highlightStyle = new(PendingReferenceBrush, false, false, false);
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

    // Candidates are identified from the visible line only. Rendering never starts discovery or I/O.
    public bool IsPendingAt(TextDocument document, int offset)
    {
        if (!_referencesPending || offset < 0 || offset >= document.TextLength) return false;
        var line = document.GetLineByOffset(offset);
        string text = document.GetText(line);
        int start = offset - line.Offset;
        if (start >= text.Length || !IsIdentifierCharacter(text[start])) return false;
        int end = start + 1;
        while (start > 0 && IsIdentifierCharacter(text[start - 1])) start--;
        while (end < text.Length && IsIdentifierCharacter(text[end])) end++;
        string token = text[start..end];
        return !_highlightStyles.ContainsKey(token) && _availableNames?.Contains(token) != true && IsPendingCandidate(text, start, end);
    }

    private static bool IsPendingCandidate(string text, int start, int end)
    {
        string token = text[start..end];
        if (token.Length < 2 || !char.IsLetter(token[0]) || NonReferenceKeywords.Contains(token)) return false;
        char quote = '\0';
        for (int i = 0; i < start; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*') return false;
        }
        if (quote != '\0') return false;
        int next = end;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        int previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
        return next < text.Length && text[next] == '(' || previous >= 0 && text[previous] == '.' || char.IsUpper(token[0]);
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

using System.Text.Json.Serialization;

namespace Surf2.Models;

public sealed class ReferenceHighlightSettings
{
    public List<ReferenceHighlightStyleSetting> Styles { get; set; } = [];

    public bool EnsureDefaultStyleEntries()
    {
        bool changed = false;
        Styles ??= [];

        foreach (ReferenceHighlightStyleSetting defaultStyle in CreateDefaultStyles())
        {
            if (Styles.Any(style =>
                    string.Equals(style.Language, defaultStyle.Language, StringComparison.OrdinalIgnoreCase) &&
                    style.Kind == defaultStyle.Kind))
            {
                continue;
            }

            Styles.Add(defaultStyle);
            changed = true;
        }

        return changed;
    }

    public ReferenceHighlightStyleSetting GetStyle(string language, ReferenceEntityKind kind)
    {
        EnsureDefaultStyleEntries();

        return Styles.FirstOrDefault(style =>
                   string.Equals(style.Language, language, StringComparison.OrdinalIgnoreCase) &&
                   style.Kind == kind)
               ?? CreateDefaultStyle(language, kind, "#B45309", isBold: true);
    }

    public static IReadOnlyList<ReferenceHighlightStyleSetting> CreateDefaultStyles()
    {
        return
        [
            CreateDefaultStyle("C#", ReferenceEntityKind.Class, "#1D4ED8", isBold: true),
            CreateDefaultStyle("C#", ReferenceEntityKind.Interface, "#2563EB", isItalic: true),
            CreateDefaultStyle("C#", ReferenceEntityKind.Struct, "#0369A1", isBold: true),
            CreateDefaultStyle("C#", ReferenceEntityKind.Enum, "#0F766E", isBold: true),
            CreateDefaultStyle("C#", ReferenceEntityKind.Delegate, "#7C3AED", isItalic: true),
            CreateDefaultStyle("C#", ReferenceEntityKind.Method, "#B45309", isBold: true),
            CreateDefaultStyle("VB", ReferenceEntityKind.Class, "#1D4ED8", isBold: true),
            CreateDefaultStyle("VB", ReferenceEntityKind.Interface, "#2563EB", isItalic: true),
            CreateDefaultStyle("VB", ReferenceEntityKind.Struct, "#0369A1", isBold: true),
            CreateDefaultStyle("VB", ReferenceEntityKind.Enum, "#0F766E", isBold: true),
            CreateDefaultStyle("VB", ReferenceEntityKind.Method, "#B45309", isBold: true),
            CreateDefaultStyle("JavaScript", ReferenceEntityKind.Class, "#047857", isBold: true),
            CreateDefaultStyle("JavaScript", ReferenceEntityKind.Function, "#7C3AED", isBold: true),
            CreateDefaultStyle("SQL Server", ReferenceEntityKind.StoredProcedure, "#C2410C", isBold: true),
            CreateDefaultStyle("SQL Server", ReferenceEntityKind.Function, "#A21CAF", isBold: true),
            CreateDefaultStyle("SQL Server", ReferenceEntityKind.View, "#0369A1", isBold: true),
            CreateDefaultStyle("SQL Server", ReferenceEntityKind.Trigger, "#7C2D12", isBold: true, isItalic: true),
            CreateDefaultStyle("SQL Server", ReferenceEntityKind.Table, "#BE123C", isBold: true),
            CreateDefaultStyle("SQL Server", ReferenceEntityKind.Field, "#0F766E", isItalic: true)
        ];
    }

    private static ReferenceHighlightStyleSetting CreateDefaultStyle(
        string language,
        ReferenceEntityKind kind,
        string foreground,
        bool isBold = false,
        bool isItalic = false,
        bool isUnderline = false)
    {
        return new ReferenceHighlightStyleSetting
        {
            Language = language,
            Kind = kind,
            Foreground = foreground,
            IsBold = isBold,
            IsItalic = isItalic,
            IsUnderline = isUnderline
        };
    }
}

public sealed class ReferenceHighlightStyleSetting
{
    public string Language { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReferenceEntityKind Kind { get; set; }

    public string Foreground { get; set; } = "#B45309";

    public bool IsBold { get; set; }

    public bool IsItalic { get; set; }

    public bool IsUnderline { get; set; }

    [JsonIgnore]
    public string DisplayLabel => $"{Language} > {Kind}";

    public ReferenceHighlightStyleSetting Clone()
    {
        return new ReferenceHighlightStyleSetting
        {
            Language = Language,
            Kind = Kind,
            Foreground = Foreground,
            IsBold = IsBold,
            IsItalic = IsItalic,
            IsUnderline = IsUnderline
        };
    }
}

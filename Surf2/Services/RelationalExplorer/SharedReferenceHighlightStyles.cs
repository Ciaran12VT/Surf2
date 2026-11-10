using System.Collections.Frozen;
using System.Collections.Immutable;
using Surf2.Models;

namespace Surf2.Services.RelationalExplorer;

public sealed record ReferenceStyleValue(string Language, ReferenceEntityKind Kind, string Foreground,
    bool IsBold, bool IsItalic, bool IsUnderline)
{
    public static ReferenceStyleValue Capture(ReferenceHighlightStyleSetting style) => new(style.Language, style.Kind,
        style.Foreground, style.IsBold, style.IsItalic, style.IsUnderline);
    public static ImmutableArray<ReferenceStyleValue> CaptureWithRuntimeDefaults(ReferenceHighlightSettings settings)
    {
        var source = settings.Styles ?? [];
        if (source.Count > 1024) throw new ExplorerLimitException("Highlight styles exceed their metadata budget.");
        // Defaults belong to this rendering snapshot, never the persisted settings collection.
        var rendering = new ReferenceHighlightSettings { Styles = source.ToList() };
        rendering.EnsureDefaultStyleEntries();
        if (rendering.Styles.Count > 1024) throw new ExplorerLimitException("Highlight styles exceed their metadata budget.");
        return rendering.Styles.Select(Capture).ToImmutableArray();
    }
    public ReferenceHighlightStyleSetting ToSetting() => new() { Language = Language, Kind = Kind, Foreground = Foreground,
        IsBold = IsBold, IsItalic = IsItalic, IsUnderline = IsUnderline };
}

// Immutable, GUI-free name/style metadata. Build once on a worker, share by reference across editors.
public sealed class SharedReferenceHighlightStyles
{
    public SharedReferenceHighlightStyles(SharedReferencePaintLookup paint, ImmutableArray<ReferenceStyleValue> styles,
        IEnumerable<string> preferredLanguages, CancellationToken ct = default)
    {
        Context = paint.Context;
        if (styles.IsDefault || styles.Length > 1024) throw new ExplorerLimitException("Highlight styles exceed their metadata budget.");
        var languages = preferredLanguages.Distinct(StringComparer.OrdinalIgnoreCase).Take(17).ToArray();
        if (languages.Length > 16) throw new ExplorerLimitException("Highlight languages exceed their metadata budget.");
        var maps = new Dictionary<string, FrozenDictionary<string, ReferenceStyleValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in languages)
        {
            var names = new Dictionary<string, ReferenceStyleValue>(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<PaintStyleKey, ReferenceStyleValue>();
            foreach (string name in paint.Names.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (!paint.TryGetStyle(name, language, out var key) || key == null) continue;
                if (!values.TryGetValue(key, out var style))
                {
                    style = styles.FirstOrDefault(s => s.Kind == key.Kind && s.Language.Equals(key.Language, StringComparison.OrdinalIgnoreCase))
                        ?? new(key.Language, key.Kind, "#B45309", true, false, false);
                    values.Add(key, style);
                }
                names.Add(name, style);
            }
            maps.Add(language, names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
        }
        ByLanguage = maps.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    public Surf2.Storage.Relational.Index.IndexRequestContext Context { get; }
    public FrozenDictionary<string, FrozenDictionary<string, ReferenceStyleValue>> ByLanguage { get; }
}

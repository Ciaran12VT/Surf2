using System.Collections.Frozen;
using System.Collections.Immutable;
using Surf2.Models;

namespace Surf2.Storage.Relational.Index;

public static class ReferenceMetadata
{
    public static string Normalize(string token) => token.Trim().Trim('[', ']', '`', '"', '\'')
        .Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
    internal static bool IsAscii(string text) => text.All(char.IsAscii);
    internal static byte[] LookupHash(string normalized) => IndexSql.Hash(normalized.ToUpperInvariant());

    // Apply only to a completed candidate sequence, not a truncated page.
    public static ImmutableArray<SymbolSummary> Resolve(IEnumerable<SymbolSummary> candidates, string token, int? argumentCount = null)
    {
        string key = Normalize(token);
        if (key.Length == 0) return [];
        var all = candidates.ToArray();
        var matches = all.Where(s => HasName(s.Definition, key)).ToArray();
        if (matches.Length == 0 && key.Contains('.', StringComparison.Ordinal))
        {
            key = key.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            if (key.Length == 0) return [];
            matches = all.Where(s => HasName(s.Definition, key)).ToArray();
        }
        var distinct = matches.DistinctBy(s => $"{s.Definition.Kind}|{s.Definition.Locator}|{s.Definition.LineNumber}|{s.Definition.ColumnNumber}|{s.Definition.QualifiedName}").ToArray();
        if (distinct.Any(s => s.Definition.Kind != ReferenceEntityKind.File))
            distinct = distinct.Where(s => s.Definition.Kind != ReferenceEntityKind.File).ToArray();
        if (argumentCount.HasValue)
        {
            int count = argumentCount.Value;
            var overloads = distinct.Where(s => s.Definition.Kind is ReferenceEntityKind.Method or ReferenceEntityKind.Function or ReferenceEntityKind.StoredProcedure)
                .Where(s => s.Definition.MinimumArgumentCount.HasValue && s.Definition.MaximumArgumentCount.HasValue &&
                    count >= s.Definition.MinimumArgumentCount.Value && count <= s.Definition.MaximumArgumentCount.Value).ToArray();
            if (overloads.Length > 0)
            {
                long best = overloads.Min(s => ArgumentScore(s.Definition, count));
                distinct = overloads.Where(s => ArgumentScore(s.Definition, count) == best).ToArray();
            }
        }
        return distinct.OrderBy(s => NavigationRank(s.Definition.Kind)).ThenBy(s => s.Definition.QualifiedName)
            .ThenBy(s => s.Definition.Locator).ToImmutableArray();
    }
    internal static bool HasName(SymbolInput s, string name) =>
        string.Equals(Normalize(s.Name), name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Normalize(s.QualifiedName), name, StringComparison.OrdinalIgnoreCase);
    private static long ArgumentScore(SymbolInput s, int count)
    {
        int max = s.MaximumArgumentCount ?? count, min = s.MinimumArgumentCount ?? count;
        if ((s.ParameterCount ?? max) == count) return 0;
        long slack = max == int.MaxValue ? 100 : Math.Max(0L, (long)max - count);
        return slack * 10 + Math.Abs((long)count - min);
    }
    private static int NavigationRank(ReferenceEntityKind kind) => kind switch
    {
        ReferenceEntityKind.Method => 0, ReferenceEntityKind.Function => 1, ReferenceEntityKind.StoredProcedure => 2,
        ReferenceEntityKind.View => 3, ReferenceEntityKind.Trigger => 4, ReferenceEntityKind.Class => 5,
        ReferenceEntityKind.Interface => 6, ReferenceEntityKind.Struct => 7, ReferenceEntityKind.Enum => 8,
        ReferenceEntityKind.Delegate => 9, ReferenceEntityKind.Table => 10, ReferenceEntityKind.Field => 11,
        ReferenceEntityKind.File => 12, _ => 99
    };
}

public sealed record HighlightName(string Name, ImmutableArray<ReferenceEntityKind> Kinds);

// This object is shared by editors. Style IDs are language/kind metadata, never WPF brushes.
// Construction runs outside painting; there are no lazy values or I/O callbacks.
public sealed class SharedHighlightMetadata
{
    public SharedHighlightMetadata(IndexRequestContext context, IEnumerable<HighlightName> names, string stylePolicyVersion)
    {
        Context = context;
        StylePolicyVersion = stylePolicyVersion;
        Names = names.Where(n => n.Name.Length > 1).GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key,
                g => g.SelectMany(n => n.Kinds).Where(k => k != ReferenceEntityKind.File).Distinct().ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
    }
    public IndexRequestContext Context { get; }
    public string StylePolicyVersion { get; }
    public FrozenDictionary<string, ImmutableArray<ReferenceEntityKind>> Names { get; }
}

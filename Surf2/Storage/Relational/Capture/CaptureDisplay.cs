using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

public static class CaptureDisplay
{
    public const int FormatVersion = 1;

    // Match DatabaseDocumentService and ResourceComparisonService, including
    // first-property ordinal-ignore-case lookup and the raw numeric spelling.
    public static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => value.ToString()
    };

    public static string Cell(JsonElement row, string sourceName)
    {
        if (row.ValueKind == JsonValueKind.Object)
            foreach (var property in row.EnumerateObject())
                if (string.Equals(property.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                    return Value(property.Value);
        return string.Empty;
    }

    public static string EscapeDelimited(string value, char delimiter)
    {
        if (value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal sealed class FrozenCaptureQuery
{
    internal static CaptureQuery Snapshot(CaptureQuery? query)
    {
        if (query?.Filters?.Count > CaptureLimits.MaximumColumns ||
            query?.AnyMatchColumnOrdinals?.Count > CaptureLimits.MaximumColumns ||
            query?.Sorts?.Count > CaptureLimits.MaximumColumns)
            throw new CaptureLimitException("Too many filter/sort columns.");
        return new(query?.Filters?.ToArray(), query?.AnyMatchColumnOrdinals?.ToArray(), query?.Sorts?.ToArray(),
            query?.SortCultureName ?? CultureInfo.CurrentCulture.Name);
    }

    internal CaptureFilter[] Filters { get; }
    internal CaptureFilter[] EffectiveFilters { get; }
    internal CaptureSort[] Sorts { get; }
    internal bool UseAny { get; }
    internal CompareInfo CompareInfo { get; }
    internal string Fingerprint { get; }

    internal FrozenCaptureQuery(CaptureQuery? query, int columnCount)
    {
        query = Snapshot(query);
        Filters = (query.Filters ?? []).ToArray();
        var anyColumns = (query.AnyMatchColumnOrdinals ?? []).Distinct().Order().ToArray();
        Sorts = (query.Sorts ?? []).ToArray();
        var seenFilters = new HashSet<int>();
        var seenSorts = new HashSet<int>();
        foreach (var filter in Filters)
        {
            if (filter == null || filter.Text == null) throw new ArgumentException("Invalid filter.", nameof(query));
            ValidateColumn(filter.ColumnOrdinal);
            if (!seenFilters.Add(filter.ColumnOrdinal)) throw new ArgumentException("Duplicate filter column.", nameof(query));
            if (filter.Text.Length > 4096) throw new CaptureLimitException("A filter exceeds 4096 characters.");
        }
        foreach (int column in anyColumns) ValidateColumn(column);
        foreach (var sort in Sorts)
        {
            if (sort == null) throw new ArgumentException("Invalid sort.", nameof(query));
            ValidateColumn(sort.ColumnOrdinal);
            if (!seenSorts.Add(sort.ColumnOrdinal)) throw new ArgumentException("Duplicate sort column.", nameof(query));
        }
        Filters = Filters.OrderBy(x => x.ColumnOrdinal).ToArray();
        var active = Filters.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToArray();
        var any = active.Where(x => Array.BinarySearch(anyColumns, x.ColumnOrdinal) >= 0).ToArray();
        UseAny = any.Length > 0;
        EffectiveFilters = UseAny ? any : active;
        // String sorting in the WPF collection view is culture-sensitive. Pin
        // that culture for an operation rather than adopting SQL's collation.
        CompareInfo = CultureInfo.GetCultureInfo(query.SortCultureName ?? CultureInfo.CurrentCulture.Name).CompareInfo;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write(CaptureDisplay.FormatVersion);
        writer.Write(CompareInfo.Name);
        writer.Write(CompareInfo.Version.FullVersion);
        writer.Write(CompareInfo.Version.SortId.ToByteArray());
        writer.Write(UseAny);
        writer.Write(EffectiveFilters.Length);
        foreach (var filter in EffectiveFilters)
        {
            writer.Write(filter.ColumnOrdinal);
            writer.Write(filter.Text.Length);
            // Hash exact UTF-16 code units, even for an unpaired surrogate in
            // user-entered literal text; replacement encoding could alias cursors.
            foreach (char codeUnit in filter.Text) writer.Write((ushort)codeUnit);
        }
        writer.Write(Sorts.Length);
        foreach (var sort in Sorts) { writer.Write(sort.ColumnOrdinal); writer.Write(sort.Descending); }
        writer.Flush();
        Fingerprint = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));

        void ValidateColumn(int ordinal)
        {
            if (ordinal < 0 || ordinal >= columnCount) throw new ArgumentOutOfRangeException(nameof(query), "Unknown column ordinal.");
        }
    }

    internal bool Matches(JsonElement row, IReadOnlyList<CaptureColumnDefinition> columns)
    {
        if (EffectiveFilters.Length == 0) return true;
        foreach (var filter in EffectiveFilters)
        {
            bool match = CaptureDisplay.Cell(row, columns[filter.ColumnOrdinal].SourceName)
                .IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0;
            if (UseAny && match) return true;
            if (!UseAny && !match) return false;
        }
        return !UseAny;
    }

    internal string[] SortKeys(CaptureRow row, IReadOnlyList<CaptureColumnDefinition> columns) =>
        Sorts.Select(sort => CaptureDisplay.Cell(row.Value, columns[sort.ColumnOrdinal].SourceName)).ToArray();

    internal int Compare(string[] left, long leftOrdinal, string[] right, long rightOrdinal)
    {
        for (int i = 0; i < Sorts.Length; i++)
        {
            int comparison = CompareInfo.Compare(left[i], right[i], CompareOptions.None);
            if (comparison != 0) return Sorts[i].Descending ? -Math.Sign(comparison) : Math.Sign(comparison);
        }
        return leftOrdinal.CompareTo(rightOrdinal);
    }
}

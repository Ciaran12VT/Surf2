using System.IO;

namespace Surf2.Storage.Relational.Capture;

internal static class CaptureImportColumns
{
    internal static CaptureColumnDefinition[] Select(IReadOnlyList<string> headerColumns,
        IReadOnlyList<CaptureColumnDefinition> sourceColumns)
    {
        ArgumentNullException.ThrowIfNull(headerColumns);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        if (headerColumns.Count > CaptureLimits.MaximumColumns || sourceColumns.Count > CaptureLimits.MaximumColumns)
            throw new CaptureLimitException("The dataset header or source metadata exceeds the 128-column capture limit.");
        var metadata = sourceColumns.ToArray();
        var exact = new Dictionary<string, CaptureColumnDefinition>(StringComparer.Ordinal);
        var caseInsensitive = new Dictionary<string, List<CaptureColumnDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in metadata)
        {
            if (column == null || column.SourceName == null) throw new InvalidDataException("Null source column metadata.");
            CaptureLayout.ValidateText(column.SourceName);
            if (column.SourceDataType != null) CaptureLayout.ValidateText(column.SourceDataType);
            if (!exact.TryAdd(column.SourceName, column))
                throw new InvalidDataException("Duplicate exact source column metadata is ambiguous.");
            if (!caseInsensitive.TryGetValue(column.SourceName, out var matching))
                caseInsensitive[column.SourceName] = matching = [];
            matching.Add(column);
        }
        var selected = headerColumns.Count > 0 ? headerColumns.Select(Merge).ToArray()
            : metadata.OrderBy(column => column.SourceOrdinal ?? int.MaxValue).ToArray();
        _ = new CaptureLayout(selected);
        return selected;

        CaptureColumnDefinition Merge(string sourceName)
        {
            if (sourceName == null) throw new InvalidDataException("A dataset column name is null.");
            CaptureLayout.ValidateText(sourceName);
            if (exact.TryGetValue(sourceName, out var column)) return column with { SourceName = sourceName };
            if (!caseInsensitive.TryGetValue(sourceName, out var matching)) return new(sourceName);
            if (matching.Count != 1)
                throw new InvalidDataException("A dataset column cannot be matched to source metadata unambiguously by case.");
            return matching[0] with { SourceName = sourceName };
        }
    }
}

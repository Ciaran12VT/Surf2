using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalComparison;

public sealed record ComparisonTableInput(IReadOnlyList<string> Headers, IReadOnlyList<string> PreferredKeys,
    long MaximumRows, IAsyncEnumerable<CaptureRow> Rows, bool IsMemoryResident = false);

#if !RELATIONAL_COMPARISON_PROTOTYPE
public sealed partial class RelationalComparisonService
{
    public async Task<ComparisonTableInput> DescribeTableAsync(RelationalComparisonTarget target, CancellationToken ct = default)
    {
        RequireEpoch(target);
        if (!target.Resource.IsTableData || target.RevisionKey == null || target.SnapshotKey == null)
            throw new InvalidOperationException("An identity-bound captured table is required.");
        var data = await _runtime.CapturedData.GetForRevisionAsync(target.RevisionKey.Value, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The selected table has no complete captured dataset.");
        TableMetadataDetails? details = null;
        if (target.VersionKey.HasValue)
        {
            await using var historical = await _runtime.Snapshots.OpenHistoricalSnapshotAsync(target.SnapshotKey.Value, target.VersionKey.Value, ct).ConfigureAwait(false);
            var entries = await historical.FindEntriesAsync(HistoricalCollection.Tables,
                target.Resource.TableSchemaName, target.Resource.TableName, ct).ConfigureAwait(false);
            if (entries.Count != 0) details = await historical.ReadTableMetadataAsync(entries[0], ct).ConfigureAwait(false);
        }
        else
        {
            var metadata = await FindByNameAsync(target.SnapshotKey.Value, DatabaseVersionedResourceKind.TableMetadata,
                target.Resource.TableSchemaName, target.Resource.TableName, null, ct).ConfigureAwait(false);
            if (metadata != null) details = await _runtime.Snapshots.ReadTableMetadataAsync(metadata.RevisionKey, ct).ConfigureAwait(false);
        }
        var headers = details?.Columns.Count > 0
            ? details.Columns.OrderBy(x => x.Column.Ordinal).Select(x => x.Column.ColumnName).ToArray()
            : data.Columns.Select(x => x.SourceName).ToArray();
        var keys = details?.PrimaryKeys.OrderBy(x => x.Column.KeyOrdinal).Select(x => x.Column.ColumnName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return new(headers, keys, data.Summary.ActualRowCount,
            _runtime.CapturedData.StreamRowsAsync(data.Summary.DataSetKey, cancellationToken: ct),
            _runtime.Session.SnapshotMemory.ContainsSnapshot(target.SnapshotKey.Value));
    }

    public Task<ComparisonResultStore> CompareTablesAsync(ComparisonTableInput left, ComparisonTableInput right,
        IReadOnlyList<string> keyColumns, CancellationToken ct = default) =>
        Task.Run(() => left.IsMemoryResident && right.IsMemoryResident
            ? ComparisonTableEngine.BuildInMemoryAsync(left, right, keyColumns, Limits, ct)
            : ComparisonTableEngine.BuildAsync(left, right, keyColumns, Limits, ct), ct);
}
#endif

public static class ComparisonTableEngine
{
    private sealed record TableRow(string Json);
    private static readonly SpoolCodec<TableRow> RowCodec = new((w, x) => SpoolBinary.WriteString(w, x.Json), r => new(SpoolBinary.ReadString(r)));

    /// <summary>Compares preloaded rows and keeps the complete result in memory.</summary>
    public static async Task<ComparisonResultStore> BuildInMemoryAsync(ComparisonTableInput left, ComparisonTableInput right,
        IReadOnlyList<string> keyColumns, ComparisonLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new(); limits.Validate(); ct.ThrowIfCancellationRequested();
        var schema = Schema(left, right, keyColumns, limits);
        var a = await ReadAsync(left).ConfigureAwait(false);
        var b = await ReadAsync(right).ConfigureAwait(false);
        var rows = new List<ComparisonResultRow>();
        foreach (string key in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            bool hasA = a.TryGetValue(key, out var rowA), hasB = b.TryGetValue(key, out var rowB);
            var row = CompareRow(key, hasA ? rowA : null, hasB ? rowB : null, schema);
            _ = SpoolBinary.Encode(row, ComparisonCodecs.Result, limits.MaximumRecordBytes);
            rows.Add(row);
        }
        return ComparisonResultStore.FromMemory(rows.ToArray(), limits, schema.Layout);

        async Task<Dictionary<string, JsonElement>> ReadAsync(ComparisonTableInput input)
        {
            if (input.MaximumRows < 0) throw new ComparisonLimitException("Table row count cannot be negative.");
            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            long suffix = 2, count = 0;
            await foreach (var row in input.Rows.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > input.MaximumRows) throw new ComparisonLimitException("Selected table exceeded its pinned row count.");
                if (row.Value.ValueKind != JsonValueKind.Object) continue;
                string key = string.Join(" | ", schema.Keys.Select(k => Value(row.Value, k)));
                if (string.IsNullOrWhiteSpace(key)) key = "(blank key)";
                string unique = key;
                while (result.ContainsKey(unique)) { ct.ThrowIfCancellationRequested(); unique = key + " #" + suffix++; }
                // A row stream may dispose its JsonDocument as soon as enumeration advances.
                result.Add(unique, row.Value.Clone());
            }
            return result;
        }
    }

    private sealed record TableComparisonSchema(string[] Keys, HashSet<string> LeftHeaders, HashSet<string> RightHeaders,
        ComparisonTableColumn[] Columns, int[] PreviewOrder, ComparisonTableLayout Layout);

    private static TableComparisonSchema Schema(ComparisonTableInput left, ComparisonTableInput right,
        IReadOnlyList<string> keyColumns, ComparisonLimits limits)
    {
        string[] keys = keyColumns.ToArray();
        if (keys.Length == 0 || keys.Length > CaptureLimits.MaximumColumns ||
            keys.Any(k => !left.Headers.Contains(k, StringComparer.OrdinalIgnoreCase) || !right.Headers.Contains(k, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Select common columns to identify table rows.", nameof(keyColumns));
        var leftHeaders = left.Headers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightHeaders = right.Headers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = left.Headers.Concat(right.Headers).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new ComparisonTableColumn(name, leftHeaders.Contains(name), rightHeaders.Contains(name))).ToArray();
        if (columns.Length > CaptureLimits.MaximumColumns * 2 || columns.Sum(x => 2L * x.Name.Length + 64) > limits.SortBufferBytes)
            throw new ComparisonLimitException("Comparison column metadata exceeds its memory budget.");
        return new(keys, leftHeaders, rightHeaders, columns,
            Enumerable.Range(0, columns.Length).OrderBy(i => columns[i].Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            new(Array.AsReadOnly(columns), Array.AsReadOnly(keys)));
    }

    private static ComparisonResultRow CompareRow(string key, JsonElement? left, JsonElement? right, TableComparisonSchema schema)
    {
        var leftValues = ReadValues(left, schema.LeftHeaders);
        var rightValues = ReadValues(right, schema.RightHeaders);
        var cells = new ComparisonTableCell[schema.Columns.Length];
        for (int i = 0; i < schema.Columns.Length; i++)
        {
            var column = schema.Columns[i];
            string leftValue = leftValues.GetValueOrDefault(column.Name, string.Empty);
            string rightValue = rightValues.GetValueOrDefault(column.Name, string.Empty);
            var cellStatus = right == null ? ComparisonCellStatus.Removed : left == null ? ComparisonCellStatus.Added
                : !column.ExistsOnRight ? ComparisonCellStatus.Removed : !column.ExistsOnLeft ? ComparisonCellStatus.Added
                : leftValue != rightValue ? ComparisonCellStatus.Altered : ComparisonCellStatus.Identical;
            cells[i] = new(leftValue, rightValue, cellStatus);
        }
        string[] changed = left != null && right != null
            ? schema.PreviewOrder.Where(i => cells[i].Status != ComparisonCellStatus.Identical).Select(i => schema.Columns[i].Name).ToArray()
            : [];
        string status = right == null ? "Removed" : left == null ? "Added" : changed.Length == 0 ? "Identical" : "Altered";
        return new(key, status, string.Join(", ", changed),
            left == null ? "" : Preview(cells, schema.Columns, schema.PreviewOrder, left: true),
            right == null ? "" : Preview(cells, schema.Columns, schema.PreviewOrder, left: false), TableCells: cells);
    }

    public static async Task<ComparisonResultStore> BuildAsync(ComparisonTableInput left, ComparisonTableInput right,
        IReadOnlyList<string> keyColumns, ComparisonLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new(); limits.Validate();
        var schema = Schema(left, right, keyColumns, limits);
        var scratch = new ComparisonScratch(limits);
        try
        {
            var sorter = new ComparisonExternalSort<TableRow>(scratch, RowCodec);
            string leftPath = await sorter.BuildAsync(Prepare(left), ct).ConfigureAwait(false);
            string rightPath = await sorter.BuildAsync(Prepare(right), ct).ConfigureAwait(false);
            string resultPath = scratch.NewFile(); long count = 0;
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var output = File.Open(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var a = sorter.Read(leftPath, ct).GetEnumerator())
            using (var b = sorter.Read(rightPath, ct).GetEnumerator())
            {
                bool hasA = a.MoveNext(), hasB = b.MoveNext();
                while (hasA || hasB)
                {
                    ct.ThrowIfCancellationRequested();
                    int order = !hasA ? 1 : !hasB ? -1 : StringComparer.OrdinalIgnoreCase.Compare(a.Current.Key, b.Current.Key);
                    using var jsonA = hasA && order <= 0 ? JsonDocument.Parse(a.Current.Value.Json) : null;
                    using var jsonB = hasB && order >= 0 ? JsonDocument.Parse(b.Current.Value.Json) : null;
                    var row = CompareRow(order <= 0 ? a.Current.Key : b.Current.Key, jsonA?.RootElement, jsonB?.RootElement, schema);
                    SpoolBinary.Append(output, resultPath, row, ComparisonCodecs.Result, scratch);
                    counts[row.Status] = counts.GetValueOrDefault(row.Status) + 1; count++;
                    if (order <= 0) hasA = a.MoveNext();
                    if (order >= 0) hasB = b.MoveNext();
                }
            }
            scratch.Delete(leftPath); scratch.Delete(rightPath);
            return ComparisonResultStore.Complete(scratch, resultPath, count, counts, schema.Layout);
        }
        catch (Exception failure)
        {
            try { scratch.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Table comparison failed and scratch cleanup also failed.", failure, cleanup); }
            throw;
        }

        async IAsyncEnumerable<SortEntry<TableRow>> Prepare(ComparisonTableInput input,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            using var index = new ComparisonDiskKeys(scratch, input.MaximumRows);
            long ordinal = 0, rawCount = 0;
            await foreach (var row in input.Rows.WithCancellation(ct).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
                if (++rawCount > input.MaximumRows) throw new ComparisonLimitException("Selected table exceeded its pinned row count.");
                if (row.Value.ValueKind != JsonValueKind.Object) continue;
                string key = index.Add(string.Join(" | ", schema.Keys.Select(k => Value(row.Value, k))), ct);
                yield return new(key, ordinal++, new(row.Value.GetRawText()));
            }
        }
    }
    internal static string Value(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object) return "";
        foreach (var p in row.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return CaptureDisplay.Value(p.Value);
        return "";
    }
    private static Dictionary<string, string> ReadValues(JsonElement? row, HashSet<string> headers)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row == null) return values;
        foreach (var property in row.Value.EnumerateObject())
        {
            if (headers.Contains(property.Name) && !values.ContainsKey(property.Name))
                values.Add(property.Name, CaptureDisplay.Value(property.Value));
        }
        return values;
    }

    private static string Preview(ComparisonTableCell[] cells, ComparisonTableColumn[] columns, int[] order, bool left)
    {
        var result = new StringBuilder(803);
        for (int i = 0; i < order.Length; i++)
        {
            if (i != 0) Add("; ");
            int column = order[i];
            Add(columns[column].Name); Add("="); Add(left ? cells[column].Left : cells[column].Right);
            if (result.Length > 800) return result.ToString(0, 800) + "...";
        }
        return result.ToString();
        void Add(string x)
        {
            if (result.Length <= 800) result.Append(x.AsSpan(0, Math.Min(x.Length, 801 - result.Length)));
        }
    }
}

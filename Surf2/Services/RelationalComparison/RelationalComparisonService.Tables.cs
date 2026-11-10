using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalComparison;

public sealed record ComparisonTableInput(IReadOnlyList<string> Headers, IReadOnlyList<string> PreferredKeys,
    long MaximumRows, IAsyncEnumerable<CaptureRow> Rows);

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
        var metadata = await FindByNameAsync(target.SnapshotKey.Value, DatabaseVersionedResourceKind.TableMetadata,
            target.Resource.TableSchemaName, target.Resource.TableName, target.VersionKey, ct).ConfigureAwait(false);
        TableMetadataDetails? details = null;
        if (metadata != null)
        {
            if (target.VersionKey.HasValue)
            {
                await using var historical = await _runtime.Snapshots.OpenHistoricalSnapshotAsync(target.SnapshotKey.Value, target.VersionKey.Value, ct).ConfigureAwait(false);
                var entry = await FindHistoricalTableAsync(historical, FromResource(metadata, target.Epoch, target.VersionKey), ct).ConfigureAwait(false);
                details = await historical.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false);
            }
            else details = await _runtime.Snapshots.ReadTableMetadataAsync(metadata.RevisionKey, ct).ConfigureAwait(false);
        }
        var headers = details?.Columns.Count > 0
            ? details.Columns.OrderBy(x => x.Column.Ordinal).Select(x => x.Column.ColumnName).ToArray()
            : data.Columns.Select(x => x.SourceName).ToArray();
        var keys = details?.PrimaryKeys.OrderBy(x => x.Column.KeyOrdinal).Select(x => x.Column.ColumnName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return new(headers, keys, data.Summary.ActualRowCount,
            _runtime.CapturedData.StreamRowsAsync(data.Summary.DataSetKey, cancellationToken: ct));
    }

    public Task<ComparisonResultStore> CompareTablesAsync(ComparisonTableInput left, ComparisonTableInput right,
        IReadOnlyList<string> keyColumns, CancellationToken ct = default) =>
        Task.Run(() => ComparisonTableEngine.BuildAsync(left, right, keyColumns, Limits, ct), ct);
}
#endif

public static class ComparisonTableEngine
{
    private sealed record TableRow(string Json);
    private static readonly SpoolCodec<TableRow> RowCodec = new((w, x) => SpoolBinary.WriteString(w, x.Json), r => new(SpoolBinary.ReadString(r)));

    public static async Task<ComparisonResultStore> BuildAsync(ComparisonTableInput left, ComparisonTableInput right,
        IReadOnlyList<string> keyColumns, ComparisonLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new(); limits.Validate();
        string[] keys = keyColumns.ToArray();
        if (keys.Length == 0 || keys.Length > CaptureLimits.MaximumColumns ||
            keys.Any(k => !left.Headers.Contains(k, StringComparer.OrdinalIgnoreCase) || !right.Headers.Contains(k, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Select common columns to identify table rows.", nameof(keyColumns));
        string[] headers = left.Headers.Concat(right.Headers).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (headers.Length > CaptureLimits.MaximumColumns * 2 || headers.Sum(x => 2L * x.Length + 64) > limits.SortBufferBytes)
            throw new ComparisonLimitException("Comparison column metadata exceeds its memory budget.");
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
                    string[] changed = order == 0 ? headers.Where(h => Value(jsonA!.RootElement, h) != Value(jsonB!.RootElement, h)).ToArray() : [];
                    string status = order < 0 ? "Removed" : order > 0 ? "Added" : changed.Length == 0 ? "Identical" : "Altered";
                    var row = new ComparisonResultRow(order <= 0 ? a.Current.Key : b.Current.Key, status, string.Join(", ", changed),
                        jsonA == null ? "" : Preview(jsonA.RootElement, headers), jsonB == null ? "" : Preview(jsonB.RootElement, headers));
                    SpoolBinary.Append(output, resultPath, row, ComparisonCodecs.Result, scratch);
                    counts[status] = counts.GetValueOrDefault(status) + 1; count++;
                    if (order <= 0) hasA = a.MoveNext();
                    if (order >= 0) hasB = b.MoveNext();
                }
            }
            scratch.Delete(leftPath); scratch.Delete(rightPath);
            return ComparisonResultStore.Complete(scratch, resultPath, count, counts);
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
                string key = index.Add(string.Join(" | ", keys.Select(k => Value(row.Value, k))), ct);
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
    private static string Preview(JsonElement row, IReadOnlyList<string> headers)
    {
        var result = new StringBuilder(803);
        for (int i = 0; i < headers.Count; i++)
        {
            if (i != 0) Add("; ");
            Add(headers[i]); Add("="); Add(Value(row, headers[i]));
            if (result.Length > 800) return result.ToString(0, 800) + "...";
        }
        return result.ToString();
        void Add(string x)
        {
            if (result.Length <= 800) result.Append(x.AsSpan(0, Math.Min(x.Length, 801 - result.Length)));
        }
    }
}

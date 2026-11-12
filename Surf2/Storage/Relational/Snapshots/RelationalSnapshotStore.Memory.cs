using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class RelationalSnapshotStore
{
    private SnapshotPage<T>? MemoryPage<T>(long ownerKey, long? versionKey, string query, int? kind,
        int pageSize, SnapshotCursor? cursor, Func<T, (long Order, long Key)> position, long? historyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var memory = session.SnapshotMemory.Find(ownerKey);
        if (memory == null) return null;
        if (cursor != null && !memory.Header.RowVersion.AsSpan().SequenceEqual(cursor.HeadToken)) throw new SnapshotConcurrencyException();
        if (versionKey.HasValue && !memory.Changes.ContainsKey(versionKey.Value)) throw new KeyNotFoundException("Snapshot version not found.");
        if (historyKey.HasValue && !memory.HistoryVersions.ContainsKey(historyKey.Value)) throw new KeyNotFoundException("Snapshot history not found.");
        object? rows = query switch
        {
            "resources" => memory.Resources.Where(r => !kind.HasValue || (int?)r.Kind == kind),
            "objects" => memory.CurrentObjects.Where(r => !kind.HasValue || (int?)r.Resource.Kind == kind),
            "tables" => memory.CurrentTables,
            "histories" => memory.Histories,
            "versions" => memory.Histories.FirstOrDefault() is { } h ? memory.HistoryVersions[h.HistoryKey] : [],
            "changes" => memory.Changes.GetValueOrDefault(versionKey!.Value) ?? [],
            "columns" => memory.CurrentColumns.Select((v, i) => (Value: v, Entry: memory.CurrentColumnEntries[i])),
            "keys" => memory.CurrentKeys.Select((v, i) => (Value: v, Entry: memory.CurrentKeyEntries[i])),
            "selections" => memory.Selections,
            _ when historyKey.HasValue => memory.HistoryVersions[historyKey.Value],
            _ => null
        };
        if (rows is not IEnumerable<T> typed) return null;
        var result = new List<T>(); long bytes = 0; bool more = false;
        foreach (var item in typed.Where(item => position(item).Order > (cursor?.SortOrdinal ?? -1) ||
                     position(item).Order == cursor?.SortOrdinal && position(item).Key > cursor.EntryKey))
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count == pageSize) { more = true; break; }
            long size = MemoryRowBytes(item!);
            SnapshotReadGuard.RequireBytes(size, MaximumMetadataPageBytes);
            if (size > MaximumMetadataPageBytes - bytes) { more = true; break; }
            bytes += size; result.Add((T)CopyMemoryRow(item!));
        }
        SnapshotCursor? next = null;
        if (more)
        {
            var p = position(result[^1]);
            next = new(ownerKey, versionKey, memory.Header.RowVersion.ToArray(), p.Order, p.Key, kind, Epoch, query);
        }
        return new(result, next, Epoch);
    }

    private static SnapshotResourceSummary Copy(SnapshotResourceSummary value) => value with { RowVersion = value.RowVersion.ToArray() };

    private static object CopyMemoryRow(object item) => item switch
    {
        SnapshotResourceSummary resource => Copy(resource),
        ObjectSummary value => value with { Resource = Copy(value.Resource) },
        TableSummary value => value with { Resource = Copy(value.Resource) },
        SnapshotHistorySummary value => value with { RowVersion = value.RowVersion.ToArray() },
        ValueTuple<OrderedColumn, long> value => (SnapshotMemoryCache.Clone(value.Item1), value.Item2),
        ValueTuple<OrderedPrimaryKey, long> value => (SnapshotMemoryCache.Clone(value.Item1), value.Item2),
        _ => item
    };

    private static long MemoryRowBytes(object item) => item switch
    {
        SnapshotResourceSummary r => Bytes(r.SchemaName, r.ObjectName, r.OriginalResourceKey),
        ObjectSummary o => MemoryRowBytes(o.Resource) + 2L * (o.TypeDescription.Length + (long)o.ParentSchemaName.Length + o.ParentObjectName.Length),
        TableSummary t => MemoryRowBytes(t.Resource),
        SnapshotHistorySummary h => Bytes(h.SnapshotId),
        SnapshotVersionSummary v => Bytes(v.VersionId, v.VersionName),
        SnapshotChangeSummary c => Bytes(c.OriginalResourceKey, c.DisplayName, c.RelativePath),
        ValueTuple<OrderedColumn, long> c => Bytes(c.Item1.Column.SchemaName, c.Item1.Column.TableName, c.Item1.Column.ColumnName, c.Item1.Column.DataType),
        ValueTuple<OrderedPrimaryKey, long> k => Bytes(k.Item1.Column.SchemaName, k.Item1.Column.TableName, k.Item1.Column.ConstraintName, k.Item1.Column.ColumnName),
        FullDataSelection s => Bytes(s.TableName),
        _ => throw new InvalidOperationException("Unsupported snapshot memory page.")
    };
    private static long Bytes(params string[] values) => 256 + values.Sum(value => 2L * value.Length);
}

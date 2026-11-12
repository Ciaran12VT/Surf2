using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

internal sealed record SnapshotLogicalResource(string SchemaName, string ObjectName, DatabaseVersionedResourceKind? Kind);
internal sealed record HistoricalSnapshotMemory(SnapshotSummary Header, long VersionKey, long HistoryKey,
    HistoricalSnapshotEntry[] Entries, IReadOnlyDictionary<long, SnapshotResourceSummary> Resources,
    IReadOnlyDictionary<long, ObjectSummary> Objects, IReadOnlyDictionary<long, TableMetadataDetails> Tables,
    long? ReplayBaselineVersionKey)
{
    public IReadOnlyDictionary<long, SnapshotLogicalResource>? LogicalResources { get; init; }
}

public sealed partial class HistoricalSnapshotContext
{
    private HistoricalSnapshotMemory? _memory;
    private Func<bool>? _memoryIsCurrent;
    public bool IsMemoryResident => _memory != null;

    internal static HistoricalSnapshotContext FromMemory(HistoricalSnapshotMemory memory, Guid epoch,
        Func<bool>? isCurrent = null)
    {
        var context = new HistoricalSnapshotContext(null!, epoch,
            memory.Header with { RowVersion = memory.Header.RowVersion.ToArray() }, memory.VersionKey, memory.HistoryKey)
        {
            _memory = memory, _memoryIsCurrent = isCurrent, _building = false,
            ReplayBaselineVersionKey = memory.ReplayBaselineVersionKey
        };
        context.InstallEntryCache(memory.Entries);
        return context;
    }

    // Freeze once during scope preload. No live SQL connection survives in the result.
    internal async Task<HistoricalSnapshotMemory> FreezeAsync(CancellationToken ct)
    {
        var entries = new List<HistoricalSnapshotEntry>();
        foreach (var collection in Enum.GetValues<HistoricalCollection>())
            await foreach (var entry in StreamAsync(collection, ct).ConfigureAwait(false)) entries.Add(entry);
        var resources = new Dictionary<long, SnapshotResourceSummary>();
        var objects = new Dictionary<long, ObjectSummary>();
        var tables = new Dictionary<long, TableMetadataDetails>();
        foreach (var batch in entries.Where(entry => entry.ResourceKey.HasValue && entry.RevisionKey.HasValue).Chunk(128))
            foreach (var pair in await ResourceSummariesAsync(batch, ct).ConfigureAwait(false)) resources.Add(pair.Key, pair.Value);
        foreach (var batch in entries.Where(entry => entry.Collection == HistoricalCollection.Objects).Chunk(128))
            foreach (var pair in await ObjectSummariesAsync(batch, ct).ConfigureAwait(false)) objects.Add(pair.Key, pair.Value);
        foreach (var table in entries.Where(entry => entry.Collection == HistoricalCollection.Tables))
            tables.Add(table.EntryKey, await ReadTableMetadataAsync(table, ct).ConfigureAwait(false));
        await ValidateAsync(ct).ConfigureAwait(false);
        return new(Header, VersionKey, HistoryKey, entries.ToArray(), resources, objects, tables, ReplayBaselineVersionKey);
    }

    internal static TableMetadataDetails CopyDetails(TableMetadataDetails value) => new(value.RevisionKey,
        CopyTable(value.Table), value.Columns.Select(item => item with { Column = CopyColumn(item.Column) }).ToArray(),
        value.PrimaryKeys.Select(item => item with { Column = new SqlPrimaryKeyColumn
        {
            SchemaName = item.Column.SchemaName, TableName = item.Column.TableName,
            ConstraintName = item.Column.ConstraintName, ColumnName = item.Column.ColumnName, KeyOrdinal = item.Column.KeyOrdinal
        } }).ToArray());

    internal static SqlColumn CopyColumn(SqlColumn column) => new()
    {
        SchemaName = column.SchemaName, TableName = column.TableName, ColumnName = column.ColumnName,
        DataType = column.DataType, MaxLength = column.MaxLength, NumericPrecision = column.NumericPrecision,
        NumericScale = column.NumericScale, IsNullable = column.IsNullable, IsIdentity = column.IsIdentity, Ordinal = column.Ordinal
    };
}

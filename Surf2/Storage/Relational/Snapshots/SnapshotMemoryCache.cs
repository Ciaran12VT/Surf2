using System.Data;
using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed record SnapshotMemoryProgress(string Message, int Completed, int Total);

/// <summary>Complete, scope-owned snapshot read models. A partial load is never visible to readers.</summary>
public sealed class SnapshotMemoryCache
{
    private readonly RelationalSession _session;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loader = new(1, 1);
    private readonly AsyncLocal<int> _bypass = new();
    private readonly Dictionary<long, SnapshotMemory> _snapshots = [];
    private long _generation;

    internal SnapshotMemoryCache(RelationalSession session) => _session = session;
    public bool ContainsSnapshot(long key) => Find(key) != null;
    public bool ContainsSnapshots(IEnumerable<long> keys)
    { _session.RequireCurrentIdentity(); lock (_gate) return _bypass.Value == 0 && keys.All(_snapshots.ContainsKey); }
    public void Clear() { lock (_gate) { _generation++; _snapshots.Clear(); } }
    public void Retain(IReadOnlyCollection<long> keys)
    {
        var retained = keys.ToHashSet();
        lock (_gate)
        {
            var removed = _snapshots.Keys.Where(key => !retained.Contains(key)).ToArray();
            if (removed.Length == 0) return;
            _generation++;
            foreach (long key in removed) _snapshots.Remove(key);
        }
    }
    public void Invalidate(long snapshotKey) { lock (_gate) { _generation++; _snapshots.Remove(snapshotKey); } }
    public bool TryGetSnapshot(long key, out SnapshotSummary value)
    {
        if (Find(key) is { } memory) { value = memory.Header with { RowVersion = memory.Header.RowVersion.ToArray() }; return true; }
        value = null!; return false;
    }
    public bool TryGetVersions(long snapshotKey, out IReadOnlyList<SnapshotVersionSummary> versions)
    {
        if (Find(snapshotKey) is { } memory) { versions = memory.Histories.FirstOrDefault() is { } h ? Array.AsReadOnly(memory.HistoryVersions[h.HistoryKey]) : []; return true; }
        versions = []; return false;
    }
    public bool TryGetPreviousVersion(SnapshotVersionSummary selected, out long? previous)
    {
        previous = null;
        if (Find(selected.SnapshotKey) is not { } memory) return false;
        var versions = memory.HistoryVersions.Values.FirstOrDefault(v => v.Any(x => x.VersionKey == selected.VersionKey));
        var stored = versions?.FirstOrDefault(v => v.VersionKey == selected.VersionKey);
        if (stored == null || stored.VersionNumber != selected.VersionNumber || stored.IsInitial != selected.IsInitial) throw new SnapshotConcurrencyException();
        previous = versions!.Where(v => v.VersionNumber < selected.VersionNumber).OrderByDescending(v => v.VersionNumber)
            .ThenBy(v => v.SortOrdinal).ThenBy(v => v.VersionKey).FirstOrDefault()?.VersionKey;
        return true;
    }
    public bool TryGetColumn(long key, out OrderedColumn value)
    {
        foreach (var memory in All()) if (memory.Columns.TryGetValue(key, out var column)) { value = Clone(column); return true; }
        value = null!; return false;
    }
    internal SnapshotMemory? Find(long key)
    { _session.RequireCurrentIdentity(); lock (_gate) return _bypass.Value == 0 ? _snapshots.GetValueOrDefault(key) : null; }
    internal SnapshotMemory[] All()
    { _session.RequireCurrentIdentity(); lock (_gate) return _bypass.Value == 0 ? _snapshots.Values.ToArray() : []; }

    public async Task PreloadAsync(IReadOnlyCollection<long> snapshotKeys, IProgress<SnapshotMemoryProgress>? progress = null,
        CancellationToken ct = default)
    {
        var keys = snapshotKeys.Distinct().ToArray();
        await _loader.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_gate) generation = _generation;
            for (int index = 0; index < keys.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (ContainsSnapshot(keys[index])) continue;
                _bypass.Value++;
                SnapshotMemory loaded;
                try { loaded = await LoadAsync(keys[index], index, keys.Length, progress, ct).ConfigureAwait(false); }
                finally { _bypass.Value--; }
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_generation != generation) throw new OperationCanceledException("Snapshot memory was invalidated while loading.", ct);
                    _snapshots[keys[index]] = loaded;
                }
                progress?.Report(new($"Loaded history and comparisons for {loaded.Header.DisplayName}", index + 1, keys.Length));
            }
        }
        finally { _loader.Release(); }
    }

    private async Task<SnapshotMemory> LoadAsync(long key, int index, int total,
        IProgress<SnapshotMemoryProgress>? progress, CancellationToken ct)
    {
        var store = new RelationalSnapshotStore(_session, new RelationalContentStore(),
            new() { SelectedDefinitionBytes = SnapshotReadLimits.MaximumDefinitionBytes });
        var capture = new RelationalCaptureStore(_session);
        var header = await store.GetSnapshotAsync(key, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Loaded snapshot no longer exists.");
        var memory = new SnapshotMemory(header);
        void Report(string phase) => progress?.Report(new($"{header.DisplayName}: {phase}", index, total));
        Report("loading snapshot history");
        memory.Resources = await ReadAll(c => store.ListResourcesAsync(key, pageSize: 1000, cursor: c, ct: ct), ct).ConfigureAwait(false);
        memory.CurrentObjects = await ReadAll(c => store.ListObjectsAsync(key, pageSize: 1000, cursor: c, ct: ct), ct).ConfigureAwait(false);
        memory.CurrentTables = await ReadAll(c => store.ListTablesAsync(key, pageSize: 1000, cursor: c, ct: ct), ct).ConfigureAwait(false);
        memory.CurrentColumns = await ReadAll(c => store.ListCurrentColumnsAsync(key, 1000, c, ct), ct).ConfigureAwait(false);
        memory.CurrentKeys = await ReadAll(c => store.ListCurrentPrimaryKeysAsync(key, 1000, c, ct), ct).ConfigureAwait(false);
        memory.Selections = await ReadAll(c => store.ListFullDataSelectionsAsync(key, 1000, c, ct), ct).ConfigureAwait(false);
        memory.Histories = await ReadAll(c => store.ListHistoriesAsync(key, 1000, c, ct), ct).ConfigureAwait(false);
        await ReadIdentityMetadataAsync(memory, ct).ConfigureAwait(false);
        foreach (var history in memory.Histories)
        {
            var versions = await ReadAll(c => store.ListHistoryVersionsAsync(key, history.HistoryKey, 1000, c, ct), ct).ConfigureAwait(false);
            memory.HistoryVersions.Add(history.HistoryKey, versions);
            foreach (var version in versions)
                memory.Changes.Add(version.VersionKey, await ReadAll(c => store.ListChangesAsync(key, version.VersionKey, 1000, c, ct), ct).ConfigureAwait(false));
        }

        var revisions = new List<(long Key, DatabaseVersionedResourceKind? Kind)>();
        await using (var connection = await _session.OpenAsync(ct).ConfigureAwait(false))
        {
            await using var command = Command(connection, null, """
                SELECT rr.RevisionKey,r.Kind FROM surf.SnapshotResourceRevision rr
                JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
                WHERE r.SnapshotKey=@Snapshot AND rr.IsSealed=1 AND
                    (r.CurrentRevisionKey=rr.RevisionKey
                     OR EXISTS(SELECT 1 FROM surf.SnapshotChange c WHERE c.SnapshotKey=@Snapshot AND c.PreviousRevisionKey=rr.RevisionKey)
                     OR EXISTS(SELECT 1 FROM surf.SnapshotVersionBaseline b JOIN surf.SnapshotVersion v ON v.VersionKey=b.VersionKey
                         WHERE v.SnapshotKey=@Snapshot AND b.RevisionKey=rr.RevisionKey))
                ORDER BY rr.RevisionKey;
                """, Key("@Snapshot", key));
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                revisions.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(1)));
        }
        Report("loading all definition revisions");
        foreach (var batch in revisions.Where(r => r.Kind is not DatabaseVersionedResourceKind.TableMetadata and not DatabaseVersionedResourceKind.TableData)
            .Select(r => r.Key).Chunk(RelationalSnapshotStore.MaximumObjectBatchSize))
        {
            var identities = await store.ReadObjectTextIdentitiesAsync(batch, ct).ConfigureAwait(false);
            foreach (var pair in identities) memory.ObjectIdentities.Add(pair.Key, pair.Value);
            var pending = new List<long>(); long bytes = 0;
            foreach (long revision in batch)
            {
                long size = identities[revision].ByteCount;
                if (size > RelationalSnapshotStore.MaximumDefinitionBatchBytes)
                {
                    await Flush().ConfigureAwait(false);
                    memory.Objects.Add(revision, await store.ReadObjectAsync(revision, ct).ConfigureAwait(false));
                    continue;
                }
                if (pending.Count != 0 && size > RelationalSnapshotStore.MaximumDefinitionBatchBytes - bytes) await Flush().ConfigureAwait(false);
                pending.Add(revision); bytes += size;
            }
            await Flush().ConfigureAwait(false);
            async Task Flush()
            {
                if (pending.Count == 0) return;
                foreach (var pair in await store.ReadObjectsAsync(pending, ct).ConfigureAwait(false)) memory.Objects.Add(pair.Key, pair.Value);
                pending.Clear(); bytes = 0;
            }
        }
        foreach (var revision in revisions.Where(r => r.Kind == DatabaseVersionedResourceKind.TableMetadata))
        {
            ct.ThrowIfCancellationRequested();
            var table = await store.ReadTableMetadataAsync(revision.Key, ct).ConfigureAwait(false);
            memory.Tables.Add(revision.Key, table);
            foreach (var column in table.Columns) memory.Columns.TryAdd(column.ColumnRevisionKey, column);
        }
        foreach (var column in memory.CurrentColumns) memory.Columns.TryAdd(column.ColumnRevisionKey, column);
        var dataRevisions = revisions.Where(r => r.Kind == DatabaseVersionedResourceKind.TableData).ToArray();
        for (int d = 0; d < dataRevisions.Length; d++)
        {
            Report($"loading captured table data {d + 1}/{dataRevisions.Length}");
            long revision = dataRevisions[d].Key;
            memory.DataMetadata.Add(revision, await store.ReadTableDataMetadataAsync(revision, ct).ConfigureAwait(false));
            var descriptor = await capture.GetForRevisionAsync(revision, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Published captured table data is not ready.");
            var rows = new List<CaptureRow>();
            await foreach (var row in capture.StreamRowsAsync(descriptor.Summary.DataSetKey, cancellationToken: ct).ConfigureAwait(false)) rows.Add(row);
            memory.Captures.Add(descriptor.Summary.DataSetKey, new(descriptor, rows.ToArray()));
        }

        var versionKeys = memory.HistoryVersions.Values.SelectMany(v => v).Select(v => v.VersionKey).ToList();
        if (memory.Histories.FirstOrDefault() is not { } firstHistory || memory.HistoryVersions[firstHistory.HistoryKey].Length == 0)
            versionKeys.Add(0);
        for (int v = 0; v < versionKeys.Count; v++)
        {
            Report($"preparing historical versions {v + 1}/{versionKeys.Count}");
            await using var context = await store.OpenHistoricalSnapshotAsync(key, versionKeys[v], ct).ConfigureAwait(false);
            var frozen = await context.FreezeAsync(ct).ConfigureAwait(false);
            memory.Versions.Add(versionKeys[v], frozen with { LogicalResources = memory.LogicalResources });
            foreach (var table in frozen.Tables.Values)
                foreach (var column in table.Columns) memory.Columns.TryAdd(column.ColumnRevisionKey, column);
        }
        var current = await store.GetSnapshotAsync(key, ct).ConfigureAwait(false);
        if (current == null || !current.RowVersion.AsSpan().SequenceEqual(header.RowVersion)) throw new SnapshotConcurrencyException();
        return memory;
    }

    private static async Task<T[]> ReadAll<T>(Func<SnapshotCursor?, Task<SnapshotPage<T>>> read, CancellationToken ct)
    {
        var result = new List<T>(); SnapshotCursor? cursor = null;
        do { ct.ThrowIfCancellationRequested(); var page = await read(cursor).ConfigureAwait(false); result.AddRange(page.Items); cursor = page.Next; } while (cursor != null);
        return result.ToArray();
    }

    private async Task ReadIdentityMetadataAsync(SnapshotMemory memory, CancellationToken ct)
    {
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT ResourceKey,OriginalResourceKey,SchemaName,ObjectName,Kind FROM surf.SnapshotResource WHERE SnapshotKey=@Snapshot;
            SELECT ColumnRevisionKey,SortOrdinal,SchemaName,TableName,ColumnName,DataType,MaxLength,
                NumericPrecision,NumericScale,IsNullable,IsIdentity,SourceOrdinal
            FROM surf.TableColumnRevision WHERE SnapshotKey=@Snapshot;
            SELECT EntryKey FROM surf.SnapshotCurrentColumn WHERE SnapshotKey=@Snapshot ORDER BY SortOrdinal,EntryKey;
            SELECT EntryKey FROM surf.SnapshotCurrentPrimaryKey WHERE SnapshotKey=@Snapshot ORDER BY SortOrdinal,EntryKey;
            """, Key("@Snapshot", memory.Header.SnapshotKey));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long resource = reader.GetInt64(0);
            memory.ResourceIdentities.Add(resource, reader.GetString(1));
            memory.LogicalResources.Add(resource, new(reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(4)));
        }
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long key = reader.GetInt64(0);
            memory.Columns.Add(key, new(key, reader.GetInt64(1), new()
            {
                SchemaName = reader.GetString(2), TableName = reader.GetString(3), ColumnName = reader.GetString(4),
                DataType = reader.GetString(5), MaxLength = reader.GetInt32(6), NumericPrecision = reader.GetByte(7),
                NumericScale = reader.GetInt32(8), IsNullable = reader.GetBoolean(9), IsIdentity = reader.GetBoolean(10), Ordinal = reader.GetInt32(11)
            }));
        }
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var columnEntries = new List<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) columnEntries.Add(reader.GetInt64(0));
        memory.CurrentColumnEntries = columnEntries.ToArray();
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var keyEntries = new List<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keyEntries.Add(reader.GetInt64(0));
        memory.CurrentKeyEntries = keyEntries.ToArray();
    }

    internal static SqlDatabaseObject Clone(SqlDatabaseObject v) => new() { SchemaName = v.SchemaName, ObjectName = v.ObjectName,
        Kind = v.Kind, TypeDescription = v.TypeDescription, Definition = v.Definition, ParentSchemaName = v.ParentSchemaName, ParentObjectName = v.ParentObjectName };
    internal static SqlTable Clone(SqlTable v) => new() { SchemaName = v.SchemaName, TableName = v.TableName, HasFullData = v.HasFullData,
        FullDataRowCount = v.FullDataRowCount, FullDataImportedAtUtc = v.FullDataImportedAtUtc };
    internal static OrderedColumn Clone(OrderedColumn v) => v with { Column = new() { SchemaName = v.Column.SchemaName,
        TableName = v.Column.TableName, ColumnName = v.Column.ColumnName, DataType = v.Column.DataType, MaxLength = v.Column.MaxLength,
        NumericPrecision = v.Column.NumericPrecision, NumericScale = v.Column.NumericScale, IsNullable = v.Column.IsNullable,
        IsIdentity = v.Column.IsIdentity, Ordinal = v.Column.Ordinal } };
    internal static OrderedPrimaryKey Clone(OrderedPrimaryKey v) => v with { Column = new() { SchemaName = v.Column.SchemaName,
        TableName = v.Column.TableName, ConstraintName = v.Column.ConstraintName, ColumnName = v.Column.ColumnName, KeyOrdinal = v.Column.KeyOrdinal } };
    internal static TableMetadataDetails Clone(TableMetadataDetails v) => v with { Table = Clone(v.Table),
        Columns = v.Columns.Select(Clone).ToArray(), PrimaryKeys = v.PrimaryKeys.Select(Clone).ToArray() };
}

internal sealed class SnapshotMemory(SnapshotSummary header)
{
    public SnapshotSummary Header { get; } = header;
    public SnapshotResourceSummary[] Resources { get; set; } = [];
    public Dictionary<long, string> ResourceIdentities { get; } = [];
    public Dictionary<long, SnapshotLogicalResource> LogicalResources { get; } = [];
    public ObjectSummary[] CurrentObjects { get; set; } = [];
    public TableSummary[] CurrentTables { get; set; } = [];
    public OrderedColumn[] CurrentColumns { get; set; } = [];
    public long[] CurrentColumnEntries { get; set; } = [];
    public OrderedPrimaryKey[] CurrentKeys { get; set; } = [];
    public long[] CurrentKeyEntries { get; set; } = [];
    public FullDataSelection[] Selections { get; set; } = [];
    public SnapshotHistorySummary[] Histories { get; set; } = [];
    public Dictionary<long, SnapshotVersionSummary[]> HistoryVersions { get; } = [];
    public Dictionary<long, SnapshotChangeSummary[]> Changes { get; } = [];
    public Dictionary<long, HistoricalSnapshotMemory> Versions { get; } = [];
    public Dictionary<long, SqlDatabaseObject> Objects { get; } = [];
    public Dictionary<long, SnapshotObjectTextIdentity> ObjectIdentities { get; } = [];
    public Dictionary<long, TableMetadataDetails> Tables { get; } = [];
    public Dictionary<long, OrderedColumn> Columns { get; } = [];
    public Dictionary<long, TableDataMetadata> DataMetadata { get; } = [];
    public Dictionary<long, CapturedTableMemory> Captures { get; } = [];
}

internal sealed record CapturedTableMemory(CaptureDataSetDescriptor Descriptor, CaptureRow[] Rows);

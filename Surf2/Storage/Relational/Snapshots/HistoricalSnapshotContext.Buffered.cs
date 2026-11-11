using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class HistoricalSnapshotContext
{
    // A selected version's small identity plan can be replayed without one SQL
    // round trip per resource change. Payloads and captured rows never enter this
    // buffer. Larger plans retain the bounded streaming implementation.
    private const int MaximumBufferedEntries = 100_000;
    private const long MaximumBufferedBytes = 32L * 1024 * 1024;
    private HistoricalSnapshotEntry[]? _cachedEntries;
    private Dictionary<long, HistoricalSnapshotEntry>? _cachedEntryKeys;
    private Dictionary<BufferedIdentity, List<HistoricalSnapshotEntry>>? _cachedNames;

    private sealed class BufferLimitException : Exception { }
    private sealed class ReplayBudget
    {
        private long _bytes;
        private int _units;
        public void Add(HistoricalSnapshotEntry entry)
        {
            long bytes = 640L + 2L * (entry.SchemaName.Length + (long)entry.Name.Length);
            if (++_units > MaximumBufferedEntries || bytes > MaximumBufferedBytes - _bytes) throw new BufferLimitException();
            _bytes += bytes;
        }
    }

    private readonly record struct BufferedIdentity(HistoricalCollection Collection, string Schema, string Name)
    {
        public static BufferedIdentity From(HistoricalSnapshotEntry entry) => From(entry.Collection, entry.SchemaName, entry.Name);
        public static BufferedIdentity From(HistoricalCollection collection, string schema, string name) =>
            collection == HistoricalCollection.FullDataTableNames
                ? new(collection, "", FullTableDataSelection.GetTableIdentity(name)) : new(collection, schema, name);
    }
    private sealed class BufferedIdentityComparer : IEqualityComparer<BufferedIdentity>
    {
        public static readonly BufferedIdentityComparer Instance = new();
        public bool Equals(BufferedIdentity x, BufferedIdentity y) => x.Collection == y.Collection &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        public int GetHashCode(BufferedIdentity value) => HashCode.Combine(value.Collection,
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
    private static HistoricalSnapshotEntry CopyEntry(HistoricalSnapshotEntry value) => value with
    { Table = value.Table == null ? null : CopyTable(value.Table) };

    // Line-count identities need the same children and order as the table/CSV renderers.
    // A null result means the large projection was not buffered.
    internal IReadOnlyList<HistoricalSnapshotEntry>? BufferedHistoryChildren(HistoricalSnapshotEntry entry, HistoricalCollection collection) =>
        _cachedNames == null ? null : _cachedNames.GetValueOrDefault(BufferedIdentity.From(collection, entry.SchemaName, entry.Name)) ?? [];

    private async Task<HistoricalSnapshotEntry[]?> ReadBufferedSeedAsync(ReplayBudget budget, CancellationToken ct)
    {
        await using var command = Command(_connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(Name),
                EntryKey,Collection,OrderKey,ResourceKey,RevisionKey,ChildKey,SchemaName,Name,ObjectKind,
                HasFullData,FullDataRowCount,FullDataImportedAtUtc,TableSource
            FROM #HistoricalEntry ORDER BY Collection,OrderKey,EntryKey;
            """);
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var result = new List<HistoricalSnapshotEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long size = reader.GetInt64(0);
            if (size > RelationalSnapshotStore.MaximumMetadataPageBytes)
            { SnapshotReadGuard.StopReading(command); return null; }
            SnapshotReadGuard.RequireBytes(size, RelationalSnapshotStore.MaximumMetadataPageBytes, command);
            var entry = ReadEntry(reader);
            try { budget.Add(entry); }
            catch (BufferLimitException) { SnapshotReadGuard.StopReading(command); return null; }
            result.Add(entry);
        }
        return result.ToArray();
    }

    private async Task TryCacheEntriesAsync(CancellationToken ct)
    {
        var rows = await ReadBufferedSeedAsync(new ReplayBudget(), ct).ConfigureAwait(false);
        if (rows != null) InstallEntryCache(rows);
    }
    private void InstallEntryCache(HistoricalSnapshotEntry[] rows)
    {
        _cachedEntries = rows.OrderBy(row => row.Collection).ThenBy(row => row.SortOrdinal).ThenBy(row => row.EntryKey).ToArray();
        _cachedEntryKeys = _cachedEntries.ToDictionary(row => row.EntryKey);
        _cachedNames = new(BufferedIdentityComparer.Instance);
        foreach (var collection in _cachedEntries.GroupBy(row => row.Collection))
        {
            long position = 0;
            foreach (var row in collection)
            {
                var identity = BufferedIdentity.From(row);
                if (!_cachedNames.TryGetValue(identity, out var matches)) _cachedNames.Add(identity, matches = []);
                // StreamAsync(collection) historically exposes a dense collection position.
                matches.Add(row with { SortOrdinal = position++ });
            }
        }
    }

    private SnapshotPage<HistoricalSnapshotEntry> CachedPage(HistoricalCollection? collection, DatabaseVersionedResourceKind? kind,
        bool resources, int pageSize, SnapshotCursor? cursor, string query, CancellationToken ct)
    {
        var selected = _cachedEntries!.Where(entry => (!collection.HasValue || entry.Collection == collection) &&
            (!resources || entry.Collection is HistoricalCollection.Objects or HistoricalCollection.Tables or HistoricalCollection.TableDataSets));
        var result = new List<HistoricalSnapshotEntry>();
        long position = -1, bytes = 0; bool more = false;
        foreach (var entry in selected)
        {
            ct.ThrowIfCancellationRequested(); position++;
            if (position <= (cursor?.SortOrdinal ?? -1) || kind.HasValue && EntryResourceKind(entry) != kind) continue;
            if (result.Count == pageSize) { more = true; break; }
            long size = 256 + 2L * (entry.SchemaName.Length + (long)entry.Name.Length);
            SnapshotReadGuard.RequireBytes(size, RelationalSnapshotStore.MaximumMetadataPageBytes);
            if (size > RelationalSnapshotStore.MaximumMetadataPageBytes - bytes) { more = true; break; }
            bytes += size; result.Add(CopyEntry(entry) with { SortOrdinal = position });
        }
        var next = more ? new SnapshotCursor(Header.SnapshotKey, VersionKey, _headToken.ToArray(), result[^1].SortOrdinal,
            result[^1].SortOrdinal, (int?)kind, Epoch, query) : null;
        return new(result, next, Epoch);
    }
    private static DatabaseVersionedResourceKind? EntryResourceKind(HistoricalSnapshotEntry entry) => entry.Collection switch
    {
        HistoricalCollection.Tables => DatabaseVersionedResourceKind.TableMetadata,
        HistoricalCollection.TableDataSets => DatabaseVersionedResourceKind.TableData,
        HistoricalCollection.Objects => SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown),
        _ => null
    };

    private async Task<bool> TryReplayBufferedAsync(int target, CancellationToken ct)
    {
        var budget = new ReplayBudget();
        var seed = await ReadBufferedSeedAsync(budget, ct).ConfigureAwait(false);
        if (seed == null) return false;
        var state = new BufferedReplayState(seed, budget);
        HistoricalChangePosition? after = null; bool changed = false;
        try
        {
            while (true)
            {
                var changes = await ReadChanges(target, after, ct).ConfigureAwait(false);
                if (changes.Count == 0) break;
                state.Children = await ReadReplayChildrenAsync(changes.Select(change => change.Change), budget, ct).ConfigureAwait(false);
                foreach (var change in changes)
                    await HistoricalSnapshotReplay.ApplyAsync(state, change.Change, ct).ConfigureAwait(false);
                state.Children = [];
                changed = true; after = changes[^1].Position;
            }
        }
        catch (BufferLimitException) { return false; } // SQL seed remains untouched for streaming fallback.
        var result = state.Entries();
        if (changed) await WriteBufferedEntriesAsync(result, ct).ConfigureAwait(false);
        InstallEntryCache(result);
        return true;
    }

    private async Task<Dictionary<(long Revision, HistoricalCollection Collection), List<HistoricalSnapshotEntry>>> ReadReplayChildrenAsync(
        IEnumerable<HistoricalRollbackEvent> changes, ReplayBudget budget, CancellationToken ct)
    {
        long[] revisions = changes.Where(change => change.ChangeKind != DatabaseSnapshotResourceChangeKind.Added &&
            change.Previous?.Kind == DatabaseVersionedResourceKind.TableMetadata).Select(change => change.Previous!.RevisionKey).Distinct().ToArray();
        var result = new Dictionary<(long, HistoricalCollection), List<HistoricalSnapshotEntry>>();
        if (revisions.Length == 0) return result;
        await using var command = Command(_connection, null, """
            WITH children AS (
                SELECT c.TableMetadataRevisionKey Revision,2 Collection,c.SortOrdinal,c.ColumnRevisionKey Child,c.SchemaName,c.TableName
                FROM OPENJSON(@Revisions) WITH (Revision bigint '$') r JOIN surf.TableColumnRevision c ON c.TableMetadataRevisionKey=r.Revision
                UNION ALL
                SELECT k.TableMetadataRevisionKey,3,k.SortOrdinal,k.KeyColumnKey,k.SchemaName,k.TableName
                FROM OPENJSON(@Revisions) WITH (Revision bigint '$') r JOIN surf.PrimaryKeyColumn k ON k.TableMetadataRevisionKey=r.Revision)
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName),Revision,Collection,SortOrdinal,Child,SchemaName,TableName
            FROM children ORDER BY Revision,Collection,SortOrdinal,Child;
            """, Text("@Revisions", JsonSerializer.Serialize(revisions)));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long size = reader.GetInt64(0);
            if (size > RelationalSnapshotStore.MaximumMetadataPageBytes)
            { SnapshotReadGuard.StopReading(command); throw new BufferLimitException(); }
            SnapshotReadGuard.RequireBytes(size, RelationalSnapshotStore.MaximumMetadataPageBytes, command);
            long revision = reader.GetInt64(1); var collection = (HistoricalCollection)reader.GetInt32(2);
            var entry = new HistoricalSnapshotEntry(0, collection, reader.GetInt64(3), null, revision, reader.GetInt64(4), reader.GetString(5), reader.GetString(6));
            try { budget.Add(entry); }
            catch (BufferLimitException) { SnapshotReadGuard.StopReading(command); throw; }
            if (!result.TryGetValue((revision, collection), out var children)) result.Add((revision, collection), children = []);
            children.Add(entry);
        }
        return result;
    }

    private async Task WriteBufferedEntriesAsync(IReadOnlyList<HistoricalSnapshotEntry> entries, CancellationToken ct)
    {
        await Execute("TRUNCATE TABLE #HistoricalEntry;", ct).ConfigureAwait(false);
        using var table = new DataTable();
        foreach (var (name, type) in new (string, Type)[] { ("EntryKey",typeof(long)),("Collection",typeof(int)),("OrderKey",typeof(long)),
            ("ResourceKey",typeof(long)),("RevisionKey",typeof(long)),("ChildKey",typeof(long)),("SchemaName",typeof(string)),("Name",typeof(string)),
            ("ObjectKind",typeof(int)),("HasFullData",typeof(bool)),("FullDataRowCount",typeof(long)),("FullDataImportedAtUtc",typeof(DateTimeOffset)),("TableSource",typeof(int)) })
            table.Columns.Add(name, type);
        using var copy = new SqlBulkCopy(_connection, SqlBulkCopyOptions.KeepIdentity, null) { DestinationTableName = "#HistoricalEntry", BatchSize = 256 };
        foreach (DataColumn column in table.Columns) copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        long bytes = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            long size = 640 + 2L * (entry.SchemaName.Length + (long)entry.Name.Length);
            if (table.Rows.Count != 0 && (table.Rows.Count == 256 || size > RelationalSnapshotStore.MaximumMetadataPageBytes - bytes)) await Flush();
            table.Rows.Add(entry.EntryKey, (int)entry.Collection, entry.SortOrdinal, Db(entry.ResourceKey), Db(entry.RevisionKey), Db(entry.ChildKey),
                entry.SchemaName, entry.Name, Db((int?)entry.ObjectKind), Db(entry.Table?.HasFullData), Db(entry.Table?.FullDataRowCount),
                Db(entry.Table?.FullDataImportedAtUtc), (int)entry.TableSource);
            bytes += size;
        }
        await Flush();
        async Task Flush()
        {
            if (table.Rows.Count == 0) return;
            await copy.WriteToServerAsync(table, ct).ConfigureAwait(false); table.Clear(); bytes = 0;
        }
        static object Db(object? value) => value ?? DBNull.Value;
    }

    private sealed class BufferedReplayState : IHistoricalSnapshotState
    {
        private sealed class Node(HistoricalSnapshotEntry value)
        {
            public HistoricalSnapshotEntry Value = value;
            public LinkedListNode<Node> Order = null!, Match = null!;
        }
        private readonly LinkedList<Node>[] _ordered = Enumerable.Range(0, 6).Select(_ => new LinkedList<Node>()).ToArray();
        private readonly Dictionary<BufferedIdentity, LinkedList<Node>> _names = new(BufferedIdentityComparer.Instance);
        private readonly Dictionary<long, Node> _keys = [];
        private readonly ReplayBudget _budget;
        private long _nextKey;
        public Dictionary<(long Revision, HistoricalCollection Collection), List<HistoricalSnapshotEntry>> Children = [];
        public BufferedReplayState(IEnumerable<HistoricalSnapshotEntry> seed, ReplayBudget budget)
        {
            _budget = budget;
            foreach (var entry in seed) { Add(entry); _nextKey = Math.Max(_nextKey, entry.EntryKey); }
        }
        private void Add(HistoricalSnapshotEntry entry)
        {
            var node = new Node(CopyEntry(entry)); var identity = BufferedIdentity.From(entry);
            node.Order = _ordered[(int)entry.Collection].AddLast(node);
            if (!_names.TryGetValue(identity, out var names)) _names.Add(identity, names = new LinkedList<Node>());
            node.Match = names.AddLast(node); _keys.Add(entry.EntryKey, node);
        }
        private void Remove(Node node)
        {
            _ordered[(int)node.Value.Collection].Remove(node.Order);
            var identity = BufferedIdentity.From(node.Value); var matches = _names[identity]; matches.Remove(node.Match);
            if (matches.Count == 0) _names.Remove(identity);
            _keys.Remove(node.Value.EntryKey);
        }
        public HistoricalSnapshotEntry[] Entries() => _ordered.SelectMany(list => list.Select(node => node.Value)).ToArray();
        public Task RemoveAsync(HistoricalCollection collection, string schema, string name, SqlDatabaseObjectKind? kind, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_names.TryGetValue(BufferedIdentity.From(collection, schema, name), out var matches))
                for (var current = matches.First; current != null;)
                {
                    var next = current.Next;
                    if (!kind.HasValue || current.Value.Value.ObjectKind == kind) Remove(current.Value);
                    current = next;
                }
            return Task.CompletedTask;
        }
        public Task<HistoricalSnapshotEntry> AppendAsync(HistoricalSnapshotEntry entry, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); _budget.Add(entry);
            entry = entry with { EntryKey = ++_nextKey, SortOrdinal = (_ordered[(int)entry.Collection].Last?.Value.Value.SortOrdinal ?? -1) + 1 };
            Add(entry); return Task.FromResult(entry);
        }
        public async Task AppendChildrenAsync(long revisionKey, HistoricalCollection collection, CancellationToken ct)
        {
            if (Children.TryGetValue((revisionKey, collection), out var children))
                foreach (var child in children) await AppendAsync(child, ct).ConfigureAwait(false);
        }
        public Task<HistoricalSnapshotEntry?> FirstTableAsync(string schema, string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_names.TryGetValue(BufferedIdentity.From(HistoricalCollection.Tables, schema, name), out var matches)
                ? matches.First?.Value.Value : null);
        }
        public Task UpdateTableAsync(long entryKey, bool hasData, long count, DateTimeOffset? importedAt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var node = _keys[entryKey];
            var table = CopyTable(node.Value.Table!); table.HasFullData = hasData; table.FullDataRowCount = count; table.FullDataImportedAtUtc = importedAt;
            node.Value = node.Value with { Table = table }; return Task.CompletedTask;
        }
        public async Task EnsureSelectionAsync(string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var names = _ordered[5].Select(node => node.Value.Name).ToList();
            var selectionBudget = new SnapshotMetadataBudget();
            foreach (var existing in names) selectionBudget.Add(256 + 2L * existing.Length);
            selectionBudget.Add(256 + 2L * name.Length);
            var merged = FullTableDataSelection.MergeTableNames(names, [name]);
            while (_ordered[5].First is { } first) Remove(first.Value);
            foreach (var selection in merged) await AppendAsync(new(0, HistoricalCollection.FullDataTableNames, 0, null, null, null, "", selection), ct).ConfigureAwait(false);
        }
    }
}

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class HistoricalSnapshotContext
{
    public async Task ValidateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<long> CountResourcesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            if (_cachedEntries != null) return _cachedEntries.LongCount(entry => EntryResourceKind(entry).HasValue);
            return Convert.ToInt64(await ScalarAsync(_connection, null,
                "SELECT COUNT_BIG(*) FROM #HistoricalEntry WHERE Collection IN (1,4) OR Collection=0 AND ObjectKind BETWEEN 1 AND 4;", ct).ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    /// <summary>One issued entry identity, without enumerating the version again.</summary>
    public async Task<HistoricalSnapshotEntry?> FindEntryAsync(long entryKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            var entry = await EntryByKeyAsync(entryKey, ct).ConfigureAwait(false);
            return entry == null ? null : CopyEntry(entry);
        }
        finally { _gate.Release(); }
    }

    private async Task<HistoricalSnapshotEntry?> EntryByKeyAsync(long entryKey, CancellationToken ct)
    {
        if (_cachedEntryKeys != null) return _cachedEntryKeys.GetValueOrDefault(entryKey);
        await using var command = Command(_connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(Name),
                EntryKey,Collection,OrderKey,ResourceKey,RevisionKey,ChildKey,SchemaName,Name,ObjectKind,
                HasFullData,FullDataRowCount,FullDataImportedAtUtc,TableSource FROM #HistoricalEntry WHERE EntryKey=@Entry;
            """, Key("@Entry", entryKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        RowBytes(reader, command); return ReadEntry(reader);
    }

    private static bool SameEntry(HistoricalSnapshotEntry stored, HistoricalSnapshotEntry requested) =>
        stored.EntryKey == requested.EntryKey && stored.Collection == requested.Collection && stored.ResourceKey == requested.ResourceKey &&
        stored.RevisionKey == requested.RevisionKey && stored.ChildKey == requested.ChildKey && stored.ObjectKind == requested.ObjectKind &&
        stored.TableSource == requested.TableSource && StringComparer.Ordinal.Equals(stored.SchemaName, requested.SchemaName) &&
        StringComparer.Ordinal.Equals(stored.Name, requested.Name);

    public async Task<HistoricalSnapshotEntry?> FindResourceAsync(long resourceKey, CancellationToken ct = default)
    {
        string schema, name; DatabaseVersionedResourceKind? kind;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            await using var command = Command(_connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(ObjectName),SchemaName,ObjectName,Kind
                FROM surf.SnapshotResource WHERE ResourceKey=@Resource AND SnapshotKey=@Snapshot;
                """, Key("@Resource", resourceKey), Key("@Snapshot", Header.SnapshotKey));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            RowBytes(reader, command); schema = reader.GetString(1); name = reader.GetString(2);
            kind = reader.IsDBNull(3) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(3);
            var collection = kind == DatabaseVersionedResourceKind.TableMetadata ? HistoricalCollection.Tables :
                kind == DatabaseVersionedResourceKind.TableData ? HistoricalCollection.TableDataSets : HistoricalCollection.Objects;
            if (_cachedNames != null)
            {
                var found = _cachedNames.GetValueOrDefault(BufferedIdentity.From(collection, schema, name))?
                    .FirstOrDefault(entry => collection != HistoricalCollection.Objects || EntryResourceKind(entry) == kind);
                return found == null ? null : CopyEntry(found);
            }
        }
        finally { _gate.Release(); }
        var selectedCollection = kind == DatabaseVersionedResourceKind.TableMetadata ? HistoricalCollection.Tables :
            kind == DatabaseVersionedResourceKind.TableData ? HistoricalCollection.TableDataSets : HistoricalCollection.Objects;
        await foreach (var entry in StreamAsync(selectedCollection, ct).ConfigureAwait(false))
            if (HistoricalSnapshotReplay.Matches(entry, selectedCollection, schema, name) &&
                (selectedCollection != HistoricalCollection.Objects || EntryResourceKind(entry) == kind)) return entry;
        return null;
    }

    /// <summary>Matching root entries in original order, using the replay program's exact ordinal case-insensitive identity.</summary>
    public async Task<IReadOnlyList<HistoricalSnapshotEntry>> FindEntriesAsync(HistoricalCollection collection, string schema,
        string name, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            if (_cachedNames != null)
            {
                var result = new List<HistoricalSnapshotEntry>(); var budget = new SnapshotMetadataBudget();
                if (_cachedNames.TryGetValue(BufferedIdentity.From(collection, schema, name), out var matches))
                    foreach (var entry in matches)
                    {
                        ct.ThrowIfCancellationRequested(); budget.Add(256 + 2L * (entry.SchemaName.Length + (long)entry.Name.Length));
                        result.Add(CopyEntry(entry));
                    }
                return result;
            }
        }
        finally { _gate.Release(); }
        return await MatchedChildrenAsync(collection, schema, name, ct).ConfigureAwait(false);
    }

    /// <summary>Resolve one metadata page's immutable resource locators in one query.</summary>
    public async Task<IReadOnlyDictionary<long, SnapshotResourceSummary>> ResourceSummariesAsync(
        IReadOnlyList<HistoricalSnapshotEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 1000) throw new ArgumentOutOfRangeException(nameof(entries), "Resolve at most 1000 selected entries in one metadata batch.");
        var budget = new SnapshotMetadataBudget();
        foreach (var entry in entries)
        {
            if (entry.ResourceKey == null || entry.RevisionKey == null) throw new ArgumentException("Select resource entries.", nameof(entries));
            budget.Add(512 + 4L * (entry.SchemaName.Length + (long)entry.Name.Length));
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            if (entries.Count == 0) return new Dictionary<long, SnapshotResourceSummary>();
            var requested = entries.GroupBy(entry => entry.EntryKey).ToDictionary(group => group.Key, group => group.ToArray());
            var resolved = new Dictionary<long, SnapshotResourceSummary>();
            await using var command = Command(_connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(e.SchemaName)+DATALENGTH(e.Name),
                    e.EntryKey,e.Collection,e.OrderKey,e.ResourceKey,e.RevisionKey,e.ChildKey,e.SchemaName,e.Name,e.ObjectKind,
                    e.HasFullData,e.FullDataRowCount,e.FullDataImportedAtUtc,e.TableSource,r.PublicId,r.RowVersion
                FROM OPENJSON(@Entries) WITH (EntryKey bigint '$') requested
                JOIN #HistoricalEntry e ON e.EntryKey=requested.EntryKey
                JOIN surf.SnapshotResource r ON r.ResourceKey=e.ResourceKey AND r.SnapshotKey=@Snapshot;
                """, Text("@Entries", JsonSerializer.Serialize(requested.Keys)), Key("@Snapshot", Header.SnapshotKey));
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    RowBytes(reader, command); var stored = ReadEntry(reader);
                    if (!requested.TryGetValue(stored.EntryKey, out var wanted) || wanted.Any(entry => !SameEntry(stored, entry)))
                        throw new ArgumentException("An entry belongs to another historical projection.", nameof(entries));
                    var entry = wanted[0]; var kind = EntryResourceKind(entry);
                    resolved.Add(entry.EntryKey, new(entry.ResourceKey!.Value, reader.GetGuid(14), Header.SnapshotKey, kind,
                        entry.SchemaName, entry.Name, kind.HasValue ? SnapshotIdentity.LegacyResourceKey(kind.Value, entry.SchemaName, entry.Name) : "",
                        entry.RevisionKey!.Value, entry.SortOrdinal, (byte[])reader.GetValue(15), entry.TableSource));
                }
            if (resolved.Count != requested.Count) throw new KeyNotFoundException("Selected resource is absent from this historical projection.");
            await ValidateHead(ct).ConfigureAwait(false);
            return resolved;
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyDictionary<long, ObjectSummary>> ObjectSummariesAsync(
        IReadOnlyList<HistoricalSnapshotEntry> entries, CancellationToken ct)
    {
        if (entries.Any(entry => entry.Collection != HistoricalCollection.Objects)) throw new ArgumentException("Select projected object entries.", nameof(entries));
        var resources = await ResourceSummariesAsync(entries, ct).ConfigureAwait(false);
        var result = new Dictionary<long, ObjectSummary>(); var budget = new SnapshotMetadataBudget();
        foreach (var entry in entries) budget.Add(512 + 4L * (entry.SchemaName.Length + (long)entry.Name.Length));
        if (entries.Count == 0) return result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            await using var command = Command(_connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(o.TypeDescription)+DATALENGTH(o.ParentSchemaName)+DATALENGTH(o.ParentObjectName),
                    e.EntryKey,o.ObjectKind,o.TypeDescription,o.ParentSchemaName,o.ParentObjectName
                FROM OPENJSON(@Entries) WITH (EntryKey bigint '$') requested
                JOIN #HistoricalEntry e ON e.EntryKey=requested.EntryKey
                JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=e.RevisionKey;
                """, Text("@Entries", JsonSerializer.Serialize(resources.Keys)));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    budget.Add(reader.GetInt64(0), command); long key = reader.GetInt64(1);
                    result.Add(key, new(resources[key], (SqlDatabaseObjectKind)reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                }
            if (result.Count != resources.Count) throw new KeyNotFoundException("Projected object revision missing.");
            await ValidateHead(ct).ConfigureAwait(false); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<TableMetadataDetails> ReadSelectedTableMetadataAsync(HistoricalSnapshotEntry requested, CancellationToken ct)
    {
        if (requested.Collection != HistoricalCollection.Tables || requested.Table == null || requested.RevisionKey == null)
            throw new ArgumentException("A projected table entry is required.", nameof(requested));
        var table = await FindEntryAsync(requested.EntryKey, ct).ConfigureAwait(false);
        if (table == null || !SameEntry(table, requested)) throw new ArgumentException("Table belongs to another projection.", nameof(requested));
        var budget = new SnapshotMetadataBudget();
        budget.Add(256 + 2L * (table.SchemaName.Length + (long)table.Name.Length));
        var columns = await MatchedChildrenAsync(HistoricalCollection.Columns, table.SchemaName, table.Name, ct).ConfigureAwait(false);
        var keys = await MatchedChildrenAsync(HistoricalCollection.PrimaryKeys, table.SchemaName, table.Name, ct).ConfigureAwait(false);
        var resultColumns = new List<OrderedColumn>(); var resultKeys = new List<OrderedPrimaryKey>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); await ValidateHead(ct).ConfigureAwait(false);
            foreach (var batch in columns.Chunk(128))
            {
                await using var command = Command(_connection, null, """
                    SELECT CONVERT(bigint,256)+DATALENGTH(c.SchemaName)+DATALENGTH(c.TableName)+DATALENGTH(c.ColumnName)+DATALENGTH(c.DataType),
                        c.ColumnRevisionKey,j.Position,c.SchemaName,c.TableName,c.ColumnName,c.DataType,c.MaxLength,c.NumericPrecision,c.NumericScale,c.IsNullable,c.IsIdentity,c.SourceOrdinal
                    FROM OPENJSON(@Children) WITH (Child bigint '$.Child',Position bigint '$.Position') j
                    JOIN surf.TableColumnRevision c ON c.ColumnRevisionKey=j.Child ORDER BY j.Position;
                    """, Text("@Children", ChildJson(batch)));
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
                int count = 0;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    budget.Add(reader.GetInt64(0), command); count++;
                    resultColumns.Add(new(reader.GetInt64(1), reader.GetInt64(2), new() { SchemaName = reader.GetString(3), TableName = reader.GetString(4),
                        ColumnName = reader.GetString(5), DataType = reader.GetString(6), MaxLength = reader.GetInt32(7), NumericPrecision = reader.GetByte(8),
                        NumericScale = reader.GetInt32(9), IsNullable = reader.GetBoolean(10), IsIdentity = reader.GetBoolean(11), Ordinal = reader.GetInt32(12) }));
                }
                if (count != batch.Length) throw new KeyNotFoundException("Projected column missing.");
            }
            foreach (var batch in keys.Chunk(128))
            {
                await using var command = Command(_connection, null, """
                    SELECT CONVERT(bigint,256)+DATALENGTH(k.SchemaName)+DATALENGTH(k.TableName)+DATALENGTH(k.ConstraintName)+DATALENGTH(k.ColumnName),
                        k.KeyColumnKey,j.Position,k.SchemaName,k.TableName,k.ConstraintName,k.ColumnName,k.KeyOrdinal
                    FROM OPENJSON(@Children) WITH (Child bigint '$.Child',Position bigint '$.Position') j
                    JOIN surf.PrimaryKeyColumn k ON k.KeyColumnKey=j.Child ORDER BY j.Position;
                    """, Text("@Children", ChildJson(batch)));
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
                int count = 0;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    budget.Add(reader.GetInt64(0), command); count++;
                    resultKeys.Add(new(reader.GetInt64(1), reader.GetInt64(2), new() { SchemaName = reader.GetString(3), TableName = reader.GetString(4),
                        ConstraintName = reader.GetString(5), ColumnName = reader.GetString(6), KeyOrdinal = reader.GetInt32(7) }));
                }
                if (count != batch.Length) throw new KeyNotFoundException("Projected primary key missing.");
            }
            await ValidateHead(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        return new(table.RevisionKey!.Value, CopyTable(table.Table!), resultColumns, resultKeys);
    }
    private static string ChildJson(IEnumerable<HistoricalSnapshotEntry> entries) =>
        JsonSerializer.Serialize(entries.Select(entry => new { Child = entry.ChildKey, Position = entry.SortOrdinal }));
    private async Task<IReadOnlyList<HistoricalSnapshotEntry>> MatchedChildrenAsync(HistoricalCollection collection, string schema, string name, CancellationToken ct)
    {
        var names = _cachedNames;
        if (names != null) return names.GetValueOrDefault(BufferedIdentity.From(collection, schema, name)) ?? [];
        var result = new List<HistoricalSnapshotEntry>(); var budget = new SnapshotMetadataBudget();
        await foreach (var entry in StreamAsync(collection, ct).ConfigureAwait(false))
            if (HistoricalSnapshotReplay.Matches(entry, collection, schema, name))
            { budget.Add(256 + 2L * (entry.SchemaName.Length + (long)entry.Name.Length)); result.Add(entry); }
        return result;
    }
}

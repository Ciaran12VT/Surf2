using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

/// <summary>Disposable, SQL-temp-backed selected-version identity plan. No definitions or data rows are staged.</summary>
public sealed class HistoricalSnapshotContext : IAsyncDisposable, IHistoricalSnapshotState
{
    private readonly SqlConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _headToken;
    private bool _building = true;
    private bool _disposed;
    public SnapshotSummary Header { get; }
    public long VersionKey { get; }
    public long HistoryKey { get; }
    public Guid Epoch { get; }

    private HistoricalSnapshotContext(SqlConnection connection, Guid epoch, SnapshotSummary header, long versionKey, long historyKey)
    { _connection = connection; Epoch = epoch; Header = header; VersionKey = versionKey; HistoryKey = historyKey; _headToken=header.RowVersion.ToArray(); }

    internal static async Task<HistoricalSnapshotContext> BuildAsync(SqlConnection connection, Guid epoch, long snapshotKey, long versionKey, CancellationToken ct)
    {
        HistoricalSnapshotContext? context = null;
        try
        {
            SnapshotSummary header;
            int targetNumber;
            long historyKey;
            await using (var command = Command(connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(s.OriginalSnapshotId)+DATALENGTH(s.DisplayName)+DATALENGTH(s.DatabaseName),
                    s.SnapshotKey, s.PublicId, s.OriginalSnapshotId, s.DisplayName, s.DatabaseName,
                    v.CreatedAtUtc, s.SortOrdinal, s.RowVersion, v.VersionNumber, v.HistoryKey
                FROM surf.DatabaseSnapshot s JOIN surf.SnapshotVersion v ON v.SnapshotKey=s.SnapshotKey
                WHERE s.SnapshotKey=@Snapshot AND v.VersionKey=@Version AND s.IsPublished=1;
                """, Key("@Snapshot", snapshotKey), Key("@Version", versionKey)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Published snapshot/version not found.");
                SnapshotReadGuard.RequireBytes(reader.GetInt64(0), RelationalSnapshotStore.MaximumMetadataPageBytes, command);
                header = new(reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7), versionKey, (byte[])reader.GetValue(8));
                targetNumber = reader.GetInt32(9);
                historyKey = reader.GetInt64(10);
            }
            context = new(connection, epoch, header, versionKey, historyKey);
            // Parameters make SqlClient use an RPC scope, whose local temp tables
            // disappear on return. Create both work tables in a session-level batch.
            await context.Execute(CreateWorkTablesSql, ct).ConfigureAwait(false);
            await context.Execute(SeedSql, ct, Key("@Snapshot", snapshotKey)).ConfigureAwait(false);
            HistoricalChangePosition? after = null;
            while (true)
            {
                var batch = await context.ReadChanges(targetNumber, after, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var item in batch)
                    await HistoricalSnapshotReplay.ApplyAsync(context, item.Change, ct).ConfigureAwait(false);
                after = batch[^1].Position;
            }
            context._building = false;
            await context.ValidateHead(ct).ConfigureAwait(false);
            return context;
        }
        catch
        {
            if (context != null) await context.DisposeAsync().ConfigureAwait(false);
            else await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<SnapshotPage<HistoricalSnapshotEntry>> ReadPageAsync(HistoricalCollection collection, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => ReadPage(collection, null, false, pageSize, cursor, ct);

    public async IAsyncEnumerable<HistoricalSnapshotEntry> StreamAsync(HistoricalCollection collection,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SnapshotCursor? cursor = null;
        do
        {
            var page = await ReadPageAsync(collection, 128, cursor, ct).ConfigureAwait(false);
            foreach (var entry in page.Items) { ct.ThrowIfCancellationRequested(); yield return entry; }
            cursor = page.Next;
        } while (cursor != null);
    }

    internal Task<SnapshotPage<HistoricalSnapshotEntry>> ReadResources(HistoricalCollection? collection,
        DatabaseVersionedResourceKind? kind, int pageSize, SnapshotCursor? cursor, CancellationToken ct) =>
        ReadPage(collection, kind, true, pageSize, cursor, ct);

    private async Task<SnapshotPage<HistoricalSnapshotEntry>> ReadPage(HistoricalCollection? collection, DatabaseVersionedResourceKind? kind,
        bool resources, int pageSize, SnapshotCursor? cursor, CancellationToken ct)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        string query = $"historical:{HistoryKey}:{collection}:{resources}";
        if (cursor != null && (cursor.OwnerKey != Header.SnapshotKey || cursor.VersionKey != VersionKey || cursor.Epoch != Epoch ||
            cursor.Query != query || cursor.Kind != (int?)kind))
            throw new ArgumentException("Historical cursor belongs to another projection or owner.");
        if (cursor != null && !cursor.HeadToken.AsSpan().SequenceEqual(_headToken)) throw new SnapshotConcurrencyException();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ValidateHead(ct).ConfigureAwait(false);
            await using var command = Command(_connection, null, """
                WITH entries AS (
                    SELECT e.*, ROW_NUMBER() OVER(ORDER BY e.Collection, e.OrderKey, e.EntryKey)-1 Position
                    FROM #HistoricalEntry e WHERE (@Collection IS NULL OR e.Collection=@Collection)
                        AND (@Resources=0 OR e.Collection IN (0,1,4)))
                SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(Name),
                    EntryKey, Collection, Position, ResourceKey, RevisionKey, ChildKey, SchemaName, Name,
                    ObjectKind, HasFullData, FullDataRowCount, FullDataImportedAtUtc, TableSource
                FROM entries WHERE Position>@After
                    AND (@Kind IS NULL OR Collection=1 AND @Kind=4 OR Collection=4 AND @Kind=5 OR Collection=0 AND ObjectKind=@Kind+1)
                ORDER BY Position;
                """, Int("@Collection", (int?)collection), Bit("@Resources", resources), Int("@Kind", (int?)kind),
                Int("@Take", pageSize + 1), Key("@After", cursor?.SortOrdinal ?? -1));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            var result = new List<HistoricalSnapshotEntry>();
            long bytes = 0; bool more = false;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (result.Count == pageSize) { more = true; SnapshotReadGuard.StopReading(command); break; }
                long size = RowBytes(reader, command);
                if (size > RelationalSnapshotStore.MaximumMetadataPageBytes - bytes) { more = true; SnapshotReadGuard.StopReading(command); break; }
                bytes += size; result.Add(ReadEntry(reader));
            }
            var next = more ? new SnapshotCursor(Header.SnapshotKey, VersionKey, _headToken.ToArray(), result[^1].SortOrdinal,
                result[^1].SortOrdinal, (int?)kind, Epoch, query) : null;
            return new(result, next, Epoch);
        }
        finally { _gate.Release(); }
    }

    // Resolves original logical keys by name/kind as RemoveAll does, not just by
    // the source revision's resource key. This includes tables resurrected by data.
    internal async Task<HistoricalSnapshotEntry?> FindResource(long resourceKey, CancellationToken ct)
    {
        string schema, name; DatabaseVersionedResourceKind? kind;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ValidateHead(ct).ConfigureAwait(false);
            await using var command = Command(_connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(ObjectName),
                    SchemaName, ObjectName, Kind FROM surf.SnapshotResource WHERE ResourceKey=@Resource AND SnapshotKey=@Snapshot;
                """, Key("@Resource", resourceKey), Key("@Snapshot", Header.SnapshotKey));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            SnapshotReadGuard.RequireBytes(reader.GetInt64(0), RelationalSnapshotStore.MaximumMetadataPageBytes, command);
            schema = reader.GetString(1); name = reader.GetString(2); kind = reader.IsDBNull(3) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(3);
        }
        finally { _gate.Release(); }
        var collection = kind == DatabaseVersionedResourceKind.TableMetadata ? HistoricalCollection.Tables :
            kind == DatabaseVersionedResourceKind.TableData ? HistoricalCollection.TableDataSets : HistoricalCollection.Objects;
        await foreach (var entry in StreamAsync(collection, ct).ConfigureAwait(false))
            if (string.Equals(entry.SchemaName, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (collection != HistoricalCollection.Objects || SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown) == kind)) return entry;
        return null;
    }

    internal async Task<SnapshotResourceSummary> ResourceSummary(HistoricalSnapshotEntry entry, CancellationToken ct, SnapshotMetadataBudget? budget = null)
    {
        // Includes the generated legacy key before its string is allocated.
        long bytes = 512 + 4L * (entry.SchemaName.Length + (long)entry.Name.Length);
        SnapshotReadGuard.RequireBytes(bytes, RelationalSnapshotStore.MaximumMetadataPageBytes);
        budget?.Add(bytes);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ValidateHead(ct).ConfigureAwait(false);
            await using var command = Command(_connection, null, "SELECT PublicId, RowVersion FROM surf.SnapshotResource WHERE ResourceKey=@Resource;",
                Key("@Resource", entry.ResourceKey));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Projection source resource missing.");
            DatabaseVersionedResourceKind? kind = entry.Collection switch {
                HistoricalCollection.Tables => DatabaseVersionedResourceKind.TableMetadata,
                HistoricalCollection.TableDataSets => DatabaseVersionedResourceKind.TableData,
                _ => SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown) };
            return new(entry.ResourceKey!.Value, reader.GetGuid(0), Header.SnapshotKey, kind, entry.SchemaName, entry.Name,
                kind.HasValue ? SnapshotIdentity.LegacyResourceKey(kind.Value, entry.SchemaName, entry.Name) : "",
                entry.RevisionKey!.Value, entry.SortOrdinal, (byte[])reader.GetValue(1), entry.TableSource);
        }
        finally { _gate.Release(); }
    }

    internal async Task<ObjectSummary> ObjectSummary(HistoricalSnapshotEntry entry, CancellationToken ct, SnapshotMetadataBudget? budget = null)
    {
        budget ??= new SnapshotMetadataBudget();
        var resource = await ResourceSummary(entry, ct, budget).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = Command(_connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(TypeDescription)+DATALENGTH(ParentSchemaName)+DATALENGTH(ParentObjectName),
                    ObjectKind, TypeDescription, ParentSchemaName, ParentObjectName FROM surf.DatabaseObjectRevision WHERE RevisionKey=@Revision;
                """, Key("@Revision", entry.RevisionKey));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Projected object revision missing.");
            budget.Add(reader.GetInt64(0), command);
            return new(resource, (SqlDatabaseObjectKind)reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }
        finally { _gate.Release(); }
    }

    public async Task<TableMetadataDetails> ReadTableMetadataAsync(HistoricalSnapshotEntry table, CancellationToken ct = default)
    {
        if (table.Collection != HistoricalCollection.Tables || table.Table == null || table.RevisionKey == null)
            throw new ArgumentException("A projected table entry is required.", nameof(table));
        var budget = new SnapshotMetadataBudget();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ValidateHead(ct).ConfigureAwait(false);
            await using var command=Command(_connection,null,"""
                SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(Name),
                    EntryKey,Collection,OrderKey,ResourceKey,RevisionKey,ChildKey,SchemaName,Name,ObjectKind,
                    HasFullData,FullDataRowCount,FullDataImportedAtUtc,TableSource FROM #HistoricalEntry
                WHERE EntryKey=@Entry AND Collection=1;
                """,Key("@Entry",table.EntryKey));
            using var cancel=RelationalSession.CancelCommand(command,ct);
            await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct).ConfigureAwait(false);
            if(!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Table is not in this projection.");
            budget.Add(reader.GetInt64(0), command);
            var stored=ReadEntry(reader);
            if(stored.RevisionKey!=table.RevisionKey || !string.Equals(stored.SchemaName,table.SchemaName,StringComparison.Ordinal) ||
                !string.Equals(stored.Name,table.Name,StringComparison.Ordinal)) throw new ArgumentException("Table belongs to another projection.",nameof(table));
            table=stored;
        }
        finally { _gate.Release(); }
        SqlTable effectiveTable = table.Table ?? throw new InvalidOperationException("Projected table scalars are missing.");
        long effectiveRevision = table.RevisionKey ?? throw new InvalidOperationException("Projected table source revision is missing.");
        // Fetch only this table's scalar children. Selection is against the replayed
        // root collections, including orphan/mis-owned children and duplicate names.
        var columns = new List<OrderedColumn>(); var keys = new List<OrderedPrimaryKey>();
        await foreach (var child in StreamAsync(HistoricalCollection.Columns, ct).ConfigureAwait(false))
            if (HistoricalSnapshotReplay.Matches(child, HistoricalCollection.Columns, table.SchemaName, table.Name))
                columns.Add(await ReadColumn(child, budget, ct).ConfigureAwait(false));
        await foreach (var child in StreamAsync(HistoricalCollection.PrimaryKeys, ct).ConfigureAwait(false))
            if (HistoricalSnapshotReplay.Matches(child, HistoricalCollection.PrimaryKeys, table.SchemaName, table.Name))
                keys.Add(await ReadKey(child, budget, ct).ConfigureAwait(false));
        return new(effectiveRevision, CopyTable(effectiveTable), columns, keys);
    }

    private async Task<OrderedColumn> ReadColumn(HistoricalSnapshotEntry child, SnapshotMetadataBudget budget, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ValidateHead(ct).ConfigureAwait(false);
        await using var command = Command(_connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ColumnName)+DATALENGTH(DataType),
                SchemaName, TableName, ColumnName, DataType, MaxLength, NumericPrecision, NumericScale, IsNullable, IsIdentity, SourceOrdinal
            FROM surf.TableColumnRevision WHERE ColumnRevisionKey=@Child;
            """, Key("@Child", child.ChildKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Projected column missing.");
        budget.Add(reader.GetInt64(0), command);
        return new(child.ChildKey!.Value, child.SortOrdinal, new() { SchemaName=reader.GetString(1), TableName=reader.GetString(2),
            ColumnName=reader.GetString(3), DataType=reader.GetString(4), MaxLength=reader.GetInt32(5), NumericPrecision=reader.GetByte(6),
            NumericScale=reader.GetInt32(7), IsNullable=reader.GetBoolean(8), IsIdentity=reader.GetBoolean(9), Ordinal=reader.GetInt32(10) });
        }
        finally { _gate.Release(); }
    }
    private async Task<OrderedPrimaryKey> ReadKey(HistoricalSnapshotEntry child, SnapshotMetadataBudget budget, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ValidateHead(ct).ConfigureAwait(false);
        await using var command = Command(_connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ConstraintName)+DATALENGTH(ColumnName),
                SchemaName, TableName, ConstraintName, ColumnName, KeyOrdinal FROM surf.PrimaryKeyColumn WHERE KeyColumnKey=@Child;
            """, Key("@Child", child.ChildKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Projected key missing.");
        budget.Add(reader.GetInt64(0), command);
        return new(child.ChildKey!.Value, child.SortOrdinal, new() { SchemaName=reader.GetString(1), TableName=reader.GetString(2),
            ConstraintName=reader.GetString(3), ColumnName=reader.GetString(4), KeyOrdinal=reader.GetInt32(5) });
        }
        finally { _gate.Release(); }
    }

    private async Task<List<HistoricalSnapshotEntry>> Scan(HistoricalCollection collection, long order, long key, CancellationToken ct)
    {
        await using var command = Command(_connection, null, """
            SELECT TOP (128) CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(Name),
                EntryKey, Collection, OrderKey, ResourceKey, RevisionKey, ChildKey, SchemaName, Name,
                ObjectKind, HasFullData, FullDataRowCount, FullDataImportedAtUtc, TableSource
            FROM #HistoricalEntry WHERE Collection=@Collection AND (OrderKey>@Order OR OrderKey=@Order AND EntryKey>@Key)
            ORDER BY OrderKey, EntryKey;
            """, Int("@Collection", (int)collection), Key("@Order", order), Key("@Key", key));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var entries = new List<HistoricalSnapshotEntry>(); long bytes = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long size = RowBytes(reader, command);
            if (size > RelationalSnapshotStore.MaximumMetadataPageBytes - bytes) { SnapshotReadGuard.StopReading(command); break; }
            bytes += size; entries.Add(ReadEntry(reader));
        }
        return entries;
    }

    async Task IHistoricalSnapshotState.RemoveAsync(HistoricalCollection collection, string schema, string name, SqlDatabaseObjectKind? objectKind, CancellationToken ct)
    {
        Building();
        await Execute("TRUNCATE TABLE #HistoricalMatch;", ct).ConfigureAwait(false);
        long order=-1, key=0;
        while (true)
        {
            var batch = await Scan(collection, order, key, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;
            var matches = batch.Where(e => HistoricalSnapshotReplay.Matches(e, collection, schema, name, objectKind)).ToArray();
            if (matches.Length != 0)
            {
                var parameters = matches.Select((e,i) => Key($"@K{i}", e.EntryKey)).ToArray();
                string values = string.Join(",", matches.Select((_,i) => $"(@K{i})"));
                await Execute("INSERT #HistoricalMatch(EntryKey) VALUES " + values + ";", ct, parameters).ConfigureAwait(false);
            }
            order=batch[^1].SortOrdinal; key=batch[^1].EntryKey;
        }
        await Execute("DELETE e FROM #HistoricalEntry e JOIN #HistoricalMatch m ON m.EntryKey=e.EntryKey;", ct).ConfigureAwait(false);
    }

    async Task<HistoricalSnapshotEntry> IHistoricalSnapshotState.AppendAsync(HistoricalSnapshotEntry entry, CancellationToken ct)
    {
        Building();
        long key = Convert.ToInt64(await ScalarAsync(_connection, null, """
            INSERT #HistoricalEntry(Collection, OrderKey, ResourceKey, RevisionKey, ChildKey, SchemaName, Name, ObjectKind,
                HasFullData, FullDataRowCount, FullDataImportedAtUtc, TableSource)
            OUTPUT INSERTED.EntryKey
            SELECT @Collection, COALESCE(MAX(OrderKey),-1)+1, @Resource, @Revision, @Child, @Schema, @Name, @Kind, @Full, @Count, @Imported, @Source
            FROM #HistoricalEntry WHERE Collection=@Collection;
            """, ct, Int("@Collection", (int)entry.Collection), Key("@Resource", entry.ResourceKey), Key("@Revision", entry.RevisionKey),
            Key("@Child", entry.ChildKey), Text("@Schema", entry.SchemaName), Text("@Name", entry.Name), Int("@Kind", (int?)entry.ObjectKind),
            RelationalSession.Parameter("@Full", SqlDbType.Bit, entry.Table?.HasFullData), Key("@Count", entry.Table?.FullDataRowCount),
            Date("@Imported", entry.Table?.FullDataImportedAtUtc), Int("@Source", (int)entry.TableSource)).ConfigureAwait(false));
        return entry with { EntryKey=key };
    }

    async Task IHistoricalSnapshotState.AppendChildrenAsync(long revisionKey, HistoricalCollection collection, CancellationToken ct)
    {
        Building();
        string sql = collection == HistoricalCollection.Columns ? """
            INSERT #HistoricalEntry(Collection, OrderKey, ChildKey, RevisionKey, SchemaName, Name)
            SELECT 2, @Base+ROW_NUMBER() OVER(ORDER BY SortOrdinal, ColumnRevisionKey), ColumnRevisionKey, @Revision, SchemaName, TableName
            FROM surf.TableColumnRevision WHERE TableMetadataRevisionKey=@Revision;
            """ : """
            INSERT #HistoricalEntry(Collection, OrderKey, ChildKey, RevisionKey, SchemaName, Name)
            SELECT 3, @Base+ROW_NUMBER() OVER(ORDER BY SortOrdinal, KeyColumnKey), KeyColumnKey, @Revision, SchemaName, TableName
            FROM surf.PrimaryKeyColumn WHERE TableMetadataRevisionKey=@Revision;
            """;
        object? baseOrder = await ScalarAsync(_connection, null, "SELECT COALESCE(MAX(OrderKey),-1) FROM #HistoricalEntry WHERE Collection=@Collection;",
            ct, Int("@Collection", (int)collection)).ConfigureAwait(false);
        await Execute(sql, ct, Key("@Base", Convert.ToInt64(baseOrder)), Key("@Revision", revisionKey)).ConfigureAwait(false);
    }

    async Task<HistoricalSnapshotEntry?> IHistoricalSnapshotState.FirstTableAsync(string schema, string name, CancellationToken ct)
    {
        Building();
        long order=-1, key=0;
        while (true)
        {
            var batch = await Scan(HistoricalCollection.Tables, order, key, ct).ConfigureAwait(false);
            if (batch.Count == 0) return null;
            var first = batch.FirstOrDefault(e => HistoricalSnapshotReplay.Matches(e, HistoricalCollection.Tables, schema, name));
            if (first != null) return first;
            order=batch[^1].SortOrdinal; key=batch[^1].EntryKey;
        }
    }
    Task IHistoricalSnapshotState.UpdateTableAsync(long entryKey, bool hasData, long count, DateTimeOffset? importedAt, CancellationToken ct)
    {
        Building();
        return Execute("UPDATE #HistoricalEntry SET HasFullData=@Full, FullDataRowCount=@Count, FullDataImportedAtUtc=@Imported WHERE EntryKey=@Entry;",
            ct, Key("@Entry", entryKey), Bit("@Full", hasData), Key("@Count", count), Date("@Imported", importedAt));
    }
    async Task IHistoricalSnapshotState.EnsureSelectionAsync(string name, CancellationToken ct)
    {
        Building();
        long order=-1, key=0;
        while (true)
        {
            var batch = await Scan(HistoricalCollection.FullDataTableNames, order, key, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;
            if (batch.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            order=batch[^1].SortOrdinal; key=batch[^1].EntryKey;
        }
        await ((IHistoricalSnapshotState)this).AppendAsync(new(0, HistoricalCollection.FullDataTableNames, 0, null, null, null, "", name), ct).ConfigureAwait(false);
    }

    private async Task Execute(string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var command = Command(_connection, null, sql, parameters);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    private async Task ValidateHead(CancellationToken ct)
    {
        object? token = await ScalarAsync(_connection, null, "SELECT RowVersion FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Snapshot AND IsPublished=1;",
            ct, Key("@Snapshot", Header.SnapshotKey)).ConfigureAwait(false);
        if (token is not byte[] actual || !actual.AsSpan().SequenceEqual(_headToken)) throw new SnapshotConcurrencyException();
    }
    private void Building() { if (!_building) throw new InvalidOperationException("The historical projection is immutable after construction."); }
    internal static SqlTable CopyTable(SqlTable table) => new() { SchemaName=table.SchemaName, TableName=table.TableName,
        HasFullData=table.HasFullData, FullDataRowCount=table.FullDataRowCount, FullDataImportedAtUtc=table.FullDataImportedAtUtc };
    private static long RowBytes(SqlDataReader reader, SqlCommand command)
    {
        long bytes=reader.GetInt64(0);
        SnapshotReadGuard.RequireBytes(bytes, RelationalSnapshotStore.MaximumMetadataPageBytes, command);
        return bytes;
    }
    private static HistoricalSnapshotEntry ReadEntry(SqlDataReader r)
    {
        long key=r.GetInt64(1);
        var collection=(HistoricalCollection)r.GetInt32(2);
        long order=r.GetInt64(3);
        long? resource=NullableKey(r,4), revision=NullableKey(r,5), child=NullableKey(r,6);
        string schema=r.GetString(7), name=r.GetString(8);
        SqlDatabaseObjectKind? kind=r.IsDBNull(9) ? null : (SqlDatabaseObjectKind)r.GetInt32(9);
        SqlTable? table=collection==HistoricalCollection.Tables ? new() { SchemaName=schema,TableName=name,
            HasFullData=r.GetBoolean(10),FullDataRowCount=r.GetInt64(11),FullDataImportedAtUtc=NullableDate(r,12) } : null;
        return new(key,collection,order,resource,revision,child,schema,name,kind,table,(TableRevisionSource)r.GetInt32(13));
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed=true;
                try
                {
                    if (_connection.State==ConnectionState.Open)
                        await Execute("DROP TABLE IF EXISTS #HistoricalMatch; DROP TABLE IF EXISTS #HistoricalEntry;",CancellationToken.None).ConfigureAwait(false);
                }
                catch(Exception ex) when(ex is SqlException or InvalidOperationException)
                { /* Broken connections are still disposed; pool reset also drops session-local work tables. */ }
                finally { await _connection.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally { _gate.Release(); }
    }

    private const string CreateWorkTablesSql = """
        CREATE TABLE #HistoricalEntry(EntryKey bigint IDENTITY PRIMARY KEY, Collection int NOT NULL, OrderKey bigint NOT NULL,
            ResourceKey bigint NULL, RevisionKey bigint NULL, ChildKey bigint NULL, SchemaName nvarchar(max) NOT NULL, Name nvarchar(max) NOT NULL,
            ObjectKind int NULL, HasFullData bit NULL, FullDataRowCount bigint NULL, FullDataImportedAtUtc datetimeoffset(7) NULL,
            TableSource int NOT NULL DEFAULT 0);
        CREATE INDEX IX_HistoricalEntry_Order ON #HistoricalEntry(Collection, OrderKey, EntryKey);
        CREATE TABLE #HistoricalMatch(EntryKey bigint PRIMARY KEY);
        """;

    private const string SeedSql = """
        INSERT #HistoricalEntry(Collection,OrderKey,ResourceKey,RevisionKey,SchemaName,Name,ObjectKind)
            SELECT 0, ROW_NUMBER() OVER(ORDER BY r.CurrentSortOrdinal,r.ResourceKey)-1,r.ResourceKey,o.RevisionKey,o.SchemaName,o.ObjectName,o.ObjectKind
            FROM surf.SnapshotResource r JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=r.CurrentRevisionKey WHERE r.SnapshotKey=@Snapshot;
        INSERT #HistoricalEntry(Collection,OrderKey,ResourceKey,RevisionKey,SchemaName,Name,HasFullData,FullDataRowCount,FullDataImportedAtUtc)
            SELECT 1, ROW_NUMBER() OVER(ORDER BY r.CurrentSortOrdinal,r.ResourceKey)-1,r.ResourceKey,t.RevisionKey,t.SchemaName,t.TableName,t.HasFullData,t.FullDataRowCount,t.FullDataImportedAtUtc
            FROM surf.SnapshotResource r JOIN surf.TableMetadataRevision t ON t.RevisionKey=r.CurrentRevisionKey WHERE r.SnapshotKey=@Snapshot;
        INSERT #HistoricalEntry(Collection,OrderKey,ChildKey,RevisionKey,SchemaName,Name)
            SELECT 2, ROW_NUMBER() OVER(ORDER BY x.SortOrdinal,x.EntryKey)-1,c.ColumnRevisionKey,c.TableMetadataRevisionKey,c.SchemaName,c.TableName
            FROM surf.SnapshotCurrentColumn x JOIN surf.TableColumnRevision c ON c.ColumnRevisionKey=x.ColumnRevisionKey WHERE x.SnapshotKey=@Snapshot;
        INSERT #HistoricalEntry(Collection,OrderKey,ChildKey,RevisionKey,SchemaName,Name)
            SELECT 3, ROW_NUMBER() OVER(ORDER BY x.SortOrdinal,x.EntryKey)-1,k.KeyColumnKey,k.TableMetadataRevisionKey,k.SchemaName,k.TableName
            FROM surf.SnapshotCurrentPrimaryKey x JOIN surf.PrimaryKeyColumn k ON k.KeyColumnKey=x.KeyColumnKey WHERE x.SnapshotKey=@Snapshot;
        INSERT #HistoricalEntry(Collection,OrderKey,ResourceKey,RevisionKey,SchemaName,Name)
            SELECT 4, ROW_NUMBER() OVER(ORDER BY r.CurrentSortOrdinal,r.ResourceKey)-1,r.ResourceKey,d.RevisionKey,d.SchemaName,d.TableName
            FROM surf.SnapshotResource r JOIN surf.TableDataRevision d ON d.RevisionKey=r.CurrentRevisionKey WHERE r.SnapshotKey=@Snapshot;
        INSERT #HistoricalEntry(Collection,OrderKey,ChildKey,SchemaName,Name)
            SELECT 5, ROW_NUMBER() OVER(ORDER BY SortOrdinal,SelectionKey)-1,SelectionKey,N'',OriginalTableName
            FROM surf.FullDataTableSelection WHERE SnapshotKey=@Snapshot;
        """;

    private async Task<List<(HistoricalChangePosition Position, HistoricalRollbackEvent Change)>> ReadChanges(
        int target, HistoricalChangePosition? after, CancellationToken ct)
    {
        await using var command = Command(_connection, null, """
            SELECT TOP (128) CONVERT(bigint,256)+DATALENGTH(c.OriginalResourceKey)
                    +COALESCE(DATALENGTH(o.SchemaName),0)+COALESCE(DATALENGTH(o.ObjectName),0)
                    +COALESCE(DATALENGTH(t.SchemaName),0)+COALESCE(DATALENGTH(t.TableName),0)
                    +COALESCE(DATALENGTH(d.SchemaName),0)+COALESCE(DATALENGTH(d.TableName),0)
                    +COALESCE(DATALENGTH(dt.SchemaName),0)+COALESCE(DATALENGTH(dt.TableName),0),
                c.VersionNumber,v.SortOrdinal,v.VersionKey,c.SortOrdinal,c.ChangeKey,c.Kind,c.ChangeKind,c.OriginalResourceKey,c.ResourceKey,c.PreviousRevisionKey,
                o.SchemaName,o.ObjectName,o.ObjectKind,t.SchemaName,t.TableName,t.HasFullData,t.FullDataRowCount,t.FullDataImportedAtUtc,
                d.SchemaName,d.TableName,dt.SchemaName,dt.TableName,dt.HasFullData,dt.FullDataRowCount,dt.FullDataImportedAtUtc,
                ds.ReportedRowCount,ds.ActualRowCount,ds.ImportedAtUtc
            FROM surf.SnapshotChange c
            JOIN surf.SnapshotVersion v ON v.VersionKey=c.VersionKey AND v.HistoryKey=@History
            LEFT JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=c.PreviousRevisionKey
            LEFT JOIN surf.TableMetadataRevision t ON t.RevisionKey=c.PreviousRevisionKey
            LEFT JOIN surf.TableDataRevision d ON d.RevisionKey=c.PreviousRevisionKey
            LEFT JOIN surf.TableDataRevisionMetadata dt ON dt.RevisionKey=c.PreviousRevisionKey
            LEFT JOIN surf.DataSet ds ON ds.RevisionKey=c.PreviousRevisionKey AND ds.State='Ready'
            WHERE c.SnapshotKey=@Snapshot AND c.VersionNumber>@Target AND (@Version IS NULL OR c.VersionNumber<@Version
                OR c.VersionNumber=@Version AND (v.SortOrdinal>@VersionOrder OR v.SortOrdinal=@VersionOrder AND
                    (v.VersionKey>@VersionKey OR v.VersionKey=@VersionKey AND
                        (c.SortOrdinal>@Order OR c.SortOrdinal=@Order AND c.ChangeKey>@Key))))
            ORDER BY c.VersionNumber DESC,v.SortOrdinal,v.VersionKey,c.SortOrdinal,c.ChangeKey;
            """, Key("@Snapshot",Header.SnapshotKey), Key("@History",HistoryKey), Int("@Target",target),
            Int("@Version",after?.VersionNumber), Key("@VersionOrder",after?.VersionSortOrdinal), Key("@VersionKey",after?.VersionKey),
            Key("@Order",after?.ChangeSortOrdinal), Key("@Key",after?.ChangeKey));
        using var cancel = RelationalSession.CancelCommand(command,ct);
        await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct).ConfigureAwait(false);
        var result=new List<(HistoricalChangePosition,HistoricalRollbackEvent)>(); long bytes=0;
        HistoricalChangePosition? previousPosition = after;
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long size=RowBytes(reader,command);
            if(size>RelationalSnapshotStore.MaximumMetadataPageBytes-bytes) { SnapshotReadGuard.StopReading(command); break; }
            bytes+=size;
            var position = new HistoricalChangePosition(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
            if (previousPosition.HasValue && position.CompareTo(previousPosition.Value) <= 0)
                throw new InvalidOperationException("Historical changes are not in stable version/source order.");
            previousPosition = position;
            var kind=(DatabaseVersionedResourceKind)reader.GetInt32(6);
            var changeKind=(DatabaseSnapshotResourceChangeKind)reader.GetInt32(7);
            string originalKey=reader.GetString(8);
            long resource=reader.GetInt64(9);
            long? revision=NullableKey(reader,10);
            HistoricalPayloadHeader? previous=null;
            if(revision.HasValue)
            {
                if(kind==DatabaseVersionedResourceKind.TableMetadata && !reader.IsDBNull(14))
                {
                    string schema=reader.GetString(14),name=reader.GetString(15);
                    previous=new(resource,revision.Value,kind,schema,name,Table:new() { SchemaName=schema,TableName=name,
                        HasFullData=reader.GetBoolean(16),FullDataRowCount=reader.GetInt64(17),FullDataImportedAtUtc=NullableDate(reader,18) });
                }
                else if(kind==DatabaseVersionedResourceKind.TableData && !reader.IsDBNull(19))
                {
                    string schema=reader.GetString(19),name=reader.GetString(20);
                    SqlTable? table=reader.IsDBNull(21) ? null : new() { SchemaName=reader.GetString(21),TableName=reader.GetString(22),
                        HasFullData=reader.GetBoolean(23),FullDataRowCount=reader.GetInt64(24),FullDataImportedAtUtc=NullableDate(reader,25) };
                    if(reader.IsDBNull(26)) throw new InvalidOperationException("Historical data revision lacks a Ready dataset.");
                    previous=new(resource,revision.Value,kind,schema,name,Table:table,ReportedRowCount:reader.GetInt64(26),
                        ActualRowCount:reader.GetInt64(27),DataImportedAtUtc:reader.GetFieldValue<DateTimeOffset>(28));
                }
                else if(kind is not DatabaseVersionedResourceKind.TableMetadata and not DatabaseVersionedResourceKind.TableData && !reader.IsDBNull(11))
                    previous=new(resource,revision.Value,kind,reader.GetString(11),reader.GetString(12),(SqlDatabaseObjectKind)reader.GetInt32(13));
            }
            result.Add((position,new(kind,changeKind,originalKey,previous)));
        }
        return result;
    }
}

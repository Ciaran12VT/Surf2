using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

/// <summary>Purpose-specific metadata pages and one selected immutable revision. No library load/save interface.</summary>
public sealed partial class RelationalSnapshotStore
{
    public const long MaximumMetadataPageBytes = 8 * 1024 * 1024;
    private readonly RelationalSession session;
    private readonly RelationalContentStore content;
    private readonly SnapshotReadLimits _readLimits;

    public RelationalSnapshotStore(RelationalSession session, RelationalContentStore content) : this(session, content, new SnapshotReadLimits()) { }
    public RelationalSnapshotStore(RelationalSession session, RelationalContentStore content, SnapshotReadLimits readLimits)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        _readLimits = readLimits ?? throw new ArgumentNullException(nameof(readLimits));
        _readLimits.Validate();
    }
    public Guid Epoch => session.Epoch;

    public Task<SnapshotPage<SnapshotSummary>> ListSnapshotsAsync(long userKey = 1, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(userKey, null, "snapshots", null, pageSize, cursor,
        """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(OriginalSnapshotId)+DATALENGTH(DisplayName)+DATALENGTH(DatabaseName),
            SnapshotKey, PublicId, OriginalSnapshotId, DisplayName, DatabaseName,
            ImportedAtUtc, SortOrdinal, CurrentVersionKey, RowVersion
        FROM surf.DatabaseSnapshot WHERE UserKey=@Owner AND IsPublished=1
            AND (SortOrdinal>@AfterOrder OR SortOrdinal=@AfterOrder AND SnapshotKey>@AfterKey)
        ORDER BY SortOrdinal, SnapshotKey;
        """, r => new SnapshotSummary(r.GetInt64(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetFieldValue<DateTimeOffset>(6), r.GetInt64(7), NullableKey(r, 8), (byte[])r.GetValue(9)),
        x => (x.SortOrdinal, x.SnapshotKey), ct, catalogue: true);

    public async Task<SnapshotSummary?> GetSnapshotAsync(long snapshotKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(OriginalSnapshotId)+DATALENGTH(DisplayName)+DATALENGTH(DatabaseName),
                SnapshotKey, PublicId, OriginalSnapshotId, DisplayName, DatabaseName, ImportedAtUtc,
                SortOrdinal, CurrentVersionKey, RowVersion
            FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Snapshot AND IsPublished=1;
            """, Key("@Snapshot", snapshotKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        return new(reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7), NullableKey(reader, 8), (byte[])reader.GetValue(9));
    }

    public Task<SnapshotPage<SnapshotResourceSummary>> ListResourcesAsync(long snapshotKey,
        DatabaseVersionedResourceKind? kind = null, long? versionKey = null, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) =>
        versionKey.HasValue ? HistoricalResourcePage(snapshotKey, versionKey.Value, null, kind, pageSize, cursor, ct) :
        ResourcePage(snapshotKey, null, "resources", kind, pageSize, cursor,
            ResourceColumns, ResourceMetadataBytes, "", MapResource, x => (x.SortOrdinal, x.ResourceKey), ct);

    public Task<SnapshotPage<ObjectSummary>> ListObjectsAsync(long snapshotKey,
        DatabaseVersionedResourceKind? kind = null, long? versionKey = null, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default)
    {
        if (kind is DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (versionKey.HasValue) return HistoricalObjectPage(snapshotKey, versionKey.Value, kind, pageSize, cursor, ct);
        return ResourcePage(snapshotKey, versionKey, "objects", kind, pageSize, cursor,
            ResourceColumns + ", o.ObjectKind, o.TypeDescription, o.ParentSchemaName, o.ParentObjectName",
            ResourceMetadataBytes + "+DATALENGTH(o.TypeDescription)+DATALENGTH(o.ParentSchemaName)+DATALENGTH(o.ParentObjectName)",
            "AND o.RevisionKey IS NOT NULL", r => new ObjectSummary(MapResource(r), (SqlDatabaseObjectKind)r.GetInt32(12),
                r.GetString(13), r.GetString(14), r.GetString(15)), x => (x.Resource.SortOrdinal, x.Resource.ResourceKey), ct);
    }

    public Task<SnapshotPage<TableSummary>> ListTablesAsync(long snapshotKey, long? versionKey = null,
        int pageSize = 100, SnapshotCursor? cursor = null, CancellationToken ct = default) =>
        versionKey.HasValue ? HistoricalTablePage(snapshotKey, versionKey.Value, pageSize, cursor, ct) :
        ResourcePage(snapshotKey, null, "tables", DatabaseVersionedResourceKind.TableMetadata, pageSize, cursor,
            ResourceColumns + ", t.HasFullData, t.FullDataRowCount, t.FullDataImportedAtUtc", ResourceMetadataBytes, "AND t.RevisionKey IS NOT NULL",
            r => new TableSummary(MapResource(r), r.GetBoolean(12), r.GetInt64(13), NullableDate(r, 14)),
            x => (x.Resource.SortOrdinal, x.Resource.ResourceKey), ct);

    public async Task<SnapshotResourceSummary?> ResolveResourceAsync(long resourceKey, long? versionKey = null, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        if (versionKey.HasValue)
        {
            object? historicalOwner = await ScalarAsync(connection, null, "SELECT SnapshotKey FROM surf.SnapshotResource WHERE ResourceKey=@Resource;",
                ct, Key("@Resource", resourceKey)).ConfigureAwait(false);
            if (historicalOwner is not long ownerKey) return null;
            await using var historical = await OpenHistoricalSnapshotAsync(ownerKey, versionKey.Value, ct).ConfigureAwait(false);
            var entry = await historical.FindResource(resourceKey, ct).ConfigureAwait(false);
            return entry == null ? null : await historical.ResourceSummary(entry, ct).ConfigureAwait(false);
        }
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        object? owner = await ScalarAsync(connection, transaction, "SELECT SnapshotKey FROM surf.SnapshotResource WHERE ResourceKey=@Resource;",
            ct, Key("@Resource", resourceKey)).ConfigureAwait(false);
        if (owner is not long snapshotKey) return null;
        await Head(connection, transaction, snapshotKey, versionKey, false, ct).ConfigureAwait(false);
        await using var command = Command(connection, transaction, "SELECT " + ResourceMetadataBytes + ", " + ResourceColumns + " " + ResourceFrom +
            " WHERE r.ResourceKey=@Resource AND chosen.RevisionKey IS NOT NULL AND rr.IsSealed=1;",
            Key("@Owner", snapshotKey), Key("@Version", versionKey), Key("@Resource", resourceKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        return MapResource(reader);
    }

    // Exact legacy-identity lookup. Returns ALL matching logical records so an
    // ambiguous legacy key is never silently bound to the first resource.
    public async Task<IReadOnlyList<long>> FindResourceKeysAsync(long snapshotKey, string originalResourceKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT r.ResourceKey FROM surf.SnapshotResource r JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
            WHERE r.SnapshotKey=@Snapshot AND s.IsPublished=1 AND r.ResourceKeyHash=@Hash
              AND DATALENGTH(r.OriginalResourceKey)=DATALENGTH(@Original)
              AND r.OriginalResourceKey COLLATE Latin1_General_100_BIN2=@Original COLLATE Latin1_General_100_BIN2
            ORDER BY r.ResourceKey;
            """, Key("@Snapshot", snapshotKey), Hash("@Hash", SnapshotIdentity.Hash(originalResourceKey)), Text("@Original", originalResourceKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (result.Count == 1000) throw new InvalidOperationException("Legacy identity is excessively ambiguous; resolve by migration identity mapping.");
            result.Add(reader.GetInt64(0));
        }
        return result;
    }

    public async Task<SqlDatabaseObject> ReadObjectAsync(long revisionKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(o.SchemaName)+DATALENGTH(o.ObjectName)+DATALENGTH(o.TypeDescription)
                    +DATALENGTH(o.ParentSchemaName)+DATALENGTH(o.ParentObjectName), DATALENGTH(c.Text),
                o.SchemaName, o.ObjectName, o.ObjectKind, o.TypeDescription, o.ParentSchemaName, o.ParentObjectName,
                CONVERT(varbinary(max), c.Text)
            FROM surf.DatabaseObjectRevision o JOIN surf.TextContent c ON c.ContentKey=o.DefinitionContentKey
            JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=o.RevisionKey
            JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
            WHERE o.RevisionKey=@Revision AND rr.IsSealed=1 AND s.IsPublished=1;
            """, Key("@Revision", revisionKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Published object revision not found.");
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        long definitionBytes = reader.GetInt64(1);
        SnapshotReadGuard.RequireBytes(definitionBytes, _readLimits.SelectedDefinitionBytes, command);
        var value = new SqlDatabaseObject { SchemaName = reader.GetString(2), ObjectName = reader.GetString(3), Kind = (SqlDatabaseObjectKind)reader.GetInt32(4),
            TypeDescription = reader.GetString(5), ParentSchemaName = reader.GetString(6), ParentObjectName = reader.GetString(7) };
        await using var bytes = reader.GetStream(8);
        value.Definition = await SnapshotReadGuard.ReadUtf16Async(bytes, definitionBytes, _readLimits.SelectedDefinitionBytes, ct, command).ConfigureAwait(false);
        return value;
    }

    public async Task<TableMetadataDetails> ReadTableMetadataAsync(long revisionKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(t.SchemaName)+DATALENGTH(t.TableName),
                t.SchemaName, t.TableName, t.HasFullData, t.FullDataRowCount, t.FullDataImportedAtUtc
            FROM surf.TableMetadataRevision t JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=t.RevisionKey
            JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
            WHERE t.RevisionKey=@Revision AND rr.IsSealed=1 AND s.IsPublished=1;
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ColumnName)+DATALENGTH(DataType),
                ColumnRevisionKey, SortOrdinal, SchemaName, TableName, ColumnName, DataType, MaxLength,
                NumericPrecision, NumericScale, IsNullable, IsIdentity, SourceOrdinal
            FROM surf.TableColumnRevision WHERE TableMetadataRevisionKey=@Revision ORDER BY SortOrdinal, ColumnRevisionKey;
            SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ConstraintName)+DATALENGTH(ColumnName),
                KeyColumnKey, SortOrdinal, SchemaName, TableName, ConstraintName, ColumnName, KeyOrdinal
            FROM surf.PrimaryKeyColumn WHERE TableMetadataRevisionKey=@Revision ORDER BY SortOrdinal, KeyColumnKey;
            """, Key("@Revision", revisionKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Published table revision not found.");
        var budget = new SnapshotMetadataBudget();
        budget.Add(reader.GetInt64(0), command);
        SqlTable table = MapTable(reader);
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var columns = new List<OrderedColumn>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) { budget.Add(reader.GetInt64(0), command); columns.Add(MapColumn(reader)); }
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var keys = new List<OrderedPrimaryKey>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) { budget.Add(reader.GetInt64(0), command); keys.Add(MapKey(reader)); }
        return new(revisionKey, table, columns, keys);
    }

    public async Task<TableDataMetadata> ReadTableDataMetadataAsync(long revisionKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT CONVERT(bigint,256)+COALESCE(DATALENGTH(d.SchemaName),0)+COALESCE(DATALENGTH(d.TableName),0)
                    +DATALENGTH(data.SchemaName)+DATALENGTH(data.TableName),
                d.RevisionKey, d.SchemaName, d.TableName, d.HasFullData, d.FullDataRowCount, d.FullDataImportedAtUtc, data.SchemaName, data.TableName
            FROM surf.SnapshotResourceRevision rr JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
            JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
            LEFT JOIN surf.TableDataRevisionMetadata d ON d.RevisionKey=rr.RevisionKey
            JOIN surf.TableDataRevision data ON data.RevisionKey=rr.RevisionKey
            WHERE rr.RevisionKey=@Revision AND r.Kind=5 AND rr.IsSealed=1 AND s.IsPublished=1;
            """, Key("@Revision", revisionKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Published data revision not found.");
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        return new(revisionKey, reader.IsDBNull(1) ? null : MapTable(reader, 2), reader.GetString(7), reader.GetString(8));
    }

    public Task<SnapshotPage<OrderedColumn>> ListCurrentColumnsAsync(long snapshotKey, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, null, "columns", null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(c.SchemaName)+DATALENGTH(c.TableName)+DATALENGTH(c.ColumnName)+DATALENGTH(c.DataType),
            c.ColumnRevisionKey, x.SortOrdinal, c.SchemaName, c.TableName, c.ColumnName, c.DataType,
            c.MaxLength, c.NumericPrecision, c.NumericScale, c.IsNullable, c.IsIdentity, c.SourceOrdinal, x.EntryKey
        FROM surf.SnapshotCurrentColumn x JOIN surf.TableColumnRevision c ON c.ColumnRevisionKey=x.ColumnRevisionKey
        WHERE x.SnapshotKey=@Owner AND (x.SortOrdinal>@AfterOrder OR x.SortOrdinal=@AfterOrder AND x.EntryKey>@AfterKey)
        ORDER BY x.SortOrdinal, x.EntryKey;
        """, r => (Value: MapColumn(r), Entry: r.GetInt64(13)), x => (x.Value.SortOrdinal, x.Entry), ct).Project(x => x.Value);

    public Task<SnapshotPage<OrderedPrimaryKey>> ListCurrentPrimaryKeysAsync(long snapshotKey, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, null, "keys", null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(c.SchemaName)+DATALENGTH(c.TableName)+DATALENGTH(c.ConstraintName)+DATALENGTH(c.ColumnName),
            c.KeyColumnKey, x.SortOrdinal, c.SchemaName, c.TableName, c.ConstraintName, c.ColumnName, c.KeyOrdinal, x.EntryKey
        FROM surf.SnapshotCurrentPrimaryKey x JOIN surf.PrimaryKeyColumn c ON c.KeyColumnKey=x.KeyColumnKey
        WHERE x.SnapshotKey=@Owner AND (x.SortOrdinal>@AfterOrder OR x.SortOrdinal=@AfterOrder AND x.EntryKey>@AfterKey)
        ORDER BY x.SortOrdinal, x.EntryKey;
        """, r => (Value: MapKey(r), Entry: r.GetInt64(8)), x => (x.Value.SortOrdinal, x.Entry), ct).Project(x => x.Value);

    public Task<SnapshotPage<FullDataSelection>> ListFullDataSelectionsAsync(long snapshotKey, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, null, "selections", null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(OriginalTableName), SelectionKey, SortOrdinal, OriginalTableName FROM surf.FullDataTableSelection
        WHERE SnapshotKey=@Owner AND (SortOrdinal>@AfterOrder OR SortOrdinal=@AfterOrder AND SelectionKey>@AfterKey)
        ORDER BY SortOrdinal, SelectionKey;
        """, r => new FullDataSelection(r.GetInt64(1), r.GetInt64(2), r.GetString(3)), x => (x.SortOrdinal, x.SelectionKey), ct);

    private const string ResourceMetadataBytes = """
        CONVERT(bigint,256)+DATALENGTH(COALESCE(o.SchemaName,t.SchemaName,data.SchemaName,r.SchemaName))
            +DATALENGTH(COALESCE(o.ObjectName,t.TableName,data.TableName,r.ObjectName))+DATALENGTH(r.OriginalResourceKey)
        """;

    private const string ResourceColumns = """
        r.ResourceKey, r.PublicId, r.SnapshotKey, r.Kind,
        COALESCE(o.SchemaName, t.SchemaName, data.SchemaName, r.SchemaName), COALESCE(o.ObjectName, t.TableName, data.TableName, r.ObjectName),
        r.OriginalResourceKey, chosen.RevisionKey, r.CurrentSortOrdinal, r.RowVersion, rr.IsSealed
        """;

    // Current-only projection. Selected versions execute HistoricalSnapshotReplay
    // because table/data side effects and RemoveAll+append cannot be reduced to
    // independent resource revision lookups.
    private const string ResourceFrom = """
        FROM surf.SnapshotResource r
        CROSS APPLY (SELECT r.CurrentRevisionKey RevisionKey) chosen
        LEFT JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=chosen.RevisionKey
        LEFT JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=chosen.RevisionKey
        LEFT JOIN surf.TableMetadataRevision t ON t.RevisionKey=chosen.RevisionKey
        LEFT JOIN surf.TableDataRevision data ON data.RevisionKey=chosen.RevisionKey
        """;

    internal static IReadOnlyList<string> ParseQueryContracts() => SnapshotContractChecks.ParseSql(
        "SELECT TOP (@Take) " + ResourceMetadataBytes + ", " + ResourceColumns + " " + ResourceFrom + " WHERE r.SnapshotKey=@Owner ORDER BY r.CurrentSortOrdinal, r.ResourceKey;" +
        "SELECT " + ResourceMetadataBytes + ", " + ResourceColumns + " " + ResourceFrom + " WHERE r.ResourceKey=@Resource AND chosen.RevisionKey IS NOT NULL AND rr.IsSealed=1;");

    private Task<SnapshotPage<T>> ResourcePage<T>(long snapshotKey, long? versionKey, string query,
        DatabaseVersionedResourceKind? kind, int pageSize, SnapshotCursor? cursor, string columns, string metadataBytes, string predicate,
        Func<SqlDataReader, T> map, Func<T, (long Order, long Key)> position, CancellationToken ct) =>
        PageAsync(snapshotKey, versionKey, query, (int?)kind, pageSize, cursor,
            "SELECT TOP (@Take) " + metadataBytes + ", " + columns + " " + ResourceFrom + " " + """
            WHERE r.SnapshotKey=@Owner AND (@Kind IS NULL OR r.Kind=@Kind)
              AND chosen.RevisionKey IS NOT NULL AND rr.IsSealed=1
              AND (r.CurrentSortOrdinal>@AfterOrder OR r.CurrentSortOrdinal=@AfterOrder AND r.ResourceKey>@AfterKey)
            """ + predicate + " ORDER BY r.CurrentSortOrdinal, r.ResourceKey;", map, position, ct);

    private async Task<SnapshotPage<T>> PageAsync<T>(long ownerKey, long? versionKey, string query, int? kind,
        int pageSize, SnapshotCursor? cursor, string sql, Func<SqlDataReader, T> map,
        Func<T, (long Order, long Key)> position, CancellationToken ct, bool catalogue = false, long? historyKey = null)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (cursor != null && (cursor.OwnerKey != ownerKey || cursor.VersionKey != versionKey || cursor.Query != query ||
            cursor.Kind != kind || cursor.Epoch != Epoch)) throw new ArgumentException("Cursor belongs to another request or database epoch.");
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        byte[] token = await Head(connection, transaction, ownerKey, versionKey, catalogue, ct, historyKey).ConfigureAwait(false);
        if (cursor != null && !token.AsSpan().SequenceEqual(cursor.HeadToken)) throw new SnapshotConcurrencyException();
        await using var command = Command(connection, transaction, sql, Key("@Owner", ownerKey), Key("@Version", versionKey), Key("@History", historyKey), Int("@Kind", kind),
            Int("@Take", pageSize + 1), Key("@AfterOrder", cursor?.SortOrdinal ?? -1), Key("@AfterKey", cursor?.EntryKey ?? 0));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var items = new List<T>(pageSize);
        bool more = false;
        long bytes = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (items.Count == pageSize) { more = true; SnapshotReadGuard.StopReading(command); break; }
            long rowBytes = reader.GetInt64(0);
            SnapshotReadGuard.RequireBytes(rowBytes, MaximumMetadataPageBytes, command);
            if (rowBytes > MaximumMetadataPageBytes - bytes) { more = true; SnapshotReadGuard.StopReading(command); break; }
            bytes += rowBytes;
            items.Add(map(reader));
        }
        SnapshotCursor? next = null;
        if (more)
        {
            var (order, key) = position(items[^1]);
            next = new(ownerKey, versionKey, token, order, key, kind, Epoch, query);
        }
        return new(items, next, Epoch);
    }

    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        await session.RequireReadyAsync(ct).ConfigureAwait(false);
        return await session.OpenAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> Head(SqlConnection connection, SqlTransaction transaction,
        long owner, long? version, bool catalogue, CancellationToken ct, long? history = null)
    {
        object? result = await ScalarAsync(connection, transaction, catalogue ?
            "SELECT RowVersion FROM surf.SnapshotCatalogueHead WHERE UserKey=@Owner;" : """
            SELECT RowVersion FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Owner AND IsPublished=1
              AND (@Version IS NULL OR EXISTS (SELECT 1 FROM surf.SnapshotVersion WHERE VersionKey=@Version AND SnapshotKey=@Owner))
              AND (@History IS NULL OR EXISTS (SELECT 1 FROM surf.SnapshotHistory WHERE HistoryKey=@History AND SnapshotKey=@Owner));
            """, ct, Key("@Owner", owner), Key("@Version", version), Key("@History", history)).ConfigureAwait(false);
        return result as byte[] ?? throw new KeyNotFoundException("Published owner or its requested version not found.");
    }

    private static SnapshotResourceSummary MapResource(SqlDataReader r) => new(r.GetInt64(1), r.GetGuid(2), r.GetInt64(3),
        r.IsDBNull(4) ? null : (DatabaseVersionedResourceKind)r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetInt64(8), r.GetInt64(9), (byte[])r.GetValue(10));
    private static SqlTable MapTable(SqlDataReader r, int start = 1) => new() { SchemaName = r.GetString(start), TableName = r.GetString(start + 1),
        HasFullData = r.GetBoolean(start + 2), FullDataRowCount = r.GetInt64(start + 3), FullDataImportedAtUtc = NullableDate(r, start + 4) };
    private static OrderedColumn MapColumn(SqlDataReader r) => new(r.GetInt64(1), r.GetInt64(2), new SqlColumn {
        SchemaName = r.GetString(3), TableName = r.GetString(4), ColumnName = r.GetString(5), DataType = r.GetString(6), MaxLength = r.GetInt32(7),
        NumericPrecision = r.GetByte(8), NumericScale = r.GetInt32(9), IsNullable = r.GetBoolean(10), IsIdentity = r.GetBoolean(11), Ordinal = r.GetInt32(12) });
    private static OrderedPrimaryKey MapKey(SqlDataReader r) => new(r.GetInt64(1), r.GetInt64(2), new SqlPrimaryKeyColumn {
        SchemaName = r.GetString(3), TableName = r.GetString(4), ConstraintName = r.GetString(5), ColumnName = r.GetString(6), KeyOrdinal = r.GetInt32(7) });
}

internal static class SnapshotPageProjection
{
    internal static async Task<SnapshotPage<TOut>> Project<TIn, TOut>(this Task<SnapshotPage<TIn>> source, Func<TIn, TOut> select)
    {
        var page = await source.ConfigureAwait(false);
        return new(page.Items.Select(select).ToArray(), page.Next, page.Epoch);
    }
}

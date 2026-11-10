using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

/// <summary>Incremental writes in a caller-owned transaction. Never commits, opens databases, or publishes format readiness.</summary>
public sealed class RelationalSnapshotWriter
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly RelationalContentStore _content;
    private readonly long? _snapshotScope;

    // Capture can enlist its incremental writes in the same publication transaction.
    public SqlConnection Connection => _connection;
    public SqlTransaction Transaction => _transaction;

    public RelationalSnapshotWriter(SqlConnection connection, SqlTransaction transaction, RelationalContentStore content)
        : this(connection, transaction, content, null) { }

    internal RelationalSnapshotWriter(SqlConnection connection, SqlTransaction transaction, RelationalContentStore content, long? snapshotScope)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(content);
        if (connection.State != ConnectionState.Open || transaction.Connection != connection)
            throw new ArgumentException("An open connection and its active transaction are required.");
        _connection = connection;
        _transaction = transaction;
        _content = content;
        _snapshotScope = snapshotScope;
    }

    public Task<long> CreateSnapshotAsync(SnapshotHeader header, CancellationToken ct = default)
    {
        if (_snapshotScope.HasValue) throw new InvalidOperationException("A targeted save cannot create an unrelated snapshot.");
        Order(header.SortOrdinal);
        return Insert("""
            INSERT surf.DatabaseSnapshot(UserKey, OriginalSnapshotId, SnapshotIdHash, DisplayName, DatabaseName, ImportedAtUtc, SortOrdinal)
            OUTPUT INSERTED.SnapshotKey VALUES(@User, @Id, @Hash, @Display, @Database, @Imported, @Order);
            """, ct, Key("@User", header.UserKey), Text("@Id", header.SnapshotId), Hash("@Hash", SnapshotIdentity.Hash(header.SnapshotId)),
            Text("@Display", header.DisplayName), Text("@Database", header.DatabaseName), Date("@Imported", header.ImportedAtUtc), Key("@Order", header.SortOrdinal));
    }

    public Task<long> CreateResourceAsync(long snapshotKey, DatabaseVersionedResourceKind? kind,
        string schema, string name, string originalResourceKey, long sortOrdinal, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        if (kind.HasValue && !Enum.IsDefined(kind.Value)) throw new ArgumentOutOfRangeException(nameof(kind));
        Order(sortOrdinal);
        return Insert("""
            INSERT surf.SnapshotResource(SnapshotKey, Kind, SchemaName, ObjectName, NameHash, OriginalResourceKey, ResourceKeyHash, CurrentSortOrdinal)
            OUTPUT INSERTED.ResourceKey VALUES(@Snapshot, @Kind, @Schema, @Name, @NameHash, @Original, @Hash, @Order);
            """, ct, Key("@Snapshot", snapshotKey), Int("@Kind", (int?)kind), Text("@Schema", schema), Text("@Name", name),
            Hash("@NameHash", SnapshotIdentity.NameHash(schema, name)), Text("@Original", originalResourceKey),
            Hash("@Hash", SnapshotIdentity.Hash(originalResourceKey)), Key("@Order", sortOrdinal));
    }

    public async Task<SnapshotRevisionHandle> InsertCurrentObjectAsync(long snapshotKey, SqlDatabaseObject value,
        long sortOrdinal, string? originalResourceKey = null, CancellationToken ct = default)
    {
        var kind = SnapshotIdentity.ResourceKind(value.Kind);
        long resource = await CreateResourceAsync(snapshotKey, kind, value.SchemaName, value.ObjectName,
            originalResourceKey ?? (kind.HasValue ? SnapshotIdentity.LegacyResourceKey(kind.Value, value.SchemaName, value.ObjectName) : string.Empty), sortOrdinal, ct).ConfigureAwait(false);
        long revision = await InsertObjectRevisionAsync(resource, value, ct).ConfigureAwait(false);
        await SealRevisionAsync(revision, ct).ConfigureAwait(false);
        await SetCurrentRevisionAsync(resource, revision, sortOrdinal, ct: ct).ConfigureAwait(false);
        return new(resource, revision);
    }

    public async Task<SnapshotRevisionHandle> InsertCurrentTableAsync(long snapshotKey, SqlTable value,
        long sortOrdinal, string? originalResourceKey = null, CancellationToken ct = default)
    {
        long resource = await CreateResourceAsync(snapshotKey, DatabaseVersionedResourceKind.TableMetadata,
            value.SchemaName, value.TableName, originalResourceKey ?? SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.TableMetadata,
                value.SchemaName, value.TableName), sortOrdinal, ct).ConfigureAwait(false);
        long revision = await InsertTableRevisionAsync(resource, value, ct).ConfigureAwait(false);
        // Caller appends individual columns/keys and then seals the revision.
        await SetCurrentRevisionAsync(resource, revision, sortOrdinal, ct: ct).ConfigureAwait(false);
        return new(resource, revision);
    }

    public async Task<long> InsertObjectRevisionAsync(long resourceKey, SqlDatabaseObject value, CancellationToken ct = default)
    {
        await GuardKind(resourceKey, SnapshotIdentity.ResourceKind(value.Kind), ct).ConfigureAwait(false);
        long contentKey = await _content.PutTextAsync(_connection, _transaction, value.Definition, ct).ConfigureAwait(false);
        long revision = await NewRevision(resourceKey, ct).ConfigureAwait(false);
        await Insert("""
            INSERT surf.DatabaseObjectRevision(RevisionKey, SchemaName, ObjectName, ObjectKind, TypeDescription, ParentSchemaName, ParentObjectName, DefinitionContentKey)
            OUTPUT INSERTED.RevisionKey VALUES(@Revision, @Schema, @Name, @Kind, @Description, @ParentSchema, @ParentName, @Content);
            """, ct, Key("@Revision", revision), Text("@Schema", value.SchemaName), Text("@Name", value.ObjectName),
            Int("@Kind", (int)value.Kind), Text("@Description", value.TypeDescription), Text("@ParentSchema", value.ParentSchemaName),
            Text("@ParentName", value.ParentObjectName), Key("@Content", contentKey)).ConfigureAwait(false);
        return revision;
    }

    public async Task<long> InsertTableRevisionAsync(long resourceKey, SqlTable value, CancellationToken ct = default)
    {
        await GuardKind(resourceKey, DatabaseVersionedResourceKind.TableMetadata, ct).ConfigureAwait(false);
        long revision = await NewRevision(resourceKey, ct).ConfigureAwait(false);
        await InsertTable(revision, value, false, ct).ConfigureAwait(false);
        return revision;
    }

    /// <summary>Capture writes its dataset against the returned revision, then seals it. No rows are accepted here.</summary>
    public async Task<long> InsertTableDataRevisionAsync(long resourceKey, SqlTable? previousPayloadTable = null, CancellationToken ct = default)
    {
        await GuardKind(resourceKey, DatabaseVersionedResourceKind.TableData, ct).ConfigureAwait(false);
        long revision = await NewRevision(resourceKey, ct).ConfigureAwait(false);
        await RequiredInsert("""
            INSERT surf.TableDataRevision(RevisionKey, SchemaName, TableName) OUTPUT INSERTED.RevisionKey
            SELECT @Revision, SchemaName, ObjectName FROM surf.SnapshotResource WHERE ResourceKey=@Resource;
            """, ct, Key("@Revision", revision), Key("@Resource", resourceKey)).ConfigureAwait(false);
        if (previousPayloadTable != null) await InsertTable(revision, previousPayloadTable, true, ct).ConfigureAwait(false);
        return revision;
    }

    public async Task<long> InsertTableDataRevisionAsync(long resourceKey, string schemaName, string tableName,
        SqlTable? previousPayloadTable = null, CancellationToken ct = default)
    {
        long revision = await InsertTableDataRevisionAsync(resourceKey, previousPayloadTable, ct).ConfigureAwait(false);
        await Scalar("UPDATE surf.TableDataRevision SET SchemaName=@Schema, TableName=@Table WHERE RevisionKey=@Revision;",
            ct, Key("@Revision", revision), Text("@Schema", schemaName), Text("@Table", tableName)).ConfigureAwait(false);
        return revision;
    }

    public async Task<long> InsertColumnAsync(long snapshotKey, long? tableRevisionKey, SqlColumn value,
        long sortOrdinal, long? currentCollectionOrdinal = null, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        Order(sortOrdinal);
        if (currentCollectionOrdinal.HasValue) Order(currentCollectionOrdinal.Value);
        await GuardTable(snapshotKey, tableRevisionKey, ct).ConfigureAwait(false);
        long key = await Insert("""
            INSERT surf.TableColumnRevision(SnapshotKey, TableMetadataRevisionKey, SchemaName, TableName, ColumnName, DataType, MaxLength,
                NumericPrecision, NumericScale, IsNullable, IsIdentity, SourceOrdinal, SortOrdinal)
            OUTPUT INSERTED.ColumnRevisionKey VALUES(@Snapshot, @Revision, @Schema, @Table, @Name, @Type, @Length, @Precision, @Scale, @Nullable, @Identity, @SourceOrder, @Order);
            """, ct, Key("@Snapshot", snapshotKey), Key("@Revision", tableRevisionKey), Text("@Schema", value.SchemaName), Text("@Table", value.TableName),
            Text("@Name", value.ColumnName), Text("@Type", value.DataType), Int("@Length", value.MaxLength),
            RelationalSession.Parameter("@Precision", SqlDbType.TinyInt, value.NumericPrecision), Int("@Scale", value.NumericScale),
            Bit("@Nullable", value.IsNullable), Bit("@Identity", value.IsIdentity), Int("@SourceOrder", value.Ordinal), Key("@Order", sortOrdinal)).ConfigureAwait(false);
        if (currentCollectionOrdinal.HasValue)
            await Insert("""
                INSERT surf.SnapshotCurrentColumn(SnapshotKey, ColumnRevisionKey, SortOrdinal)
                OUTPUT INSERTED.EntryKey VALUES(@Snapshot, @Column, @Order);
                """, ct, Key("@Snapshot", snapshotKey), Key("@Column", key), Key("@Order", currentCollectionOrdinal)).ConfigureAwait(false);
        return key;
    }

    public async Task<long> InsertPrimaryKeyAsync(long snapshotKey, long? tableRevisionKey, SqlPrimaryKeyColumn value,
        long sortOrdinal, long? currentCollectionOrdinal = null, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        Order(sortOrdinal);
        if (currentCollectionOrdinal.HasValue) Order(currentCollectionOrdinal.Value);
        await GuardTable(snapshotKey, tableRevisionKey, ct).ConfigureAwait(false);
        // Compare exact spelling after the hash, including trailing spaces.
        object? existing = tableRevisionKey.HasValue ? await Scalar("""
            SELECT TOP (1) ConstraintKey FROM surf.PrimaryKeyConstraint WITH (UPDLOCK, HOLDLOCK)
            WHERE SnapshotKey=@Snapshot AND TableMetadataRevisionKey=@Revision AND ConstraintNameHash=@Hash
              AND DATALENGTH(ConstraintName)=DATALENGTH(@Name)
              AND ConstraintName COLLATE Latin1_General_100_BIN2=@Name COLLATE Latin1_General_100_BIN2
            ORDER BY ConstraintKey;
            """, ct, Key("@Snapshot", snapshotKey), Key("@Revision", tableRevisionKey), Hash("@Hash", SnapshotIdentity.Hash(value.ConstraintName)), Text("@Name", value.ConstraintName)).ConfigureAwait(false) : null;
        long constraint = existing is long found ? found : await Insert("""
            INSERT surf.PrimaryKeyConstraint(SnapshotKey, TableMetadataRevisionKey, ConstraintName, ConstraintNameHash)
            OUTPUT INSERTED.ConstraintKey VALUES(@Snapshot, @Revision, @Name, @Hash);
            """, ct, Key("@Snapshot", snapshotKey), Key("@Revision", tableRevisionKey), Text("@Name", value.ConstraintName),
            Hash("@Hash", SnapshotIdentity.Hash(value.ConstraintName))).ConfigureAwait(false);
        long key = await Insert("""
            INSERT surf.PrimaryKeyColumn(SnapshotKey, TableMetadataRevisionKey, ConstraintKey, SchemaName, TableName, ConstraintName, ColumnName, KeyOrdinal, SortOrdinal)
            OUTPUT INSERTED.KeyColumnKey VALUES(@Snapshot, @Revision, @Constraint, @Schema, @Table, @ConstraintName, @Column, @KeyOrder, @Order);
            """, ct, Key("@Snapshot", snapshotKey), Key("@Revision", tableRevisionKey), Key("@Constraint", constraint), Text("@Schema", value.SchemaName),
            Text("@Table", value.TableName), Text("@ConstraintName", value.ConstraintName), Text("@Column", value.ColumnName), Int("@KeyOrder", value.KeyOrdinal), Key("@Order", sortOrdinal)).ConfigureAwait(false);
        if (currentCollectionOrdinal.HasValue)
            await Insert("""
                INSERT surf.SnapshotCurrentPrimaryKey(SnapshotKey, KeyColumnKey, SortOrdinal)
                OUTPUT INSERTED.EntryKey VALUES(@Snapshot, @Key, @Order);
                """, ct, Key("@Snapshot", snapshotKey), Key("@Key", key), Key("@Order", currentCollectionOrdinal)).ConfigureAwait(false);
        return key;
    }

    public Task<long> InsertFullDataSelectionAsync(long snapshotKey, string name, long sortOrdinal, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        Order(sortOrdinal);
        return Insert("""
            INSERT surf.FullDataTableSelection(SnapshotKey, OriginalTableName, SortOrdinal)
            OUTPUT INSERTED.SelectionKey VALUES(@Snapshot, @Name, @Order);
            """, ct, Key("@Snapshot", snapshotKey), Text("@Name", name), Key("@Order", sortOrdinal));
    }

    public Task<long> InsertHistoryAsync(long snapshotKey, string originalSnapshotId, int nextVersionNumber, long sortOrdinal, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        Order(sortOrdinal);
        return Insert("""
            INSERT surf.SnapshotHistory(SnapshotKey, OriginalSnapshotId, NextVersionNumber, SortOrdinal)
            OUTPUT INSERTED.HistoryKey VALUES(@Snapshot, @Id, @Next, @Order);
            """, ct, Key("@Snapshot", snapshotKey), Text("@Id", originalSnapshotId), Int("@Next", nextVersionNumber), Key("@Order", sortOrdinal));
    }

    public Task<long> InsertVersionAsync(long historyKey, SnapshotVersionHeader header, CancellationToken ct = default)
    {
        Order(header.SortOrdinal);
        return RequiredInsert("""
            INSERT surf.SnapshotVersion(HistoryKey, SnapshotKey, OriginalVersionId, VersionIdHash, VersionName, VersionNumber, CreatedAtUtc, IsInitial, SortOrdinal)
            OUTPUT INSERTED.VersionKey
            SELECT HistoryKey, SnapshotKey, @Id, @Hash, @Name, @Number, @Created, @Initial, @Order
            FROM surf.SnapshotHistory WHERE HistoryKey=@History AND (@Scope IS NULL OR SnapshotKey=@Scope);
            """, ct, Key("@History", historyKey), Key("@Scope", _snapshotScope), Text("@Id", header.VersionId), Hash("@Hash", SnapshotIdentity.Hash(header.VersionId)), Text("@Name", header.VersionName),
            Int("@Number", header.VersionNumber), Date("@Created", header.CreatedAtUtc), Bit("@Initial", header.IsInitial), Key("@Order", header.SortOrdinal));
    }

    public async Task<long> InsertChangeAsync(long versionKey, long resourceKey, SnapshotChangeHeader header, CancellationToken ct = default)
    {
        Order(header.SortOrdinal);
        if (!Enum.IsDefined(header.Kind) || !Enum.IsDefined(header.ChangeKind)) throw new ArgumentOutOfRangeException(nameof(header));
        if (header.ChangeKind == DatabaseSnapshotResourceChangeKind.Added && header.PreviousRevisionKey.HasValue)
            throw new ArgumentException("An Added reverse change cannot contain a previous revision.", nameof(header));
        await GuardKind(resourceKey, header.Kind, ct).ConfigureAwait(false);
        return await RequiredInsert("""
            INSERT surf.SnapshotChange(VersionKey, SnapshotKey, VersionNumber, ResourceKey, Kind, ChangeKind, OriginalResourceKey, DisplayName, RelativePath, SortOrdinal, PreviousRevisionKey)
            OUTPUT INSERTED.ChangeKey
            SELECT v.VersionKey, v.SnapshotKey, v.VersionNumber, r.ResourceKey, @Kind, @Change, @Original, @Display, @Path, @Order, @Previous
            FROM surf.SnapshotVersion v JOIN surf.SnapshotResource r ON r.SnapshotKey=v.SnapshotKey
            WHERE v.VersionKey=@Version AND r.ResourceKey=@Resource;
            """, ct, Key("@Version", versionKey), Key("@Resource", resourceKey), Int("@Kind", (int)header.Kind), Int("@Change", (int)header.ChangeKind),
            Text("@Original", header.ResourceKey), Text("@Display", header.DisplayName), Text("@Path", header.RelativePath),
            Key("@Order", header.SortOrdinal), Key("@Previous", header.PreviousRevisionKey)).ConfigureAwait(false);
    }

    public async Task SealRevisionAsync(long revisionKey, CancellationToken ct = default)
    {
        await Scalar("""
            IF NOT EXISTS (SELECT 1 FROM surf.SnapshotResourceRevision rr JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
              WHERE rr.RevisionKey=@Revision AND (@Scope IS NULL OR r.SnapshotKey=@Scope) AND (
                (r.Kind IS NULL OR r.Kind BETWEEN 0 AND 3) AND EXISTS(SELECT 1 FROM surf.DatabaseObjectRevision o WHERE o.RevisionKey=rr.RevisionKey)
                OR r.Kind=4 AND EXISTS(SELECT 1 FROM surf.TableMetadataRevision t WHERE t.RevisionKey=rr.RevisionKey)
                OR r.Kind=5 AND EXISTS(SELECT 1 FROM surf.TableDataRevision d JOIN surf.DataSet ds ON ds.RevisionKey=d.RevisionKey
                    WHERE d.RevisionKey=rr.RevisionKey AND ds.State='Ready')))
                THROW 51001, 'Incomplete or mismatched typed snapshot revision or dataset.', 1;
            UPDATE surf.SnapshotResourceRevision SET IsSealed=1 WHERE RevisionKey=@Revision;
            """, ct, Key("@Revision", revisionKey), Key("@Scope", _snapshotScope)).ConfigureAwait(false);
    }

    public async Task<byte[]> SetCurrentRevisionAsync(long resourceKey, long? revisionKey, long sortOrdinal,
        byte[]? expectedRowVersion = null, CancellationToken ct = default)
    {
        Order(sortOrdinal);
        if (expectedRowVersion != null && expectedRowVersion.Length != 8) throw new ArgumentException("Invalid rowversion.");
        object? result = await Scalar("""
            UPDATE surf.SnapshotResource SET CurrentRevisionKey=@Revision, CurrentSortOrdinal=@Order
            OUTPUT INSERTED.RowVersion WHERE ResourceKey=@Resource AND (@Expected IS NULL OR RowVersion=@Expected)
                AND (@Scope IS NULL OR SnapshotKey=@Scope);
            """, ct, Key("@Resource", resourceKey), Key("@Revision", revisionKey), Key("@Order", sortOrdinal),
            Token("@Expected", expectedRowVersion), Key("@Scope", _snapshotScope)).ConfigureAwait(false);
        return result as byte[] ?? throw new SnapshotConcurrencyException();
    }

    public async Task SetHistoryNextVersionAsync(long historyKey, int nextVersionNumber, byte[] expectedRowVersion, CancellationToken ct = default)
    {
        if (expectedRowVersion.Length != 8) throw new ArgumentException("Invalid rowversion.");
        object? result = await Scalar("""
            UPDATE surf.SnapshotHistory SET NextVersionNumber=@Next OUTPUT INSERTED.HistoryKey
            WHERE HistoryKey=@History AND RowVersion=@Expected AND (@Scope IS NULL OR SnapshotKey=@Scope);
            """, ct, Key("@History", historyKey), Int("@Next", nextVersionNumber), Token("@Expected", expectedRowVersion), Key("@Scope", _snapshotScope)).ConfigureAwait(false);
        if (result is not long) throw new SnapshotConcurrencyException();
    }

    /// <summary>Publishes a validated snapshot, not the database format. Call only after capture validates/seals its datasets.</summary>
    public async Task<byte[]> PublishSnapshotAsync(long snapshotKey, long? currentVersionKey, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        await ValidateSnapshotAsync(snapshotKey, ct).ConfigureAwait(false);
        await Scalar("""
            UPDATE h SET UserKey=h.UserKey FROM surf.SnapshotCatalogueHead h
            JOIN surf.DatabaseSnapshot s ON s.UserKey=h.UserKey WHERE s.SnapshotKey=@Snapshot;
            """, ct, Key("@Snapshot", snapshotKey)).ConfigureAwait(false);
        object? result = await Scalar("""
            DECLARE @SelectedVersion bigint = (SELECT TOP (1) v.VersionKey FROM surf.SnapshotVersion v
                WHERE v.HistoryKey=(SELECT TOP (1) h.HistoryKey FROM surf.SnapshotHistory h
                    WHERE h.SnapshotKey=@Snapshot ORDER BY h.SortOrdinal,h.HistoryKey)
                ORDER BY v.VersionNumber DESC,v.SortOrdinal,v.VersionKey);
            IF (@Version IS NULL AND @SelectedVersion IS NOT NULL) OR (@Version IS NOT NULL AND
                (@SelectedVersion IS NULL OR @Version<>@SelectedVersion))
                THROW 51004, 'Current version must be the first history maximum in stable source order.', 1;
            UPDATE surf.DatabaseSnapshot SET CurrentVersionKey=@Version, IsPublished=1
            OUTPUT INSERTED.RowVersion WHERE SnapshotKey=@Snapshot;
            """, ct, Key("@Snapshot", snapshotKey), Key("@Version", currentVersionKey)).ConfigureAwait(false);
        return result as byte[] ?? throw new KeyNotFoundException("Snapshot not found.");
    }

    public async Task UpdateSnapshotHeaderAsync(long snapshotKey, string displayName, string databaseName,
        DateTimeOffset importedAtUtc, long sortOrdinal, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        Order(sortOrdinal);
        object? result = await Scalar("""
            UPDATE surf.DatabaseSnapshot SET DisplayName=@Display, DatabaseName=@Database, ImportedAtUtc=@Imported, SortOrdinal=@Order
            OUTPUT INSERTED.SnapshotKey WHERE SnapshotKey=@Snapshot;
            """, ct, Key("@Snapshot", snapshotKey), Text("@Display", displayName), Text("@Database", databaseName),
            Date("@Imported", importedAtUtc), Key("@Order", sortOrdinal)).ConfigureAwait(false);
        if (result is not long) throw new KeyNotFoundException("Snapshot not found.");
    }

    // Explicit removals only. Unloaded/unqueried entries are never inferred to
    // be deletions; immutable revision children are retained for history.
    public Task RemoveCurrentColumnEntryAsync(long snapshotKey, long columnRevisionKey, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        return Scalar("DELETE surf.SnapshotCurrentColumn WHERE SnapshotKey=@Snapshot AND ColumnRevisionKey=@Column;", ct,
            Key("@Snapshot", snapshotKey), Key("@Column", columnRevisionKey));
    }
    public Task RemoveCurrentPrimaryKeyEntryAsync(long snapshotKey, long keyColumnKey, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        return Scalar("DELETE surf.SnapshotCurrentPrimaryKey WHERE SnapshotKey=@Snapshot AND KeyColumnKey=@Key;", ct,
            Key("@Snapshot", snapshotKey), Key("@Key", keyColumnKey));
    }
    public Task RemoveFullDataSelectionAsync(long snapshotKey, long selectionKey, CancellationToken ct = default)
    {
        GuardSnapshot(snapshotKey);
        return Scalar("DELETE surf.FullDataTableSelection WHERE SnapshotKey=@Snapshot AND SelectionKey=@Selection;", ct,
            Key("@Snapshot", snapshotKey), Key("@Selection", selectionKey));
    }

    internal async Task ValidateSnapshotAsync(long snapshotKey, CancellationToken ct)
    {
        GuardSnapshot(snapshotKey);
        await Scalar("""
            IF EXISTS (SELECT 1 FROM surf.SnapshotResource r JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=r.CurrentRevisionKey
                WHERE r.SnapshotKey=@Snapshot AND rr.IsSealed=0)
                OR EXISTS (SELECT 1 FROM surf.SnapshotChange c JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=c.PreviousRevisionKey
                WHERE c.SnapshotKey=@Snapshot AND rr.IsSealed=0)
                THROW 51002, 'Unsealed snapshot revisions cannot be published.', 1;
            IF EXISTS (SELECT 1 FROM surf.SnapshotHistory h WHERE h.SnapshotKey=@Snapshot AND
                (h.NextVersionNumber < 1 OR h.NextVersionNumber <= (SELECT MAX(v.VersionNumber) FROM surf.SnapshotVersion v WHERE v.HistoryKey=h.HistoryKey)))
                THROW 51003, 'NextVersionNumber must exceed existing version numbers without renumbering.', 1;
            """, ct, Key("@Snapshot", snapshotKey)).ConfigureAwait(false);
    }

    private Task<long> NewRevision(long resourceKey, CancellationToken ct) => Insert("""
        INSERT surf.SnapshotResourceRevision(ResourceKey, SnapshotKey) OUTPUT INSERTED.RevisionKey
        SELECT ResourceKey, SnapshotKey FROM surf.SnapshotResource WHERE ResourceKey=@Resource;
        """, ct, Key("@Resource", resourceKey));

    private Task<long> InsertTable(long revision, SqlTable value, bool data, CancellationToken ct) => Insert(data ? """
        INSERT surf.TableDataRevisionMetadata(RevisionKey, SchemaName, TableName, HasFullData, FullDataRowCount, FullDataImportedAtUtc)
        OUTPUT INSERTED.RevisionKey VALUES(@Revision, @Schema, @Name, @Full, @Count, @Imported);
        """ : """
        INSERT surf.TableMetadataRevision(RevisionKey, SnapshotKey, SchemaName, TableName, HasFullData, FullDataRowCount, FullDataImportedAtUtc)
        OUTPUT INSERTED.RevisionKey SELECT RevisionKey, SnapshotKey, @Schema, @Name, @Full, @Count, @Imported
        FROM surf.SnapshotResourceRevision WHERE RevisionKey=@Revision;
        """, ct, Key("@Revision", revision), Text("@Schema", value.SchemaName), Text("@Name", value.TableName), Bit("@Full", value.HasFullData),
        Key("@Count", value.FullDataRowCount), Date("@Imported", value.FullDataImportedAtUtc));

    private async Task GuardKind(long resourceKey, DatabaseVersionedResourceKind? kind, CancellationToken ct)
    {
        object? result = await Scalar("""
            SELECT ResourceKey FROM surf.SnapshotResource WITH (UPDLOCK, HOLDLOCK)
            WHERE ResourceKey=@Resource AND (Kind=@Kind OR Kind IS NULL AND @Kind IS NULL)
                AND (@Scope IS NULL OR SnapshotKey=@Scope);
            """, ct, Key("@Resource", resourceKey), Int("@Kind", (int?)kind), Key("@Scope", _snapshotScope)).ConfigureAwait(false);
        if (result is not long) throw new ArgumentException("Resource missing or its kind does not match the typed revision.");
    }

    private async Task GuardTable(long snapshotKey, long? revisionKey, CancellationToken ct)
    {
        if (!revisionKey.HasValue) return;
        object? result = await Scalar("""
            SELECT rr.RevisionKey FROM surf.TableMetadataRevision t
            JOIN surf.SnapshotResourceRevision rr WITH (UPDLOCK, HOLDLOCK) ON rr.RevisionKey=t.RevisionKey
            JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
            WHERE rr.RevisionKey=@Revision AND r.SnapshotKey=@Snapshot AND rr.IsSealed=0;
            """, ct, Key("@Revision", revisionKey), Key("@Snapshot", snapshotKey)).ConfigureAwait(false);
        if (result is not long) throw new ArgumentException("Metadata revision missing, sealed, or owned by another snapshot.");
    }

    private async Task<long> RequiredInsert(string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        object? result = await Scalar(sql, ct, parameters).ConfigureAwait(false);
        return result is long key ? key : throw new ArgumentException("Owner not found or belongs to another snapshot.");
    }
    private Task<long> Insert(string sql, CancellationToken ct, params SqlParameter[] parameters) => InsertAsync(_connection, _transaction, sql, ct, parameters);
    private Task<object?> Scalar(string sql, CancellationToken ct, params SqlParameter[] parameters) => ScalarAsync(_connection, _transaction, sql, ct, parameters);
    private void GuardSnapshot(long snapshotKey)
    {
        if (_snapshotScope.HasValue && _snapshotScope.Value != snapshotKey)
            throw new ArgumentException("This targeted writer belongs to another snapshot.", nameof(snapshotKey));
    }
    private static void Order(long value) { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); }
}

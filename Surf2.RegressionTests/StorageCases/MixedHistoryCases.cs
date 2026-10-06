using System.Data;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyMixedHistoryAsync(RelationalSession session, RelationalContentStore content,
        RelationalSnapshotStore snapshots, RelationalCaptureStore capture, Action<bool, string> check)
    {
        DatabaseSnapshotLibrary library = MixedHistoryLibrary();
        var (snapshotKey, versionKeys) = await SeedMixedHistoryAsync(session, content, capture, library);
        await VerifyHistoryOracleAsync(session, snapshots, capture, library, snapshotKey, versionKeys, check, exerciseLifetime: true);
    }

    private static async Task VerifyHistoryOracleAsync(RelationalSession session, RelationalSnapshotStore snapshots,
        RelationalCaptureStore capture, DatabaseSnapshotLibrary library, long snapshotKey,
        IReadOnlyDictionary<string, long> versionKeys, Action<bool, string> check, bool exerciseLifetime = false)
    {
        var oracle = new DatabaseSnapshotHistoryService(new DatabaseDocumentService());
        foreach (var version in library.Histories[0].Versions)
        {
            DatabaseMetadataSnapshot expected = oracle.ReconstructSnapshot(library, library.Snapshots[0].SnapshotId, version.VersionId);
            await using var context = await snapshots.OpenHistoricalSnapshotAsync(snapshotKey, versionKeys[version.VersionId]);
            var actual = new DatabaseMetadataSnapshot
            {
                SnapshotId = context.Header.SnapshotId, DisplayName = context.Header.DisplayName,
                DatabaseName = context.Header.DatabaseName, ImportedAtUtc = context.Header.ImportedAtUtc
            };
            foreach (var collection in Enum.GetValues<HistoricalCollection>())
            {
                var entries = await SnapshotPagesAsync(c => context.ReadPageAsync(collection, pageSize: 1, cursor: c), check,
                    "Mixed history " + version.VersionId + " " + collection);
                check(entries.Select(x => x.SortOrdinal).SequenceEqual(Enumerable.Range(0, entries.Count).Select(x => (long)x)),
                    "Mixed history projection exposes contiguous final collection order: " + version.VersionId + " " + collection);
                foreach (var entry in entries)
                {
                    switch (collection)
                    {
                        case HistoricalCollection.Objects:
                            actual.Objects.Add(await snapshots.ReadObjectAsync(entry.RevisionKey!.Value));
                            break;
                        case HistoricalCollection.Tables:
                            actual.Tables.Add(entry.Table!);
                            var metadata = await context.ReadTableMetadataAsync(entry);
                            var expectedColumns = expected.Columns.Where(x => SameTableName(x.SchemaName, x.TableName, entry.SchemaName, entry.Name)).ToArray();
                            var expectedKeys = expected.PrimaryKeys.Where(x => SameTableName(x.SchemaName, x.TableName, entry.SchemaName, entry.Name)).ToArray();
                            SameModel(expectedColumns, metadata.Columns.Select(x => x.Column).ToArray(), check,
                                "Selected historical table reads replayed columns, including data companions: " + version.VersionId + " " + entry.Name);
                            SameModel(expectedKeys, metadata.PrimaryKeys.Select(x => x.Column).ToArray(), check,
                                "Selected historical table reads replayed keys: " + version.VersionId + " " + entry.Name);
                            break;
                        case HistoricalCollection.Columns:
                            actual.Columns.Add(await ReadMixedColumnAsync(session, entry.ChildKey!.Value));
                            break;
                        case HistoricalCollection.PrimaryKeys:
                            actual.PrimaryKeys.Add(await ReadMixedKeyAsync(session, entry.ChildKey!.Value));
                            break;
                        case HistoricalCollection.TableDataSets:
                            var names = await snapshots.ReadTableDataMetadataAsync(entry.RevisionKey!.Value);
                            var descriptor = Required(await capture.GetForRevisionAsync(entry.RevisionKey.Value), "mixed selected dataset");
                            var data = new SqlTableDataSet { SchemaName = names.SchemaName, TableName = names.TableName,
                                RowCount = descriptor.Summary.ReportedRowCount, ImportedAtUtc = descriptor.Summary.ImportedAtUtc };
                            await foreach (var row in capture.StreamRowsAsync(descriptor.Summary.DataSetKey)) data.Rows.Add(row.Value);
                            actual.TableDataSets.Add(data);
                            break;
                        case HistoricalCollection.FullDataTableNames:
                            actual.FullDataTableNames.Add(entry.Name);
                            break;
                    }
                }
            }
            SameModel(expected.Objects, actual.Objects, check, "Legacy oracle object RemoveAll+append order: " + version.VersionId);
            SameModel(expected.Tables, actual.Tables, check, "Legacy oracle mixed table flags, counts, timestamps and RemoveAll+append order: " + version.VersionId);
            SameModel(expected.Columns, actual.Columns, check, "Legacy oracle graph-wide column removal and payload append order: " + version.VersionId);
            SameModel(expected.PrimaryKeys, actual.PrimaryKeys, check, "Legacy oracle graph-wide key removal and payload append order: " + version.VersionId);
            SameModel(expected.FullDataTableNames, actual.FullDataTableNames, check, "Legacy oracle full-data duplicate removal and first-match append semantics: " + version.VersionId);
            check(expected.TableDataSets.Count == actual.TableDataSets.Count && expected.TableDataSets.Select((data, i) =>
                data.SchemaName == actual.TableDataSets[i].SchemaName && data.TableName == actual.TableDataSets[i].TableName &&
                data.RowCount == actual.TableDataSets[i].RowCount && data.ImportedAtUtc.EqualsExact(actual.TableDataSets[i].ImportedAtUtc) &&
                data.Rows.Count == actual.TableDataSets[i].Rows.Count && data.Rows.Select((row, j) => JsonEvidenceEquals(row, actual.TableDataSets[i].Rows[j])).All(x => x)).All(x => x),
                "Legacy oracle graph-wide dataset removal, exact selected rows and append order: " + version.VersionId);
            check(expected.ImportedAtUtc.EqualsExact(actual.ImportedAtUtc), "Selected historical header uses target version creation timestamp: " + version.VersionId);
        }
        if (!exerciseLifetime) return;
        await using var stale = await snapshots.OpenHistoricalSnapshotAsync(snapshotKey, versionKeys["mixed-initial"]);
        var page = await stale.ReadPageAsync(HistoricalCollection.Tables, pageSize: 1);
        await ThrowsAsync<ArgumentException>(() => stale.ReadPageAsync(HistoricalCollection.Columns, pageSize: 1, cursor: Required(page.Next, "mixed history cursor")), check,
            "Historical SQL-temp projection cursor cannot cross collections");
        await snapshots.ExecuteMutationAsync(snapshotKey, stale.Header.RowVersion,
            (writer, ct) => writer.UpdateSnapshotHeaderAsync(snapshotKey, "mixed edited", "mixed database", EvidenceTime, 20, ct));
        await ThrowsAsync<SnapshotConcurrencyException>(() => stale.ReadPageAsync(HistoricalCollection.Tables), check,
            "Historical SQL-temp projection rejects a changed owner generation");
        await stale.DisposeAsync();
        await ThrowsAsync<ObjectDisposedException>(() => stale.ReadPageAsync(HistoricalCollection.Tables), check,
            "Disposed historical projection cannot retain a live reader");
    }

    private static DatabaseSnapshotLibrary MixedHistoryLibrary()
    {
        var current = new DatabaseMetadataSnapshot
        {
            SnapshotId = "mixed-history-fixture", DisplayName = "mixed current", DatabaseName = "mixed database", ImportedAtUtc = EvidenceTime,
            Objects = [MixedObject("p", "current-first"), MixedObject("q", "unchanged"), MixedObject("P", "current-duplicate")],
            Tables = [MixedTable("A", 10), MixedTable("B", 20), MixedTable("a", 30), MixedTable("C", 40)],
            Columns = [MixedColumn("A", "current-a1"), MixedColumn("B", "b"), MixedColumn("a", "current-a2"), MixedColumn("C", "c"), MixedColumn("orphan", "orphan")],
            PrimaryKeys = [MixedKey("A", "current-a1"), MixedKey("B", "b"), MixedKey("a", "current-a2"), MixedKey("C", "c"), MixedKey("orphan", "orphan")],
            TableDataSets = [MixedData("A", 10), MixedData("B", 20), MixedData("a", 30), MixedData("C", 40)],
            FullDataTableNames = ["dbo.A", "dbo.B", "DBO.a", "dbo.C", "unknown.selected"]
        };
        var initial = new DatabaseSnapshotVersion { VersionId = "mixed-initial", VersionName = "initial", VersionNumber = 1,
            CreatedAtUtc = EvidenceTime.AddTicks(1), IsInitial = true };
        var early = new DatabaseSnapshotVersion { VersionId = "mixed-early", VersionName = "early", VersionNumber = 4, CreatedAtUtc = EvidenceTime.AddTicks(4), Changes =
        [
            MixedChange(DatabaseVersionedResourceKind.TableMetadata, DatabaseSnapshotResourceChangeKind.Modified, "A", MetadataPayload("A", "early-a")),
            MixedChange(DatabaseVersionedResourceKind.TableMetadata, DatabaseSnapshotResourceChangeKind.Deleted, "C", MetadataPayload("C", "restored-c")),
            MixedChange(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Added, "D", null),
            MixedChange(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Modified, "E", null)
        ] };
        var middle = new DatabaseSnapshotVersion { VersionId = "mixed-middle", VersionName = "middle", VersionNumber = 7, CreatedAtUtc = EvidenceTime.AddTicks(7), Changes =
        [
            MixedChange(DatabaseVersionedResourceKind.TableMetadata, DatabaseSnapshotResourceChangeKind.Added, "C", null),
            MixedChange(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Added, "B", null),
            MixedChange(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Deleted, "D", new() { Kind = DatabaseVersionedResourceKind.TableData,
                Table = MixedTable("D", -3), TableDataSet = MixedData("D", -7) }),
            MixedChange(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Modified, "E", new() { Kind = DatabaseVersionedResourceKind.TableData,
                Table = null, TableDataSet = MixedData("E", -2) }),
            MixedChange(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, "q", null)
        ] };
        var latest = new DatabaseSnapshotVersion { VersionId = "mixed-latest", VersionName = "latest", VersionNumber = 12, CreatedAtUtc = EvidenceTime.AddTicks(12), Changes =
        [
            MixedChange(DatabaseVersionedResourceKind.TableMetadata, DatabaseSnapshotResourceChangeKind.Modified, "A", MetadataPayload("A", "previous-a")),
            MixedChange(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Modified, "A", new() { Kind = DatabaseVersionedResourceKind.TableData,
                Table = MixedTable("A", -9), TableDataSet = MixedData("A", 99) }),
            MixedChange(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, "p", new() { Kind = DatabaseVersionedResourceKind.StoredProcedure,
                DatabaseObject = MixedObject("p", "old-p") })
        ] };
        return new() { Snapshots = [current], Histories = [new() { SnapshotId = current.SnapshotId, NextVersionNumber = 20,
            Versions = [initial, middle, early, latest] }] };
    }

    private static SqlTable MixedTable(string name, long count) => new() { SchemaName = "dbo", TableName = name,
        HasFullData = count > 0, FullDataRowCount = count, FullDataImportedAtUtc = count > 0 ? EvidenceTime : null };
    private static SqlColumn MixedColumn(string table, string name) => new() { SchemaName = "dbo", TableName = table,
        ColumnName = name, DataType = "decimal", MaxLength = 17, NumericPrecision = 38, NumericScale = 9, IsNullable = true, Ordinal = 7 };
    private static SqlPrimaryKeyColumn MixedKey(string table, string name) => new() { SchemaName = "dbo", TableName = table,
        ConstraintName = "PK " + table, ColumnName = name, KeyOrdinal = 3 };
    private static SqlDatabaseObject MixedObject(string name, string definition) => new() { SchemaName = "dbo", ObjectName = name,
        Kind = SqlDatabaseObjectKind.StoredProcedure, TypeDescription = "SQL_STORED_PROCEDURE", Definition = definition };
    private static SqlTableDataSet MixedData(string table, long reported) => new() { SchemaName = "dbo", TableName = table,
        RowCount = reported, ImportedAtUtc = EvidenceTime.AddTicks(reported), Rows = [Json("{\"x\":1.2300}"), Json("{\"x\":null}")] };
    private static DatabaseSnapshotResourcePayload MetadataPayload(string name, string column) => new()
    {
        Kind = DatabaseVersionedResourceKind.TableMetadata, Table = MixedTable(name, -1),
        Columns = [MixedColumn(name, column + "-2"), MixedColumn(name, column + "-1")],
        PrimaryKeys = [MixedKey(name, column + "-2"), MixedKey(name, column + "-1")]
    };
    private static DatabaseSnapshotResourceChange MixedChange(DatabaseVersionedResourceKind kind,
        DatabaseSnapshotResourceChangeKind change, string name, DatabaseSnapshotResourcePayload? previous) => new()
    {
        Kind = kind, ChangeKind = change, ResourceKey = SnapshotIdentity.LegacyResourceKey(kind, "dbo", name),
        DisplayName = "mixed " + name, RelativePath = "mixed/" + name, PreviousPayload = previous
    };

    private static async Task<(long Key, Dictionary<string, long> Versions)> SeedMixedHistoryAsync(RelationalSession session,
        RelationalContentStore content, RelationalCaptureStore capture, DatabaseSnapshotLibrary library)
    {
        var source = library.Snapshots[0];
        var resources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var tableRevisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        var datasets = new List<(long Resource, long Revision, SqlTableDataSet Data, long? CurrentOrder)>();
        long snapshot = 0;
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            snapshot = await writer.CreateSnapshotAsync(new(source.SnapshotId, source.DisplayName, source.DatabaseName, source.ImportedAtUtc, 20));
            for (int i = 0; i < source.Objects.Count; i++)
            {
                var value = source.Objects[i];
                var handle = await writer.InsertCurrentObjectAsync(snapshot, value, i);
                resources.TryAdd(SnapshotIdentity.LegacyResourceKey(SnapshotIdentity.ResourceKind(value.Kind)!.Value, value.SchemaName, value.ObjectName), handle.ResourceKey);
            }
            for (int i = 0; i < source.Tables.Count; i++)
            {
                var table = source.Tables[i];
                var handle = await writer.InsertCurrentTableAsync(snapshot, table, i);
                string key = SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.TableMetadata, table.SchemaName, table.TableName);
                resources.TryAdd(key, handle.ResourceKey);
                tableRevisions.TryAdd(key, handle.RevisionKey);
            }
            for (int i = 0; i < source.Columns.Count; i++)
            {
                var column = source.Columns[i];
                tableRevisions.TryGetValue(SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.TableMetadata, column.SchemaName, column.TableName), out long owner);
                await writer.InsertColumnAsync(snapshot, owner == 0 ? null : owner, column, i, i);
            }
            for (int i = 0; i < source.PrimaryKeys.Count; i++)
            {
                var key = source.PrimaryKeys[i];
                tableRevisions.TryGetValue(SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.TableMetadata, key.SchemaName, key.TableName), out long owner);
                await writer.InsertPrimaryKeyAsync(snapshot, owner == 0 ? null : owner, key, i, i);
            }
            for (int i = 0; i < source.FullDataTableNames.Count; i++) await writer.InsertFullDataSelectionAsync(snapshot, source.FullDataTableNames[i], i);
            for (int i = 0; i < source.TableDataSets.Count; i++)
            {
                var data = source.TableDataSets[i];
                string original = SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.TableData, data.SchemaName, data.TableName);
                long resource = await writer.CreateResourceAsync(snapshot, DatabaseVersionedResourceKind.TableData, data.SchemaName, data.TableName, original, i);
                resources.TryAdd(original, resource);
                long revision = await writer.InsertTableDataRevisionAsync(resource, data.SchemaName, data.TableName);
                datasets.Add((resource, revision, data, i));
            }
            long history = await writer.InsertHistoryAsync(snapshot, source.SnapshotId, 20, 0);
            for (int i = 0; i < library.Histories[0].Versions.Count; i++)
            {
                var version = library.Histories[0].Versions[i];
                long versionKey = await writer.InsertVersionAsync(history, new(version.VersionId, version.VersionName, version.VersionNumber, version.CreatedAtUtc, version.IsInitial, i));
                versions.Add(version.VersionId, versionKey);
                for (int j = 0; j < version.Changes.Count; j++)
                {
                    var change = version.Changes[j];
                    if (!resources.TryGetValue(change.ResourceKey, out long resource))
                    {
                        string name = change.ResourceKey.Split('|', 2)[1].Split('.', 2)[1];
                        resource = await writer.CreateResourceAsync(snapshot, change.Kind, "dbo", name, change.ResourceKey, 100 + resources.Count);
                        resources.Add(change.ResourceKey, resource);
                    }
                    long? revision = null;
                    var payload = change.PreviousPayload;
                    if (payload?.DatabaseObject != null)
                        revision = await writer.InsertObjectRevisionAsync(resource, payload.DatabaseObject);
                    else if (payload?.Kind == DatabaseVersionedResourceKind.TableMetadata && payload.Table != null)
                    {
                        revision = await writer.InsertTableRevisionAsync(resource, payload.Table);
                        for (int c = 0; c < payload.Columns.Count; c++) await writer.InsertColumnAsync(snapshot, revision, payload.Columns[c], c);
                        for (int k = 0; k < payload.PrimaryKeys.Count; k++) await writer.InsertPrimaryKeyAsync(snapshot, revision, payload.PrimaryKeys[k], k);
                    }
                    else if (payload?.TableDataSet != null)
                    {
                        revision = await writer.InsertTableDataRevisionAsync(resource, payload.TableDataSet.SchemaName, payload.TableDataSet.TableName, payload.Table);
                        datasets.Add((resource, revision.Value, payload.TableDataSet, null));
                    }
                    if (revision.HasValue && change.Kind != DatabaseVersionedResourceKind.TableData) await writer.SealRevisionAsync(revision.Value);
                    await writer.InsertChangeAsync(versionKey, resource, new(change.Kind, change.ChangeKind, change.ResourceKey,
                        change.DisplayName, change.RelativePath, j, revision));
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT RevisionKey FROM surf.TableMetadataRevision WHERE SnapshotKey=@Snapshot;";
            command.Parameters.Add(RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshot));
            var metadataRevisions = new List<long>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) metadataRevisions.Add(reader.GetInt64(0));
            foreach (long revision in metadataRevisions) await writer.SealRevisionAsync(revision);
        });
        foreach (var pending in datasets)
        {
            var handle = await capture.CreateDataSetAsync(new(pending.Revision, new CaptureColumnDefinition[] { new("x") },
                pending.Data.RowCount, pending.Data.ImportedAtUtc, pending.Data.Rows.Count));
            await capture.AppendRowsAsync(handle, JsonRows(pending.Data.Rows), 0);
            await capture.CompleteDataSetAsync(handle);
        }
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            foreach (var pending in datasets)
            {
                await writer.SealRevisionAsync(pending.Revision);
                if (pending.CurrentOrder.HasValue) await writer.SetCurrentRevisionAsync(pending.Resource, pending.Revision, pending.CurrentOrder.Value);
            }
            await writer.PublishSnapshotAsync(snapshot, versions["mixed-latest"]);
        });
        return (snapshot, versions);
    }

    private static bool SameTableName(string schema, string name, string otherSchema, string otherName) =>
        string.Equals(schema, otherSchema, StringComparison.OrdinalIgnoreCase) && string.Equals(name, otherName, StringComparison.OrdinalIgnoreCase);

    // Read only scalar children chosen by the production SQL projection. These are
    // independent test diagnostics, not a replacement history or migration implementation.
    private static async Task<SqlColumn> ReadMixedColumnAsync(RelationalSession session, long key)
    {
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SchemaName, TableName, ColumnName, DataType, MaxLength, NumericPrecision,
                NumericScale, IsNullable, IsIdentity, SourceOrdinal FROM surf.TableColumnRevision WHERE ColumnRevisionKey=@Key;
            """;
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, key));
        await using var r = await command.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("Projected regression column not found.");
        return new() { SchemaName = r.GetString(0), TableName = r.GetString(1), ColumnName = r.GetString(2), DataType = r.GetString(3),
            MaxLength = r.GetInt32(4), NumericPrecision = r.GetByte(5), NumericScale = r.GetInt32(6), IsNullable = r.GetBoolean(7),
            IsIdentity = r.GetBoolean(8), Ordinal = r.GetInt32(9) };
    }

    private static async Task<SqlPrimaryKeyColumn> ReadMixedKeyAsync(RelationalSession session, long key)
    {
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SchemaName, TableName, ConstraintName, ColumnName, KeyOrdinal FROM surf.PrimaryKeyColumn WHERE KeyColumnKey=@Key;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, key));
        await using var r = await command.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("Projected regression key not found.");
        return new() { SchemaName = r.GetString(0), TableName = r.GetString(1), ConstraintName = r.GetString(2), ColumnName = r.GetString(3), KeyOrdinal = r.GetInt32(4) };
    }
}

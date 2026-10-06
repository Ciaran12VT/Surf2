using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private sealed record SnapshotFixture(long Key, long OtherKey, long HistoryKey,
        long InitialVersion, long MiddleVersion, long CurrentVersion,
        SnapshotRevisionHandle CurrentObject, long OldObjectRevision, long MiddleObjectRevision,
        SnapshotRevisionHandle AddedObject, SnapshotRevisionHandle DeletedObject,
        SnapshotRevisionHandle UnchangedObject, SnapshotRevisionHandle UnknownObject,
        SnapshotRevisionHandle Table, long OldTableRevision, long DataResource,
        long CurrentDataRevision, long OldDataRevision, SqlDatabaseObject CurrentObjectValue,
        SqlDatabaseObject OldObjectValue, SqlTable TableValue, SqlTable OldTableValue,
        SqlColumn[] Columns, SqlPrimaryKeyColumn[] Keys);

    private static async Task<SnapshotFixture> SeedSnapshotsAsync(RelationalSession session, RelationalContentStore content)
    {
        SnapshotFixture? result = null;
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            long key = await writer.CreateSnapshotAsync(new("same-legacy-id ", "Snapshot \u00e9 ", "evidence database ", EvidenceTime, 7));
            long other = await writer.CreateSnapshotAsync(new("same-legacy-id ", "Duplicate identity", "other fixture", EvidenceTime, 7));
            await writer.CreateSnapshotAsync(new("unpublished", "Hidden staged snapshot", "fixture", EvidenceTime, 0));
            var currentValue = Object(SqlDatabaseObjectKind.StoredProcedure, "Case ", "current\r\nSELECT N'\u00e9\ud83d\ude00';\n");
            var current = await writer.InsertCurrentObjectAsync(key, currentValue, 2, "legacy-key ");
            var oldValue = Object(SqlDatabaseObjectKind.StoredProcedure, "Case ", "old\r\nSELECT 1.2300;\n");
            long ignoredOld = await writer.InsertObjectRevisionAsync(current.ResourceKey,
                Object(SqlDatabaseObjectKind.StoredProcedure, "Case ", "earlier change at the same version"));
            long oldRevision = await writer.InsertObjectRevisionAsync(current.ResourceKey, oldValue);
            long middleRevision = await writer.InsertObjectRevisionAsync(current.ResourceKey,
                Object(SqlDatabaseObjectKind.StoredProcedure, "Case ", "middle\nSELECT -0;"));
            await writer.SealRevisionAsync(ignoredOld);
            await writer.SealRevisionAsync(oldRevision);
            await writer.SealRevisionAsync(middleRevision);
            var added = await writer.InsertCurrentObjectAsync(key, Object(SqlDatabaseObjectKind.View, "case", "added view"), 2, "legacy-key ");
            var unchanged = await writer.InsertCurrentObjectAsync(key, Object(SqlDatabaseObjectKind.Function, "Case", "unchanged function"), 2, "legacy-key");
            var unknown = await writer.InsertCurrentObjectAsync(key, Object(SqlDatabaseObjectKind.Unknown, "unknown", "unknown source kind"), 9);
            long deletedResource = await writer.CreateResourceAsync(key, DatabaseVersionedResourceKind.Trigger,
                "schema] ", "deleted", "deleted-key", 3);
            long deletedRevision = await writer.InsertObjectRevisionAsync(deletedResource,
                Object(SqlDatabaseObjectKind.Trigger, "deleted", "deleted trigger"));
            await writer.SealRevisionAsync(deletedRevision);

            var tableValue = new SqlTable { SchemaName = "schema] ", TableName = "T ", HasFullData = true,
                FullDataRowCount = 1234567890123, FullDataImportedAtUtc = EvidenceTime };
            var table = await writer.InsertCurrentTableAsync(key, tableValue, 4);
            SqlColumn[] columns =
            [
                new() { SchemaName = tableValue.SchemaName, TableName = tableValue.TableName, ColumnName = "Id ",
                    DataType = "decimal", MaxLength = 17, NumericPrecision = 38, NumericScale = 7,
                    IsNullable = false, IsIdentity = true, Ordinal = 9 },
                new() { SchemaName = tableValue.SchemaName, TableName = tableValue.TableName, ColumnName = "id",
                    DataType = "nvarchar(max)", MaxLength = -1, NumericPrecision = 0, NumericScale = 0,
                    IsNullable = true, IsIdentity = false, Ordinal = 2 }
            ];
            SqlPrimaryKeyColumn[] keys =
            [
                new() { SchemaName = tableValue.SchemaName, TableName = tableValue.TableName, ConstraintName = "PK ", ColumnName = "Id ", KeyOrdinal = 2 },
                new() { SchemaName = tableValue.SchemaName, TableName = tableValue.TableName, ConstraintName = "PK ", ColumnName = "id", KeyOrdinal = 1 }
            ];
            for (int i = 0; i < columns.Length; i++) await writer.InsertColumnAsync(key, table.RevisionKey, columns[i], 5, i);
            for (int i = 0; i < keys.Length; i++) await writer.InsertPrimaryKeyAsync(key, table.RevisionKey, keys[i], 5, i);
            await writer.InsertColumnAsync(key, null, new SqlColumn { SchemaName = "orphan", TableName = "missing", ColumnName = "orphan", DataType = "int" }, 0, 2);
            await writer.InsertPrimaryKeyAsync(key, null, new SqlPrimaryKeyColumn { SchemaName = "orphan", TableName = "missing", ConstraintName = "orphan-key", ColumnName = "orphan", KeyOrdinal = 0 }, 0, 2);
            await writer.SealRevisionAsync(table.RevisionKey);
            var oldTableValue = new SqlTable { SchemaName = tableValue.SchemaName, TableName = tableValue.TableName,
                HasFullData = false, FullDataRowCount = -7, FullDataImportedAtUtc = null };
            long oldTable = await writer.InsertTableRevisionAsync(table.ResourceKey, oldTableValue);
            await writer.InsertColumnAsync(key, oldTable, columns[1], 0);
            await writer.InsertPrimaryKeyAsync(key, oldTable, keys[1], 0);
            await writer.SealRevisionAsync(oldTable);
            await writer.InsertFullDataSelectionAsync(key, "schema].T ", 0);
            await writer.InsertFullDataSelectionAsync(key, "schema].T ", 0);
            await writer.InsertFullDataSelectionAsync(key, "missing.selected", 1);

            long dataResource = await writer.CreateResourceAsync(key, DatabaseVersionedResourceKind.TableData,
                tableValue.SchemaName, tableValue.TableName, "data-key", 5);
            long currentData = await writer.InsertTableDataRevisionAsync(dataResource);
            long oldData = await writer.InsertTableDataRevisionAsync(dataResource, tableValue.SchemaName, tableValue.TableName, oldTableValue);
            long history = await writer.InsertHistoryAsync(key, "history-original-id ", 40, 6);
            long initial = await writer.InsertVersionAsync(history, new("v-initial ", "Initial", 4, EvidenceTime, true, 8));
            long middle = await writer.InsertVersionAsync(history, new("v-middle", "Middle", 9, EvidenceTime.AddTicks(1), false, 8));
            long latest = await writer.InsertVersionAsync(history, new("v-latest", "Current", 17, EvidenceTime.AddTicks(2), false, 9));
            await writer.InsertChangeAsync(middle, current.ResourceKey, Change(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, 0, ignoredOld));
            await writer.InsertChangeAsync(middle, current.ResourceKey, Change(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, 1, oldRevision));
            await writer.InsertChangeAsync(middle, current.ResourceKey, Change(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, 2, null));
            await writer.InsertChangeAsync(middle, added.ResourceKey, Change(DatabaseVersionedResourceKind.View, DatabaseSnapshotResourceChangeKind.Added, 3, null,
                SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.View, "schema] ", "case")));
            await writer.InsertChangeAsync(middle, deletedResource, Change(DatabaseVersionedResourceKind.Trigger, DatabaseSnapshotResourceChangeKind.Deleted, 4, deletedRevision));
            await writer.InsertChangeAsync(middle, unchanged.ResourceKey, Change(DatabaseVersionedResourceKind.Function, DatabaseSnapshotResourceChangeKind.Modified, 5, null));
            await writer.InsertChangeAsync(middle, table.ResourceKey, Change(DatabaseVersionedResourceKind.TableMetadata, DatabaseSnapshotResourceChangeKind.Modified, 6, oldTable));
            await writer.InsertChangeAsync(latest, current.ResourceKey, Change(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, 0, middleRevision));
            await writer.InsertChangeAsync(latest, dataResource, Change(DatabaseVersionedResourceKind.TableData, DatabaseSnapshotResourceChangeKind.Modified, 1, oldData));
            await writer.PublishSnapshotAsync(other, null);
            result = new(key, other, history, initial, middle, latest, current, oldRevision, middleRevision,
                added, new(deletedResource, deletedRevision), unchanged, unknown, table, oldTable,
                dataResource, currentData, oldData, currentValue, oldValue, tableValue, oldTableValue, columns, keys);
        });
        return Required(result, "snapshot seed");
    }

    private static SqlDatabaseObject Object(SqlDatabaseObjectKind kind, string name, string definition) => new()
    {
        SchemaName = "schema] ", ObjectName = name, Kind = kind, TypeDescription = "type description ",
        ParentSchemaName = "parent.schema ", ParentObjectName = "parent] ", Definition = definition
    };

    private static SnapshotChangeHeader Change(DatabaseVersionedResourceKind kind,
        DatabaseSnapshotResourceChangeKind change, long ordinal, long? previous, string original = "original change key ") =>
        new(kind, change, original, "Display \u00e9 ", "path/with space .sql", ordinal, previous);

    private static async Task VerifySnapshotsAsync(RelationalSession session, RelationalContentStore content,
        RelationalSnapshotStore store, SnapshotFixture f, string fixtureConnectionString, Action<bool, string> check)
    {
        var catalogue = await SnapshotPagesAsync(c => store.ListSnapshotsAsync(pageSize: 1, cursor: c), check, "Snapshot catalogue");
        check(catalogue.Select(x => x.SnapshotKey).SequenceEqual(new[] { f.Key, f.OtherKey }) &&
            catalogue.All(x => x.SnapshotId == "same-legacy-id "), "Duplicate legacy snapshot IDs and tied order remain separate; unpublished snapshot is hidden");
        var head = Required(await store.GetSnapshotAsync(f.Key), "snapshot head");
        check(head.DisplayName == "Snapshot \u00e9 " && head.DatabaseName == "evidence database " &&
            head.ImportedAtUtc == EvidenceTime && head.ImportedAtUtc.Offset == EvidenceTime.Offset &&
            head.CurrentVersionKey == f.CurrentVersion, "Snapshot header, offset, ticks and current version preserved");
        var current = await SnapshotPagesAsync(c => store.ListResourcesAsync(f.Key, pageSize: 1, cursor: c), check, "Current resources");
        check(current.Count == 6 && current.Select(x => x.ResourceKey).Distinct().Count() == 6 &&
            current.All(x => x.ResourceKey != f.DeletedObject.ResourceKey), "Current typed resources exclude deleted heads without deduplicating names");
        var objects = await SnapshotPagesAsync(c => store.ListObjectsAsync(f.Key, pageSize: 1, cursor: c), check, "Current objects");
        check(objects.Count == 4 && objects.Single(x => x.Resource.ResourceKey == f.UnknownObject.ResourceKey).Resource.Kind == null &&
            objects.Single(x => x.Resource.ResourceKey == f.UnknownObject.ResourceKey).ObjectKind == SqlDatabaseObjectKind.Unknown,
            "Object summaries retain Unknown and typed discriminators");
        SameModel(f.CurrentObjectValue, await store.ReadObjectAsync(f.CurrentObject.RevisionKey), check, "Current object all fields and exact definition preserved");
        var tables = await store.ListTablesAsync(f.Key);
        check(tables.Items.Count == 1 && tables.Items[0].HasFullData && tables.Items[0].FullDataRowCount == f.TableValue.FullDataRowCount,
            "Table summary does not load or substitute captured row counts");
        var details = await store.ReadTableMetadataAsync(f.Table.RevisionKey);
        SameModel(f.TableValue, details.Table, check, "Typed table metadata preserved");
        SameModel(f.Columns, details.Columns.Select(x => x.Column).ToArray(), check, "All column types, precision, scale, identity and source order preserved");
        SameModel(f.Keys, details.PrimaryKeys.Select(x => x.Column).ToArray(), check, "Composite primary key columns preserve collection order independently of key ordinal");
        var columns = await SnapshotPagesAsync(c => store.ListCurrentColumnsAsync(f.Key, pageSize: 1, cursor: c), check, "Current columns");
        var keys = await SnapshotPagesAsync(c => store.ListCurrentPrimaryKeysAsync(f.Key, pageSize: 1, cursor: c), check, "Current keys");
        check(columns.Count == 3 && columns[2].Column.SchemaName == "orphan" && keys.Count == 3 && keys[2].Column.SchemaName == "orphan",
            "Snapshot-wide orphan columns and keys are retained");
        var selections = await SnapshotPagesAsync(c => store.ListFullDataSelectionsAsync(f.Key, pageSize: 1, cursor: c), check, "Full-data selections");
        check(selections.Select(x => x.TableName).SequenceEqual(new[] { "schema].T ", "schema].T ", "missing.selected" }),
            "Full-data selection duplicates and orphan names preserved");
        check((await store.FindResourceKeysAsync(f.Key, "legacy-key ")).SequenceEqual(new[] { f.CurrentObject.ResourceKey, f.AddedObject.ResourceKey }) &&
            (await store.FindResourceKeysAsync(f.Key, "legacy-key")).SequenceEqual(new[] { f.UnchangedObject.ResourceKey }) &&
            (await store.FindResourceKeysAsync(f.OtherKey, "legacy-key ")).Count == 0,
            "Exact legacy lookup preserves trailing spaces, ambiguity and owner boundary");

        var history = Required(await store.GetHistoryAsync(f.Key), "history");
        check(history.NextVersionNumber == 40 && history.SnapshotId == "history-original-id " && history.SortOrdinal == 6,
            "History identity and next number are not synthesized");
        var versions = await SnapshotPagesAsync(c => store.ListVersionsAsync(f.Key, pageSize: 1, cursor: c), check, "History versions");
        check(versions.Select(x => x.VersionNumber).SequenceEqual(new[] { 4, 9, 17 }) && versions[0].IsInitial &&
            versions.Select(x => x.ChangeCount).SequenceEqual(new long[] { 0, 7, 2 }), "History preserves number gaps, tied order, initial flag and bounded change counts");
        var changes = await SnapshotPagesAsync(c => store.ListChangesAsync(f.Key, f.MiddleVersion, pageSize: 1, cursor: c), check, "Reverse changes");
        check(changes.Count == 7 && changes[0].PreviousRevisionKey != changes[1].PreviousRevisionKey && changes[2].PreviousRevisionKey == null &&
            changes.Where(x => x.ChangeKind != DatabaseSnapshotResourceChangeKind.Added).All(x => x.OriginalResourceKey == "original change key ") &&
            changes.All(x => x.DisplayName == "Display \u00e9 " && x.RelativePath == "path/with space .sql"),
            "Reverse history changes retain duplicates, null payloads and original text");
        var initialResources = await SnapshotPagesAsync(c => store.ListResourcesAsync(f.Key, versionKey: f.InitialVersion, pageSize: 1, cursor: c), check, "Initial reverse resources");
        check(initialResources.Count == 5 && initialResources.All(x => x.ResourceKey != f.AddedObject.ResourceKey && x.ResourceKey != f.DataResource) &&
            initialResources.Any(x => x.ResourceKey == f.DeletedObject.ResourceKey), "Reverse Added removes, reverse Deleted restores, and metadata rollback removes its data resource");
        check(Required(await store.ResolveResourceAsync(f.CurrentObject.ResourceKey, f.InitialVersion), "old object").RevisionKey == f.OldObjectRevision &&
            Required(await store.ResolveResourceAsync(f.CurrentObject.ResourceKey, f.MiddleVersion), "middle object").RevisionKey == f.MiddleObjectRevision &&
            Required(await store.ResolveResourceAsync(f.CurrentObject.ResourceKey, f.CurrentVersion), "latest object").RevisionKey == f.CurrentObject.RevisionKey,
            "Reverse resolution chooses earliest later version, last effective change and ignores null Modified");
        check(Required(await store.ResolveResourceAsync(f.UnchangedObject.ResourceKey, f.InitialVersion), "null rollback").RevisionKey == f.UnchangedObject.RevisionKey &&
            await store.ResolveResourceAsync(f.AddedObject.ResourceKey, f.InitialVersion) == null,
            "Null previous payload is a no-op, not an absent resource");
        SameModel(f.OldObjectValue, await store.ReadObjectAsync(f.OldObjectRevision), check, "Historical definition remains immutable and readable");
        var oldTable = await store.ReadTableMetadataAsync(f.OldTableRevision);
        SameModel(f.OldTableValue, oldTable.Table, check, "Historical table retains negative evidence count and nullable timestamp");
        SameModel(new[] { f.Columns[1] }, oldTable.Columns.Select(x => x.Column).ToArray(), check, "Historical table columns do not come from current metadata");
        var currentData = await store.ReadTableDataMetadataAsync(f.CurrentDataRevision);
        var oldData = await store.ReadTableDataMetadataAsync(f.OldDataRevision);
        check(currentData.Table == null && currentData.SchemaName == f.TableValue.SchemaName && currentData.TableName == f.TableValue.TableName &&
            oldData.SchemaName == f.TableValue.SchemaName && oldData.TableName == f.TableValue.TableName, "TableData schema/name and null previous Table are independent of table metadata");
        SameModel(f.OldTableValue, oldData.Table!, check, "TableData previous Table payload is preserved independently");
        await ThrowsAsync<KeyNotFoundException>(() => store.ListResourcesAsync(f.OtherKey, versionKey: f.InitialVersion), check,
            "Version from a different snapshot is rejected");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListResourcesAsync(f.Key, pageSize: 1001), check,
            "Snapshot page row ceiling is enforced");

        var first = await store.ListResourcesAsync(f.Key, pageSize: 1);
        var cursor = Required(first.Next, "resource cursor");
        await ThrowsAsync<ArgumentException>(() => store.ListResourcesAsync(f.OtherKey, pageSize: 1, cursor: cursor), check, "Snapshot cursor cannot cross owners");
        await ThrowsAsync<ArgumentException>(() => store.ListObjectsAsync(f.Key, pageSize: 1, cursor: cursor), check, "Snapshot cursor cannot cross query types");
        var otherSession = new RelationalSession(fixtureConnectionString);
        var otherStore = new RelationalSnapshotStore(otherSession, content);
        await ThrowsAsync<ArgumentException>(() => otherStore.ListResourcesAsync(f.Key, pageSize: 1, cursor: cursor), check, "Snapshot cursor cannot cross connection epochs");
        byte[] changed = await store.ExecuteMutationAsync(f.Key, head.RowVersion,
            (writer, ct) => writer.UpdateSnapshotHeaderAsync(f.Key, "Targeted header", "evidence database ", EvidenceTime, 7, ct));
        check(!changed.SequenceEqual(head.RowVersion), "Targeted snapshot mutation advances its optimistic token");
        await ThrowsAsync<SnapshotConcurrencyException>(() => store.ListResourcesAsync(f.Key, pageSize: 1, cursor: cursor), check,
            "Snapshot head change invalidates an in-flight page");
        bool staleCallbackRan = false;
        await ThrowsAsync<SnapshotConcurrencyException>(() => store.ExecuteMutationAsync(f.Key, head.RowVersion, (_, _) =>
        {
            staleCallbackRan = true;
            return Task.CompletedTask;
        }), check, "Stale snapshot writer is rejected");
        check(!staleCallbackRan, "Stale snapshot writer never executes its callback");
        await ThrowsAsync<FixtureMutationException>(() => store.ExecuteMutationAsync(f.Key, changed, async (writer, ct) =>
        {
            await writer.UpdateSnapshotHeaderAsync(f.Key, "Must roll back", "not persisted", EvidenceTime, 99, ct);
            throw new FixtureMutationException();
        }), check, "Snapshot mutation failure propagates");
        var after = Required(await store.GetSnapshotAsync(f.Key), "after rollback");
        check(after.DisplayName == "Targeted header" && after.RowVersion.SequenceEqual(changed), "Failed mutation rolls back header and rowversion");
        check((await store.ListResourcesAsync(f.Key)).Items.Count == current.Count &&
            (await store.GetSnapshotAsync(f.OtherKey))!.DisplayName == "Duplicate identity", "Targeted mutation preserves unloaded resources and other snapshots");
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            var resource = Required(await store.ResolveResourceAsync(f.CurrentObject.ResourceKey), "resource token");
            await writer.SetCurrentRevisionAsync(resource.ResourceKey, resource.RevisionKey, resource.SortOrdinal, resource.RowVersion);
            await ThrowsAsync<SnapshotConcurrencyException>(() => writer.SetCurrentRevisionAsync(resource.ResourceKey,
                resource.RevisionKey, resource.SortOrdinal, resource.RowVersion), check, "Resource rowversion rejects a repeated stale head write");
            await writer.SetHistoryNextVersionAsync(f.HistoryKey, 41, history.RowVersion);
            await ThrowsAsync<SnapshotConcurrencyException>(() => writer.SetHistoryNextVersionAsync(f.HistoryKey, 42, history.RowVersion), check,
                "History rowversion rejects a stale next-number write");
        });
    }

    private sealed class FixtureMutationException : Exception { }
}

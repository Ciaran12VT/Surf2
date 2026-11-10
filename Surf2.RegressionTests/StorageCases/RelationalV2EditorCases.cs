using System.Text.Json;
using System.IO;
using Surf2.Models;
using Surf2.Storage;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyRelationalV2EditorAsync(Action<bool, string> check)
    {
        var models = MigrationModels();
        string snapshotId = models.Snapshots.Snapshots[0].SnapshotId;
        var snapshot = new DatabaseMetadataSnapshot
        {
            SnapshotId = snapshotId, DisplayName = "Master pasted metadata", DatabaseName = "fixture", ImportedAtUtc = EvidenceTime,
            Objects = [new() { SchemaName = "reporting.v2", ObjectName = "Recalculate", Kind = SqlDatabaseObjectKind.StoredProcedure,
                Definition = "CREATE PROCEDURE [reporting.v2].Recalculate AS SELECT 1;" }],
            Tables = [new() { SchemaName = "dbo", TableName = "Items", HasFullData = true, FullDataRowCount = 1, FullDataImportedAtUtc = EvidenceTime },
                new() { SchemaName = "audit.v2", TableName = "Other.rows" }],
            Columns = [new() { SchemaName = "dbo", TableName = "Items", ColumnName = "Id", DataType = "int", Ordinal = 1 },
                new() { SchemaName = "audit.v2", TableName = "Other.rows", ColumnName = "Label", DataType = "nvarchar", Ordinal = 1 },
                new() { SchemaName = "dbo", TableName = "Items", ColumnName = "Value", DataType = "int", Ordinal = 2 },
                new() { SchemaName = "dbo", TableName = "Orphan", ColumnName = "Detached", DataType = "int", Ordinal = 1 }],
            TableDataSets = [new() { SchemaName = "dbo", TableName = "Items", RowCount = 1, ImportedAtUtc = EvidenceTime,
                Rows = [JsonSerializer.SerializeToElement(new { Id = 2, Value = 42 })] }],
            FullDataTableNames = ["[dbo].[Items] WHERE [Id] > 0", "[audit.v2].[Other.rows] TOP 7"]
        };
        models.Snapshots.Snapshots.Clear(); models.Snapshots.Snapshots.Add(snapshot); models.Snapshots.Histories.Clear();
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        var service = new RelationalSnapshotEditorService(runtime);
        var summary = (await runtime.Snapshots.ListSnapshotsAsync()).Items.Single();
        long scopeKey = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT ScopeKey FROM surf.Scope WHERE ScopeId=N'migration-scope';"));
        var scope = (await runtime.StateStore.ReadScopeAsync(scopeKey))!;
        var edited = await service.ReadAsync(summary);
        check(JsonSerializer.Serialize(edited, ModelJson) == JsonSerializer.Serialize(snapshot, ModelJson),
            "Explicit editor read preserves selected master metadata, row values, predicates and interleaved/orphan columns");
        string originalDefinition = edited.Objects[0].Definition;
        edited.Objects[0].Definition = "CREATE PROCEDURE [reporting.v2].Recalculate AS SELECT 2;";
        edited.FullDataTableNames = ["[dbo].[Items] TOP 1", "[audit.v2].[Other.rows] WHERE [Label] IS NOT NULL"];
        var expected = new SnapshotRuntimeToken(runtime.Session.Epoch, summary.SnapshotKey, summary.RowVersion);
        var saved = await service.SaveAsync(edited, scope.Value, scope.Token, expected);
        var reopened = await service.ReadAsync(saved.Snapshot);
        check(reopened.Objects[0].Definition == edited.Objects[0].Definition && reopened.Objects[0].SchemaName == "reporting.v2" &&
            reopened.Tables[1].SchemaName == "audit.v2" && reopened.Tables[1].TableName == "Other.rows" &&
            reopened.FullDataTableNames.SequenceEqual(edited.FullDataTableNames) &&
            reopened.Columns.Select(x => x.ColumnName).SequenceEqual(snapshot.Columns.Select(x => x.ColumnName)) &&
            reopened.TableDataSets[0].Rows[0].GetRawText() == snapshot.TableDataSets[0].Rows[0].GetRawText(),
            "Editing preserves dotted schema/table names, TOP/WHERE selections, global child order and captured rows");
        var versions = await runtime.Snapshots.ListVersionsAsync(summary.SnapshotKey);
        long initial = versions.Items.Single(v => v.IsInitial).VersionKey;
        await using (var history = await runtime.Snapshots.OpenHistoricalSnapshotAsync(summary.SnapshotKey, initial))
        {
            var objects = await history.ReadPageAsync(HistoricalCollection.Objects);
            var old = await runtime.Snapshots.ReadObjectAsync(objects.Items.Single().RevisionKey!.Value);
            var selections = await history.ReadPageAsync(HistoricalCollection.FullDataTableNames);
            var columns = await history.ReadPageAsync(HistoricalCollection.Columns);
            check(old.Definition == originalDefinition && selections.Items.Select(x => x.Name).SequenceEqual(snapshot.FullDataTableNames) &&
                columns.Items.Select(x => x.Name).SequenceEqual(snapshot.Columns.Select(x => x.TableName)),
                "Prior version retains the original definition, exact predicates and interleaved/orphan column order");
        }
        bool staleSnapshot = false;
        try { await service.SaveAsync(edited, saved.UpdatedScope, saved.UpdatedScopeToken, expected); }
        catch (SnapshotConcurrencyException) { staleSnapshot = true; }
        check(staleSnapshot, "The editor rejects a stale snapshot expectation");
        var secondEdit = edited.Clone(); secondEdit.Objects[0].Definition = "CREATE PROCEDURE [reporting.v2].Recalculate AS SELECT 3;";
        secondEdit.FullDataTableNames = ["[dbo].[Items] WHERE [Id] > 1"];
        var secondSave = await service.SaveAsync(secondEdit, saved.UpdatedScope, saved.UpdatedScopeToken,
            new(runtime.Session.Epoch, saved.Snapshot.SnapshotKey, saved.Snapshot.RowVersion));
        var imported = edited.Clone(); imported.DisplayName = "Second pasted snapshot";
        var added = await service.SaveAsync(imported, secondSave.UpdatedScope, secondSave.UpdatedScopeToken, null);
        check(added.Snapshot.SnapshotKey != saved.Snapshot.SnapshotKey && added.Snapshot.SnapshotId != snapshotId &&
            added.UpdatedScope.Resources.Any(x => x.Kind == ResourceKind.DatabaseSnapshot && x.Path == added.Snapshot.SnapshotId),
            "Pasted metadata creates a distinct snapshot and atomically adds its selected-scope membership");
        bool staleScope = false;
        try { await service.SaveAsync(secondEdit, secondSave.UpdatedScope, secondSave.UpdatedScopeToken,
            new(runtime.Session.Epoch, secondSave.Snapshot.SnapshotKey, secondSave.Snapshot.RowVersion)); }
        catch (StateConflictException) { staleScope = true; }
        check(staleScope, "The editor rejects a scope expectation made stale by a newly imported snapshot");

        string package = Path.Combine(fixture.OwnedDirectory, "v2-editor.surf2.zip");
        var exported = await new RelationalPackageExporter(runtime.Session).ExportAsync(package,
            new(AllowBlockingConsistentFallback: true));
        await using (var destination = await SqlFixture.CreateAsync())
        {
            await new RelationalPackageImporter(destination.DestinationConnectionString).ImportAsync(package,
                PackageImportOptions(exported.ManifestSha256, Guid.NewGuid()));
            var restoredRuntime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(destination.DestinationConnectionString));
            var restoredEditor = new RelationalSnapshotEditorService(restoredRuntime);
            var restoredHead = (await restoredRuntime.Snapshots.GetSnapshotAsync(summary.SnapshotKey))!;
            var restoredCurrent = await restoredEditor.ReadAsync(restoredHead);
            check(restoredCurrent.Objects[0].Definition == secondEdit.Objects[0].Definition &&
                restoredCurrent.FullDataTableNames.SequenceEqual(secondEdit.FullDataTableNames),
                "V2 package roundtrip retains the latest edited definition and predicate");
            foreach (var (versionKey, definition, selections) in new[] {
                (initial, originalDefinition, snapshot.FullDataTableNames),
                (saved.VersionKey, edited.Objects[0].Definition, edited.FullDataTableNames) })
            {
                await using var history = await restoredRuntime.Snapshots.OpenHistoricalSnapshotAsync(summary.SnapshotKey, versionKey);
                var objects = await history.ReadPageAsync(HistoricalCollection.Objects);
                var old = await restoredRuntime.Snapshots.ReadObjectAsync(objects.Items.Single().RevisionKey!.Value);
                var oldSelections = await history.ReadPageAsync(HistoricalCollection.FullDataTableNames);
                var oldColumns = await history.ReadPageAsync(HistoricalCollection.Columns);
                check(old.Definition == definition && oldSelections.Items.Select(x => x.Name).SequenceEqual(selections) &&
                    oldColumns.Items.Select(x => x.Name).SequenceEqual(snapshot.Columns.Select(x => x.TableName)),
                    "V2 package roundtrip retains an exact earlier metadata baseline across two edited versions");
            }
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

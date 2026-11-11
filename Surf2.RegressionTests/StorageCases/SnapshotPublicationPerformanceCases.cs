using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunSnapshotPublicationPerformanceChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels();
        var original = new DatabaseMetadataSnapshot { SnapshotId = models.Snapshots.Snapshots[0].SnapshotId,
            DisplayName = "Publication reuse", DatabaseName = "fixture", ImportedAtUtc = EvidenceTime };
        for (int i = 0; i < 100; i++) original.Objects.Add(new() { SchemaName = "dbo", ObjectName = $"P{i:D3}",
            Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = $"CREATE PROC dbo.P{i:D3} AS SELECT {i};" });
        foreach (string name in new[] { "First", "Second" })
        {
            original.Tables.Add(new() { SchemaName = "dbo", TableName = name, HasFullData = true,
                FullDataRowCount = 40, FullDataImportedAtUtc = EvidenceTime });
            original.Columns.Add(new() { SchemaName = "dbo", TableName = name, ColumnName = "Id", DataType = "int", Ordinal = 1 });
            original.PrimaryKeys.Add(new() { SchemaName = "dbo", TableName = name, ConstraintName = "PK_" + name,
                ColumnName = "Id", KeyOrdinal = 1 });
            var data = new SqlTableDataSet { SchemaName = "dbo", TableName = name, RowCount = 40, ImportedAtUtc = EvidenceTime };
            for (int i = 0; i < 40; i++) data.Rows.Add(JsonSerializer.SerializeToElement(new { Id = i, Label = name + i }));
            original.TableDataSets.Add(data);
            original.FullDataTableNames.Add("[dbo].[" + name + "] WHERE [Id] >= 0");
        }
        original.Columns.Insert(1, new() { SchemaName = "dbo", TableName = "Orphan", ColumnName = "Detached", DataType = "int", Ordinal = 7 });
        models.Snapshots.Snapshots.Clear(); models.Snapshots.Snapshots.Add(original); models.Snapshots.Histories.Clear();
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        // Legacy captures can carry companion scalars that differ from the current
        // table. Model a valid such discrepancy in this generated fixture only.
        await fixture.DestinationSqlAsync("""
            INSERT surf.TableDataRevisionMetadata(RevisionKey,SchemaName,TableName,HasFullData,FullDataRowCount,FullDataImportedAtUtc)
            SELECT d.RevisionKey,d.SchemaName,d.TableName,1,9000,ds.ImportedAtUtc
            FROM surf.TableDataRevision d JOIN surf.DataSet ds ON ds.RevisionKey=d.RevisionKey
            WHERE d.TableName=N'First' AND NOT EXISTS
                (SELECT 1 FROM surf.TableDataRevisionMetadata m WHERE m.RevisionKey=d.RevisionKey);
            UPDATE surf.TableDataRevisionMetadata SET FullDataRowCount=9000 WHERE TableName=N'First';
            """);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_Publication_" + Guid.NewGuid().ToString("N"), Pooling = true }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var editor = new RelationalSnapshotEditorService(runtime);
        var head = (await runtime.Snapshots.ListSnapshotsAsync()).Items.Single();
        long scopeKey = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT ScopeKey FROM surf.Scope WHERE ScopeId=N'migration-scope';"));
        var scope = (await runtime.StateStore.ReadScopeAsync(scopeKey))!;
        var edited = await editor.ReadAsync(head);
        var before = (await runtime.Snapshots.ListResourcesAsync(head.SnapshotKey, pageSize: 128)).Items.ToDictionary(value => value.ResourceKey);
        long revisions = await CountAsync("SnapshotResourceRevision"), dataSets = await CountAsync("DataSet");
        byte[] catalogue = (byte[])(await fixture.DestinationSqlAsync("SELECT RowVersion FROM surf.SnapshotCatalogueHead WHERE UserKey=1;"))!;
        RelationalCaptureResult saved;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            saved = await editor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
            check(trace.Commands.Count < 80, "A 100-object no-op editor save uses bounded batch reads instead of per-object staging transactions");
            check(!trace.Commands.Any(command => command.Text.Contains("CAPTURE.DATA_", StringComparison.Ordinal) ||
                command.Text.Contains("INSERT SURF.DATASET", StringComparison.Ordinal)),
                "An editor no-op reuses every captured dataset without reading or writing its physical rows");
        }
        byte[] afterNoOpCatalogue = (byte[])(await fixture.DestinationSqlAsync("SELECT RowVersion FROM surf.SnapshotCatalogueHead WHERE UserKey=1;"))!;
        check(saved.VersionKey == 0 && saved.Snapshot.CurrentVersionKey == null && saved.Snapshot.RowVersion.SequenceEqual(head.RowVersion) &&
            saved.UpdatedScopeToken.Version.SequenceEqual(scope.Token.Version) && await CountAsync("SnapshotVersion") == 0 &&
            await CountAsync("SnapshotResourceRevision") == revisions && await CountAsync("DataSet") == dataSets &&
            catalogue.SequenceEqual(afterNoOpCatalogue),
            "A migrated snapshot with no history remains a true no-op: no version, revision, dataset, head or scope token changes");
        var firstCapture = before.Values.Single(value => value.Kind == DatabaseVersionedResourceKind.TableData && value.ObjectName == "First");
        check((await runtime.Snapshots.ReadTableDataMetadataAsync(firstCapture.RevisionKey)).Table!.FullDataRowCount == 9000,
            "An unchanged editor save preserves differing legacy capture companion metadata exactly");

        edited.Objects[37].Definition += " -- edited";
        using (var trace = new StateAccessSqlTrace(connection))
        {
            saved = await editor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
            check(trace.Commands.Count(command => command.Text.StartsWith("UPDATE SURF.SNAPSHOTRESOURCE SET CURRENTREVISIONKEY=", StringComparison.Ordinal)) == 1,
                "Editing one of 100 procedures updates only its current revision pointer");
            check(!trace.Commands.Any(command => command.Text.Contains("CAPTURE.DATA_", StringComparison.Ordinal) ||
                command.Text.Contains("INSERT SURF.DATASET", StringComparison.Ordinal)),
                "A procedure edit does not read or copy unchanged captures");
        }
        var after = (await runtime.Snapshots.ListResourcesAsync(head.SnapshotKey, pageSize: 128)).Items;
        var changes = await runtime.Snapshots.ListChangesAsync(head.SnapshotKey, saved.VersionKey);
        check(changes.Items.Count == 1 && changes.Items[0].ChangeKind == DatabaseSnapshotResourceChangeKind.Modified &&
            changes.Items[0].ResourceKey == before.Values.Single(value => value.ObjectName == "P037").ResourceKey &&
            after.Count(value => value.RevisionKey != before[value.ResourceKey].RevisionKey) == 1 &&
            await CountAsync("SnapshotResourceRevision") == revisions + 1 && await CountAsync("DataSet") == dataSets,
            "One procedure edit creates exactly one revision and Modified row while reusing every other resource and capture");
        long versions = await CountAsync("SnapshotVersion");
        head = saved.Snapshot;
        edited.DisplayName = "Renamed without file changes";
        saved = await editor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
        check(saved.Snapshot.DisplayName == edited.DisplayName && !saved.Snapshot.RowVersion.SequenceEqual(head.RowVersion) &&
            saved.VersionKey == head.CurrentVersionKey && await CountAsync("SnapshotVersion") == versions &&
            saved.UpdatedScopeToken.Version.SequenceEqual(scope.Token.Version),
            "Header-only edits persist and update the snapshot token without adding a history version or changing scope membership");
        await ThrowsAsync<SnapshotConcurrencyException>(() => editor.SaveAsync(edited, scope.Value, scope.Token, Token(head)), check,
            "Even an otherwise unchanged save rejects a stale snapshot token");
        head = saved.Snapshot;

        string previousSelection = edited.FullDataTableNames[0];
        edited.FullDataTableNames[0] = "[dbo].[First] TOP 7";
        saved = await editor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
        check(saved.VersionKey != head.CurrentVersionKey && (await runtime.Snapshots.ListChangesAsync(head.SnapshotKey, saved.VersionKey)).Items.Count == 0,
            "A predicate-only edit creates an exact version without false Modified file rows");
        await using (var prior = await runtime.Snapshots.OpenHistoricalSnapshotAsync(head.SnapshotKey, head.CurrentVersionKey!.Value))
            check((await prior.ReadPageAsync(HistoricalCollection.FullDataTableNames)).Items[0].Name == previousSelection,
                "A predicate-only publication retains the exact previous selection baseline");
        head = saved.Snapshot;
        var priorColumns = edited.Columns.Select(value => value.ColumnName).ToArray();
        (edited.Columns[0], edited.Columns[1]) = (edited.Columns[1], edited.Columns[0]);
        edited.Columns[0].ColumnName = "Detached edited";
        saved = await editor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
        check(saved.VersionKey != head.CurrentVersionKey && (await runtime.Snapshots.ListChangesAsync(head.SnapshotKey, saved.VersionKey)).Items.Count == 0,
            "Global child order and orphan-only edits are versioned even when all rendered resource files are unchanged");
        await using (var prior = await runtime.Snapshots.OpenHistoricalSnapshotAsync(head.SnapshotKey, head.CurrentVersionKey!.Value))
        {
            var oldColumns = (await prior.ReadPageAsync(HistoricalCollection.Columns)).Items;
            var names = new List<string>();
            foreach (var entry in oldColumns) names.Add((await runtime.Snapshots.ReadRuntimeColumnAsync(head.SnapshotKey, entry.ChildKey!.Value, default)).ColumnName);
            check(names.SequenceEqual(priorColumns), "The exact prior baseline preserves interleaved and orphan column values and order");
        }
        head = saved.Snapshot;
        versions = await CountAsync("SnapshotVersion");
        var uncachedEditor = new RelationalSnapshotEditorService(runtime);
        saved = await uncachedEditor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
        check(saved.Snapshot.RowVersion.SequenceEqual(head.RowVersion) && await CountAsync("SnapshotVersion") == versions &&
            await CountAsync("DataSet") == dataSets,
            "A fresh editor also recognizes unchanged capture values and reuses their immutable datasets");

        // Restore stages table revisions independently of the editor. The publisher
        // must normalize equivalent sealed values before deciding whether to version.
        var restored = await new RelationalSnapshotHistoryService(runtime).RestoreVersionAsync(Token(head), head.CurrentVersionKey!.Value);
        var restoredCapture = (await runtime.Snapshots.ListResourcesAsync(head.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items
            .Single(value => value.ObjectName == "First");
        check(restored.VersionKey == head.CurrentVersionKey && await CountAsync("SnapshotVersion") == versions &&
            restoredCapture.RevisionKey == firstCapture.RevisionKey &&
            (await runtime.Snapshots.ReadTableDataMetadataAsync(restoredCapture.RevisionKey)).Table!.FullDataRowCount == 9000,
            "Restoring the current version reuses equivalent staged table payloads without manufacturing another history version");
        head = restored.Snapshot;

        edited.Tables[0].FullDataRowCount = 42;
        edited.Tables[0].FullDataImportedAtUtc = EvidenceTime.AddMinutes(1);
        saved = await editor.SaveAsync(edited, scope.Value, scope.Token, Token(head));
        var currentCapture = (await runtime.Snapshots.ListResourcesAsync(head.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items
            .Single(value => value.ObjectName == "First");
        var companion = (await runtime.Snapshots.ReadTableDataMetadataAsync(currentCapture.RevisionKey)).Table!;
        check(currentCapture.RevisionKey != firstCapture.RevisionKey && companion.FullDataRowCount == 42 &&
            companion.FullDataImportedAtUtc == edited.Tables[0].FullDataImportedAtUtc && await CountAsync("DataSet") == dataSets + 1,
            "An explicit table scalar edit refreshes its capture companion while reusing other unchanged captures");
        await using (var prior = await runtime.Snapshots.OpenHistoricalSnapshotAsync(head.SnapshotKey, head.CurrentVersionKey!.Value))
            check((await prior.ReadPageAsync(HistoricalCollection.Tables)).Items.Single(value => value.Name == "First").Table!.FullDataRowCount == 40,
                "The history baseline retains the previous table scalar after its capture companion changes");
        check((await runtime.Snapshots.ReadTableDataMetadataAsync(firstCapture.RevisionKey)).Table!.FullDataRowCount == 9000,
            "Refreshing a capture companion leaves its immutable historical metadata intact");
        head = saved.Snapshot;
        var changedScopeToken = await runtime.StateStore.SaveScopeAsync(scope.Value, scope.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => editor.SaveAsync(edited, scope.Value, scope.Token, Token(head)), check,
            "Even an otherwise unchanged save rejects a stale scope token");
        check(!changedScopeToken.Version.SequenceEqual(scope.Token.Version), "The scope conflict fixture changes its owned scope token");
        await fixture.VerifySourceUnchangedAsync(check);

        SnapshotRuntimeToken Token(SnapshotSummary value) => new(runtime.Session.Epoch, value.SnapshotKey, value.RowVersion);
        async Task<long> CountAsync(string table) => Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf." + table + ";"));
    }
}

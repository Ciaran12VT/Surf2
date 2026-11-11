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

public static partial class StorageRegressionSuite
{
    public static async Task RunHistoryFileRowsChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels(); string snapshotId = models.Snapshots.Snapshots[0].SnapshotId;
        var first = new DatabaseMetadataSnapshot
        {
            SnapshotId = snapshotId, DisplayName = "History rows", DatabaseName = "fixture", ImportedAtUtc = EvidenceTime,
            Objects = [new() { ObjectName = "P", Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = "SELECT 1;\r\nSELECT 2;\r\n" },
                new() { ObjectName = "Removed", Kind = SqlDatabaseObjectKind.View, Definition = "SELECT 3;\n" }],
            Tables = [new() { TableName = "Items", HasFullData = true, FullDataRowCount = 2 }],
            Columns = [new() { TableName = "Items", ColumnName = "Value", DataType = "nvarchar", MaxLength = 80, Ordinal = 9 }],
            TableDataSets = [new() { TableName = "Items", RowCount = 2, ImportedAtUtc = EvidenceTime,
                Rows = [JsonSerializer.SerializeToElement(new { Value = "line one\r\nline two", Omitted = "hidden" }),
                    JsonSerializer.SerializeToElement(new { Value = "" })] }],
            FullDataTableNames = ["dbo.Items"]
        };
        models.Snapshots.Snapshots.Clear(); models.Snapshots.Histories.Clear(); models.Snapshots.Snapshots.Add(first);
        var master = new DatabaseSnapshotHistoryService(new DatabaseDocumentService()); master.EnsureInitialVersion(models.Snapshots, first);
        var current = first.Clone(); current.Objects[0].Definition = "SELECT 1;\r\nSELECT 4;\r\nSELECT 5;\r\n";
        current.Objects.RemoveAt(1); current.Objects.Add(new() { ObjectName = "Added", Kind = SqlDatabaseObjectKind.Function, Definition = "RETURN 6;" });
        current.TableDataSets[0].Rows[1] = JsonSerializer.SerializeToElement(new { Value = "new,data" });
        master.RecordSnapshotReplacement(models.Snapshots, first, current);
        models.Snapshots.Snapshots[0] = current;
        var unversioned = first.Clone(); unversioned.SnapshotId = "history-rows-no-history"; unversioned.DisplayName = "No saved history";
        models.Snapshots.Snapshots.Add(unversioned);
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_HistoryFiles_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var service = new RelationalSnapshotHistoryService(runtime);
        var snapshots = (await runtime.Snapshots.ListSnapshotsAsync()).Items;
        var snapshot = snapshots.Single(value => value.SnapshotId == snapshotId);
        var versions = await service.GetDisplayedVersionsAsync(snapshot);
        check(versions.Select(version => version.VersionNumber).SequenceEqual(new[] { 2, 1 }), "History versions retain master newest-first ordering");
        foreach (var version in versions)
        {
            IReadOnlyList<RelationalHistoryFileSummary> rows;
            using (var trace = new StateAccessSqlTrace(connection))
            {
                rows = await service.ListFileRowsAsync(version);
                StateAccessNoReads(trace.Commands, check, "History name/filter catalogue", "TextContent", "capture.Data_");
            }
            var expectedVersion = models.Snapshots.Histories[0].Versions.Single(value => value.VersionId == version.VersionId);
            var expected = master.CreateVersionSummary(models.Snapshots, models.Snapshots.Histories[0], expectedVersion).Changes;
            check(rows.Count == expected.Count && rows.All(row => expected.Any(value => value.ResourceKey == row.OriginalResourceKey &&
                value.Kind == row.Kind && value.ChangeKind == row.ChangeKind)), "History child rows match master initial-all-files and later-changed-files semantics");
            var batched = new List<RelationalHistoryFileLineCount>();
            using (var trace = new StateAccessSqlTrace(connection))
            {
                await foreach (var result in service.CountFileLinesAsync(rows)) batched.Add(result);
                int projections = trace.Commands.Count(command => command.Text.Contains("CREATE TABLE #HISTORICALENTRY", StringComparison.Ordinal));
                check(projections == (version.IsInitial ? 1 : 2),
                    "Batch history counts reuse one current and at most one previous projection across table/CSV files");
            }
            check(batched.Count == rows.Count && batched.All(result => result.Error == null) &&
                batched.Select(result => result.File).SequenceEqual(rows), "Batch history counts yield every selected file in order without losing deleted rows");
            foreach (var row in rows)
            {
                var expectedRow = expected.Single(value => value.ResourceKey == row.OriginalResourceKey);
                var counts = await service.CountFileLinesAsync(row);
                var batchCount = batched.Single(result => result.File.OriginalResourceKey == row.OriginalResourceKey);
                check(counts.Added == expectedRow.AddedLines && counts.Removed == expectedRow.RemovedLines,
                    "History exact line counts match master for " + version.VersionName + " " + row.DisplayName);
                check(batchCount.Added == expectedRow.AddedLines && batchCount.Removed == expectedRow.RemovedLines &&
                    batchCount.Added == counts.Added && batchCount.Removed == counts.Removed,
                    "Batched and individual line counts agree with the independent master history oracle");
                var reconstructed = master.ReconstructSnapshot(models.Snapshots, snapshotId, version.VersionId);
                bool exists = master.TryGetResourceContent(reconstructed, row.Kind, row.OriginalResourceKey, out string text, out _);
                string actual = await service.ReadFileTextAsync(row);
                check(exists ? actual == text : row.Entry == null && actual.Contains("does not exist in", StringComparison.Ordinal),
                    "History view uses selected-version text including CSV headers/newlines or an absent-file message");
                if (exists)
                {
                    var target = await service.FileTargetAsync(row);
                    check(target.VersionKey == version.VersionKey && target.ResourceKey == row.Entry!.ResourceKey,
                        "History file target pins its selected version and current file identity");
                }
            }

            // Reopen both the runtime and the issued rows. Count loads should use SQL's
            // stored values, with no text/CSV reads or count-time version reconstruction.
            var reopened = new RelationalSnapshotHistoryService(new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection)));
            var reopenedRows = await reopened.ListFileRowsAsync(version);
            if (version.IsInitial)
                check(await service.CountVersionFilesAsync(snapshot, version) == rows.Count,
                    "The initial version heading counts all files and stores its result");
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var saved = new List<RelationalHistoryFileLineCount>();
                await foreach (var result in reopened.CountFileLinesAsync(reopenedRows)) saved.Add(result);
                check(saved.Select(result => (result.File.OriginalResourceKey, result.Added, result.Removed, result.Error)).SequenceEqual(
                    batched.Select(result => (result.File.OriginalResourceKey, result.Added, result.Removed, result.Error))),
                    "A new runtime reuses the persisted exact line counts in file order");
                if (version.IsInitial)
                    check(await reopened.CountVersionFilesAsync(snapshot, version) == rows.Count &&
                        trace.Commands.Any(command => command.Text.Contains("SELECT FILECOUNT FROM SURF.HISTORYINITIALFILECOUNT", StringComparison.Ordinal)),
                        "A new runtime loads the saved initial version heading count");
                check(trace.Commands.Any(command => command.Text.Contains("JOIN SURF.HISTORYFILELINECOUNT", StringComparison.Ordinal)) &&
                    !trace.Commands.Any(command => command.Text.Contains("CREATE TABLE #HISTORICALENTRY", StringComparison.Ordinal)),
                    "A warm count load reads the saved counts without recreating history projections");
                StateAccessNoReads(trace.Commands, check, "Saved history counts skip definitions and captured rows", "TextContent", "capture.Data_");
            }

            // The existing limit/cancellation tests below require cold reads.
            await fixture.DestinationSqlAsync("DELETE FROM surf.HistoryFileLineCount WHERE SnapshotKey=@Snapshot AND VersionKey=@Version;",
                RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshot.SnapshotKey),
                RelationalSession.Parameter("@Version", SqlDbType.BigInt, version.VersionKey));
            if (!version.IsInitial)
            {
                var tiny = new RelationalSnapshotHistoryService(runtime, new() { MaximumDefinitionBytes = 40 });
                var limited = new List<RelationalHistoryFileLineCount>();
                await foreach (var result in tiny.CountFileLinesAsync(rows)) limited.Add(result);
                var oversized = limited.Single(result => result.File.Kind == DatabaseVersionedResourceKind.StoredProcedure);
                var laterSmall = limited.Single(result => result.File.Kind == DatabaseVersionedResourceKind.Function);
                var expectedSmall = expected.Single(value => value.Kind == DatabaseVersionedResourceKind.Function);
                check(limited.Count == rows.Count && !string.IsNullOrWhiteSpace(oversized.Error) &&
                    limited.IndexOf(laterSmall) > limited.IndexOf(oversized) && laterSmall.Error == null &&
                    laterSmall.Added == expectedSmall.AddedLines && laterSmall.Removed == expectedSmall.RemovedLines,
                    "An oversized file reports its own batch error while later small files still receive exact counts");
            }
            else
            {
                using var cancellation = new CancellationTokenSource();
                using var trace = new StateAccessSqlTrace(connection);
                var structured = rows.Where(row => row.Kind is DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData).ToArray();
                await using (var enumerator = service.CountFileLinesAsync(structured, cancellation.Token).GetAsyncEnumerator())
                {
                    check(await enumerator.MoveNextAsync() && enumerator.Current.Error == null, "Batch cancellation fixture opens a shared metadata projection");
                    cancellation.Cancel();
                    await ThrowsAsync<OperationCanceledException>(async () => { await enumerator.MoveNextAsync(); }, check,
                        "Batch cancellation propagates instead of being reported as a per-file count error");
                }
                check(trace.Commands.Any(command => command.Text.Contains("DROP TABLE IF EXISTS #HISTORICALENTRY", StringComparison.Ordinal)),
                    "Cancelled batch history counts dispose their shared temporary projection");
            }
        }

        var latestRows = await service.ListFileRowsAsync(versions[0]);
        var latest = new List<RelationalHistoryFileLineCount>();
        await foreach (var result in service.CountFileLinesAsync(latestRows)) latest.Add(result);
        await fixture.DestinationSqlAsync("UPDATE surf.DatabaseSnapshot SET DisplayName=DisplayName WHERE SnapshotKey=@Snapshot;",
            RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshot.SnapshotKey));
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.CountFileLinesAsync(latestRows[0]), check,
            "A saved count cannot authorize a previously issued row after its snapshot head changes");
        var afterHeadChange = new RelationalSnapshotHistoryService(new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection)));
        var reissuedRows = await afterHeadChange.ListFileRowsAsync(versions[0]);
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var counts = new List<RelationalHistoryFileLineCount>();
            await foreach (var result in afterHeadChange.CountFileLinesAsync(reissuedRows)) counts.Add(result);
            check(counts.Select(result => (result.Added, result.Removed, result.Error)).SequenceEqual(latest.Select(result => (result.Added, result.Removed, result.Error))) &&
                !trace.Commands.Any(command => command.Text.Contains("CREATE TABLE #HISTORICALENTRY", StringComparison.Ordinal)),
                "A snapshot header change preserves persisted counts for identical immutable diff inputs");
            StateAccessNoReads(trace.Commands, check, "Reissued history files reuse their existing immutable diff counts", "TextContent", "capture.Data_");
        }

        var noHistory = snapshots.Single(value => value.SnapshotId == unversioned.SnapshotId);
        var synthetic = (await service.GetDisplayedVersionsAsync(noHistory)).Single();
        var initialRows = await service.ListFileRowsAsync(synthetic);
        check(synthetic.VersionKey == 0 && synthetic.IsInitial && synthetic.VersionName == "V1" && initialRows.Count == 4,
            "A migrated snapshot without history shows one read-only initial version containing all files");
        var selected = initialRows.First(row => row.Kind == DatabaseVersionedResourceKind.StoredProcedure);
        await using var context = await service.OpenVersionAsync(noHistory.SnapshotKey, 0);
        check(await service.ReadFileTextAsync(context, selected.Entry!) == first.Objects[0].Definition &&
            (await service.FileTargetAsync(selected)).VersionKey == 0,
            "Synthetic initial file open/compare uses the same bounded current projection");
        check(await service.CountVersionFilesAsync(noHistory, synthetic) == initialRows.Count && (await service.CountFileLinesAsync(selected)).Added > 0 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("""
                SELECT (SELECT COUNT_BIG(*) FROM surf.HistoryInitialFileCount WHERE SnapshotKey=@Snapshot)
                     + (SELECT COUNT_BIG(*) FROM surf.HistoryFileLineCount WHERE SnapshotKey=@Snapshot);
                """, RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, noHistory.SnapshotKey))) == 0,
            "The synthetic live row calculates its counts without storing any history counts");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.SnapshotVersion WHERE SnapshotKey=@Snapshot;",
            RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, noHistory.SnapshotKey))) == 0,
            "Browsing a synthetic initial version writes no persistent history");
        await fixture.DestinationSqlAsync("UPDATE surf.DatabaseSnapshot SET DisplayName=DisplayName WHERE SnapshotKey=@Snapshot;",
            RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, noHistory.SnapshotKey));
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.CountFileLinesAsync(selected), check,
            "History counts reject a displayed file after its snapshot head changed");
        await ThrowsAsync<SnapshotConcurrencyException>(async () =>
        {
            await foreach (var _ in service.CountFileLinesAsync(initialRows)) { }
        }, check, "Batch history counts propagate a changed head instead of returning misleading per-file errors");
        await ThrowsAsync<SnapshotConcurrencyException>(() => context.ReadPageAsync(HistoricalCollection.Objects), check,
            "Synthetic current projection rejects a changed snapshot head");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

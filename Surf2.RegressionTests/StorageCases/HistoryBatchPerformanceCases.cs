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
    public static async Task RunHistoryBatchPerformanceChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels();
        var first = new DatabaseMetadataSnapshot
        {
            SnapshotId = models.Snapshots.Snapshots[0].SnapshotId, DisplayName = "Batched history", DatabaseName = "fixture",
            ImportedAtUtc = EvidenceTime,
            Objects = Enumerable.Range(0, 100).Select(index => new SqlDatabaseObject
            {
                ObjectName = "P" + index.ToString("D3"), Kind = SqlDatabaseObjectKind.StoredProcedure,
                Definition = "SELECT " + index + ";\r\nSELECT 1;\r\n"
            }).ToList()
        };
        models.Snapshots.Snapshots.Clear(); models.Snapshots.Histories.Clear(); models.Snapshots.Snapshots.Add(first);
        var master = new DatabaseSnapshotHistoryService(new DatabaseDocumentService());
        master.EnsureInitialVersion(models.Snapshots, first);
        var current = first.Clone();
        foreach (var item in current.Objects) item.Definition += "SELECT 2;\r\n";
        master.RecordSnapshotReplacement(models.Snapshots, first, current);
        models.Snapshots.Snapshots[0] = current;
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_HistoryBatch_" + Guid.NewGuid().ToString("N"), Pooling = true }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var service = new RelationalSnapshotHistoryService(runtime);
        var snapshot = (await runtime.Snapshots.ListSnapshotsAsync()).Items.Single();
        var version = (await service.GetDisplayedVersionsAsync(snapshot)).First();
        IReadOnlyList<RelationalHistoryFileSummary> rows;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            rows = await service.ListFileRowsAsync(version);
            check(rows.Count == 100 && trace.Commands.Count < 160,
                "A 100-file modified history loads its metadata with a bounded number of SQL requests");
            StateAccessNoReads(trace.Commands, check, "History file listing does not load definitions", "TextContent", "capture.Data_");
        }
        var history = models.Snapshots.Histories.Single();
        var expected = master.CreateVersionSummary(models.Snapshots, history, history.Versions.Last()).Changes;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var actual = new List<RelationalHistoryFileLineCount>();
            await foreach (var result in service.CountFileLinesAsync(rows)) actual.Add(result);
            check(actual.Count == 100 && actual.All(result => result.Error == null && expected.Any(value =>
                    value.ResourceKey == result.File.OriginalResourceKey && value.AddedLines == result.Added && value.RemovedLines == result.Removed)),
                "Batched modified-file line counts match master for all 100 procedures");
            check(trace.Commands.Count <= 40,
                "100 modified procedures need at most 40 count requests instead of 1,600 individual requests");
            check(!trace.Commands.Any(command => command.Text.Contains("CREATE TABLE #HISTORICALENTRY", StringComparison.Ordinal)),
                "Procedure line counting reads immutable revisions without reconstructing historical versions");
        }
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var target = await service.FileTargetAsync(rows[^1]);
            check(target.ResourceKey == rows[^1].Entry!.ResourceKey && target.VersionKey == version.VersionKey && trace.Commands.Count <= 4,
                "Selecting the last historical file reuses its issued metadata with one guarded head read");
            check(!trace.Commands.Any(command => command.Text.Contains("#HISTORICALENTRY", StringComparison.Ordinal)),
                "Selecting a listed historical file does not reconstruct or rescan its version");
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.FileTargetAsync(rows[0] with
        { Version = rows[0].Version with { VersionKey = long.MaxValue } }), check,
            "Issued history metadata cannot be copied into another version locator");
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.FileTargetAsync(rows[0] with
        { Entry = rows[0].Entry! with { EntryKey = long.MaxValue } }), check,
            "Issued history metadata cannot be copied into another projected entry locator");
        await ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.CountFileLinesAsync(rows, canceled.Token)) { }
        }, check, "History batch cancellation is propagated before returning line counts");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

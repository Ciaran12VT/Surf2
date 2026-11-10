using System.Data;
using System.Diagnostics;
using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;

public static partial class StorageRegressionSuite
{
    public static async Task RunMigrationValidationChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels();
        var snapshot = models.Snapshots.Snapshots.Single();
        const int children = 513;
        snapshot.Tables.Add(MixedTable("batch-only", 0));
        snapshot.Columns.AddRange(Enumerable.Range(0, children).Select(i => MixedColumn("batch-only", "batch-current-" + i)));
        snapshot.PrimaryKeys.AddRange(Enumerable.Range(0, children).Select(i => MixedKey("batch-only", "batch-current-" + i)));
        var previous = models.Snapshots.Histories[0].Versions.SelectMany(v => v.Changes)
            .First(c => c.PreviousPayload?.Kind == DatabaseVersionedResourceKind.TableMetadata).PreviousPayload!;
        previous.Columns = Enumerable.Range(0, children).Select(i => MixedColumn("A", "batch-previous-" + i)).ToList();
        previous.PrimaryKeys = Enumerable.Range(0, children).Select(i => MixedKey("A", "batch-previous-" + i)).ToList();
        previous.Columns[300].DataType = new string('x', 5_000_000);
        previous.PrimaryKeys[300].ColumnName = new string('k', 5_000_000);
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        var request = MigrationRequest(fixture);
        using (var cancellation = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => new RelationalMigrator().ConvertAsync(request,
                new InlineProgress(message =>
                {
                    if (message == "Validating preservation and publishing the destination") cancellation.Cancel();
                }), cancellation.Token), check, "Large migration can stop after import without publishing the destination");
        }
        string directory = Directory.GetDirectories(request.StagingRoot).Single();
        string manifest = Path.Combine(directory, "manifest.json");
        byte[] oldManifest = await File.ReadAllBytesAsync(manifest);
        byte[] schemaChecksum = RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts());
        long mapped = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap;"));
        await using var stage = await LegacySourceStage.ReopenAsync(directory);
        var session = RelationalSession.ForMigrationValidation(fixture.DestinationConnectionString, stage.MigrationIdentity, stage.Fingerprint);
        session.Metrics.Enable();
        var updates = new List<MigrationProgressUpdate>();
        using (var reporting = new MigrationProgressReporter(updates: new ValidationProgress(updates.Add)))
        {
            var timer = Stopwatch.StartNew();
            var receipt = await new RelationalMigrationValidator(session, stage) { Progress = reporting }.ValidateAsync();
            timer.Stop();
            var metrics = session.Metrics.Snapshot();
            Console.WriteLine($"Batched validation fixture: {mapped:N0} mapped units, {metrics.Started:N0} SQL commands, {timer.Elapsed.TotalSeconds:F3}s.");
            check(receipt.MigrationIdentity == stage.MigrationIdentity && receipt.DataRows > 0,
                "Batched validator retains full model, history, dataset and ownership preservation checks");
            check(previous.Columns[300].DataType.Length * 2L > 8 * 1024 * 1024 && previous.PrimaryKeys[300].ColumnName.Length * 2L > 8 * 1024 * 1024,
                "Historical metadata larger than a catalogue page validates through the compatible bounded single-row fallback");
            check(metrics.Started < 800 && metrics.Failed == 0 && metrics.ActiveCommands == 0,
                "More than 2,000 current/historical child fields validate with fewer than 800 SQL commands and no retained readers");
            check(updates.Any(p => p.Phase == "Validating migrated content and reverse history" && p.Total == mapped) &&
                updates.Any(p => p.Phase == "Checking relational coverage and ownership") &&
                updates.Any(p => p.Phase == "Rechecking source files and image hashes"),
                "Validation reports a checkpoint-backed total and separate coverage/hash phases");
        }
        check(Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo;"), "Migrating"),
            "Successful read-only validation alone does not publish Ready");

        long column = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT ColumnRevisionKey FROM surf.SnapshotCurrentColumn WHERE SortOrdinal=300;"));
        long owner = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT TableMetadataRevisionKey FROM surf.TableColumnRevision WHERE ColumnRevisionKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, column)));
        await fixture.DestinationSqlAsync("UPDATE surf.TableColumnRevision SET TableMetadataRevisionKey=NULL WHERE ColumnRevisionKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, column));
        await ThrowsAsync<InvalidDataException>(() => new RelationalMigrationValidator(session, stage).ValidateAsync(), check,
            "Batched owner validation rejects a corrupt current child beyond the first page");
        await fixture.DestinationSqlAsync("UPDATE surf.TableColumnRevision SET TableMetadataRevisionKey=@Owner WHERE ColumnRevisionKey=@Key;",
            RelationalSession.Parameter("@Owner", SqlDbType.BigInt, owner), RelationalSession.Parameter("@Key", SqlDbType.BigInt, column));

        long historicalColumn = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT ColumnRevisionKey FROM surf.TableColumnRevision WHERE ColumnName=N'batch-previous-300';"));
        await fixture.DestinationSqlAsync("UPDATE surf.TableColumnRevision SET SortOrdinal=299 WHERE ColumnRevisionKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, historicalColumn));
        await ThrowsAsync<InvalidDataException>(() => new RelationalMigrationValidator(session, stage).ValidateAsync(), check,
            "Batched historical pages reject duplicate or reordered source ordinals across page boundaries");
        await fixture.DestinationSqlAsync("UPDATE surf.TableColumnRevision SET SortOrdinal=300 WHERE ColumnRevisionKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, historicalColumn));
        check(Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo;"), "Migrating"),
            "Failed preservation checks keep the same recoverable unpublished destination");

        var result = await new RelationalMigrator().ConvertAsync(request with { ResumeStageDirectory = directory },
            detailedProgress: new ValidationProgress(updates.Add));
        check(result.MigrationIdentity == stage.MigrationIdentity && !result.AlreadyCompleted &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap;")) == mapped,
            "Updated migrator resumes the existing imported destination without duplicating mappings");
        byte[] resumedManifest = await File.ReadAllBytesAsync(manifest);
        check(oldManifest.SequenceEqual(resumedManifest) &&
            schemaChecksum.SequenceEqual(RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts())),
            "Progress logs and batching do not modify the recovery manifest or schema checksum");
        check(updates.Any(p => p.Phase == "Publishing validated destination") &&
            Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo;"), "Ready"),
            "Resume distinguishes publication from validation and publishes only after preservation succeeds");
        check(Directory.GetFiles(directory, "*.progress.jsonl").Length >= 2,
            "Interrupted and resumed attempts each retain a separate progress log alongside the unchanged manifest");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private sealed class ValidationProgress(Action<MigrationProgressUpdate> report) : IProgress<MigrationProgressUpdate>
    {
        public void Report(MigrationProgressUpdate value) => report(value);
    }
}

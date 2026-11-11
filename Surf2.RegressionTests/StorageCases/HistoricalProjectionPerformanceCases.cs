using System.Data;
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
    public static async Task RunHistoricalProjectionPerformanceChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels(); string snapshotId = models.Snapshots.Snapshots[0].SnapshotId;
        var current = new DatabaseMetadataSnapshot { SnapshotId = snapshotId, DisplayName = "History projection performance",
            DatabaseName = "fixture", ImportedAtUtc = EvidenceTime };
        for (int i = 0; i < 500; i++) current.Objects.Add(new() { SchemaName = "dbo", ObjectName = $"P{i:D4}",
            Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = $"SELECT {i}; -- version1" });
        current.Tables.Add(new() { SchemaName = "dbo", TableName = "Items" });
        for (int i = 0; i < 40; i++) current.Columns.Add(new() { SchemaName = "dbo", TableName = "Items", ColumnName = "C" + i,
            DataType = "int", Ordinal = i, IsNullable = i % 2 == 0 });
        current.PrimaryKeys.Add(new() { SchemaName = "dbo", TableName = "Items", ColumnName = "C0", ConstraintName = "PK_Items", KeyOrdinal = 1 });
        models.Snapshots.Snapshots.Clear(); models.Snapshots.Histories.Clear(); models.Snapshots.Snapshots.Add(current);
        var master = new DatabaseSnapshotHistoryService(new DatabaseDocumentService()); master.EnsureInitialVersion(models.Snapshots, current);
        for (int version = 2; version <= 3; version++)
        {
            var next = current.Clone();
            foreach (var item in next.Objects) item.Definition += "\n-- version" + version;
            // Include a historical table payload and changed root children in replay.
            next.Columns[0].DataType = version == 2 ? "bigint" : "decimal";
            master.RecordSnapshotReplacement(models.Snapshots, current, next);
            models.Snapshots.Snapshots[0] = current = next;
        }
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_HistoryProjection_" + Guid.NewGuid().ToString("N"), Pooling = true }.ConnectionString;
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var snapshot = (await runtime.Snapshots.ListSnapshotsAsync()).Items.Single();
        var history = models.Snapshots.Histories.Single();
        var versions = await new RelationalSnapshotHistoryService(runtime).GetDisplayedVersionsAsync(snapshot);
        HistoricalSnapshotContext projection;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            projection = await runtime.Snapshots.OpenHistoricalSnapshotAsync(snapshot.SnapshotKey, versions[^1].VersionKey);
            check(trace.Commands.Count < 100, "A 500-object/two-revision rollback builds its metadata projection in fewer than 100 SQL commands");
            StateAccessNoReads(trace.Commands, check, "Historical reconstruction stays metadata-only", "TextContent", "capture.Data_");
        }
        await using (projection)
        {
            var objects = new List<HistoricalSnapshotEntry>();
            await foreach (var entry in projection.StreamAsync(HistoricalCollection.Objects)) objects.Add(entry);
            var expected = master.ReconstructSnapshot(models.Snapshots, snapshotId, history.Versions[0].VersionId);
            check(objects.Select(entry => entry.Name).SequenceEqual(expected.Objects.Select(item => item.ObjectName)),
                "Buffered historical rollback preserves master's RemoveAll-and-append resource order");
            check(await projection.CountResourcesAsync() == 501, "Initial history counts recognized files without rebuilding file summaries");
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var last = await projection.FindEntryAsync(objects[^1].EntryKey);
                check(last != null && last.RevisionKey == objects[^1].RevisionKey && trace.Commands.Count <= 2,
                    "Selected historical identity uses a point lookup instead of scanning preceding pages");
            }
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var summaries = new List<SnapshotResourceSummary>();
                foreach (var batch in objects.Chunk(128)) summaries.AddRange((await projection.ResourceSummariesAsync(batch)).Values);
                check(summaries.Count == 500 && summaries.All(summary => summary.Kind == DatabaseVersionedResourceKind.StoredProcedure) && trace.Commands.Count <= 16,
                    "Historical comparison locators resolve 500 resources in metadata batches rather than per-resource queries");
            }
            await ThrowsAsync<ArgumentException>(() => projection.ResourceSummariesAsync([objects[0] with { RevisionKey = long.MaxValue }]), check,
                "Batched resource summaries reject a forged immutable revision locator");
            var table = (await projection.ReadPageAsync(HistoricalCollection.Tables)).Items.Single();
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var matched = await projection.FindEntriesAsync(HistoricalCollection.Columns, "DBO", "items");
                check(matched.Count == 40 && matched.Select(entry => entry.SortOrdinal).SequenceEqual(Enumerable.Range(0, 40).Select(value => (long)value)) &&
                    trace.Commands.Count <= 2, "Case-insensitive root-child lookup preserves order without scanning unrelated column pages");
            }
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var details = await projection.ReadTableMetadataAsync(table);
                SameModel(expected.Columns, details.Columns.Select(column => column.Column).ToList(), check,
                    "Selected historical table metadata preserves all previous ordered column values");
                SameModel(expected.PrimaryKeys, details.PrimaryKeys.Select(key => key.Column).ToList(), check,
                    "Selected historical table metadata preserves previous primary-key values");
                check(trace.Commands.Count <= 8, "A 40-column historical table loads children in batches rather than 80-plus child/check queries");
            }
            table.Table!.HasFullData = true;
            check(!(await projection.FindEntryAsync(table.EntryKey))!.Table!.HasFullData,
                "Mutating a returned table model cannot modify the cached immutable historical projection");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => projection.FindEntryAsync(objects[0].EntryKey, canceled.Token), check,
                "Point history reads honor cancellation before issuing SQL");
            await HistoricalObjectBatchChecksAsync(fixture, runtime, objects.Take(3).Select(entry => entry.RevisionKey!.Value).ToArray(), check);
            await fixture.DestinationSqlAsync("UPDATE surf.DatabaseSnapshot SET DisplayName=DisplayName WHERE SnapshotKey=@Snapshot;",
                RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshot.SnapshotKey));
            await ThrowsAsync<SnapshotConcurrencyException>(() => projection.FindEntryAsync(objects[0].EntryKey), check,
                "Cached point history reads reject a changed snapshot head");
            await ThrowsAsync<SnapshotConcurrencyException>(() => projection.ResourceSummariesAsync(objects.Take(128).ToArray()), check,
                "Batched history locators retain the snapshot concurrency guard");
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task HistoricalObjectBatchChecksAsync(SqlFixture fixture, RelationalRuntime runtime, long[] revisions,
        Action<bool, string> check)
    {
        // Owned fixture only: point distinct sealed revisions at exact raw content
        // so the new bulk reader is tested independently of JSON serialization.
        string raw = "\0high-\ud800-low-\udfff-pair-\ud83d\ude80\r\ntrailing  ";
        var content = new RelationalContentStore();
        long shared, different;
        await using (var connection = await runtime.Session.OpenAsync())
        {
            shared = await content.PutTextAsync(connection, null, raw);
            different = await content.PutTextAsync(connection, null, raw + " ");
        }
        await fixture.DestinationSqlAsync("""
            UPDATE surf.DatabaseObjectRevision SET DefinitionContentKey=@Shared WHERE RevisionKey IN (@First,@Second);
            UPDATE surf.DatabaseObjectRevision SET DefinitionContentKey=@Different WHERE RevisionKey=@Third;
            """, RelationalSession.Parameter("@Shared", SqlDbType.BigInt, shared),
            RelationalSession.Parameter("@Different", SqlDbType.BigInt, different),
            RelationalSession.Parameter("@First", SqlDbType.BigInt, revisions[0]),
            RelationalSession.Parameter("@Second", SqlDbType.BigInt, revisions[1]),
            RelationalSession.Parameter("@Third", SqlDbType.BigInt, revisions[2]));
        var identities = await runtime.Snapshots.ReadObjectTextIdentitiesAsync(revisions);
        check(identities.Count == 3 && identities[revisions[0]].ContentKey == identities[revisions[1]].ContentKey &&
            identities[revisions[0]].ContentKey != identities[revisions[2]].ContentKey && identities[revisions[0]].ByteCount == raw.Length * 2L,
            "Batched immutable identities share exact bodies while retaining trailing-space differences and raw UTF-16 byte counts");
        var definitions = await runtime.Snapshots.ReadObjectDefinitionsAsync([revisions[0], revisions[1], revisions[2], revisions[0]]);
        check(definitions.Count == 3 && definitions[revisions[0]] == raw && definitions[revisions[1]] == raw && definitions[revisions[2]] == raw + " ",
            "Batched definitions preserve every NUL, unpaired surrogate, surrogate pair, newline and trailing code unit");
        await ThrowsAsync<KeyNotFoundException>(() => runtime.Snapshots.ReadObjectsAsync([revisions[0], long.MaxValue]), check,
            "A missing immutable revision rejects the complete definition batch instead of returning partial results");
        await ThrowsAsync<KeyNotFoundException>(() => runtime.Snapshots.ReadObjectTextIdentitiesAsync([revisions[0], long.MaxValue]), check,
            "A missing immutable revision rejects the complete identity batch instead of returning partial results");
        await ThrowsAsync<ArgumentException>(() => runtime.Snapshots.ReadObjectsAsync(Enumerable.Repeat(revisions[0], 129).ToArray()), check,
            "Definition batch input remains bounded even when all requested revision keys repeat");
        var limited = new RelationalSnapshotStore(runtime.Session, content, new() { SelectedDefinitionBytes = raw.Length * 2L - 2 });
        await ThrowsAsync<SnapshotReadLimitException>(() => limited.ReadObjectsAsync(revisions), check,
            "Batched definitions enforce the configured per-definition byte limit before reading the rejected body");
        check((await limited.ReadObjectTextIdentitiesAsync(revisions)).Count == 3,
            "Definition size limits do not prevent bounded identity-only reads needed to report per-file errors");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => runtime.Snapshots.ReadObjectDefinitionsAsync(revisions, canceled.Token), check,
            "Batched definition reads propagate cancellation before opening SQL");
    }
}

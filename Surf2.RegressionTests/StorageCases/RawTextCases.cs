using System.Buffers.Binary;
using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    public static async Task RunRawTextChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        await fixture.MarkTestFixtureReadyAsync();
        var session = new RelationalSession(fixture.DestinationConnectionString);
        await session.RequireReadyAsync();
        var store = new RelationalContentStore();
        string[] values = ["", "normal\r\ntext \n", "high-\uD800-end", "low-\uDC00-end", "pair-\uD83D\uDE00-end", "\uFFFD"];
        var keys = new HashSet<long>();
        await using var connection = await session.OpenAsync();
        foreach (string value in values)
        {
            long key = await store.PutTextAsync(connection, null, value);
            check(keys.Add(key), "Distinct raw UTF-16 values have distinct exact content identities");
            check(await store.PutTextAsync(connection, null, value) == key, "Raw UTF-16 content deduplicates by hash and exact binary evidence");
            check(await store.ReadTextAsync(session, key) == value, "Common content reader round-trips every raw UTF-16 code unit");
            byte[] expected = new byte[value.Length * 2];
            for (int i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(i * 2, 2), value[i]);
            var stored = (byte[])(await fixture.DestinationSqlAsync("SELECT CONVERT(varbinary(max),Text) FROM surf.TextContent WHERE ContentKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, key)))!;
            check(stored.AsSpan().SequenceEqual(expected), "SQL content stores exact UTF-16LE bytes rather than encoder replacement characters");
            check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT LineCount FROM surf.TextContent WHERE ContentKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, key))) == 1 + value.LongCount(c => c == '\n'), "Raw content insertion retains exact newline counts");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => store.PutTextAsync(connection, null, "must-not-publish", cancelled.Token),
            check, "Pre-cancelled raw text insertion does not publish a replacement value");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.TextContent;")) == values.Length,
            "Cancelled raw insertion leaves the selected content store unchanged");
        long snapshotKey = 0, resourceKey = 0;
        const string definition = RelationalDocumentRawTextSqlChecks.CurrentDefinition;
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, store);
            snapshotKey = await writer.CreateSnapshotAsync(new("raw-document-fixture", "Raw fixture", "Fixture", EvidenceTime, 0));
            var selected = await writer.InsertCurrentObjectAsync(snapshotKey, new SqlDatabaseObject
            {
                SchemaName = "dbo", ObjectName = "RawProcedure", Kind = SqlDatabaseObjectKind.StoredProcedure,
                Definition = definition
            }, 0);
            resourceKey = selected.ResourceKey;
            await writer.PublishSnapshotAsync(snapshotKey, null);
        });
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        await using (var documents = new RelationalDocumentService(runtime))
        {
            var address = await documents.ResolveResourceAsync(snapshotKey, resourceKey, null);
            var plan = await documents.DescribeTextAsync(address);
            var before = runtime.QueryMetrics.Snapshot();
            var first = await documents.ReadTextAsync(plan);
            var warm = await documents.ReadTextAsync(plan);
            var after = runtime.QueryMetrics.Snapshot();
            check(first.Text == definition && ReferenceEquals(first, warm),
                "Actual selected-document SQL reader and warm cache preserve raw UTF-16 without source replacement");
            check(after.Active == 0 && after.Completed == before.Completed + 2 && after.RowsRead == before.RowsRead + 1 &&
                after.BytesRead == before.BytesRead + definition.Length * 2L && after.CacheHits == before.CacheHits + 1 && after.Queries == before.Queries,
                "Actual selected-document fetch telemetry counts one validated output and one warm hit, without inventing SQL roundtrips");
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

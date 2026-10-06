using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static readonly DateTimeOffset EvidenceTime =
        new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.FromMinutes(330)).AddTicks(1234567);
    private static readonly JsonSerializerOptions ModelJson = CreateModelJson();

    // SQL is opt-in at the parent harness. No connection string or user data is accepted here.
    public static async Task RunAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await VerifySchemaAsync(fixture, check);
        var session = new RelationalSession(fixture.DestinationConnectionString);
        var content = new RelationalContentStore();
        var snapshots = new RelationalSnapshotStore(session, content);
        var capture = new RelationalCaptureStore(session, new CaptureLimits
        {
            WriteBatchRows = 2, ReadBatchRows = 2, MaxPageRows = 3,
            // Bulk schema/storage overhead belongs to the write budget, not read/page budgets.
            WriteBatchBytes = 128 * 1024, ReadBatchBytes = 32 * 1024, MaxPageBytes = 32 * 1024
        });
        var state = new RelationalStateStore(session, content);
        SnapshotFixture snapshot = await SeedSnapshotsAsync(session, content);
        CaptureFixture data = await SeedCaptureAsync(session, content, capture, snapshot, check);
        StateFixture stateFixture = await SeedStateAsync(session, state, check);

        await ThrowsAsync<InvalidOperationException>(() => session.RequireReadyAsync(), check,
            "Migrating session refuses runtime access");
        await ThrowsAsync<InvalidOperationException>(() => snapshots.ListSnapshotsAsync(), check,
            "Migrating snapshot catalogue refuses reads");
        await ThrowsAsync<InvalidOperationException>(() => snapshots.ReadObjectAsync(snapshot.CurrentObject.RevisionKey), check,
            "Migrating typed snapshot read is refused");
        await ThrowsAsync<InvalidOperationException>(() => snapshots.OpenHistoricalSnapshotAsync(snapshot.Key, snapshot.InitialVersion), check,
            "Migrating historical SQL projection is refused");
        await ThrowsAsync<InvalidOperationException>(() => capture.GetDescriptorAsync(data.Current.DataSetKey), check,
            "Migrating capture descriptor refuses reads");
        await ThrowsAsync<InvalidOperationException>(() => capture.ReadPageAsync(data.Current.DataSetKey), check,
            "Migrating captured row page is refused");
        await ThrowsAsync<InvalidOperationException>(() => state.ReadSettingsAsync(), check,
            "Migrating state read is refused");
        await ThrowsAsync<InvalidOperationException>(() => state.ListScopesAsync(), check,
            "Migrating state catalogue refuses reads");

        // Deliberately bypass conversion only for this storage-unit fixture. This callback
        // is NOT evidence of migration validation and must never become a production publisher.
        Func<Task> markTestFixtureReady = fixture.MarkTestFixtureReadyAsync;
        await markTestFixtureReady();
        await session.RequireReadyAsync();
        check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational,
            "Explicit test-only Ready callback opens runtime access");

        await VerifySnapshotsAsync(session, content, snapshots, snapshot, fixture.DestinationConnectionString, check);
        await VerifyCaptureAsync(session, capture, snapshots, snapshot, data, check);
        await VerifyStateAsync(session, state, stateFixture, check);
        await VerifyMixedHistoryAsync(session, content, snapshots, capture, check);
        await VerifyIndexStorageAsync(session, snapshots, state, snapshot, stateFixture, check);
        await fixture.VerifySourceUnchangedAsync(check);
        await VerifyMigrationAsync(check);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, Action<bool, string> check, string name)
        where TException : Exception
    {
        bool rejected = false;
        try { await action(); }
        catch (TException) { rejected = true; }
        check(rejected, name);
    }

    private static T Required<T>(T? value, string name) where T : class =>
        value ?? throw new InvalidOperationException("Missing regression fixture result: " + name);

    private static void SameModel<T>(T expected, T actual, Action<bool, string> check, string name) =>
        check(JsonSerializer.Serialize(expected, ModelJson) == JsonSerializer.Serialize(actual, ModelJson), name);

    private static JsonSerializerOptions CreateModelJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            // Only persisted writable fields. In particular, never invoke ScopedResource.Exists
            // or other computed getters that can inspect filesystem paths.
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Set == null) info.Properties.RemoveAt(i);
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    private static async Task InTransactionAsync(RelationalSession session,
        Func<SqlConnection, SqlTransaction, Task> action)
    {
        await using var connection = await session.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await action(connection, transaction);
        await transaction.CommitAsync();
    }

    private static async Task<IReadOnlyList<T>> SnapshotPagesAsync<T>(
        Func<SnapshotCursor?, Task<SnapshotPage<T>>> page, Action<bool, string> check, string name)
    {
        var result = new List<T>();
        SnapshotCursor? cursor = null;
        int pages = 0;
        do
        {
            if (++pages > 100) throw new InvalidOperationException("Snapshot paging failed to terminate: " + name);
            var next = await page(cursor);
            check(next.Items.Count <= 1 && (next.Next == null || next.Items.Count == 1), name + " page bound/progress");
            result.AddRange(next.Items);
            cursor = next.Next;
        } while (cursor != null);
        return result;
    }
}

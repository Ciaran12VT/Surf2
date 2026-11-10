using System.Buffers.Binary;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunRuntimeCaptureChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync(new Dictionary<string, string>
        {
            ["database-snapshots"] = "{\"SchemaVersion\":2,\"Snapshots\":[],\"Histories\":[]}",
            ["diagram-library"] = "{\"Diagrams\":[]}",
            ["scope-library"] = "{\"Scopes\":[]}"
        });
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        await RuntimeCaptureSourceSqlAsync(fixture, """
            ALTER TABLE app.Surf2Documents ADD FixtureDecimal decimal(18,4) NULL,
                FixtureNull nvarchar(30) NULL, FixtureFlag bit NULL, FixtureText nvarchar(100) NULL,
                FixtureBytes varbinary(10) NULL;
            """);
        await RuntimeCaptureSourceSqlAsync(fixture, """
            UPDATE app.Surf2Documents SET FixtureDecimal=1.2300, FixtureFlag=1,
                FixtureText=N'line one'+NCHAR(13)+NCHAR(10)+N'line two', FixtureBytes=0x0001FF;
            """);
        const string sourceDefinition = "CREATE PROCEDURE dbo.fixture_p AS SELECT N'first captured definition' AS Marker; /* raw \uD800 fixture */";
        await RuntimeCaptureRawSourceDefinitionAsync(fixture, sourceDefinition);
        var sourceDefinitionBytes = (byte[])Required(await RuntimeCaptureSourceSqlAsync(fixture,
            "SELECT CONVERT(varbinary(max),definition) FROM sys.sql_modules WHERE object_id=OBJECT_ID(N'dbo.fixture_p');"), "raw source definition canary");
        var sourceDefinitionChars = new char[sourceDefinitionBytes.Length / 2];
        for (int i = 0; i < sourceDefinitionChars.Length; i++)
            sourceDefinitionChars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(sourceDefinitionBytes.AsSpan(i * 2, 2));
        check(sourceDefinitionBytes.Length % 2 == 0 && new string(sourceDefinitionChars) == sourceDefinition &&
            sourceDefinitionChars.Contains('\uD800'), "Fixture binary canary proves the SQL source stores the exact unmatched UTF-16 definition before capture");
        await RuntimeCaptureSourceSqlAsync(fixture, "CREATE VIEW dbo.fixture_v AS SELECT DocumentKey FROM app.Surf2Documents;");
        await fixture.MarkTestFixtureReadyAsync();
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        var scope = new Scope { ScopeId = "runtime-capture-scope", Name = "Runtime capture scope" };
        var scopeToken = await runtime.StateStore.CreateScopeAsync(scope, 0, Guid.NewGuid());
        var limits = new CaptureLimits { WriteBatchRows = 2, WriteBatchBytes = 128 * 1024,
            ReadBatchRows = 2, ReadBatchBytes = 32 * 1024, MaxPageRows = 2, MaxPageBytes = 32 * 1024 };
        var service = new RelationalDatabaseCaptureService(runtime, new() { Capture = limits });
        var sourcePage = await service.ListSourceTablesAsync(fixture.SourceConnectionString, 1);
        check(sourcePage.Items.Count == 1 && sourcePage.Items[0].SchemaName == "app" &&
            sourcePage.Items[0].TableName == "Surf2Documents" && sourcePage.NextObjectId == null,
            "Runtime source table selection is a bounded metadata-only page");
        var selection = sourcePage.Items;
        string originalSource = await RuntimeCaptureSourceImageAsync(fixture);
        await ThrowsAsync<SourceSnapshotUnavailableException>(() => service.CaptureAsync(new(fixture.SourceConnectionString,
            "No blocking consent", selection, scope, scopeToken)), check, "Disabled source snapshot isolation requires explicit blocking-capture consent");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DatabaseSnapshot;")) == 0,
            "Missing blocking consent rejects before any destination snapshot staging");
        var first = await service.CaptureAsync(new(fixture.SourceConnectionString, "Live fixture capture", selection, scope, scopeToken,
            AllowSerializableFallback: true));
        check(first.SourceIsolation == IsolationLevel.Serializable && first.CapturedRows == 3 && first.Snapshot.CurrentVersionKey == first.VersionKey &&
            !first.UpdatedScopeToken.Version.AsSpan().SequenceEqual(scopeToken.Version),
            "Fresh runtime capture publishes one initial version and an updated scope token");
        var loadedScope = Required(await runtime.StateStore.ReadScopeAsync(scopeToken.Key), "runtime capture scope");
        check(loadedScope.Value.Resources.Count == 1 && loadedScope.Value.Resources[0].Path == first.Snapshot.SnapshotId &&
            loadedScope.Value.Resources[0].Kind == ResourceKind.DatabaseSnapshot &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.ScopeResource WHERE SnapshotKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, first.Snapshot.SnapshotKey))) == 1,
            "Fresh capture publishes its resolved membership in the same destination transaction");
        var firstData = (await runtime.Snapshots.ListResourcesAsync(first.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Single();
        var firstProcedure = (await runtime.Snapshots.ListResourcesAsync(first.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items.Single();
        var capturedDefinitionBytes = (byte[])Required(await fixture.DestinationSqlAsync("""
            SELECT CONVERT(varbinary(max),c.Text) FROM surf.DatabaseObjectRevision o
            JOIN surf.TextContent c ON c.ContentKey=o.DefinitionContentKey WHERE o.RevisionKey=@Revision;
            """, RelationalSession.Parameter("@Revision", SqlDbType.BigInt, firstProcedure.RevisionKey)), "raw captured definition canary");
        check(capturedDefinitionBytes.AsSpan().SequenceEqual(sourceDefinitionBytes),
            "Runtime capture retains exact source definition bytes including its unmatched UTF-16 code unit");
        check((await runtime.Snapshots.ReadObjectAsync(firstProcedure.RevisionKey)).Definition == sourceDefinition,
            "Selected typed snapshot read agrees with the raw stored definition bytes without Unicode replacement");
        var firstRows = await RuntimeCaptureRowsAsync(runtime, limits, firstData.RevisionKey, check);
        check(firstRows.Count == 3 && firstRows.All(r => r.GetProperty("FixtureDecimal").GetRawText() == "1.2300" &&
            r.GetProperty("FixtureNull").ValueKind == JsonValueKind.Null && r.GetProperty("FixtureFlag").GetBoolean() &&
            r.GetProperty("FixtureText").GetString() == "line one\r\nline two" && r.GetProperty("FixtureBytes").GetString() == "AAH/"),
            "Stream-source capture preserves decimal token spelling, nulls, bits, line breaks and binary base64");
        var history = Required(await runtime.Snapshots.GetHistoryAsync(first.Snapshot.SnapshotKey), "runtime initial history");
        var firstVersions = await runtime.Snapshots.ListHistoryVersionsAsync(first.Snapshot.SnapshotKey, history.HistoryKey, 1);
        check(firstVersions.Items.Single().IsInitial && firstVersions.Items.Single().VersionNumber == 1 && firstVersions.Next == null,
            "Fresh runtime capture has a selected initial version, not a materialized legacy library");
        check(await RuntimeCaptureSourceImageAsync(fixture) == originalSource, "Fresh runtime capture leaves its SQL source unchanged");

        await RuntimeCaptureSourceSqlAsync(fixture, "ALTER PROCEDURE dbo.fixture_p AS SELECT N'replacement captured definition' AS Marker;");
        await RuntimeCaptureSourceSqlAsync(fixture, "UPDATE app.Surf2Documents SET FixtureDecimal=9.5000,FixtureText=N'replacement';");
        // Change isolation only on this generated source database, never on caller/user databases.
        await RuntimeCaptureSourceSqlAsync(fixture, "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
        string changedSource = await RuntimeCaptureSourceImageAsync(fixture);
        var firstToken = new SnapshotRuntimeToken(runtime.Session.Epoch, first.Snapshot.SnapshotKey, first.Snapshot.RowVersion);
        var second = await service.CaptureAsync(new(fixture.SourceConnectionString, "Replacement fixture", selection,
            first.UpdatedScope, first.UpdatedScopeToken, firstToken, "Replacement"));
        check(second.SourceIsolation == IsolationLevel.Snapshot && second.Snapshot.SnapshotKey == first.Snapshot.SnapshotKey && second.VersionKey != first.VersionKey &&
            second.UpdatedScopeToken.Version.AsSpan().SequenceEqual(first.UpdatedScopeToken.Version),
            "Replacement changes only the selected snapshot head and retains unchanged scope membership/token");
        var secondData = (await runtime.Snapshots.ListResourcesAsync(first.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Single();
        var secondProcedure = (await runtime.Snapshots.ListResourcesAsync(first.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items.Single();
        check(secondData.ResourceKey == firstData.ResourceKey && secondProcedure.ResourceKey == firstProcedure.ResourceKey &&
            secondData.RevisionKey != firstData.RevisionKey && secondProcedure.RevisionKey != firstProcedure.RevisionKey,
            "Replacement retains resource identity while staging new immutable revisions");
        await using (var historical = await runtime.Snapshots.OpenHistoricalSnapshotAsync(first.Snapshot.SnapshotKey, first.VersionKey))
        {
            var oldData = await historical.ReadPageAsync(HistoricalCollection.TableDataSets, 1);
            var oldTables = await historical.ReadPageAsync(HistoricalCollection.Tables, 1);
            check(oldData.Items.Single().RevisionKey == firstData.RevisionKey && oldTables.Items.Single().Table!.HasFullData &&
                oldTables.Items.Single().Table!.FullDataRowCount == 3,
                "Mixed metadata/data reverse history restores the first capture's flags, count and immutable dataset");
            var oldRows = await RuntimeCaptureRowsAsync(runtime, limits, oldData.Items.Single().RevisionKey!.Value, check);
            check(oldRows.Select(r => r.GetRawText()).SequenceEqual(firstRows.Select(r => r.GetRawText())),
                "Replacement reverse history restores all selected original raw rows exactly");
            var oldObjects = await historical.ReadPageAsync(HistoricalCollection.Objects, 1);
            check((await runtime.Snapshots.ReadObjectAsync(oldObjects.Items.Single().RevisionKey!.Value)).Definition
                .Contains("first captured definition", StringComparison.Ordinal), "Replacement reverse history restores the first selected definition");
        }
        var rollback = await runtime.Snapshots.ListChangesAsync(second.Snapshot.SnapshotKey, second.VersionKey, 10);
        check(rollback.Items.Count == 4 && rollback.Items.Last().Kind == DatabaseVersionedResourceKind.TableData &&
            rollback.Items.All(c => c.PreviousRevisionKey != null), "Replacement records metadata before data reversals with selected previous revision handles");
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.CaptureAsync(new(fixture.SourceConnectionString, "Stale replacement",
            selection, second.UpdatedScope, second.UpdatedScopeToken, firstToken)), check, "Stale expected snapshot token refuses runtime replacement");
        var wrongScope = new Scope { ScopeId = "wrong-runtime-scope", Name = "Wrong runtime scope" };
        var wrongToken = await runtime.StateStore.CreateScopeAsync(wrongScope, 1, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => service.CaptureAsync(new(fixture.SourceConnectionString, "Wrong membership",
            selection, wrongScope, wrongToken, new(runtime.Session.Epoch, second.Snapshot.SnapshotKey, second.Snapshot.RowVersion))),
            check, "Replacement refuses an unrelated scope without publishing a snapshot head");
        var publishedBefore = await RuntimeCapturePublishedStateAsync(fixture);
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new RuntimeCaptureProgress(p => { if (p.Phase == "Publishing") cancel.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => service.CaptureAsync(new(fixture.SourceConnectionString, "Cancelled fresh capture",
                selection, second.UpdatedScope, second.UpdatedScopeToken), progress, cancel.Token), check,
                "Runtime capture cancellation at publication refuses a new published snapshot/membership");
        }
        check(await RuntimeCapturePublishedStateAsync(fixture) == publishedBefore,
            "Cancelled and wrong-owner stages leave published snapshot heads, histories and memberships unchanged");
        await runtime.StateStore.SaveScopeAsync(second.UpdatedScope, second.UpdatedScopeToken, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => service.CaptureAsync(new(fixture.SourceConnectionString, "Stale scope",
            selection, second.UpdatedScope, second.UpdatedScopeToken)), check, "Stale expected scope token refuses capture before publication");
        await ThrowsAsync<ArgumentException>(() => service.ListSourceTablesAsync(fixture.DestinationConnectionString), check,
            "Runtime capture refuses its relational destination as source");
        string instance = (string)Required(await RuntimeCaptureSourceSqlAsync(fixture,
            "SELECT CONVERT(nvarchar(256),SERVERPROPERTY('InstanceName'));"), "fixture LocalDB instance");
        if (!instance.StartsWith("LOCALDB#", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated alias fixture requires its generated LocalDB instance pipe.");
        string alias = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { DataSource = @"np:\\.\pipe\" + instance + @"\tsql\query" }.ConnectionString;
        bool physicalRejected = false;
        try { await service.ListSourceTablesAsync(alias); }
        catch (ArgumentException error) { physicalRejected = error.Message.Contains("same physical SQL database", StringComparison.Ordinal); }
        check(physicalRejected, "An alternate named-pipe endpoint to the fixture destination is rejected by opened physical server/database identity");
        await VerifyRuntimeHistoryAsync(fixture, runtime, first, second, firstRows, limits, check);
        check(await RuntimeCaptureSourceImageAsync(fixture) == changedSource, "Replacement, conflicts and cancellation leave the fixture SQL source unchanged");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task<List<JsonElement>> RuntimeCaptureRowsAsync(RelationalRuntime runtime, CaptureLimits limits,
        long revision, Action<bool, string> check)
    {
        var capture = new RelationalCaptureStore(runtime.Session, limits);
        var descriptor = Required(await capture.GetForRevisionAsync(revision), "runtime capture descriptor");
        var rows = new List<JsonElement>(); CapturePageCursor? cursor = null;
        do
        {
            var page = await capture.ReadPageAsync(descriptor.Summary.DataSetKey, maxRows: 2, cursor: cursor);
            check(page.Rows.Count <= 2 && page.EstimatedBytes <= 32 * 1024, "Runtime capture selected row page obeys row/byte budgets");
            rows.AddRange(page.Rows.Select(r => r.Value.Clone())); cursor = page.NextCursor;
        } while (cursor != null);
        return rows;
    }
    private static async Task<string> RuntimeCapturePublishedStateAsync(SqlFixture fixture) =>
        (string)Required(await fixture.DestinationSqlAsync("""
            SELECT (SELECT SnapshotKey,CurrentVersionKey,RowVersion FROM surf.DatabaseSnapshot WHERE IsPublished=1 ORDER BY SnapshotKey FOR JSON PATH) AS Snapshots,
                (SELECT v.VersionKey,v.SnapshotKey,v.HistoryKey,v.VersionNumber FROM surf.SnapshotVersion v JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=v.SnapshotKey
                 WHERE s.IsPublished=1 ORDER BY v.VersionKey FOR JSON PATH) AS Versions,
                (SELECT ScopeResourceKey,ScopeKey,SnapshotKey FROM surf.ScopeResource ORDER BY ScopeResourceKey FOR JSON PATH) AS Memberships
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
            """), "published capture state");
    private static async Task<string> RuntimeCaptureSourceImageAsync(SqlFixture fixture) =>
        (string)Required(await RuntimeCaptureSourceSqlAsync(fixture, """
            SELECT (SELECT * FROM app.Surf2Documents ORDER BY DocumentKey FOR JSON PATH,INCLUDE_NULL_VALUES) AS Rows,
                (SELECT CONVERT(varbinary(max),definition) AS DefinitionUtf16Le FROM sys.sql_modules
                 WHERE object_id=OBJECT_ID(N'dbo.fixture_p') FOR JSON PATH) AS Definition
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
            """), "source capture image");
    private static async Task<object?> RuntimeCaptureSourceSqlAsync(SqlFixture fixture, string sql)
    {
        await using var connection = new SqlConnection(fixture.SourceConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 120;
        return await command.ExecuteScalarAsync();
    }
    private static async Task RuntimeCaptureRawSourceDefinitionAsync(SqlFixture fixture, string definition)
    {
        var bytes = new byte[checked(definition.Length * 2)];
        for (int i = 0; i < definition.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), definition[i]);
        await using var connection = new SqlConnection(fixture.SourceConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandTimeout = 120;
        command.CommandText = "DECLARE @Definition nvarchar(max)=CONVERT(nvarchar(max),@Raw); EXEC(@Definition);";
        command.Parameters.Add(RelationalSession.Parameter("@Raw", SqlDbType.VarBinary, bytes, -1));
        await command.ExecuteNonQueryAsync();
    }
    private sealed class RuntimeCaptureProgress(Action<RelationalCaptureProgress> report) : IProgress<RelationalCaptureProgress>
    { public void Report(RelationalCaptureProgress value) => report(value); }
}

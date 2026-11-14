using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.LiveSql;
using Surf2.Services.RelationalSnapshots;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.SourceResources;
using Surf2.Storage.Relational;
using Surf2.Storage;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    public static async Task RunLiveSqlChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync(new Dictionary<string, string>
        {
            ["database-snapshots"] = "{\"Snapshots\":[],\"Histories\":[]}",
            ["diagram-library"] = "{\"Diagrams\":[]}", ["scope-library"] = "{\"Scopes\":[]}"
        });
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        await fixture.MarkTestFixtureReadyAsync();
        await RuntimeCaptureSourceSqlAsync(fixture, """
            CREATE TABLE dbo.LiveFixture(Id int PRIMARY KEY, Value nvarchar(20));
            INSERT dbo.LiveFixture VALUES(1,N'one'),(2,NULL);
            CREATE USER Surf2ReadFixture WITHOUT LOGIN;
            GRANT VIEW DEFINITION TO Surf2ReadFixture;
            GRANT SELECT TO Surf2ReadFixture;
            """);
        await using (var connection = new SqlConnection(fixture.SourceConnectionString))
        {
            await connection.OpenAsync();
            async Task Act(string sql) { using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
            async Task<SqlAccessQualification> AsReader()
            {
                await Act("EXECUTE AS USER=N'Surf2ReadFixture';");
                try { return await LiveSqlAccess.QualifyAsync(connection); }
                finally { await Act("REVERT;"); }
            }
            check((await AsReader()).Status == SqlAccessStatus.Qualified, "non-owner database definition + SELECT qualifies");
            await Act("DENY VIEW DEFINITION ON OBJECT::dbo.LiveFixture TO Surf2ReadFixture;");
            check((await AsReader()).Status != SqlAccessStatus.Qualified, "specific definition denial cannot hide an object and pass complete-access qualification");
            await Act("REVOKE VIEW DEFINITION ON OBJECT::dbo.LiveFixture TO Surf2ReadFixture; DENY SELECT ON OBJECT::dbo.LiveFixture(Value) TO Surf2ReadFixture;");
            check((await AsReader()).Status == SqlAccessStatus.InsufficientAccess, "column denial is rejected despite database SELECT grant");
            await Act("REVOKE SELECT ON OBJECT::dbo.LiveFixture(Value) TO Surf2ReadFixture;");
        }
        var service = new LiveSqlService(fixture.SourceConnectionString);
        await using (var result = await service.QueryAsync("SELECT CAST(123456789012345678901234567890.12345678 AS decimal(38,8)) X,CAST(NULL AS nvarchar(1)) X,N'' AS Y,0x00FF AS B"))
        {
            var row = await result.ReadRowAsync(0);
            check(row[0].Value == "123456789012345678901234567890.12345678" && row[1].Kind == "Null" && row[2].Value == "" && row[3].Value == "AP8=", "typed live result preserves precision/null/empty/binary and duplicate names");
            check(result.Columns[0].Name == result.Columns[1].Name && result.Columns[0].Ordinal != result.Columns[1].Ordinal, "duplicate live columns have ordinal identity");
        }
        await using (var result = await service.QueryAsync("SELECT * FROM dbo.LiveFixture", limits: new(MaximumRows: 1)))
            check(result.RowCount == 1 && result.IsPartial, "row limited live query reports partial");
        await using (var result = await service.QueryAsync("SELECT REPLICATE(CAST(N'x' AS nvarchar(max)),2000),REPLICATE(CAST(N'y' AS nvarchar(max)),2000)", limits: new(MaximumBytes: 1024)))
            check(result.RowCount == 0 && result.IsPartial, "oversized query row stops during cell reading before exceeding result memory budget");
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        var scope = new Scope { Name = "Live SQL revision fixture" };
        var scopeToken = await runtime.StateStore.CreateScopeAsync(scope, 0, Guid.NewGuid());
        var capture = new RelationalDatabaseCaptureService(runtime);
        var table = (await capture.ListSourceTablesAsync(fixture.SourceConnectionString)).Items.Single(t => t.TableName == "LiveFixture");
        var selection = new[] { table, new RelationalSourceTable(0, "dbo", "AbsentFixture") };
        var first = await capture.CaptureAsync(new(fixture.SourceConnectionString, "Live history", selection, scope, scopeToken, AllowSerializableFallback: true));
        check(first.SkippedTables.SequenceEqual(new[] { "dbo.AbsentFixture" }), "confirmed absent request is skipped and reported");
        var unchanged = await capture.CaptureAsync(new(fixture.SourceConnectionString, "Live history", selection,
            first.UpdatedScope, first.UpdatedScopeToken, new(runtime.Session.Epoch, first.Snapshot.SnapshotKey, first.Snapshot.RowVersion), AllowSerializableFallback: true));
        check(unchanged.VersionKey == first.VersionKey && unchanged.Snapshot.ImportedAtUtc == first.Snapshot.ImportedAtUtc,
            "unchanged full-table refresh retains version and capture date");
        var events = await runtime.Snapshots.ReadRefreshEventsAsync(first.Snapshot.SnapshotKey);
        check(events.Count == 2 && events[0].Outcome == SnapshotRefreshOutcome.Unchanged && events[1].Outcome == SnapshotRefreshOutcome.Changed,
            "every successful refresh records its outcome independently of revisions");
        var snapshotView = await runtime.Explorer.OpenScopeAsync(unchanged.UpdatedScopeToken.Key);
        var label = snapshotView.Resources.Single(r => r.Snapshot != null).Snapshot!.RefreshDisplay;
        check(label?.VersionNumber == 1 && label.LastOutcome == SnapshotRefreshOutcome.Unchanged && label.Label("Live history").Contains("Last Refreshed"),
            "Explorer shows actual capture version/time separately from latest refresh");
        check((await runtime.Snapshots.ListFullDataSelectionsAsync(first.Snapshot.SnapshotKey)).Items.Any(s => s.TableName.Contains("AbsentFixture")), "absent table remains in saved recipe");
        await RuntimeCaptureSourceSqlAsync(fixture, "DROP TABLE dbo.LiveFixture;");
        var removed = await capture.CaptureAsync(new(fixture.SourceConnectionString, "Live history", selection,
            unchanged.UpdatedScope, unchanged.UpdatedScopeToken, new(runtime.Session.Epoch, unchanged.Snapshot.SnapshotKey, unchanged.Snapshot.RowVersion), AllowSerializableFallback: true));
        check(removed.SkippedTables.Count == 2 && removed.VersionKey != unchanged.VersionKey, "verified removal publishes a revision without stale current table rows");
        await RuntimeCaptureSourceSqlAsync(fixture, "CREATE TABLE dbo.LiveFixture(Id int, NewValue nvarchar(20)); INSERT dbo.LiveFixture VALUES(3,N'replacement');");
        var replaced = await capture.CaptureAsync(new(fixture.SourceConnectionString, "Live history", selection,
            removed.UpdatedScope, removed.UpdatedScopeToken, new(runtime.Session.Epoch, removed.Snapshot.SnapshotKey, removed.Snapshot.RowVersion), AllowSerializableFallback: true));
        check(replaced.CapturedRows == 1, "same-name replacement captures automatically using current columns despite stale ObjectId");
        await using var history = await runtime.Snapshots.OpenHistoricalSnapshotAsync(first.Snapshot.SnapshotKey, first.VersionKey);
        check((await history.ReadPageAsync(HistoricalCollection.TableDataSets, 10)).Items.Count == 1, "removed and replaced table historical capture remains readable");
        var sqlSource = new SqlConnectionStringBuilder(fixture.SourceConnectionString);
        var liveMember = new ScopedResource { Kind = ResourceKind.LiveDatabase, Path = "sql://regression",
            Source = new() { Provider = SourceProvider.LiveSql, Endpoint = sqlSource.DataSource, DatabaseName = sqlSource.InitialCatalog },
            DisplayNameOverride = "Live index fixture" };
        var liveScope = new Scope { Name = "Live index fixture", Resources = [liveMember] };
        var liveToken = await runtime.StateStore.CreateScopeAsync(liveScope, 1, Guid.NewGuid());
        var reusable = await runtime.StateStore.ListSourceResourceChoicesAsync(SourceProvider.LiveSql, scopeToken.Key);
        check(reusable.Items.Count == 1 && reusable.Items[0].Resource.Source?.SourceId == liveMember.Source.SourceId &&
            reusable.Items[0].Resource.Source?.DatabaseName == sqlSource.InitialCatalog, "existing live source picker retains its credential-free source identity and database locator");
        check((await runtime.StateStore.ListSourceResourceChoicesAsync(SourceProvider.LiveSql, liveToken.Key)).Items.Count == 0 &&
            (await runtime.StateStore.ListSourceResourceChoicesAsync(SourceProvider.Git, scopeToken.Key)).Items.Count == 0,
            "existing source picker excludes current scope registrations and other providers");
        ResourceWorkspace.Register(liveScope); ResourceWorkspace.Admit(liveMember);
        LiveSourceWorkspace.Register(liveMember, service, await service.QualifyAsync());
        try
        {
            var view = await runtime.Explorer.OpenScopeAsync(liveToken.Key);
            var memory = await MemoryReferenceIndex.BuildAsync(view, ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings()),
                _ => Task.CompletedTask, CancellationToken.None);
            check(memory.Entities.Any(e => e.Kind == ReferenceEntityKind.Table && e.Name == "LiveFixture" && e.FilePath.StartsWith("livesql://")) &&
                memory.Entities.Any(e => e.Kind == ReferenceEntityKind.Field && e.Name == "NewValue" && e.FilePath.StartsWith("livesql://")),
                "default in-memory reference index includes live tables and columns with logical navigation");
            var refresher = new RelationalScopeIndexRefresher(runtime.Session, runtime.Index, runtime.Snapshots);
            var indexed = await refresher.RefreshAsync(view, ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings()));
            check(indexed.FullyPublished && indexed.Published > 0, $"live SQL definitions publish symbols using logical document owners ({indexed.Published} published, {indexed.Failed} failed, {indexed.FailureCode})");
            var warmed = await refresher.RefreshAsync(view, ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings()), physicalRootsToReconcile: new HashSet<long>());
            check(warmed.FullyPublished && warmed.ReusedResources == 1, "live SQL resource completion reuses only the admitted session");
        }
        finally { ResourceWorkspace.ReleaseExcept(); LiveSourceWorkspace.ReleaseExcept(); }
    }
}

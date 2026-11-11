using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunV2CatalogueChecksAsync(Action<bool, string> check)
    {
        await V2SearchFailureChecksAsync(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_V2Catalogue_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var seeded = await SeedQueryIntegrationAsync(fixture, session, content,
            new RelationalCaptureStore(session), new RelationalStateStore(session, content));
        const string rawDefinition = "SELECT N'raw-\uD800-tail';";
        await InTransactionAsync(session, async (sql, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(sql, transaction, content);
            for (int i = 0; i < 999; i++)
                await writer.InsertCurrentObjectAsync(seeded.SnapshotKey, new SqlDatabaseObject
                {
                    SchemaName = "dbo", ObjectName = "V2Proc" + i.ToString("D4"),
                    Kind = SqlDatabaseObjectKind.StoredProcedure,
                    Definition = i == 998 ? rawDefinition : "SELECT N'v2-catalogue-marker';"
                }, i + 10);
            await writer.PublishSnapshotAsync(seeded.SnapshotKey, null);
        });
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        ExplorerScope scope;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            scope = await runtime.Explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
            check(scope.DatabaseCatalogue?[seeded.SnapshotKey].Count(item => item.ObjectKind == SqlDatabaseObjectKind.StoredProcedure) == 1000,
                "V2 cold scope loads a thousand procedure headers without a derived document index");
            check(trace.Commands.Count(command => command.Text.Contains("OPENJSON(@SNAPSHOTS)", StringComparison.Ordinal)) == 1,
                "V2 loads distinct scoped snapshot catalogues in one metadata query despite duplicate aliases");
            StateAccessNoReads(trace.Commands, check, "V2 scope catalogue", "TextContent", "Asset", "SymbolDefinition", "ResourceDocument");
        }
        var roots = await runtime.Explorer.GetRootsAsync(scope);
        var root = roots.First(node => node.ResourceKind == ResourceKind.DatabaseSnapshot);
        var categories = await V2CatalogueChildrenAsync(runtime.Explorer, scope, root);
        var category = categories.Single(node => node.Category == ExplorerCategory.Procedures);
        ImmutableArray<ExplorerNodeSummary> procedures;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            procedures = await V2CatalogueChildrenAsync(runtime.Explorer, scope, category);
            check(procedures.Length == 1000, "V2 expansion exposes the complete catalogue category");
            check(trace.Commands.Count <= 16, "A thousand V2 children use a constant SQL guard budget");
            StateAccessNoReads(trace.Commands, check, "V2 category expansion", "SnapshotResource", "DatabaseObjectRevision", "TextContent");
        }
        await using var documents = new RelationalDocumentService(runtime);
        var selected = procedures.Single(node => node.ObjectName == "V2Proc0998");
        RelationalDocumentAddress address;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            address = await documents.ResolveExplorerNodeAsync(scope, selected);
            check(address.Resource?.ResourceKey == selected.SnapshotResourceKey && address.TextFileNameSeed?.Contains("First alias") == true,
                "V2 selected open keeps the clicked resource and alias hierarchy");
            check(trace.Commands.Count <= 20 && trace.Commands.All(command => !command.Text.Contains("OPENJSON(@SNAPSHOTS)", StringComparison.Ordinal) &&
                !command.Text.Contains("ORDER BY R.CURRENTSORTORDINAL", StringComparison.Ordinal)),
                "V2 selected open performs direct key reads without relisting category siblings");
        }
        var plan = await documents.DescribeTextAsync(address);
        var selectedText = await documents.ReadTextAsync(plan);
        check(selectedText.Text == rawDefinition, "V2 selected definition preserves raw UTF-16 content");
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var batch = await runtime.SearchSources.SearchDefinitionsAsync(scope, [selected], new IndexTextMatcher("\uD800", false), 4096, default);
            check(batch.Single().Status == IndexScanStatus.Matched, "V2 batched substring search preserves lone UTF-16 surrogates");
            check(trace.Commands.Count <= 4, "V2 definition batch uses one read operation and constant readiness checks");
        }
        using (var trace = new StateAccessSqlTrace(connection))
        {
            long matches = 0;
            ExplorerSearchCoverage? final = null;
            await foreach (var batch in runtime.Explorer.SearchAsync(new(scope, "v2-catalogue-marker", false,
                IndexSearchTarget.Content, Guid.NewGuid()), runtime.SearchSources))
            {
                matches += batch.Hits.Length;
                final = batch.Coverage;
            }
            check(matches == 1996 && final?.Completed == true && !final.GenerationChanged,
                "V2 content search scans current SQL definitions and preserves both aliases without indexing");
            check(trace.Commands.Count < 350, "V2 search batches SQL reads across a thousand procedures");
            StateAccessNoReads(trace.Commands, check, "V2 authoritative search", "SymbolDefinition", "ResourceDocument", "DocumentRevision");
        }
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.Document;")) == 0,
            "V2 browse, direct open, and content search never require or populate the derived document index");
        await CheckV2CatalogueScaleAsync(fixture, session, content, connection, seeded, check);
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task CheckV2CatalogueScaleAsync(SqlFixture fixture, RelationalSession session,
        RelationalContentStore content, string connection, QueryIntegrationFixture seeded, Action<bool, string> check)
    {
        const int extraProcedures = 11_673;
        long unrelated = 0;
        await InTransactionAsync(session, async (sql, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(sql, transaction, content);
            unrelated = await writer.CreateSnapshotAsync(new("catalogue-unrelated", "Unrelated fixture", "fixture", EvidenceTime, 1));
        });
        await SeedV2CatalogueScaleAsync(fixture, seeded, unrelated, extraProcedures);

        // Capture only the generated fixture's parameterized metadata command. Replay it for the
        // actual plan instead of making the application depend on SQL Server diagnostic privileges.
        string? catalogueSql = null;
        SqlParameter[] parameters = [];
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        ExplorerScope scope;
        var elapsed = Stopwatch.StartNew();
        using (var trace = new StateAccessSqlTrace(connection, command =>
        {
            if (!command.CommandText.Contains("OPENJSON(@Snapshots)", StringComparison.OrdinalIgnoreCase)) return;
            catalogueSql = command.CommandText;
            parameters = command.Parameters.Cast<SqlParameter>().Select(parameter => (SqlParameter)((ICloneable)parameter).Clone()).ToArray();
        }))
        {
            scope = await runtime.Explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
            var loads = trace.Commands.Where(command => command.Text.Contains("OPENJSON(@SNAPSHOTS)", StringComparison.Ordinal)).ToArray();
            check(loads.Length == 1 && loads[0].Numbers.GetValueOrDefault("@Take") == 100_001,
                "A cold large scope uses one row-bounded catalogue query for its selected snapshots");
            StateAccessNoReads(trace.Commands, check, "Large startup catalogue", "TextContent", "Asset", "SymbolDefinition", "ResourceDocument");
        }
        elapsed.Stop();
        var catalogue = scope.DatabaseCatalogue ?? throw new InvalidOperationException("Large catalogue was not loaded.");
        var rows = catalogue[seeded.SnapshotKey];
        check(catalogue.Count == 1 && rows.Count(row => row.ObjectKind == SqlDatabaseObjectKind.StoredProcedure) == 1000 + extraProcedures &&
            rows.All(row => row.Resource.SnapshotKey == seeded.SnapshotKey && row.Resource.ObjectName != "RetiredScaleName"),
            "A large selected catalogue excludes unrelated snapshots and retired revisions");
        check(rows.Any(row => row.Resource.SchemaName == "revised" && row.Resource.ObjectName == "CurrentScaleName" &&
            row.Resource.OriginalResourceKey == "catalogue-scale:CatalogueScale11673"),
            "A current revision supplies exact names even when its original resource locator differs");
        check(rows.SequenceEqual(rows.OrderBy(row => row.Resource.SortOrdinal).ThenBy(row => row.Resource.ResourceKey)),
            "A large catalogue restores source order and stable ties after streaming");
        Console.WriteLine($"Large startup catalogue: {rows.Length:N0} rows in {elapsed.Elapsed.TotalMilliseconds:N0} ms on the generated fixture.");

        await using var sql = await runtime.Session.OpenAsync();
        await using var planCommand = sql.CreateCommand();
        planCommand.CommandText = "SET STATISTICS XML ON;\n" + (catalogueSql ?? throw new InvalidOperationException("No catalogue command captured."));
        planCommand.Parameters.AddRange(parameters);
        await using var reader = await planCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        var plans = new List<XDocument>();
        int streamed = 0;
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount == 14) streamed++;
                else if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string)) plans.Add(XDocument.Parse(reader.GetString(0)));
                else throw new InvalidOperationException("Unexpected catalogue plan result.");
            }
        } while (await reader.NextResultAsync());

        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var operators = plans.SelectMany(plan => plan.Descendants(ns + "RelOp")).ToArray();
        bool wideWork = operators.Any(op => (string?)op.Attribute("PhysicalOp") is "Sort" or "Hash Match" &&
            ((long?)op.Attribute("AvgRowSize") ?? 0) > 512);
        bool revisionScan = operators.Any(op => (string?)op.Attribute("PhysicalOp") is "Clustered Index Scan" or "Index Scan" or "Table Scan" &&
            op.Descendants(ns + "Object").Any(source => (string?)source.Attribute("Table") is "[SnapshotResourceRevision]" or
                "[DatabaseObjectRevision]" or "[TableMetadataRevision]" or "[TableDataRevision]"));
        check(operators.Length > 0 && streamed == rows.Length && !wideWork && !revisionScan,
            "The actual large catalogue plan avoids wide sorts, wide hashes and revision-table scans");
    }

    private static Task<object?> SeedV2CatalogueScaleAsync(SqlFixture fixture, QueryIntegrationFixture seeded,
        long unrelated, int count) => fixture.DestinationSqlAsync("""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        DECLARE @Resources TABLE(ResourceKey bigint PRIMARY KEY, SnapshotKey bigint NOT NULL, N int NOT NULL, Name nvarchar(64) NOT NULL);
        DECLARE @Revisions TABLE(ResourceKey bigint PRIMARY KEY, RevisionKey bigint NOT NULL);
        WITH digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
        numbers AS (SELECT a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+1 n FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e)
        MERGE surf.SnapshotResource AS target
        USING (SELECT n, s.SnapshotKey, N'CatalogueScale'+CONVERT(nvarchar(16),n) Name
            FROM numbers CROSS JOIN (VALUES(@Snapshot),(@Unrelated)) s(SnapshotKey) WHERE n<=@Count) AS source
        ON 1=0 WHEN NOT MATCHED THEN INSERT(SnapshotKey,Kind,SchemaName,ObjectName,NameHash,OriginalResourceKey,ResourceKeyHash,CurrentSortOrdinal)
            VALUES(source.SnapshotKey,0,N'dbo',source.Name,HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'3:dbo'+CONVERT(nvarchar(16),LEN(source.Name))+N':'+source.Name)),
                N'catalogue-scale:'+source.Name,HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'catalogue-scale:'+source.Name)),10000+(@Count-source.n)/2)
        OUTPUT inserted.ResourceKey,source.SnapshotKey,source.n,source.Name INTO @Resources;

        INSERT surf.SnapshotResourceRevision(ResourceKey,SnapshotKey,IsSealed)
            OUTPUT inserted.ResourceKey,inserted.RevisionKey INTO @Revisions
            SELECT ResourceKey,SnapshotKey,1 FROM @Resources;
        INSERT surf.DatabaseObjectRevision(RevisionKey,SchemaName,ObjectName,ObjectKind,TypeDescription,ParentSchemaName,ParentObjectName,DefinitionContentKey)
            SELECT v.RevisionKey,CASE WHEN r.SnapshotKey=@Snapshot AND r.N=@Count THEN N'revised' ELSE N'dbo' END,
                CASE WHEN r.SnapshotKey=@Snapshot AND r.N=@Count THEN N'CurrentScaleName' ELSE r.Name END,
                o.ObjectKind,o.TypeDescription,o.ParentSchemaName,o.ParentObjectName,o.DefinitionContentKey
            FROM @Resources r JOIN @Revisions v ON v.ResourceKey=r.ResourceKey
            CROSS JOIN surf.DatabaseObjectRevision o WHERE o.RevisionKey=@SourceRevision;
        UPDATE r SET CurrentRevisionKey=v.RevisionKey FROM surf.SnapshotResource r JOIN @Revisions v ON v.ResourceKey=r.ResourceKey;

        DELETE FROM @Revisions;
        INSERT surf.SnapshotResourceRevision(ResourceKey,SnapshotKey,IsSealed)
            OUTPUT inserted.ResourceKey,inserted.RevisionKey INTO @Revisions
            SELECT ResourceKey,SnapshotKey,1 FROM @Resources WHERE SnapshotKey=@Snapshot;
        INSERT surf.DatabaseObjectRevision(RevisionKey,SchemaName,ObjectName,ObjectKind,TypeDescription,ParentSchemaName,ParentObjectName,DefinitionContentKey)
            SELECT v.RevisionKey,N'dbo',N'RetiredScaleName',o.ObjectKind,o.TypeDescription,o.ParentSchemaName,o.ParentObjectName,o.DefinitionContentKey
            FROM @Revisions v CROSS JOIN surf.DatabaseObjectRevision o WHERE o.RevisionKey=@SourceRevision;
        UPDATE surf.DatabaseSnapshot SET IsPublished=1 WHERE SnapshotKey IN(@Snapshot,@Unrelated);
        UPDATE surf.SnapshotCatalogueHead SET UserKey=UserKey WHERE UserKey=1;
        COMMIT TRANSACTION;
        """, RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, seeded.SnapshotKey),
        RelationalSession.Parameter("@Unrelated", SqlDbType.BigInt, unrelated),
        RelationalSession.Parameter("@SourceRevision", SqlDbType.BigInt, seeded.Procedure.RevisionKey),
        RelationalSession.Parameter("@Count", SqlDbType.Int, count));

    private static async Task<ImmutableArray<ExplorerNodeSummary>> V2CatalogueChildrenAsync(RelationalExplorerService explorer,
        ExplorerScope scope, ExplorerNodeSummary parent)
    {
        var result = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
        await foreach (var batch in explorer.GetChildrenAsync(scope, parent))
        {
            if (batch.State == ExplorerChildrenState.Failed) throw new InvalidOperationException(batch.FailureCode);
            result.AddRange(batch.Nodes);
        }
        return result.ToImmutable();
    }

    private static async Task V2SearchFailureChecksAsync(Action<bool, string> check)
    {
        var snapshot = new SnapshotSummary(1, Guid.NewGuid(), "test-snapshot", "Test", "Test", EvidenceTime, 0, null, []);
        var scope = new ExplorerScope(new(Guid.NewGuid(), 1, 0, "scope", "snapshot", "diagram", false, [], [], false),
            "test-scope", "Test", [
                new(1, 0, "file", ResourceKind.File, Path.Combine(Path.GetTempPath(), "surf-v2-fake.cs"), "", true, true, null, null, null, null, false),
                new(2, 1, "db", ResourceKind.DatabaseSnapshot, snapshot.SnapshotId, "", true, true, snapshot, null, null, null, false)
            ], [], "en-US");
        var explorer = new RelationalExplorerService(new V2FailureMetadata(scope), new V2FailurePhysical());
        var sources = new V2FailureSources();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var _ in explorer.SearchAsync(new(scope, "test", false, IndexSearchTarget.Content, Guid.NewGuid()), sources, timeout.Token)) { }
            check(false, "Concurrent source failure must reach the search owner");
        }
        catch (InvalidOperationException error) when (error.Message == V2FailureSources.ExpectedFailure)
        {
            check(sources.DatabaseStarted.Task.IsCompletedSuccessfully && sources.DatabaseCancelled.Task.IsCompletedSuccessfully,
                "V2 file and SQL workers overlap and a file failure cancels and drains the SQL worker");
        }
    }

    private sealed class V2FailureMetadata(ExplorerScope scope) : IExplorerMetadataQueries
    {
        public Task<ExplorerScope> ReadScopeAsync(long key, IReadOnlySet<string>? unloaded, string? culture, CancellationToken ct) => Task.FromResult(scope);
        public Task<bool> IsCurrentAsync(IndexRequestContext context, CancellationToken ct) => Task.FromResult(true);
        public async IAsyncEnumerable<ExplorerDatabaseItem> ReadDatabaseCategoryAsync(ExplorerScope request, ExplorerResource resource,
            ExplorerCategory category, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (category == ExplorerCategory.Procedures)
                yield return new(new(1, Guid.NewGuid(), 1, DatabaseVersionedResourceKind.StoredProcedure, "dbo", "Test", "Test", 1, 0, []),
                    SqlDatabaseObjectKind.StoredProcedure);
        }
    }

    private sealed class V2FailurePhysical : IPhysicalExplorerQueries
    {
        public Task<ExplorerAvailability> ProbeAsync(string path, bool directory, CancellationToken ct) => Task.FromResult(ExplorerAvailability.Present);
        public Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string culture, ExplorerLimits limits, CancellationToken ct) =>
            Task.FromResult(ImmutableArray<PhysicalExplorerEntry>.Empty);
    }

    private sealed class V2FailureSources : IExplorerSearchSources, IExplorerCurrentContentSearch
    {
        internal const string ExpectedFailure = "Expected concurrent source failure.";
        internal TaskCompletionSource DatabaseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DatabaseCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImmutableArray<ExplorerSearchOutcome>> SearchDefinitionsAsync(ExplorerScope scope,
            ImmutableArray<ExplorerNodeSummary> nodes, IndexTextMatcher matcher, int maximumCharacters, CancellationToken ct)
        {
            if (!nodes.Any(node => node.Role == ExplorerNodeRole.DatabaseDocument)) return [];
            DatabaseStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { if (ct.IsCancellationRequested) DatabaseCancelled.TrySetResult(); }
            return [];
        }
        public async Task<string> ReadCurrentContentAsync(ExplorerNodeSummary node, int maximumCharacters, CancellationToken ct)
        {
            await DatabaseStarted.Task.WaitAsync(ct);
            throw new InvalidOperationException(ExpectedFailure);
        }
        public Task<ImmutableArray<ExplorerIndexBinding>> BindIndexDocumentsAsync(ExplorerScope scope, ImmutableArray<ExplorerNodeSummary> nodes, CancellationToken ct) =>
            throw new InvalidOperationException("Current-source search must not bind derived documents.");
        public IAsyncEnumerable<IndexSearchBatch> SearchIndexAsync(IndexSearchRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Current-source search must not query the derived index.");
        public Task<ExplorerTableSearch> SearchTableAsync(ExplorerNodeSummary node, IndexTextMatcher matcher, int maximumCharacters, CancellationToken ct) =>
            throw new InvalidOperationException("The fixture has no tables.");
        public Task<ExplorerTableSearch> SearchTableAsync(ExplorerScope scope, ExplorerNodeSummary node, IndexTextMatcher matcher, int maximumCharacters, CancellationToken ct) =>
            SearchTableAsync(node, matcher, maximumCharacters, ct);
    }
}

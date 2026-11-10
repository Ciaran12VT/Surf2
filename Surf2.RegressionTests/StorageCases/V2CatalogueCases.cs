using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
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
        await fixture.VerifySourceUnchangedAsync(check);
    }

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

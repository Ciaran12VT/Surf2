using System.Collections.Immutable;
using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunScopeIndexChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_ScopeIndex_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var snapshots = new RelationalSnapshotStore(session, content);
        var state = new RelationalStateStore(session, content);
        var capture = new RelationalCaptureStore(session);
        var seeded = await SeedQueryIntegrationAsync(fixture, session, content, capture, state);
        string root = Path.Combine(fixture.OwnedDirectory, "scope-index-source");
        Directory.CreateDirectory(root);
        foreach (string directory in new[] { ".git", ".vs", "bin", "obj", "node_modules", "src" })
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
            await File.WriteAllTextAsync(Path.Combine(root, directory, "ignored.cs"),
                directory == "src" ? "class VisibleSource { }" : new string('x', 20000));
        }
        string explicitFile = Path.Combine(root, "bin", "explicit.cs");
        await File.WriteAllTextAsync(explicitFile, "class ExplicitGenerated { }");
        string visibleFile = Path.Combine(root, "src", "ignored.cs");
        var selected = Required(await state.ReadScopeAsync(seeded.ScopeToken.Key), "scope index owner");
        selected.Value.Resources.Add(new() { ResourceId = "folder", Kind = ResourceKind.Folder, Path = root, IncludeChildren = false });
        selected.Value.Resources.Add(new() { ResourceId = "explicit", Kind = ResourceKind.File, Path = explicitFile });
        await state.SaveScopeAsync(selected.Value, selected.Token, Guid.NewGuid());
        var limits = new ExplorerLimits(PageSize: 1, MaximumDocumentCharacters: 8192);
        var index = new RelationalIndexStore(session, content);
        var explorer = new RelationalExplorerService(new RelationalExplorerMetadataAdapter(session, index, limits), limits: limits);
        var physical = new ObservedScopeIndexPhysical();
        var refresher = new RelationalScopeIndexRefresher(session, index, snapshots, physical, limits);
        var policy = ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings());
        var scope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        var folder = scope.Resources.Single(r => r.ResourceId == "folder");

        // Simulate generated memberships left by the previous worker, including an unrelated scope owner.
        var other = Required(await state.ReadScopeAsync(seeded.OtherScopeKey), "other scope index owner");
        other.Value.Resources.Add(new() { ResourceId = "other-folder", Kind = ResourceKind.Folder, Path = root });
        await state.SaveScopeAsync(other.Value, other.Token, Guid.NewGuid());
        var otherScope = await explorer.OpenScopeAsync(seeded.OtherScopeKey);
        var otherFolder = otherScope.Resources.Single();
        long sharedGenerated = 0;
        for (int i = 0; i < 65; i++)
        {
            string path = Path.Combine(root, "bin", "legacy-" + i + ".cs");
            await File.WriteAllTextAsync(path, "generated-search-marker");
            var handle = await index.RegisterAsync(new(new FileDocumentOwner(path), "legacy-" + i, "C#"));
            var membership = new DocumentMembership(folder.ScopeResourceKey, "legacy-" + i, path, path, Path.GetDirectoryName(path)!, i);
            var locator = new IndexLocator(folder.ScopeResourceKey, 0, path);
            await index.SetMembershipAsync(handle.DocumentKey, i == 0 ? [membership, membership with { ScopeResourceKey = otherFolder.ScopeResourceKey }] : [membership],
                i == 0 ? [locator, locator with { ScopeResourceKey = otherFolder.ScopeResourceKey }] : [locator]);
            if (i == 0) sharedGenerated = handle.DocumentKey;
        }
        scope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        var updates = new List<ExplorerIndexRefreshProgress>();
        var cold = await QueryIntegrationObservedAsync(connection,
            () => refresher.RefreshAsync(scope, policy, p => { updates.Add(p); return Task.CompletedTask; }), commands =>
        {
            check(commands.Count(c => c.Text.Contains("DELETE l FROM surf.DocumentLocator l WHERE l.ScopeResourceKey=@Resource", StringComparison.OrdinalIgnoreCase)) == 2,
                "65 obsolete generated memberships are pruned in two bounded transactions, not one per document");
        }, check, "cold scope reference index");
        check(cold.Completed && cold.FullyPublished && cold.Failed == 0 && cold.Considered == 5 && cold.Published == 5,
            "Cold reference indexing publishes shared snapshot objects, diagram, visible source and explicit generated file once");
        check(physical.Directories.All(path => Path.GetFileName(path) is not (".git" or ".vs" or "bin" or "obj" or "node_modules")),
            "Reference discovery never enters the five legacy-excluded child directories");
        check(updates.Any(p => p.Resource == "First alias") && !updates.Any(p => p.Resource == "Second alias" && p.Document != null),
            "Shared snapshot aliases retain memberships without repeating their document enumeration");
        check(updates.Any(p => p.Phase == "Indexing" && p.Document == "dbo.NeedleProc") &&
            updates.Any(p => p.Phase == "Reconciling") && updates[^1].Phase == "Finished" &&
            updates.Zip(updates.Skip(1)).All(pair => pair.First.Elapsed <= pair.Second.Elapsed),
            "Progress identifies the current resource/document, reconciliation and terminal phase with monotonic elapsed time");
        check(await ScopeIndexScalarAsync(session, "SELECT COUNT_BIG(*) FROM surf.ResourceDocument WHERE DocumentKey=@Document AND ScopeResourceKey=@Resource;",
            sharedGenerated, otherFolder.ScopeResourceKey) == 1 &&
            await ScopeIndexScalarAsync(session, "SELECT COUNT_BIG(*) FROM surf.ResourceDocument WHERE DocumentKey=@Document AND ScopeResourceKey=@Resource;",
            sharedGenerated, folder.ScopeResourceKey) == 0,
            "Generated membership pruning removes only this successfully enumerated owner and preserves another scope");
        var currentScope = scope with { Context = cold.Context! };
        var references = new RelationalReferenceService(session, index, limits);
        var paint = await references.LoadPaintAsync(currentScope);
        check(paint.Coverage.FullyPublished && paint.Paint.Names.ContainsKey("VisibleSource") && paint.Paint.Names.ContainsKey("ExplicitGenerated"),
            "Generated exclusions preserve explicit file references and the final published catalogue");
        var sources = new RelationalExplorerSearchSources(session, index, snapshots, capture, limits);
        var hits = new List<ExplorerSearchHit>();
        await foreach (var batch in explorer.SearchAsync(new(currentScope, "generated-search-marker", false, IndexSearchTarget.Content, Guid.NewGuid()), sources))
            hits.AddRange(batch.Hits);
        check(hits.Count == 65 && hits.All(h => h.Node.FullPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "Explicit content search still scans unindexed generated files instead of applying reference exclusions");

        var warmScope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        var warm = await QueryIntegrationObservedAsync(connection, () => refresher.RefreshAsync(warmScope, policy), commands =>
        {
            check(!commands.Any(c => c.Text.Contains("c.Text", StringComparison.OrdinalIgnoreCase) || c.Text.Contains("INSERT surf.DocumentRevision", StringComparison.OrdinalIgnoreCase)),
                "Warm immutable snapshot refresh neither rereads definition bodies nor republishes derived content");
            check(!commands.Any(c => c.Text.Contains("SELECT d.Freshness,r.SourceFingerprint", StringComparison.OrdinalIgnoreCase) ||
                c.Text.Contains("SELECT Version FROM surf.Document", StringComparison.OrdinalIgnoreCase)),
                "Existing document lookup reuses published metadata and avoids separate state/version round trips");
        }, check, "warm scope reference index");
        check(warm.Completed && warm.FullyPublished && warm.Published == 0 && warm.Unchanged == 5,
            "An unchanged restart reuses all five published sources");

        var unloaded = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, new HashSet<string> { "explicit" });
        var subset = await refresher.RefreshAsync(unloaded, policy);
        var loadedView = unloaded with { Context = subset.Context! };
        check(subset.Completed && subset.FullyPublished && subset.UnloadedResources == 1 && !subset.Context!.DiscoveryReconciled &&
            MainWindow.RelationalIndexStatus(subset).Contains("unloaded resources excluded", StringComparison.Ordinal),
            "Loaded-view discovery finishes without declaring unloaded resources failures or publishing a whole-scope SQL marker");
        check(!(await references.LoadPaintAsync(loadedView)).Coverage.FullyPublished,
            "An unacknowledged loaded subset is never accepted as a complete reference catalogue");
        references.AcceptCompletedDiscovery(subset);
        var loadedPaint = await references.LoadPaintAsync(loadedView);
        check(loadedPaint.Coverage.FullyPublished && !loadedPaint.Paint.Names.ContainsKey("ExplicitGenerated") && loadedPaint.Paint.Names.ContainsKey("VisibleSource"),
            "Only the completed exact loaded-resource view becomes ready and unloaded sources remain excluded");
        check((await references.ResolveAsync(loadedView.Context, [new ReferenceQuery("NeedleProc")])).Single().Candidates.Length == 1,
            "Reference navigation remains available in a verified loaded view with both loaded database aliases");
        var otherContext = await index.CaptureContextAsync(scope.Context.ScopeKey, [folder.ScopeResourceKey]);
        check(!references.IsDiscoveryReady(otherContext) && !(await new RelationalReferenceService(session, index, limits).LoadPaintAsync(loadedView)).Coverage.FullyPublished,
            "Different unloaded selections and fresh reference-service owners cannot reuse another view's in-memory discovery proof");
        await File.WriteAllTextAsync(visibleFile, "class ChangedSource { }");
        var changed = await refresher.RefreshAsync(await explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(changed.FullyPublished && changed.Published == 1 && changed.Unchanged == 4,
            "A changed physical source republishes while unchanged captured sources remain reusable");
        var changedPaint = await references.LoadPaintAsync(scope with { Context = changed.Context! });
        check(changedPaint.Paint.Names.ContainsKey("ChangedSource") && !changedPaint.Paint.Names.ContainsKey("VisibleSource"),
            "Changed source publication replaces old symbols without keeping stale highlights");
        File.Delete(visibleFile);
        var missingOwner = Required(await state.ReadScopeAsync(seeded.ScopeToken.Key), "scope missing source owner");
        missingOwner.Value.Resources.Add(new() { ResourceId = "missing", Kind = ResourceKind.File, Path = visibleFile, DisplayNameOverride = "Missing source" });
        await state.SaveScopeAsync(missingOwner.Value, missingOwner.Token, Guid.NewGuid());
        var missing = await refresher.RefreshAsync(await explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(missing.Completed && !missing.FullyPublished && missing.Failed == 1 && missing.FailureCode == "Missing" &&
            missing.FailedResource == "Missing source" && MainWindow.RelationalIndexStatus(missing).Contains("Missing source", StringComparison.Ordinal),
            "Missing-source failure completes with its reason and resource while never claiming reference readiness");
        bool rejected = false;
        try { references.AcceptCompletedDiscovery(missing); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && !references.IsDiscoveryReady(missing.Context!), "Incomplete source discovery cannot replace or validate a reference view");
        using var cancelled = new CancellationTokenSource();
        var cancelledScope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        await ThrowsAsync<OperationCanceledException>(() => refresher.RefreshAsync(cancelledScope, policy, p =>
        { cancelled.Cancel(); return Task.CompletedTask; }, cancelled.Token), check, "Reference refresh cancellation interrupts work without publishing completed coverage");
        check(MainWindow.RelationalIndexStatus(new(7, 2, 5, 0, 0, false, false, Resource: "Database", Document: "dbo.Procedure",
                Phase: "Indexing", Elapsed: TimeSpan.FromMinutes(10))).Contains("00:10:00", StringComparison.Ordinal) &&
            MainWindow.RelationalIndexStatus(warm).StartsWith("References ready:", StringComparison.Ordinal),
            "Index status exposes elapsed/current work and distinguishes warm readiness");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task<long> ScopeIndexScalarAsync(RelationalSession session, string sql, long document, long resource)
    {
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.Add(RelationalSession.Parameter("@Document", SqlDbType.BigInt, document));
        command.Parameters.Add(RelationalSession.Parameter("@Resource", SqlDbType.BigInt, resource));
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private sealed class ObservedScopeIndexPhysical : IPhysicalExplorerQueries
    {
        private readonly PhysicalExplorerQueries _inner = new();
        public List<string> Directories { get; } = [];
        public Task<ExplorerAvailability> ProbeAsync(string path, bool directory, CancellationToken ct) => _inner.ProbeAsync(path, directory, ct);
        public Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string culture, ExplorerLimits limits, CancellationToken ct)
        { Directories.Add(path); return _inner.ReadDirectoryAsync(path, culture, limits, ct); }
    }
}

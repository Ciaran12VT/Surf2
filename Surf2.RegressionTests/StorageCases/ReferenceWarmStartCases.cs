using System.Data;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunReferenceWarmStartChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_ReferenceWarmStart_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var initial = new ReferenceWarmStartOwner(connection);
        var seeded = await SeedQueryIntegrationAsync(fixture, initial.Session, initial.Content,
            new RelationalCaptureStore(initial.Session), initial.State);
        var policy = ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings());
        var scope = await initial.Explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        var cold = await initial.Refresher.RefreshAsync(scope, policy);
        check(cold.FullyPublished && cold.Considered == 3 && cold.Published == 3 && cold.ReusedResources == 0,
            "Cold captured reference discovery publishes a procedure, table and diagram once despite two database aliases");
        check(await ReferenceWarmStartProofCountAsync(fixture, seeded.ScopeToken.Key) == 3,
            "Successful cold discovery persists completion separately for both captured aliases and the diagram");

        var small = await ReferenceWarmStartRestartAsync(connection, seeded.ScopeToken.Key, null, policy, check, "three-document restart");
        check(small.Value.Owner.Session.Epoch != initial.Session.Epoch &&
            small.Value.Result.Considered == 0 && small.Value.Result.Published == 0 &&
            small.Value.Result.Unchanged == 3 && small.Value.Result.ReusedResources == 3 && small.Value.Paint.Coverage.FullyPublished,
            "A new session/refresher restores captured readiness from durable proofs without examining any individual document");
        await ReferenceWarmStartCachedResolutionAsync(connection, small.Value, "NeedleProc", check);

        var unloaded = await ReferenceWarmStartRestartAsync(connection, seeded.ScopeToken.Key,
            new HashSet<string> { "db-b" }, policy, check, "restart with unloaded database alias");
        check(unloaded.Value.Result.UnloadedResources == 1 && unloaded.Value.Result.ReusedResources == 2 &&
            unloaded.Value.Result.Considered == 0 && unloaded.Value.Result.Unchanged == 3 && unloaded.Value.Paint.Coverage.FullyPublished,
            "A fresh process reuses captured proofs for the exact loaded view without checking an intentionally unloaded alias");
        var diagramOnly = await ReferenceWarmStartRestartAsync(connection, seeded.ScopeToken.Key,
            new HashSet<string> { "db-a", "db-b" }, policy, check, "diagram-only restart");
        var excluded = await diagramOnly.Value.Owner.References.ResolveAsync(diagramOnly.Value.Scope.Context,
            [new ReferenceQuery("NeedleProc")]);
        check(diagramOnly.Value.Result.ReusedResources == 1 && diagramOnly.Value.Result.Unchanged == 1 &&
            diagramOnly.Value.Paint.Coverage.FullyPublished && !diagramOnly.Value.Paint.Paint.Names.ContainsKey("NeedleProc") &&
            excluded.Single().Candidates.IsEmpty,
            "Changing the unloaded selection excludes database highlights and targets instead of reusing another view's metadata");
        await ReferenceWarmStartRebaseAsync(fixture, connection, unloaded.Value, policy, check);

        // Simulate a completion written under an older renderer/policy without changing authoritative source data.
        await fixture.DestinationSqlAsync("""
            UPDATE c SET DependencyHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'older-reference-policy'))
            FROM surf.ReferenceResourceCompletion c JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=c.ScopeResourceKey
            WHERE sr.ScopeKey=@Scope AND sr.ResourceId=N'db-a';
            """, RelationalSession.Parameter("@Scope", SqlDbType.BigInt, seeded.ScopeToken.Key));
        var olderPolicy = new ReferenceWarmStartOwner(connection);
        var policyResult = await olderPolicy.Refresher.RefreshAsync(await olderPolicy.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(policyResult.FullyPublished && policyResult.ReusedResources == 1 && policyResult.Considered == 2 &&
            policyResult.Published == 0 && policyResult.Unchanged == 3,
            "An incompatible policy dependency rebuilds both aliases of that captured owner while retaining the independent diagram proof");

        var selected = Required(await initial.State.ReadScopeAsync(seeded.ScopeToken.Key), "warm scope resource edit");
        selected.Value.Name = "Warm scope edited";
        selected.Value.Resources.Single(r => r.ResourceId == "db-a").DisplayNameOverride = "Edited captured alias";
        await initial.State.SaveScopeAsync(selected.Value, selected.Token, Guid.NewGuid());
        check(await ReferenceWarmStartProofCountAsync(fixture, seeded.ScopeToken.Key) == 3,
            "Authoritative scope edits preserve proof rows; dependency validation, not writer-incompatible triggers, determines reuse");
        await ThrowsAsync<IndexGenerationChangedException>(() => small.Value.Owner.References.ResolveAsync(small.Value.Scope.Context,
            [new ReferenceQuery("NeedleProc")]), check, "A cached reference catalogue rejects a changed scope/resource version");
        var edited = new ReferenceWarmStartOwner(connection);
        var editedResult = await edited.Refresher.RefreshAsync(await edited.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(editedResult.FullyPublished && editedResult.ReusedResources == 1 && editedResult.Considered == 2 && editedResult.Published == 0,
            "Editing a captured alias reconciles both database aliases while retaining the unaffected diagram completion");

        long replacementRevision = 0;
        await InTransactionAsync(initial.Session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, initial.Content);
            replacementRevision = await writer.InsertObjectRevisionAsync(seeded.Procedure.ResourceKey, new SqlDatabaseObject
            {
                SchemaName = "dbo", ObjectName = "NeedleProc", Kind = SqlDatabaseObjectKind.StoredProcedure,
                Definition = "CREATE PROCEDURE [dbo].[NeedleProc] AS SELECT N'warm-reference-new-current-revision-marker';"
            });
            await writer.SealRevisionAsync(replacementRevision);
            await writer.SetCurrentRevisionAsync(seeded.Procedure.ResourceKey, replacementRevision, 0);
        });
        check(await ReferenceWarmStartSnapshotProofCountAsync(fixture, seeded.ScopeToken.Key, seeded.SnapshotKey) == 2,
            "A raw current-revision change leaves old proof rows for source-version validation without republishing the snapshot");
        var replaced = new ReferenceWarmStartOwner(connection);
        var replacedResult = await replaced.Refresher.RefreshAsync(await replaced.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(replacedResult.FullyPublished && replacedResult.ReusedResources == 1 && replacedResult.Published == 1 &&
            await ReferenceWarmStartCurrentSourceAsync(fixture, seeded.Procedure.ResourceKey) == replacementRevision,
            "Source-version validation detects a raw pointer change without snapshot publication and republishes the selected definition");

        var diagram = Required(await initial.State.ReadDiagramAsync(seeded.DiagramKey), "warm diagram edit");
        diagram.Value.Document.Objects[0].LabelText = "changed diagram projection";
        await initial.State.SaveDiagramAsync(diagram.Value, diagram.Token, Guid.NewGuid());
        check(await ReferenceWarmStartProofCountAsync(fixture, seeded.ScopeToken.Key) == 3,
            "Saving a diagram retains old proof rows for diagram-version validation without installing an authoritative-table trigger");
        var changedDiagram = new ReferenceWarmStartOwner(connection);
        var diagramResult = await changedDiagram.Refresher.RefreshAsync(await changedDiagram.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(diagramResult.FullyPublished && diagramResult.ReusedResources == 2 && diagramResult.Published == 1 && diagramResult.Considered == 1,
            "A new diagram revision rebuilds its projection while both completed database aliases take the fast path");

        await ReferenceWarmStartFailureAsync(fixture, connection, seeded, policy, check);
        await ReferenceWarmStartConcurrentFenceAsync(fixture, connection, seeded.ScopeToken.Key, policy, check);
        await ReferenceWarmStartScaleAsync(fixture, connection, seeded, policy, small.Commands.Count, check);
        await ReferenceWarmStartPhysicalRootsAsync(fixture, connection, policy, check);
        await ReferenceWarmStartManyRootsAsync(fixture, connection, policy, check);
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private sealed class ReferenceWarmStartOwner
    {
        public RelationalSession Session { get; }
        public RelationalContentStore Content { get; } = new();
        public RelationalStateStore State { get; }
        public RelationalIndexStore Index { get; }
        public RelationalExplorerService Explorer { get; }
        public RelationalScopeIndexRefresher Refresher { get; }
        public RelationalReferenceService References { get; }

        public ReferenceWarmStartOwner(string connection, ExplorerLimits? limits = null, IPhysicalExplorerQueries? physical = null)
        {
            limits ??= new ExplorerLimits(PageSize: 1, MaximumDocumentCharacters: 8192);
            Session = new(connection);
            State = new(Session, Content);
            Index = new(Session, Content);
            Explorer = new(new RelationalExplorerMetadataAdapter(Session, Index, limits), limits: limits);
            Refresher = new(Session, Index, new RelationalSnapshotStore(Session, Content), physical, limits);
            References = new(Session, Index, limits);
        }
    }

    private sealed record ReferenceWarmStartRun(ReferenceWarmStartOwner Owner, ExplorerScope Scope,
        ExplorerIndexRefreshProgress Result, ReferenceCatalogue Paint);
    private sealed record ReferenceWarmStartObservation<T>(T Value, IReadOnlyList<StateAccessCommand> Commands, TimeSpan Elapsed);

    private static async Task<ReferenceWarmStartObservation<ReferenceWarmStartRun>> ReferenceWarmStartRestartAsync(
        string connection, long scopeKey, HashSet<string>? unloaded, ExplorerIndexLanguagePolicy policy,
        Action<bool, string> check, string name, ExplorerLimits? limits = null)
    {
        var observation = await ReferenceWarmStartObserveAsync(connection, async () =>
        {
            var owner = new ReferenceWarmStartOwner(connection, limits);
            var scope = await owner.Explorer.OpenScopeAsync(scopeKey, unloaded);
            var result = await owner.Refresher.RefreshAsync(scope, policy);
            scope = scope with { Context = result.Context! };
            owner.References.AcceptCompletedDiscovery(result);
            return new ReferenceWarmStartRun(owner, scope, result, await owner.References.LoadPaintAsync(scope));
        });
        ReferenceWarmStartNoDocumentWork(observation.Commands, check, name);
        check(observation.Commands.Count(c => ReferenceWarmStartHas(c, "FROM surf.SymbolDefinition s")) == 1,
            "Reference symbols and coverage load in a single bounded metadata stream: " + name);
        check(true, $"Reference warm SQL evidence {name}: {observation.Commands.Count} commands including identity/readiness; " +
            $"{observation.Elapsed.TotalMilliseconds:F1} ms; {observation.Value.Result.ReusedResources} captured roots reused. " +
            "Fixture timings are observations, not a production startup-speed guarantee.");
        return observation;
    }

    private static async Task<ReferenceWarmStartObservation<T>> ReferenceWarmStartObserveAsync<T>(string connection, Func<Task<T>> action)
    {
        using var trace = new StateAccessSqlTrace(connection);
        var elapsed = Stopwatch.StartNew();
        T result = await action();
        elapsed.Stop();
        return new(result, trace.Commands, elapsed.Elapsed);
    }

    private static void ReferenceWarmStartNoDocumentWork(IReadOnlyList<StateAccessCommand> commands,
        Action<bool, string> check, string name)
    {
        check(!commands.Any(c => ReferenceWarmStartHas(c, "SELECT d.DocumentKey,d.Version,LEFT(f.OriginalPath") ||
            ReferenceWarmStartHas(c, "SELECT d.Freshness,r.SourceFingerprint") ||
            ReferenceWarmStartHas(c, "SELECT Version FROM surf.Document WHERE DocumentKey=@Document")),
            "Warm readiness performs no per-document registration/published-state lookup: " + name);
        check(!commands.Any(c => ReferenceWarmStartHas(c, "SELECT TOP(513) m.ScopeResourceKey") ||
            ReferenceWarmStartHas(c, "SELECT TOP(513) l.ScopeResourceKey") ||
            ReferenceWarmStartHas(c, "DELETE l FROM surf.DocumentLocator l") ||
            ReferenceWarmStartHas(c, "INSERT surf.ResourceDocument") || ReferenceWarmStartHas(c, "INSERT surf.DocumentLocator")),
            "Warm readiness performs no membership transactions or pruning: " + name);
        check(!commands.Any(c => ReferenceWarmStartHas(c, "FROM surf.SnapshotResource r CROSS APPLY") ||
            ReferenceWarmStartHas(c, "INSERT surf.DocumentRevision") || ReferenceWarmStartHas(c, "INSERT surf.IndexWorkItem") ||
            ReferenceWarmStartCodeRead(c)),
            "Warm readiness skips captured enumeration, definition bodies and derived publication: " + name);
        check(!commands.Any(c => ReferenceWarmStartHas(c, "SELECT Generation FROM surf.IndexCatalogueHead WITH(UPDLOCK,HOLDLOCK)")),
            "An unchanged captured restart takes no membership/completion writer lock: " + name);
    }

    private static bool ReferenceWarmStartHas(StateAccessCommand command, string text) =>
        command.Text.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool ReferenceWarmStartCodeRead(StateAccessCommand command) =>
        System.Text.RegularExpressions.Regex.IsMatch(command.Text, @"\bsurf\.(DocumentRevision|DatabaseObjectRevision|TableColumnRevision|DiagramObject)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(command.Text, @"\bc\.Text\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static async Task ReferenceWarmStartCachedResolutionAsync(string connection, ReferenceWarmStartRun run,
        string name, Action<bool, string> check)
    {
        var observed = await ReferenceWarmStartObserveAsync(connection, async () =>
        {
            bool matched = true;
            for (int i = 0; i < 4; i++)
            {
                var targets = await run.Owner.References.ResolveAsync(run.Scope.Context,
                    [new ReferenceQuery(name), new ReferenceQuery("dbo." + name), new ReferenceQuery("NoSuchWarmFixtureTarget")]);
                matched &= targets.Length == 3 && targets[0].Candidates.Length == 1 && targets[1].Candidates.Length == 1 &&
                    targets[0].Candidates[0].DocumentKey == targets[1].Candidates[0].DocumentKey && targets[2].Candidates.IsEmpty;
            }
            var paint = await run.Owner.References.LoadPaintAsync(run.Scope);
            return matched && ReferenceEquals(run.Paint.Paint, paint.Paint);
        });
        check(observed.Value, "Repeated simple/qualified/negative reference lookups resolve from the same immutable active-scope catalogue");
        check(!observed.Commands.Any(c => ReferenceWarmStartHas(c, "surf.SymbolDefinition") ||
            ReferenceWarmStartHas(c, "surf.ResourceDocument") || ReferenceWarmStartHas(c, "surf.SnapshotResource") ||
            ReferenceWarmStartHas(c, "surf.TextContent")),
            "Cached navigation/highlights retain only generation checks and never rescan reference documents or candidates");
        check(observed.Commands.Count <= 40,
            "Repeated cached navigation uses bounded context probes rather than document-count-dependent queries");
        check(true, $"Reference cached navigation SQL evidence ({run.Result.Unchanged} documents): " +
            $"{observed.Commands.Count} commands including identity/readiness; {observed.Elapsed.TotalMilliseconds:F1} ms " +
            "for four three-token resolutions and one reused highlight lookup; no document/candidate scan.");
    }

    private static async Task ReferenceWarmStartFailureAsync(SqlFixture fixture, string connection,
        QueryIntegrationFixture seeded, ExplorerIndexLanguagePolicy policy, Action<bool, string> check)
    {
        var owner = new ReferenceWarmStartOwner(connection);
        long document = Convert.ToInt64(await fixture.DestinationSqlAsync(
            "SELECT DocumentKey FROM surf.Document WHERE SnapshotResourceKey=@Resource AND Kind=1;",
            RelationalSession.Parameter("@Resource", SqlDbType.BigInt, seeded.Procedure.ResourceKey)));
        byte[] fingerprint = (byte[])Required(await fixture.DestinationSqlAsync("""
            SELECT r.SourceFingerprint FROM surf.Document d JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
            WHERE d.DocumentKey=@Document;
            """, RelationalSession.Parameter("@Document", SqlDbType.BigInt, document)), "warm failure fingerprint");
        var lease = await owner.Index.BeginWorkAsync(await owner.Index.GetHandleAsync(document), Guid.NewGuid(),
            Convert.ToHexString(fingerprint), new("database-metadata-v1", "definition-v1", "warm-failure-fixture"));
        await owner.Index.FailWorkAsync(lease, "WarmFixtureFailure");
        check(await ReferenceWarmStartSnapshotProofCountAsync(fixture, seeded.ScopeToken.Key, seeded.SnapshotKey) == 0,
            "A failed derived publication transaction invalidates all captured aliases instead of preserving a false ready proof");
        var staleScope = await owner.Explorer.OpenScopeAsync(seeded.ScopeToken.Key);
        var stalePaint = await owner.References.LoadPaintAsync(staleScope);
        check(stalePaint.Coverage.StaleOrUnindexed > 0 && !stalePaint.Coverage.FullyPublished,
            "Freshly loaded reference metadata reports failed documents and cannot authorize a negative result");
        await ThrowsAsync<ReferenceIndexNotReadyException>(() => owner.References.ResolveAsync(staleScope.Context,
            [new ReferenceQuery("NeedleProc")]), check, "Failed reference coverage rejects strict cached navigation");

        var limited = new ReferenceWarmStartOwner(connection, new ExplorerLimits(PageSize: 1, MaximumDocumentCharacters: 32));
        var failed = await limited.Refresher.RefreshAsync(await limited.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(failed.Completed && !failed.FullyPublished && failed.Failed == 1 &&
            await ReferenceWarmStartSnapshotProofCountAsync(fixture, seeded.ScopeToken.Key, seeded.SnapshotKey) == 0,
            "An unsuccessful retry never writes a captured completion proof for a partially failed owner");
        bool rejected = false;
        try { limited.References.AcceptCompletedDiscovery(failed); } catch (InvalidOperationException) { rejected = true; }
        check(rejected, "An incomplete retry cannot certify active-scope reference readiness");
        var repaired = new ReferenceWarmStartOwner(connection);
        var repair = await repaired.Refresher.RefreshAsync(await repaired.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(repair.FullyPublished && repair.Published == 1 && repair.ReusedResources == 1,
            "A successful bounded retry repairs the failed definition while reusing the unrelated diagram proof");
    }

    private static async Task ReferenceWarmStartRebaseAsync(SqlFixture fixture, string connection, ReferenceWarmStartRun run,
        ExplorerIndexLanguagePolicy policy, Action<bool, string> check)
    {
        await fixture.DestinationSqlAsync("""
            UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;
            UPDATE surf.ScopeIndexState SET ReconciledScopeVersion=NULL,ReconciledAtUtc=NULL WHERE ScopeKey=@Scope;
            """, RelationalSession.Parameter("@Scope", SqlDbType.BigInt, run.Scope.Context.ScopeKey));
        var observed = await ReferenceWarmStartObserveAsync(connection, async () =>
        {
            var proof = await run.Owner.Refresher.VerifyCompletedViewAsync(run.Scope, policy, CancellationToken.None);
            var verified = run.Scope with { Context = proof.Context! };
            var paint = await run.Owner.References.RebaseVerifiedViewAsync(verified, proof, CancellationToken.None);
            var targets = await run.Owner.References.ResolveAsync(verified.Context,
                [new ReferenceQuery("NeedleProc"), new ReferenceQuery("NoSuchWarmFixtureTarget")]);
            return (Proof: proof, Scope: verified, Paint: paint, Targets: targets);
        });
        check(observed.Value.Proof.FullyPublished && observed.Value.Proof.ReusedResources == 2 &&
            observed.Value.Scope.Context.CatalogueGeneration != run.Scope.Context.CatalogueGeneration &&
            !observed.Value.Scope.Context.DiscoveryReconciled && observed.Value.Paint.Coverage.FullyPublished,
            "Root proofs rebase a verified captured-only view across an unrelated generation/discovery-marker change");
        check(ReferenceEquals(observed.Value.Paint.Paint.Names, run.Paint.Paint.Names) &&
            observed.Value.Targets[0].Candidates.Length == 1 && observed.Value.Targets[1].Candidates.IsEmpty,
            "Rebased captured navigation retains immutable names and complete positive/negative target results");
        check(!observed.Commands.Any(c => ReferenceWarmStartHas(c, "surf.SymbolDefinition") ||
            ReferenceWarmStartHas(c, "FROM surf.ResourceDocument") || ReferenceWarmStartHas(c, "surf.TextContent")),
            "Verified rebasing reads root checkpoints and context fences without reloading candidates or scanning document coverage");
        check(true, $"Reference verified-rebase SQL evidence: {observed.Commands.Count} commands including identity/readiness; " +
            $"{observed.Elapsed.TotalMilliseconds:F1} ms; captured symbols reused across unrelated global writes.");

        long root = run.Scope.Resources.Single(r => r.IsLoaded && r.Kind == ResourceKind.DatabaseSnapshot).ScopeResourceKey;
        await fixture.DestinationSqlAsync("DELETE surf.ReferenceResourceCompletion WHERE ScopeResourceKey=@Root;",
            RelationalSession.Parameter("@Root", SqlDbType.BigInt, root));
        await ThrowsAsync<IndexGenerationChangedException>(async () =>
        {
            var proof = await run.Owner.Refresher.VerifyCompletedViewAsync(observed.Value.Scope, policy, CancellationToken.None);
            await run.Owner.References.RebaseVerifiedViewAsync(observed.Value.Scope with { Context = proof.Context! }, proof,
                CancellationToken.None);
        }, check, "Captured rebasing rejects an invalidated root proof even if the global generation did not change again");
        var restored = await run.Owner.Refresher.RefreshAsync(observed.Value.Scope, policy);
        check(restored.FullyPublished, "Normal reconciliation restores the fixture root proof after the rejection check");
    }

    private static async Task ReferenceWarmStartConcurrentFenceAsync(SqlFixture fixture, string connection, long scopeKey,
        ExplorerIndexLanguagePolicy policy, Action<bool, string> check)
    {
        var owner = new ReferenceWarmStartOwner(connection);
        var scope = await owner.Explorer.OpenScopeAsync(scopeKey);
        bool mutated = false;
        await ThrowsAsync<IndexGenerationChangedException>(() => owner.Refresher.RefreshAsync(scope, policy, async p =>
        {
            if (mutated || p.Completed) return;
            mutated = true;
            await fixture.DestinationSqlAsync("UPDATE surf.Scope SET PublicationId=@Publication WHERE ScopeKey=@Scope;",
                RelationalSession.Parameter("@Publication", SqlDbType.UniqueIdentifier, Guid.NewGuid()),
                RelationalSession.Parameter("@Scope", SqlDbType.BigInt, scopeKey));
        }), check, "A scope change after durable-proof lookup is fenced before warm readiness is published");
        check(mutated, "The warm-path race actually mutated the generated fixture scope during the refresh");
        var renewed = await ReferenceWarmStartRestartAsync(connection, scopeKey, null, policy, check, "scope-version-only restart");
        check(renewed.Value.Result.Considered == 0 && renewed.Value.Result.ReusedResources == 3 &&
            renewed.Value.Scope.Context.ScopeVersion != scope.Context.ScopeVersion,
            "A new scope context may reuse unchanged typed-root proofs but never retains the previous scope-version fence");
    }

    private static async Task ReferenceWarmStartPhysicalRootsAsync(SqlFixture fixture, string connection,
        ExplorerIndexLanguagePolicy policy, Action<bool, string> check)
    {
        string first = Path.Combine(fixture.OwnedDirectory, "reference-warm-physical", "first");
        string second = Path.Combine(fixture.OwnedDirectory, "reference-warm-physical", "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        string firstFile = Path.Combine(first, "first.cs"), secondFile = Path.Combine(second, "second.cs");
        await File.WriteAllTextAsync(firstFile, "class WarmPhysicalFirst { }");
        await File.WriteAllTextAsync(secondFile, "class WarmPhysicalSecond { }");
        var physical = new ObservedScopeIndexPhysical();
        var owner = new ReferenceWarmStartOwner(connection, physical: physical);
        var token = await owner.State.CreateScopeAsync(new Scope
        {
            ScopeId = "warm-physical-scope", Name = "Warm physical scope", Resources =
            [
                new() { ResourceId = "physical-a", Kind = ResourceKind.Folder, Path = first },
                new() { ResourceId = "physical-b", Kind = ResourceKind.Folder, Path = second }
            ]
        }, 2, Guid.NewGuid());
        var scope = await owner.Explorer.OpenScopeAsync(token.Key);
        long firstKey = scope.Resources.Single(r => r.ResourceId == "physical-a").ScopeResourceKey;
        using (var watch = new PhysicalReferenceWatch(scope, (PhysicalReferenceInvalidation _) => { }))
        {
            check(watch.UnwatchedRoots.IsEmpty && watch.InitialInvalidation.ScopeResourceKeys.Length == 2,
                "Both independent fixture folders are continuously watched before their initial reconciliation");
            var initial = await owner.Refresher.RefreshAsync(scope, policy);
            check(initial.FullyPublished && initial.Considered == 2 && initial.Published == 2 && initial.ReusedResources == 0,
                "The first physical refresh reconciles both folders rather than trusting their persisted file metadata");
            physical.Directories.Clear();
            await File.WriteAllTextAsync(firstFile, "class WarmPhysicalFirstChanged { }");
            var current = await owner.Explorer.OpenScopeAsync(token.Key);
            var targeted = await ReferenceWarmStartObserveAsync(connection, () => owner.Refresher.RefreshAsync(current, policy,
                physicalRootsToReconcile: new HashSet<long> { firstKey }));
            check(targeted.Value.FullyPublished && targeted.Value.Considered == 1 && targeted.Value.Published == 1 &&
                targeted.Value.Unchanged == 1 && targeted.Value.ReusedResources == 1,
                "A watched dirty-root refresh republishes the changed file and reuses the disjoint verified folder");
            check(physical.Directories.Count == 1 && string.Equals(physical.Directories[0], first, StringComparison.OrdinalIgnoreCase),
                "Reconciling the first dirty folder does not enumerate the independent second folder");
            check(targeted.Commands.Count(c => ReferenceWarmStartHas(c, "SELECT d.DocumentKey,d.Version,LEFT(f.OriginalPath")) == 1 &&
                targeted.Commands.Count(c => ReferenceWarmStartHas(c, "SELECT TOP(513) m.ScopeResourceKey")) == 1,
                "Only the dirty file receives a registration lookup and membership transaction");
            current = current with { Context = targeted.Value.Context! };
            owner.References.AcceptCompletedDiscovery(targeted.Value);
            var paint = await owner.References.LoadPaintAsync(current);
            check(paint.Coverage.FullyPublished && paint.Paint.Names.ContainsKey("WarmPhysicalFirstChanged") &&
                !paint.Paint.Names.ContainsKey("WarmPhysicalFirst") && paint.Paint.Names.ContainsKey("WarmPhysicalSecond"),
                "Targeted file reconciliation replaces changed symbols without losing the unrelated folder's highlights");
            check(true, $"Reference targeted physical SQL evidence: {targeted.Commands.Count} commands; " +
                $"{targeted.Elapsed.TotalMilliseconds:F1} ms; one dirty folder reconciled, one verified folder reused.");
        }

        // With the watch and session retired, the second file changes outside the continuously watched interval.
        await File.WriteAllTextAsync(secondFile, "class WarmPhysicalSecondOffline { }");
        var restartedPhysical = new ObservedScopeIndexPhysical();
        var restarted = new ReferenceWarmStartOwner(connection, physical: restartedPhysical);
        var reopened = await restarted.Explorer.OpenScopeAsync(token.Key);
        var offline = await restarted.Refresher.RefreshAsync(reopened, policy);
        check(restarted.Session.Epoch != owner.Session.Epoch && offline.FullyPublished && offline.Considered == 2 &&
            offline.Published == 1 && offline.Unchanged == 1 && offline.ReusedResources == 0 &&
            restartedPhysical.Directories.Contains(first, StringComparer.OrdinalIgnoreCase) &&
            restartedPhysical.Directories.Contains(second, StringComparer.OrdinalIgnoreCase),
            "A default new-session refresh fully reconciles physical folders and detects an edit made while the app was closed");
        reopened = reopened with { Context = offline.Context! };
        restarted.References.AcceptCompletedDiscovery(offline);
        var freshPaint = await restarted.References.LoadPaintAsync(reopened);
        check(freshPaint.Coverage.FullyPublished && freshPaint.Paint.Names.ContainsKey("WarmPhysicalSecondOffline") &&
            !freshPaint.Paint.Names.ContainsKey("WarmPhysicalSecond") && freshPaint.Paint.Names.ContainsKey("WarmPhysicalFirstChanged"),
            "Restarted highlights use the offline edit rather than certifying old persisted physical symbols as current");
    }

    private static async Task ReferenceWarmStartManyRootsAsync(SqlFixture fixture, string connection,
        ExplorerIndexLanguagePolicy policy, Action<bool, string> check)
    {
        var owner = new ReferenceWarmStartOwner(connection);
        var first = await owner.State.CreateDiagramAsync(new(new DiagramDocument
        { DiagramId = "warm-many-first", Name = "Warm many first", CreatedAtUtc = EvidenceTime, UpdatedAtUtc = EvidenceTime },
            new Dictionary<int, byte[]>()), 1, Guid.NewGuid());
        var second = await owner.State.CreateDiagramAsync(new(new DiagramDocument
        { DiagramId = "warm-many-second", Name = "Warm many second", CreatedAtUtc = EvidenceTime, UpdatedAtUtc = EvidenceTime },
            new Dictionary<int, byte[]>()), 2, Guid.NewGuid());
        var value = new Scope { ScopeId = "warm-many-roots", Name = "Warm many roots" };
        var targets = new List<ScopeResourceTarget>();
        // Split aliases across two owners so each document remains within the 512-membership contract.
        for (int i = 0; i < 513; i++)
        {
            bool useFirst = i < 257;
            value.Resources.Add(new()
            {
                ResourceId = "many-diagram-" + i, Kind = ResourceKind.Diagram,
                Path = useFirst ? "warm-many-first" : "warm-many-second", DisplayNameOverride = "Diagram alias " + i
            });
            targets.Add(new(i, null, useFirst ? first.Key : second.Key));
        }
        string path = Path.Combine(fixture.OwnedDirectory, "reference-many-roots", "source.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "class WarmManyRootFile { }");
        value.Resources.Add(new() { ResourceId = "many-file", Kind = ResourceKind.File, Path = path });
        var token = await owner.State.CreateScopeAsync(value, 3, Guid.NewGuid());
        await InTransactionAsync(owner.Session, async (c, t) =>
            token = await owner.State.ResolveScopeResourceTargetsAsync(c, t, token, targets, Guid.NewGuid()));
        var scope = await owner.Explorer.OpenScopeAsync(token.Key);
        using var watch = new PhysicalReferenceWatch(scope, (PhysicalReferenceInvalidation _) => { });
        check(watch.UnwatchedRoots.IsEmpty && scope.Resources.Length == 514 && scope.Context.UnloadedScopeResourceKeys.IsEmpty,
            "The boundary fixture has 513 loaded diagram roots plus one watched file, with no unloaded-key batch restriction");
        var initial = await owner.Refresher.RefreshAsync(scope, policy);
        check(initial.FullyPublished && initial.Published == 3 &&
            await ReferenceWarmStartProofCountAsync(fixture, token.Key) == 514,
            "Normal discovery records proofs for more than 512 roots while publishing only their three unique sources");
        var current = await owner.Explorer.OpenScopeAsync(token.Key);
        var warm = await ReferenceWarmStartObserveAsync(connection, () => owner.Refresher.RefreshAsync(current, policy,
            physicalRootsToReconcile: new HashSet<long>()));
        check(warm.Value.FullyPublished && warm.Value.ReusedResources == 514 && warm.Value.Considered == 0 &&
            warm.Value.Published == 0 && warm.Value.Unchanged == 3,
            "A continuously watched refresh reuses all 514 loaded roots without crossing the TVP limit or double-counting aliases");
        check(warm.Commands.Count(c => ReferenceWarmStartHas(c, "FROM surf.ReferenceResourceCompletion c")) == 4 &&
            warm.Commands.Count(c => ReferenceWarmStartHas(c, "INSERT #ReusedRoots(Id) SELECT Id FROM @Roots")) == 2,
            "Initial/final proof verification and reused-document counting split 514 root keys into two bounded batches");
        ReferenceWarmStartNoDocumentWork(warm.Commands, check, "514-root watched refresh");
        var restarted = new ReferenceWarmStartOwner(connection);
        var reopened = await restarted.Explorer.OpenScopeAsync(token.Key);
        var restart = await restarted.Refresher.RefreshAsync(reopened, policy);
        check(restart.FullyPublished && restart.ReusedResources == 513 && restart.Considered == 1 && restart.Published == 0 &&
            restart.Unchanged == 3,
            "A fresh process batches all 513 captured-root proofs but still reconciles the physical file after the watch gap");
        check(true, $"Reference many-root SQL evidence: {warm.Commands.Count} commands including identity/readiness; " +
            $"{warm.Elapsed.TotalMilliseconds:F1} ms; 514 loaded roots, three unique documents, two root batches.");
    }

    private static Task<long> ReferenceWarmStartProofCountAsync(SqlFixture fixture, long scopeKey) =>
        ReferenceWarmStartCountAsync(fixture, """
            SELECT COUNT_BIG(*) FROM surf.ReferenceResourceCompletion c JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=c.ScopeResourceKey
            WHERE sr.ScopeKey=@Scope;
            """, RelationalSession.Parameter("@Scope", SqlDbType.BigInt, scopeKey));

    private static Task<long> ReferenceWarmStartSnapshotProofCountAsync(SqlFixture fixture, long scopeKey, long snapshotKey) =>
        ReferenceWarmStartCountAsync(fixture, """
            SELECT COUNT_BIG(*) FROM surf.ReferenceResourceCompletion c JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=c.ScopeResourceKey
            WHERE sr.ScopeKey=@Scope AND sr.SnapshotKey=@Snapshot;
            """, RelationalSession.Parameter("@Scope", SqlDbType.BigInt, scopeKey),
            RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshotKey));

    private static Task<long> ReferenceWarmStartCurrentSourceAsync(SqlFixture fixture, long resourceKey) =>
        ReferenceWarmStartCountAsync(fixture, """
            SELECT r.SourceRevisionKey FROM surf.Document d JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
            WHERE d.SnapshotResourceKey=@Resource AND d.Kind=1;
            """, RelationalSession.Parameter("@Resource", SqlDbType.BigInt, resourceKey));

    private static async Task<long> ReferenceWarmStartCountAsync(SqlFixture fixture, string sql, params SqlParameter[] parameters) =>
        Convert.ToInt64(await fixture.DestinationSqlAsync(sql, parameters));

    private static async Task ReferenceWarmStartScaleAsync(SqlFixture fixture, string connection,
        QueryIntegrationFixture seeded, ExplorerIndexLanguagePolicy policy, int smallCommandCount, Action<bool, string> check)
    {
        const int count = 11673;
        await ReferenceWarmStartSeedScaleAsync(fixture, seeded, count);
        var scaleLimits = new ExplorerLimits(PageSize: 1, MaximumMetadataCharacters: 16 * 1024 * 1024,
            MaximumDocumentCharacters: 8192);
        var owner = new ReferenceWarmStartOwner(connection, scaleLimits with { PageSize = 128 });
        var initial = await owner.Refresher.RefreshAsync(await owner.Explorer.OpenScopeAsync(seeded.ScopeToken.Key), policy);
        check(initial.FullyPublished && initial.Published == 0 && initial.Unchanged == count + 3,
            "A disposable set-based scale seed contains 11,673 already-published definitions and establishes normal completion proofs");
        var large = await ReferenceWarmStartRestartAsync(connection, seeded.ScopeToken.Key, null, policy, check,
            "11,676-document restart", scaleLimits);
        check(large.Value.Result.Considered == 0 && large.Value.Result.ReusedResources == 3 && large.Value.Result.Unchanged == count + 3,
            "Restart readiness skips every definition in the 11,676-document fixture even with a one-row explorer page size");
        check(large.Commands.Count <= smallCommandCount + 4 && large.Commands.Count < 128,
            "Warm readiness plus metadata load has bounded command count as captured documents grow from three to 11,676");
        check(large.Value.Paint.Paint.Names.ContainsKey("Scale11673"),
            "Bulk warm highlight loading includes the final scale definition rather than truncating the catalogue");
        await ReferenceWarmStartCachedResolutionAsync(connection, large.Value, "Scale11673", check);
    }

    private static async Task ReferenceWarmStartSeedScaleAsync(SqlFixture fixture, QueryIntegrationFixture seeded, int count)
    {
        if (count is < 1 or > 11676) throw new ArgumentOutOfRangeException(nameof(count));
        // The owned LocalDB fixture clones small valid published rows in sets; it never reads or modifies an application database.
        await fixture.DestinationSqlAsync("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @SourceDocument bigint=(SELECT DocumentKey FROM surf.Document WHERE Kind=1 AND SnapshotResourceKey=@Source);
            DECLARE @Resources TABLE(N int PRIMARY KEY,ResourceKey bigint NOT NULL,Name nvarchar(128) NOT NULL);
            DECLARE @SourceRevisions TABLE(ResourceKey bigint PRIMARY KEY,RevisionKey bigint NOT NULL);
            DECLARE @Documents TABLE(ResourceKey bigint PRIMARY KEY,DocumentKey bigint NOT NULL);
            DECLARE @Revisions TABLE(DocumentKey bigint PRIMARY KEY,RevisionKey bigint NOT NULL);
            WITH digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)),
            numbers AS (SELECT a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+1 n FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d CROSS JOIN digits e)
            MERGE surf.SnapshotResource AS target
            USING (SELECT n,N'Scale'+CONVERT(nvarchar(16),n) Name,s.SnapshotKey,s.Kind FROM numbers CROSS JOIN surf.SnapshotResource s WHERE s.ResourceKey=@Source AND n<=@Count) AS source
            ON 1=0
            WHEN NOT MATCHED THEN INSERT(SnapshotKey,Kind,SchemaName,ObjectName,NameHash,OriginalResourceKey,ResourceKeyHash,CurrentSortOrdinal)
              VALUES(source.SnapshotKey,source.Kind,N'dbo',source.Name,HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'3:dbo'+CONVERT(nvarchar(16),LEN(source.Name))+N':'+source.Name)),
                N'warm-scale:'+source.Name,HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'warm-scale:'+source.Name)),source.n+10)
            OUTPUT source.n,inserted.ResourceKey,source.Name INTO @Resources;
            MERGE surf.SnapshotResourceRevision AS target
            USING (SELECT r.ResourceKey,s.SnapshotKey FROM @Resources r JOIN surf.SnapshotResource s ON s.ResourceKey=r.ResourceKey) AS source
            ON 1=0 WHEN NOT MATCHED THEN INSERT(ResourceKey,SnapshotKey,IsSealed) VALUES(source.ResourceKey,source.SnapshotKey,1)
            OUTPUT inserted.ResourceKey,inserted.RevisionKey INTO @SourceRevisions;
            INSERT surf.DatabaseObjectRevision(RevisionKey,SchemaName,ObjectName,ObjectKind,TypeDescription,ParentSchemaName,ParentObjectName,DefinitionContentKey)
            SELECT x.RevisionKey,o.SchemaName,r.Name,o.ObjectKind,o.TypeDescription,o.ParentSchemaName,o.ParentObjectName,o.DefinitionContentKey
            FROM @Resources r JOIN @SourceRevisions x ON x.ResourceKey=r.ResourceKey
            CROSS JOIN surf.SnapshotResource s JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=s.CurrentRevisionKey WHERE s.ResourceKey=@Source;
            UPDATE s SET CurrentRevisionKey=x.RevisionKey FROM surf.SnapshotResource s JOIN @SourceRevisions x ON x.ResourceKey=s.ResourceKey;
            INSERT surf.Document(Kind,SnapshotResourceKey,SnapshotKey,DisplayName,Language)
            OUTPUT inserted.SnapshotResourceKey,inserted.DocumentKey INTO @Documents
            SELECT d.Kind,r.ResourceKey,s.SnapshotKey,N'dbo.'+r.Name,d.Language FROM @Resources r
            JOIN surf.SnapshotResource s ON s.ResourceKey=r.ResourceKey CROSS JOIN surf.Document d WHERE d.DocumentKey=@SourceDocument;
            INSERT surf.DocumentRevision(DocumentKey,PublicationId,ContentKey,SnapshotResourceKey,SourceRevisionKey,SourceFingerprint,ParserVersion,RendererVersion,PolicyVersion,Language)
            OUTPUT inserted.DocumentKey,inserted.DocumentRevisionKey INTO @Revisions
            SELECT d.DocumentKey,NEWID(),p.ContentKey,d.ResourceKey,x.RevisionKey,p.SourceFingerprint,p.ParserVersion,p.RendererVersion,p.PolicyVersion,p.Language
            FROM @Documents d JOIN @SourceRevisions x ON x.ResourceKey=d.ResourceKey
            CROSS JOIN surf.Document template JOIN surf.DocumentRevision p ON p.DocumentRevisionKey=template.CurrentRevisionKey WHERE template.DocumentKey=@SourceDocument;
            UPDATE d SET CurrentRevisionKey=r.RevisionKey,Freshness=1 FROM surf.Document d JOIN @Revisions r ON r.DocumentKey=d.DocumentKey;
            INSERT surf.SymbolDefinition(DocumentKey,DocumentRevisionKey,SortOrdinal,Name,QualifiedName,Kind,Locator,LineNumber,ColumnNumber,EndLineNumber,EndColumnNumber,
              ParameterCount,MinimumArgumentCount,MaximumArgumentCount,Language,ContainerName)
            SELECT d.DocumentKey,x.RevisionKey,s.SortOrdinal,REPLACE(s.Name,N'NeedleProc',r.Name),REPLACE(s.QualifiedName,N'NeedleProc',r.Name),s.Kind,
              REPLACE(s.Locator,N'NeedleProc',r.Name),s.LineNumber,s.ColumnNumber,s.EndLineNumber,s.EndColumnNumber,
              s.ParameterCount,s.MinimumArgumentCount,s.MaximumArgumentCount,s.Language,s.ContainerName
            FROM @Documents d JOIN @Resources r ON r.ResourceKey=d.ResourceKey JOIN @Revisions x ON x.DocumentKey=d.DocumentKey
            CROSS JOIN surf.SymbolDefinition s JOIN surf.Document template ON template.DocumentKey=s.DocumentKey AND template.CurrentRevisionKey=s.DocumentRevisionKey
            WHERE template.DocumentKey=@SourceDocument;
            INSERT surf.SymbolNameLookup(SymbolKey,NameOrdinal,NormalizedName,NameHash,IsAscii)
            SELECT s.SymbolKey,n.Ordinal,UPPER(n.Name),HASHBYTES('SHA2_256',CONVERT(varbinary(max),UPPER(n.Name))),1
            FROM surf.SymbolDefinition s JOIN @Documents d ON d.DocumentKey=s.DocumentKey
            CROSS APPLY (VALUES(CONVERT(tinyint,0),s.Name),(CONVERT(tinyint,1),s.QualifiedName)) n(Ordinal,Name);
            INSERT surf.SearchProjection(DocumentKey,DocumentRevisionKey,ContentKey,ProjectionKind)
            SELECT d.DocumentKey,r.RevisionKey,p.ContentKey,p.ProjectionKind FROM @Documents d JOIN @Revisions r ON r.DocumentKey=d.DocumentKey
            CROSS JOIN surf.SearchProjection p JOIN surf.Document template ON template.DocumentKey=p.DocumentKey AND template.CurrentRevisionKey=p.DocumentRevisionKey
            WHERE template.DocumentKey=@SourceDocument;
            INSERT surf.ResourceDocument(ScopeResourceKey,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal)
            SELECT m.ScopeResourceKey,d.DocumentKey,REPLACE(m.DisplayName,N'NeedleProc',r.Name),REPLACE(m.Locator,N'NeedleProc',r.Name),
              REPLACE(m.NodeKey,N'NeedleProc',r.Name),m.ParentNodeKey,r.N+10
            FROM @Documents d JOIN @Resources r ON r.ResourceKey=d.ResourceKey CROSS JOIN surf.ResourceDocument m
            JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey WHERE m.DocumentKey=@SourceDocument AND sr.ScopeKey=@Scope;
            INSERT surf.DocumentLocator(DocumentKey,ScopeResourceKey,Kind,OriginalLocator,LocatorHash,IsAscii)
            SELECT d.DocumentKey,l.ScopeResourceKey,l.Kind,REPLACE(l.OriginalLocator,N'NeedleProc',r.Name),
              HASHBYTES('SHA2_256',CONVERT(varbinary(max),UPPER(REPLACE(l.OriginalLocator,N'NeedleProc',r.Name)))),1
            FROM @Documents d JOIN @Resources r ON r.ResourceKey=d.ResourceKey CROSS JOIN surf.DocumentLocator l
            JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=l.ScopeResourceKey WHERE l.DocumentKey=@SourceDocument AND sr.ScopeKey=@Scope;
            UPDATE surf.DatabaseSnapshot SET IsPublished=IsPublished WHERE SnapshotKey=@Snapshot;
            UPDATE surf.SnapshotCatalogueHead SET UserKey=UserKey WHERE UserKey=1;
            UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;
            COMMIT TRANSACTION;
            """, RelationalSession.Parameter("@Source", SqlDbType.BigInt, seeded.Procedure.ResourceKey),
            RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, seeded.SnapshotKey),
            RelationalSession.Parameter("@Scope", SqlDbType.BigInt, seeded.ScopeToken.Key),
            RelationalSession.Parameter("@Count", SqlDbType.Int, count));
    }
}

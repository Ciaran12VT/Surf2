using System.Collections.Immutable;
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
    public static async Task RunReferenceMetadataHotPathChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_ReferenceHotPath_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var state = new RelationalStateStore(session, content);
        var seeded = await SeedQueryIntegrationAsync(fixture, session, content, new RelationalCaptureStore(session), state);
        var index = new RelationalIndexStore(session, content);
        var limits = new ExplorerLimits(PageSize: 1);
        var explorer = new RelationalExplorerService(new RelationalExplorerMetadataAdapter(session, index, limits), limits: limits);
        var scope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        var roots = await explorer.GetRootsAsync(scope);
        var procedures = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
        var tables = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
        foreach (var root in roots.Where(r => r.ResourceKind == ResourceKind.DatabaseSnapshot))
        {
            var categories = await QueryIntegrationChildrenAsync(explorer, scope, root, check);
            procedures.AddRange(await QueryIntegrationChildrenAsync(explorer, scope, categories.Single(c => c.Category == ExplorerCategory.Procedures), check));
            tables.AddRange(await QueryIntegrationChildrenAsync(explorer, scope, categories.Single(c => c.Category == ExplorerCategory.Tables), check));
        }
        var diagram = (await QueryIntegrationChildrenAsync(explorer, scope, roots.Single(r => r.Role == ExplorerNodeRole.DiagramRoot), check)).Single();
        var source = await index.PrepareDefinitionAsync(seeded.Procedure.RevisionKey, procedures[0].FullPath, "fixture");
        var symbols = source.Symbols.ToBuilder();
        for (int i = 0; i < 257; i++)
            symbols.Add(ReferenceHotPathSymbol("FixtureSymbol" + i, "fixture.FixtureSymbol" + i, source.Language, procedures[0].FullPath));
        symbols.Add(ReferenceHotPathSymbol("Overload", "fixture.Overload", source.Language, procedures[0].FullPath) with
        { ParameterCount = 1, MinimumArgumentCount = 1, MaximumArgumentCount = int.MaxValue });
        symbols.Add(ReferenceHotPathSymbol("Overload", "fixture.Overload", source.Language, procedures[0].FullPath) with
        { ParameterCount = 4, MinimumArgumentCount = 4, MaximumArgumentCount = 4, ColumnNumber = 2, EndColumnNumber = 2 });
        symbols.Add(ReferenceHotPathSymbol("Methode\u00e9", "fixture.Methode\u00e9", source.Language, procedures[0].FullPath));
        symbols.Add(ReferenceHotPathSymbol("NeedleProc", "file.NeedleProc", source.Language, procedures[0].FullPath) with { Kind = ReferenceEntityKind.File });
        var policy = new IndexPolicy("catalogue-v1", "raw-v1", "reference-hot-path-v1");
        long document = await QueryIntegrationPublishAsync(index,
            new SnapshotDocumentOwner(seeded.SnapshotKey, seeded.Procedure.ResourceKey, false), procedures.ToImmutable(),
            source with { Symbols = symbols.ToImmutable() }, policy);
        await QueryIntegrationPublishAsync(index, new SnapshotDocumentOwner(seeded.SnapshotKey, seeded.Table.ResourceKey, true),
            tables.ToImmutable(), await index.PrepareTableCodeAsync(seeded.Table.RevisionKey, tables[0].FullPath, "fixture"),
            policy with { RendererVersion = RelationalIndexStore.TableRendererVersion });
        await QueryIntegrationPublishAsync(index, new DiagramDocumentOwner(diagram.DiagramRevisionKey!.Value), [diagram],
            await index.PrepareDiagramAsync(diagram.DiagramRevisionKey.Value), policy with { RendererVersion = RelationalIndexStore.DiagramRendererVersion });
        await index.MarkDiscoveryReconciledAsync(await index.CaptureContextAsync(seeded.ScopeToken.Key));
        scope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        var references = new RelationalReferenceService(session, index, limits);

        var paint = await QueryIntegrationObservedAsync(connection, () => references.LoadPaintAsync(scope), commands =>
        {
            StateAccessNoReads(commands, check, "Reference hot-path stream", "TextContent", "Asset", "DatabaseObjectRevision", "TableColumnRevision", "DiagramObject");
            check(commands.Count(c => c.Text.Contains("FROM surf.SymbolDefinition", StringComparison.OrdinalIgnoreCase)) == 1 &&
                commands.Count(c => c.Text.Contains("COUNT_BIG(*)", StringComparison.OrdinalIgnoreCase) &&
                    c.Text.Contains("FROM surf.ResourceDocument", StringComparison.OrdinalIgnoreCase)) == 1 &&
                !commands.Any(c => c.Text.Contains("LEFT(m.DisplayName", StringComparison.OrdinalIgnoreCase)),
                "Hundreds of symbols stream in one fenced metadata command with one coverage aggregate, even at PageSize=1");
        }, check, "one bounded reference metadata stream");
        check(paint.Coverage.FullyPublished && paint.Coverage.Documents == 5 && paint.Paint.Names.ContainsKey("FixtureSymbol256"),
            "Bulk reference coverage preserves five alias memberships and every symbol beyond the previous page boundary");
        ReferenceQuery[] queries = [new("[dbo].[NeedleProc]"), new("wrong.namespace.FixtureSymbol256"), new("Overload", 4), new("Methode\u00e9"), new("not-a-symbol")];
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var again = await references.LoadPaintAsync(scope);
            var targets = await references.ResolveAsync(scope.Context, queries);
            var verified = await references.ResolveVerifiedWithCoverageAsync(scope, queries);
            check(ReferenceEquals(again.Paint, paint.Paint) && targets[0].Candidates.Length == 1 &&
                targets[0].Candidates[0].Definition.Kind == ReferenceEntityKind.StoredProcedure &&
                targets[1].Candidates.Single().Definition.Name == "FixtureSymbol256" &&
                targets[2].Candidates.Single().Definition.ParameterCount == 4 && targets[3].Candidates.Length == 1 && targets[4].Candidates.IsEmpty,
                "Cached names preserve quoted qualified names, aliases, suffix fallback, overload ranking, Unicode and authoritative misses");
            check(verified.Coverage.FullyPublished && verified.Targets.Zip(targets).All(t =>
                t.First.Query == t.Second.Query && t.First.Candidates.SequenceEqual(t.Second.Candidates)),
                "Verified navigation preserves complete-index lookup and overload behavior");
            check(!trace.Commands.Any(c => c.Text.Contains("surf.SymbolDefinition", StringComparison.OrdinalIgnoreCase) ||
                c.Text.Contains("surf.ResourceDocument", StringComparison.OrdinalIgnoreCase) ||
                c.Text.Contains("FROM surf.ScopeResource", StringComparison.OrdinalIgnoreCase)) && trace.Commands.Count < 20,
                "Warm paint and context-only reference navigation perform constant generation guards, not symbol queries or scope-document scans");
        }

        var copiedContext = scope.Context with
        {
            UnloadedScopeResourceKeys = scope.Context.UnloadedScopeResourceKeys.ToArray().ToImmutableArray(),
            DocumentKeys = scope.Context.DocumentKeys.ToArray().ToImmutableArray()
        };
        check(RelationalReferenceService.ContextMatches(scope.Context, copiedContext) &&
            !RelationalReferenceService.ContextMatches(scope.Context, copiedContext with { Epoch = Guid.NewGuid() }) &&
            !RelationalReferenceService.ContextMatches(scope.Context, copiedContext with { RestrictDocumentKeys = true }) &&
            !RelationalReferenceService.ContextMatches(scope.Context, copiedContext with { UnloadedScopeResourceKeys = [scope.Resources[0].ScopeResourceKey] }),
            "Cache context identity compares array contents and rejects epoch, restriction and loaded-selection differences");

        var excluded = await explorer.OpenScopeAsync(seeded.ScopeToken.Key,
            new HashSet<string>(scope.Resources.Where(r => r.Kind == ResourceKind.DatabaseSnapshot).Select(r => r.ResourceId)), "en-US");
        var excludedResult = await references.ResolveWithCoverageAsync(excluded, [new("FixtureSymbol256")]);
        var excludedVerified = await references.ResolveVerifiedWithCoverageAsync(excluded, [new("Evidence")]);
        check(excludedResult.Targets.Single().Candidates.IsEmpty && excludedResult.Coverage.Documents == 1 && !excludedResult.Coverage.FullyPublished,
            "A changed unloaded selection replaces the cache and cannot leak excluded database symbols or certify a provisional view");
        check(excludedVerified.Targets.Single().Candidates.IsEmpty && !excludedVerified.Coverage.FullyPublished,
            "Verified navigation never includes targets from excluded captured databases");
        await ThrowsAsync<ReferenceIndexNotReadyException>(() => references.ResolveAsync(excluded.Context, [new("FixtureSymbol256")]), check,
            "Context-only cached provisional navigation cannot turn an excluded-view miss into a complete result");
        var databaseOnly = await explorer.OpenScopeAsync(seeded.ScopeToken.Key,
            new HashSet<string>(scope.Resources.Where(r => r.Kind == ResourceKind.Diagram).Select(r => r.ResourceId)), "en-US");
        var undiscoveredVerified = await references.ResolveVerifiedWithCoverageAsync(databaseOnly, [new("Evidence")]);
        check(undiscoveredVerified.Targets.Single().Candidates.Single().Freshness == IndexFreshness.Indexed &&
            undiscoveredVerified.Coverage.StaleOrUnindexed == 0 && !undiscoveredVerified.Coverage.DiscoveryReconciled &&
            !undiscoveredVerified.Coverage.FullyPublished,
            "Verified table navigation also tolerates an unconfirmed discovery view without falsely certifying it");
        var restricted = scope with { Context = await index.CaptureContextAsync(scope.Context.ScopeKey, documentKeys: [document]) };
        var restrictedResult = await references.ResolveWithCoverageAsync(restricted, [new("FixtureSymbol256")]);
        check(restrictedResult.Targets.Single().Candidates.Length == 1 && restrictedResult.Coverage.IsSubset && !restrictedResult.Coverage.FullyPublished,
            "Explicit document subsets retain their own candidates but never certify whole-scope completeness");
        var restrictedVerified = await references.ResolveVerifiedWithCoverageAsync(restricted, [new("FixtureSymbol256"), new("Evidence")]);
        check(restrictedVerified.Targets[0].Candidates.Length == 1 && restrictedVerified.Targets[1].Candidates.IsEmpty &&
            restrictedVerified.Coverage.IsSubset && !restrictedVerified.Coverage.FullyPublished,
            "Verified positive results preserve the caller's document subset and incomplete coverage");
        await ThrowsAsync<ExplorerLimitException>(() => new RelationalReferenceService(session, index,
            new ExplorerLimits(MaximumMetadataRows: 20)).LoadPaintAsync(scope), check,
            "The bulk metadata stream rejects row-budget overflow instead of publishing a truncated reference cache");
        await ThrowsAsync<ExplorerLimitException>(() => new RelationalReferenceService(session, index,
            new ExplorerLimits(MaximumMetadataCharacters: 20)).LoadPaintAsync(scope), check,
            "The bulk metadata stream rejects character-budget overflow instead of retaining unbounded metadata");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => references.LoadPaintAsync(scope, cancelled.Token), check,
                "Cancellation interrupts metadata construction without installing a partial cache");
            await ThrowsAsync<OperationCanceledException>(() => references.ResolveVerifiedWithCoverageAsync(scope, queries, cancelled.Token), check,
                "Verified navigation respects cancellation before returning cached targets");
        }
        await ThrowsAsync<ArgumentException>(() => references.ResolveVerifiedWithCoverageAsync(scope,
            Enumerable.Repeat(new ReferenceQuery("Evidence"), 65)), check, "Verified navigation retains the 64-token request limit");
        await ThrowsAsync<IndexGenerationChangedException>(() => references.ResolveVerifiedWithCoverageAsync(
            scope with { Context = scope.Context with { Epoch = Guid.NewGuid() } }, queries), check,
            "Verified navigation rejects a different runtime epoch");

        await references.LoadPaintAsync(scope);
        var lease = await index.BeginWorkAsync(await index.GetHandleAsync(document), Guid.NewGuid(), source.Fingerprint, policy);
        await ThrowsAsync<IndexGenerationChangedException>(() => references.ResolveAsync(scope.Context, [new("FixtureSymbol256")]), check,
            "Cached navigation still rejects a changed SQL generation before returning metadata");
        await ThrowsAsync<IndexGenerationChangedException>(() => references.ResolveVerifiedWithCoverageAsync(scope, [new("Evidence")]), check,
            "Verified positive navigation still rejects an obsolete SQL generation");
        var stale = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        var staleResult = await references.ResolveWithCoverageAsync(stale, [new("FixtureSymbol256")]);
        check(staleResult.Coverage.StaleOrUnindexed == 2 && !staleResult.Coverage.FullyPublished &&
            staleResult.Targets.Single().Candidates.Single().Freshness == IndexFreshness.Stale,
            "Coverage and candidates expose both stale aliases after a real index mutation rather than reusing a ready cache");
        await ThrowsAsync<ReferenceIndexNotReadyException>(() => references.ResolveAsync(stale, [new("Evidence")]), check,
            "Authoritative all-scope resolution remains unavailable while unrelated documents are stale");
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var verified = await references.ResolveVerifiedWithCoverageAsync(stale,
                [new("[dbo].[Evidence]"), new("FixtureSymbol256"), new("NoSuchReference")]);
            check(verified.Targets[0].Candidates.Single().Definition.Kind == ReferenceEntityKind.Table &&
                verified.Targets[0].Candidates.Single().Freshness == IndexFreshness.Indexed &&
                !verified.Coverage.FullyPublished && verified.Coverage.StaleOrUnindexed == 2,
                "A current captured table is navigable despite unrelated stale procedure aliases, without claiming complete coverage");
            check(verified.Targets[1].Candidates.IsEmpty && verified.Targets[2].Candidates.IsEmpty && !verified.Coverage.FullyPublished,
                "Verified navigation filters stale targets and does not turn an incomplete miss into an authoritative result");
            check(!trace.Commands.Any(c => c.Text.Contains("surf.SymbolDefinition", StringComparison.OrdinalIgnoreCase) ||
                c.Text.Contains("surf.ResourceDocument", StringComparison.OrdinalIgnoreCase)),
                "Incomplete-index positive navigation retains the metadata cache without scanning every document");
            StateAccessNoReads(trace.Commands, check, "Verified positive navigation", "TextContent", "Asset", "TableColumnRevision");
        }
        await index.PublishAsync(new(lease, source.Text, source.Language, source.Symbols, source.SourceRevisionKey));
        var replacement = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        var replacementResult = await references.ResolveAsync(replacement, [new("FixtureSymbol256"), new("NeedleProc")]);
        check(replacementResult[0].Candidates.IsEmpty && replacementResult[1].Candidates.Length == 1,
            "A replacement generation discards removed cached names and retains the newly published target");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static SymbolInput ReferenceHotPathSymbol(string name, string qualified, string language, string locator) =>
        new(name, qualified, ReferenceEntityKind.Method, locator, 1, 1, 1, 1, null, null, null, language, "fixture");
}

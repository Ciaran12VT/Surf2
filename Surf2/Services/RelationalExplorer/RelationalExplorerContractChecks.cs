using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

// Parent StorageSuite can call this without a database, filesystem fixture, WPF window, or parser.
public static class RelationalExplorerContractChecks
{
    public static async Task<IReadOnlyList<string>> RunPureAsync(CancellationToken ct = default)
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Explorer contract failed: " + name);
            passed.Add(name);
        }
        var context = new IndexRequestContext(Guid.NewGuid(), 1, 2, "scope", "snapshot", "diagram", false, [], [], false);
        var snapshot = new SnapshotSummary(10, Guid.NewGuid(), "snap-id", "DB /%?#", "DB", DateTimeOffset.UnixEpoch, 0, null, []);
        var resource = new ExplorerResource(20, 0, "source", ResourceKind.DatabaseSnapshot, "snap-id", "Alias", true, true, snapshot, null, null, null, false);
        var unloaded = resource with { ScopeResourceKey = 21, ResourceId = "unloaded", Alias = "Unloaded", IsLoaded = false };
        var folders = ImmutableArray.Create(new ExplorerVirtualFolder(1, 0, "group", "Group", "", ["snap-id"]));
        var scope = new ExplorerScope(context, "scope-id", "Scope", [resource, unloaded], folders, "en-IE");
        var metadata = new FakeMetadata(scope);
        var physical = new FakePhysical();
        var explorer = new RelationalExplorerService(metadata, physical);
        var roots = await explorer.GetRootsAsync(scope, ct).ConfigureAwait(false);
        Check(metadata.CategoryReads == 0 && physical.Reads == 0, "roots read no documents, categories, or directories");
        var virtualRoot = roots.Single(n => n.Role == ExplorerNodeRole.VirtualFolder);
        var moved = await Children(explorer, scope, virtualRoot, ct).ConfigureAwait(false);
        Check(moved.Single().ScopeResourceKey == 20 && roots.Single(n => n.ScopeResourceKey == 21).IsScopeResourceLoaded == false,
            "aliases and workspace-unloaded membership occurrences remain separate");
        var categories = await Children(explorer, scope, moved.Single(), ct).ConfigureAwait(false);
        Check(categories.Select(n => n.Name).SequenceEqual(new[] { "Stored Procedures", "Views", "Functions", "Triggers", "Tables" }) && metadata.CategoryReads == 0,
            "category expansion does not fetch resource content or children");
        Check((await Children(explorer, scope, roots.Single(n => n.ScopeResourceKey == 21), ct).ConfigureAwait(false)).IsEmpty,
            "unloaded snapshot has no children");
        var procedureFolder = categories.Single(n => n.Category == ExplorerCategory.Procedures);
        var procedures = await Children(explorer, scope, procedureFolder, ct).ConfigureAwait(false);
        Check(procedures.Select(n => n.MatchName).SequenceEqual(new[] { "dbo.Alpha", "dbo.Zebra" }), "selected category uses legacy culture schema/name order");
        string canonical = ExplorerCompatibility.DatabaseLocator(snapshot, ExplorerCategory.Procedures, "dbo", "Alpha", true);
        var address = await explorer.ResolveAddressAsync(scope, canonical, resource.ScopeResourceKey, ct).ConfigureAwait(false);
        Check(address != null && address.ReadableLocator.Contains("DB %2F%25%3F%23", StringComparison.Ordinal) &&
            address.CanonicalLocator == canonical && address.RevisionKey == 101 &&
            address.Hierarchy.SequenceEqual(new[] { "Group", "Alias", "Stored Procedures", "dbo.Alpha" }),
            "canonical/readable selected address and virtual alias TXT hierarchy");
        Check(address!.TextFileNameSeed == "Group_Alias_Stored Procedures_dbo.Alpha", "TXT seed leaves existing datetime/sanitization policy outside storage");
        var secondAlias = resource with { ScopeResourceKey = 22, ResourceId = "source-2", SortOrdinal = 1, Alias = "Alias Two" };
        var aliasScope = scope with { Resources = [resource, secondAlias], VirtualFolders =
            [new(11, 0, "first-alias", "First Placement", "", ["snap-id"]),
             new(12, 1, "second-alias", "Second Placement", "", ["snap-id"])] };
        var aliasMetadata = new FakeMetadata(aliasScope) { IncludeDuplicateNames = true };
        var aliasExplorer = new RelationalExplorerService(aliasMetadata, physical);
        var aliasRoots = await aliasExplorer.GetRootsAsync(aliasScope, ct).ConfigureAwait(false);
        var secondAliasRoot = (await Children(aliasExplorer, aliasScope,
            aliasRoots.Single(n => n.VirtualFolderId == "second-alias"), ct).ConfigureAwait(false)).Single();
        var secondCategories = await Children(aliasExplorer, aliasScope, secondAliasRoot, ct).ConfigureAwait(false);
        var aliasLeaves = await Children(aliasExplorer, aliasScope,
            secondCategories.Single(n => n.Category == ExplorerCategory.Procedures), ct).ConfigureAwait(false);
        var clickedDuplicate = aliasLeaves.Single(n => n.SnapshotResourceKey == 4);
        var sameNameFirst = aliasLeaves.Single(n => n.SnapshotResourceKey == 2);
        Check(clickedDuplicate.FullPath == ExplorerCompatibility.MetadataDocumentPath(context.Epoch, 10, 4, ExplorerCategory.Procedures) &&
            clickedDuplicate.FullPath != sameNameFirst.FullPath && clickedDuplicate.NodeKey == sameNameFirst.NodeKey,
            "duplicate-named metadata leaves use distinct typed FullPaths while preserving legacy virtual-folder NodeKeys");
        var clickedAddress = await aliasExplorer.ResolveAddressAsync(aliasScope, clickedDuplicate, ct).ConfigureAwait(false);
        Check(clickedAddress != null && clickedAddress.Node.ScopeResourceKey == secondAlias.ScopeResourceKey && clickedAddress.Node.SnapshotResourceKey == 4 &&
            clickedAddress.RevisionKey == 103 && clickedAddress.CanonicalLocator == clickedDuplicate.FullPath &&
            clickedAddress.TextFileNameSeed == "Second Placement_Alias Two_Stored Procedures_dbo.Alpha",
            "clicking alias two and the second same-name resource preserves typed identity, preferred membership and TXT placement");
        var preferredAddress = await aliasExplorer.ResolveAddressAsync(aliasScope, clickedDuplicate.FullPath,
            secondAlias.ScopeResourceKey, ct).ConfigureAwait(false);
        Check(preferredAddress != null && preferredAddress.TextFileNameSeed == clickedAddress!.TextFileNameSeed &&
            preferredAddress.Node.SnapshotResourceKey == clickedDuplicate.SnapshotResourceKey,
            "typed string addresses honor the supplied clicked membership rather than choosing the first alias");
        bool oldSessionRejected = false;
        try
        {
            await aliasExplorer.ResolveAddressAsync(aliasScope,
                ExplorerCompatibility.MetadataDocumentPath(Guid.NewGuid(), 10, 4, ExplorerCategory.Procedures),
                secondAlias.ScopeResourceKey, ct).ConfigureAwait(false);
        }
        catch (IndexGenerationChangedException) { oldSessionRejected = true; }
        Check(oldSessionRejected, "typed metadata addresses from another session cannot silently rebind by legacy name");
        bool oldSummaryRejected = false;
        try
        {
            await aliasExplorer.ResolveAddressAsync(aliasScope, clickedDuplicate with
            { FullPath = ExplorerCompatibility.MetadataDocumentPath(Guid.NewGuid(), 10, 4, ExplorerCategory.Procedures) }, ct).ConfigureAwait(false);
        }
        catch (IndexGenerationChangedException) { oldSummaryRejected = true; }
        Check(oldSummaryRejected, "selected summaries retain their original session binding rather than restamping equal keys in a new session");

        using var row = JsonDocument.Parse("{\"A\":null,\"a\":\"later\",\"flag\":true,\"n\":1e+03,\"nested\":{\"x\":2}}");
        ImmutableArray<string> headers = ["A", "a", "flag", "n", "missing", "nested"];
        Check(ExplorerCompatibility.MatchingColumns(row.RootElement, headers, new("", false)).SequenceEqual(new[] { 0, 1, 2, 3, 5 }),
            "empty query distinguishes missing properties and retains duplicate/case-variant header indexes");
        Check(ExplorerCompatibility.MatchingColumns(row.RootElement, headers, new("later", false)).IsEmpty,
            "first case-insensitive property wins, not last duplicate");
        Check(ExplorerCompatibility.MatchingColumns(row.RootElement, headers, new("True", false)).SequenceEqual(new[] { 2 }) &&
            ExplorerCompatibility.MatchingColumns(row.RootElement, headers, new("1e+03", false)).SequenceEqual(new[] { 3 }),
            "captured booleans and numeric spelling use legacy display formatting");
        Check(RelationalExplorerSearchSources.InferLegacyHeaders(row.RootElement).SequenceEqual(new[] { "A", "a", "flag", "n", "nested" }),
            "first-row fallback retains duplicate headers rather than capture-layout deduplication");
        using var scalar = JsonDocument.Parse("42");
        Check(RelationalExplorerSearchSources.InferLegacyHeaders(scalar.RootElement).IsEmpty,
            "primitive first-row fallback does not infer headers from a later row");
        Check(new IndexTextMatcher("%_[", false).IsMatch("x%_[Y") && new IndexTextMatcher("(a)\\s+b\\1", true).IsMatch("A\nbA"),
            "literal wildcard characters and full-document regex/backreference semantics");

        var shared = new SharedReferencePaintLookup(context,
            [new("Same", "C#", ReferenceEntityKind.Method), new("Same", "SQL Server", ReferenceEntityKind.Class),
             new("Same", "C#", ReferenceEntityKind.Field), new("File", "File", ReferenceEntityKind.File), new("x", "C#", ReferenceEntityKind.Class)], "en-IE");
        Check(shared.TryGetStyle("same", "C#", out var csharp) && csharp!.Kind == ReferenceEntityKind.Method &&
            shared.TryGetStyle("Same", null, out var fallback) && fallback!.Kind == ReferenceEntityKind.Class,
            "shared paint preserves preferred-language and kind ranking without I/O");
        Check(!shared.TryGetStyle("File", null, out _) && !shared.TryGetStyle("x", null, out _), "paint excludes files and one-character names");
        Check(!new ReferenceCoverage(0, 0, false).FullyPublished, "empty unindexed paint catalogue is not declared published");
        var indexPolicy = new IndexPolicy("parser-v1", "renderer-v1", "policy-v1");
        Check(!new ExplorerIndexRefreshProgress(0, 0, 0, 0, 0, false, false).FullyPublished &&
            !new ExplorerIndexRefreshProgress(1, 0, 0, 1, 0, true, false).FullyPublished &&
            !new ExplorerIndexRefreshProgress(0, 0, 0, 0, 1, true, false).FullyPublished,
            "initial empty, failed and unloaded refreshes never report reference readiness");
        Check(RelationalScopeIndexRefresher.CanReuse(IndexFreshness.Indexed, "fingerprint", indexPolicy, 1, "C#", "fingerprint", indexPolicy, 1, "C#") &&
            !RelationalScopeIndexRefresher.CanReuse(IndexFreshness.Failed, "fingerprint", indexPolicy, 1, "C#", "fingerprint", indexPolicy, 1, "C#") &&
            !RelationalScopeIndexRefresher.CanReuse(IndexFreshness.Indexed, "fingerprint", indexPolicy, 1, "C#", "fingerprint", indexPolicy with { ParserVersion = "parser-v2" }, 1, "C#") &&
            !RelationalScopeIndexRefresher.CanReuse(IndexFreshness.Indexed, "fingerprint", indexPolicy, 1, "C#", "fingerprint", indexPolicy, 2, "C#"),
            "unchanged publication requires successful fingerprint, parser/renderer policy, language and source revision equality");
        var physicalMembership = resource with { Kind = ResourceKind.Folder, Path = @"C:\pure", Snapshot = null };
        var generatedFile = @"C:\pure\bin\generated.cs";
        Check(RelationalScopeIndexRefresher.OwnsFile(physicalMembership, generatedFile) &&
            !RelationalScopeIndexRefresher.ReferenceFileEligible(physicalMembership, generatedFile) &&
            RelationalScopeIndexRefresher.ReferenceFileEligible(physicalMembership with { Kind = ResourceKind.File, Path = generatedFile }, generatedFile) &&
            RelationalScopeIndexRefresher.ReferenceFileEligible(physicalMembership with { Path = @"C:\pure\bin" }, generatedFile),
            "search membership includes excluded descendants but reference policy preserves explicit file and explicit root exceptions");
        var referenceMembershipScope = scope with { Resources = [physicalMembership] };
        Check(!RelationalReferenceService.ReferenceAllowed(referenceMembershipScope, generatedFile) &&
            RelationalReferenceService.ReferenceAllowed(referenceMembershipScope with { Resources = [physicalMembership,
                physicalMembership with { ScopeResourceKey = 99, Kind = ResourceKind.File, Path = generatedFile }] }, generatedFile) &&
            !RelationalReferenceService.ReferenceAllowed(referenceMembershipScope with { Resources = [physicalMembership with { IsLoaded = false }] }, @"C:\pure\visible.cs"),
            "reference exclusions are per loaded scope occurrence, not destructive document-wide symbol removal");
        var immutableStyles = new SharedReferenceHighlightStyles(shared,
            [new("C#", ReferenceEntityKind.Method, "#123456", true, false, false)], ["C#", "SQL Server"], ct);
        Check(immutableStyles.ByLanguage["C#"]["same"].Foreground == "#123456" &&
            immutableStyles.ByLanguage["SQL Server"]["same"].Kind == ReferenceEntityKind.Class &&
            immutableStyles.ByLanguage["SQL Server"]["same"].Foreground == "#B45309",
            "immutable shared style maps preserve preferred language and legacy missing-style fallback without targets or GUI access");
        var firstStyle = new ReferenceHighlightStyleSetting { Language = "c#", Kind = ReferenceEntityKind.Method,
            Foreground = "#112233", IsBold = false, IsItalic = true, IsUnderline = true };
        var secondStyle = new ReferenceHighlightStyleSetting { Language = "C#", Kind = ReferenceEntityKind.Method,
            Foreground = "#445566", IsBold = true };
        var unknownStyle = new ReferenceHighlightStyleSetting { Language = "Legacy language", Kind = ReferenceEntityKind.Method,
            Foreground = "#778899" };
        var legacyStyles = new ReferenceHighlightSettings { Styles = [firstStyle, secondStyle, unknownStyle] };
        var originalStyles = legacyStyles.Styles.Select(ReferenceStyleValue.Capture).ToImmutableArray();
        var capturedStyles = ReferenceStyleValue.CaptureWithRuntimeDefaults(legacyStyles);
        Check(legacyStyles.Styles.Select(ReferenceStyleValue.Capture).SequenceEqual(originalStyles) &&
            ReferenceEquals(legacyStyles.Styles[0], firstStyle) && ReferenceEquals(legacyStyles.Styles[1], secondStyle) &&
            capturedStyles.Take(originalStyles.Length).SequenceEqual(originalStyles) && capturedStyles.Length > originalStyles.Length,
            "runtime defaults append to a rendering snapshot without mutating, saving or deduplicating loaded legacy styles");
        var defaultedStyles = new ReferenceHighlightSettings { Styles = legacyStyles.Styles.ToList() };
        Check(defaultedStyles.EnsureDefaultStyleEntries() &&
            defaultedStyles.Styles.Take(originalStyles.Length).Select(ReferenceStyleValue.Capture).SequenceEqual(originalStyles) &&
            !defaultedStyles.EnsureDefaultStyleEntries() && ReferenceEquals(defaultedStyles.GetStyle("C#", ReferenceEntityKind.Method), firstStyle),
            "legacy default insertion preserves duplicate values, source order and first existing style selection");
        var duplicateStyleLookup = new SharedReferenceHighlightStyles(shared, capturedStyles, ["C#"], ct);
        firstStyle.Foreground = "#FFFFFF"; firstStyle.IsItalic = false; firstStyle.IsUnderline = false;
        Check(duplicateStyleLookup.ByLanguage["C#"]["same"] == originalStyles[0] &&
            duplicateStyleLookup.ByLanguage["C#"]["same"].IsItalic && duplicateStyleLookup.ByLanguage["C#"]["same"].IsUnderline,
            "duplicate-first shared paint metadata remains immutable after mutable settings change");
        var legacyExtensions = new CodeWindowSettings { BackcolorsByExtension =
            [new() { Extension = ".CS", Language = " javaScript " }, new() { Extension = ".cs", Language = "VB" },
             new() { Extension = ".SQL", Language = " " }, new() { Extension = ".sql", Language = "C#" },
             new() { Extension = ".odd", Language = "unknown" }] };
        var capturedLanguages = ExplorerIndexLanguagePolicy.Capture(legacyExtensions);
        string[] languagePaths = [@"C:\pure\file.cs", @"C:\pure\file.SQL", @"C:\pure\file.odd", @"C:\pure\file.vb", @"C:\pure\file"];
        Check(languagePaths.All(path => capturedLanguages.LanguageFor(path) == legacyExtensions.GetLanguageForFile(path)) &&
            capturedLanguages.LanguageFor(languagePaths[0]) == CodeWindowSettings.JavaScriptLanguage &&
            capturedLanguages.LanguageFor(languagePaths[1]) == CodeWindowSettings.SqlServerLanguage,
            "extension mappings preserve case-insensitive duplicate-first selection, blank fallback and legacy language normalization");
        legacyExtensions.BackcolorsByExtension[0].Language = "C#"; legacyExtensions.BackcolorsByExtension.RemoveAt(1);
        Check(capturedLanguages.Extensions.Length == 5 && capturedLanguages.LanguageFor(languagePaths[0]) == CodeWindowSettings.JavaScriptLanguage,
            "captured extension mappings preserve original order and values independently of later settings edits");
        Check(typeof(ExplorerNodeSummary).GetProperties().All(p => p.PropertyType != typeof(JsonElement) &&
            !p.PropertyType.Name.Contains("Library", StringComparison.Ordinal) && p.PropertyType != typeof(DatabaseMetadataSnapshot)),
            "node summaries exclude root libraries, JSON row bodies and snapshot aggregates");
        var symbols = new[] { Symbol(1, "Call", "dbo.Call", ReferenceEntityKind.Method, 1), Symbol(2, "Call", "dbo.Call", ReferenceEntityKind.Method, 2),
            Symbol(3, "Call", "Call", ReferenceEntityKind.File, null) };
        var targets = ReferenceMetadata.Resolve(symbols, "[missing].[Call]", 2);
        Check(targets.Length == 1 && targets[0].SymbolKey == 2, "navigation uses qualified/simple fallback, non-file preference and argument ranking");

        var a = procedures[0] with { OccurrenceKey = "a", NodeKey = "duplicate", ParentNodeKey = ExplorerCompatibility.RootKey, NaturalParentKey = ExplorerCompatibility.RootKey };
        var b = a with { OccurrenceKey = "b", Name = "Second" };
        var duplicateScope = scope with { VirtualFolders = [new(2, 0, "same-id", "First", "", ["duplicate"]), new(3, 1, "same-id", "Second", "", ["duplicate"])] };
        var placed = ExplorerCompatibility.ApplyVirtualFolders(duplicateScope, ExplorerCompatibility.RootKey, [a, b]);
        Check(placed.Single(n => n.OccurrenceKey == "a").EffectiveParentOccurrenceKey == "virtual:2" &&
            placed.Single(n => n.OccurrenceKey == "b").EffectiveParentOccurrenceKey == "virtual:3",
            "duplicate virtual-folder IDs preserve separate move occurrences");
        Guid searchId = Guid.NewGuid();
        var builder = new ExplorerSearchResultBuilder(duplicateScope, searchId);
        builder.Append(new(searchId, [new(b, [], false, [], "Second", false, IndexFreshness.Unindexed, true)], [], new(1, 1, 0, 1, false, true, false)));
        var tree = builder.Snapshot();
        Check(tree[0].Children.Single().Node.OccurrenceKey == "b" && tree[1].Children.IsEmpty,
            "virtual-folder moves apply to filtered aliases, not discarded siblings");
        var orderedScope = duplicateScope with { VirtualFolders = [new(4, 0, "ordered", "Ordered", "", ["b", "a"])] };
        var movedOrder = ExplorerCompatibility.ApplyVirtualFolders(orderedScope, ExplorerCompatibility.RootKey,
            [a with { NodeKey = "a" }, b with { NodeKey = "b" }]);
        Check(movedOrder.Where(n => n.EffectiveParentOccurrenceKey == "virtual:4").Select(n => n.OccurrenceKey).SequenceEqual(new[] { "b", "a" }),
            "virtual-folder child order follows membership keys, not original sibling order");

        var source = new FakeSources();
        var request = new ExplorerSearchRequest(scope, "needle", false, IndexSearchTarget.Content, Guid.NewGuid());
        var batches = new List<ExplorerSearchBatch>();
        await foreach (var batch in explorer.SearchAsync(request, source, ct).ConfigureAwait(false)) batches.Add(batch);
        var contentHits = batches.SelectMany(b => b.Hits).ToArray();
        Check(contentHits.Length == 2 && contentHits.Single(h => h.Node.Category == ExplorerCategory.Tables).MatchingColumnIndexes.SequenceEqual(new[] { 1 }) &&
            !contentHits.Single(h => h.Node.Category == ExplorerCategory.Tables).ContentMatched,
            "content search emits one table hit with data filters and no code highlight for data-only match");
        Check(batches[^1].Coverage.Completed && !batches[^1].Coverage.FullyCurrent && batches[^1].Coverage.Considered == 4 &&
            source.ContentReads == 2 && source.TableReads == 1,
            "unindexed discovery remains non-current, unloaded resources do not scan, containers are counted");
        var indexedSource = new FakeSources { UseIndex = true };
        var indexedBatches = new List<ExplorerSearchBatch>();
        await foreach (var batch in explorer.SearchAsync(request with { RequestIdentity = Guid.NewGuid() }, indexedSource, ct).ConfigureAwait(false)) indexedBatches.Add(batch);
        Check(indexedSource.IndexCalls == 1 && indexedSource.ContentReads == 0 && indexedBatches.SelectMany(b => b.Hits).Count() == 2,
            "published definition projections use existing batched index search rather than source reparsing");
        indexedSource = new FakeSources { UseIndex = true, IndexedFreshness = IndexFreshness.Stale, ReturnNoIndexHits = true };
        var staleBatches = new List<ExplorerSearchBatch>();
        await foreach (var batch in explorer.SearchAsync(request with { RequestIdentity = Guid.NewGuid() }, indexedSource, ct).ConfigureAwait(false)) staleBatches.Add(batch);
        Check(indexedSource.ContentReads == 2 && staleBatches.SelectMany(b => b.Hits).Any(h => h.Node.ObjectName == "Alpha"),
            "stale nonmatching projections cannot hide a current-source match");
        source.TableStatus = IndexScanStatus.TimedOut;
        var incompleteBatches = new List<ExplorerSearchBatch>();
        await foreach (var batch in explorer.SearchAsync(request with { RequestIdentity = Guid.NewGuid() }, source, ct).ConfigureAwait(false)) incompleteBatches.Add(batch);
        Check(incompleteBatches[^1].Coverage.Incomplete == 1 && !incompleteBatches[^1].Coverage.FullyCurrent &&
            incompleteBatches.SelectMany(b => b.Hits).Any(h => h.Node.Category == ExplorerCategory.Tables),
            "known table hits survive another route timing out with truthful incomplete coverage");
        metadata.Current = false;
        var changed = new List<ExplorerSearchBatch>();
        await foreach (var batch in explorer.SearchAsync(request, source, ct).ConfigureAwait(false)) changed.Add(batch);
        Check(changed[^1].Coverage.GenerationChanged && !changed[^1].Coverage.Completed && changed.All(b => b.Hits.IsEmpty),
            "generation change never publishes an empty completed success");
        metadata.Current = true;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool sawCancel = false;
        try { await foreach (var _ in explorer.SearchAsync(request, source, cancelled.Token).ConfigureAwait(false)) { } }
        catch (OperationCanceledException) { sawCancel = true; }
        Check(sawCancel, "search cancellation propagates instead of completed empty results");
        using var cancellationParent = new CancellationTokenSource();
        using var callbackRelease = new ManualResetEventSlim(false);
        var callbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationLifetime = new ExplorerCancellationLifetime(cancellationParent.Token);
        using var blockedCallback = cancellationLifetime.Token.Register(() =>
        {
            callbackStarted.TrySetResult(true); callbackRelease.Wait();
        });
        Task parentSignal = Task.CompletedTask;
        try
        {
            parentSignal = Task.Run(() => cancellationParent.Cancel(), CancellationToken.None);
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await parentSignal.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            Check(!cancellationLifetime.IsDisposed,
                "linked cancellation returns to its caller without running blocked provider callbacks inline");
            var firstCallbacks = cancellationLifetime.Cancel();
            var repeatedCallbacks = cancellationLifetime.Cancel();
            var retiring = cancellationLifetime.RetireAsync();
            Check(ReferenceEquals(firstCallbacks, repeatedCallbacks) && !firstCallbacks.IsCompleted &&
                !retiring.IsCompleted && !cancellationLifetime.IsDisposed,
                "supersession and retirement retain the first pending callback batch instead of a later completed cancellation task");
        }
        finally
        {
            callbackRelease.Set();
            await parentSignal.ConfigureAwait(false);
            await cancellationLifetime.RetireAsync().ConfigureAwait(false);
        }
        Check(cancellationLifetime.IsDisposed && cancellationLifetime.RetireAsync().IsCompleted,
            "linked registrations and owned cancellation sources retire only after provider callbacks finish");
        using var owner = new ExplorerRequestOwner();
        var ownedRequest = request with { RequestIdentity = Guid.NewGuid() };
        await foreach (var _ in owner.SearchAsync(explorer, source, ownedRequest, ct).ConfigureAwait(false)) { }
        Check(owner.Accepts(ownedRequest.RequestIdentity), "completed owner still permits its delayed dispatcher publication");
        owner.Cancel(); Check(!owner.Accepts(ownedRequest.RequestIdentity), "owner cancellation rejects delayed search results");
        var slow = new FakeSources { PauseFirstContent = true };
        async Task<bool> Pump(ExplorerSearchRequest r)
        {
            try { await foreach (var _ in owner.SearchAsync(explorer, slow, r, ct).ConfigureAwait(false)) { } return false; }
            catch (OperationCanceledException) { return true; }
        }
        var olderRequest = request with { RequestIdentity = Guid.NewGuid() };
        var newerRequest = request with { RequestIdentity = Guid.NewGuid() };
        var oldScan = Pump(olderRequest);
        await slow.Started.Task.WaitAsync(ct).ConfigureAwait(false);
        bool newerCancelled = await Pump(newerRequest).ConfigureAwait(false);
        Check(await oldScan.ConfigureAwait(false) && !newerCancelled && owner.Accepts(newerRequest.RequestIdentity) && !owner.Accepts(olderRequest.RequestIdentity),
            "a superseding search cancels the old provider read and rejects its delayed publication");

        var delayed = new DelayedPhysical();
        var physicalScope = scope with { Resources = [new(30, 0, "folder", ResourceKind.Folder, @"C:\pure", "Folder", false, true, null, null, null, null, false)], VirtualFolders = [] };
        var physicalExplorer = new RelationalExplorerService(new FakeMetadata(physicalScope), delayed);
        var physicalRoot = (await physicalExplorer.GetRootsAsync(physicalScope, ct).ConfigureAwait(false)).Single(n => n.Role == ExplorerNodeRole.Resource);
        await using var loader = new ExplorerChildrenLoader(physicalExplorer);
        using var oneWaiter = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = loader.ReadAsync(physicalScope, physicalRoot, oneWaiter.Token);
        var second = loader.ReadAsync(physicalScope, physicalRoot, ct);
        await delayed.Started.Task.WaitAsync(ct).ConfigureAwait(false);
        oneWaiter.Cancel();
        try { await first.ConfigureAwait(false); } catch (OperationCanceledException) { }
        delayed.Release.TrySetResult(true);
        var children = await second.ConfigureAwait(false);
        Check(children.Completed && delayed.Reads == 1 && !delayed.SourceCancelled,
            "duplicate expansion shares one flight; cancelling one consumer leaves the other active");
        var abandoned = new DelayedPhysical();
        var abandonedExplorer = new RelationalExplorerService(new FakeMetadata(physicalScope), abandoned);
        await using (var lastLoader = new ExplorerChildrenLoader(abandonedExplorer))
        {
            using var lastWaiter = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lastRead = lastLoader.ReadAsync(physicalScope, physicalRoot, lastWaiter.Token);
            await abandoned.Started.Task.WaitAsync(ct).ConfigureAwait(false);
            lastWaiter.Cancel();
            try { await lastRead.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        Check(abandoned.SourceCancelled, "last departing child-load consumer cancels and disposes underlying work");
        var physicalSearchExplorer = new RelationalExplorerService(new FakeMetadata(physicalScope), new SearchPhysical());
        var physicalRequest = new ExplorerSearchRequest(physicalScope, "generated", false, IndexSearchTarget.Name, Guid.NewGuid());
        var physicalBatches = new List<ExplorerSearchBatch>();
        await foreach (var batch in physicalSearchExplorer.SearchAsync(physicalRequest, new FakeSources(), ct).ConfigureAwait(false)) physicalBatches.Add(batch);
        Check(physicalBatches.SelectMany(b => b.Hits).Single().Node.FullPath == @"C:\pure\bin\generated.cs" && physicalBatches[^1].Coverage.Considered == 4,
            "physical search follows descendants despite IncludeChildren false and does not apply parser bin/obj exclusions");
        Check(physicalBatches[^1].Coverage.PhysicalDiscoveryNotFrozen && !physicalBatches[^1].Coverage.FullyCurrent,
            "observed directory enumeration is not claimed to be an atomic frozen candidate set");
        var domainMetadata = new FakeMetadata(scope) { Current = false, DomainCurrent = true };
        var domainExplorer = new RelationalExplorerService(domainMetadata, physical);
        Check((await domainExplorer.GetRootsAsync(scope, ct).ConfigureAwait(false)).Length > 0,
            "derived publication does not invalidate authoritative metadata browsing");
        var domainSearch = new List<ExplorerSearchBatch>();
        await foreach (var batch in domainExplorer.SearchAsync(request, source, ct).ConfigureAwait(false)) domainSearch.Add(batch);
        Check(domainSearch[^1].Coverage.GenerationChanged && !domainSearch[^1].Coverage.Completed,
            "search still fences derived generation when metadata browsing remains current");
        return passed;
    }

    private static async Task<ImmutableArray<ExplorerNodeSummary>> Children(RelationalExplorerService service,
        ExplorerScope scope, ExplorerNodeSummary parent, CancellationToken ct)
    {
        var nodes = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
        await foreach (var batch in service.GetChildrenAsync(scope, parent, ct).ConfigureAwait(false))
        {
            if (batch.State == ExplorerChildrenState.Failed) throw new InvalidOperationException(batch.FailureCode);
            nodes.AddRange(batch.Nodes);
        }
        return nodes.ToImmutable();
    }
    private static SymbolSummary Symbol(long key, string name, string qualified, ReferenceEntityKind kind, int? count) =>
        new(key, key, key, new(name, qualified, kind, "target", checked((int)key), 1, checked((int)key), 1,
            count, count, count, "C#", ""), IndexFreshness.Indexed);
    private sealed class FakeMetadata(ExplorerScope scope) : IExplorerMetadataQueries, IExplorerDomainFence
    {
        internal int CategoryReads;
        internal bool Current = true;
        internal bool? DomainCurrent;
        internal bool IncludeDuplicateNames;
        public Task<ExplorerScope> ReadScopeAsync(long key, IReadOnlySet<string>? unloaded, string? culture, CancellationToken ct) => Task.FromResult(scope);
        public Task<bool> IsCurrentAsync(IndexRequestContext context, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Current); }
        public Task<bool> IsDomainCurrentAsync(IndexRequestContext context, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(DomainCurrent ?? Current); }
        public async IAsyncEnumerable<ExplorerDatabaseItem> ReadDatabaseCategoryAsync(ExplorerScope selected, ExplorerResource resource,
            ExplorerCategory category, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); CategoryReads++;
            if (category == ExplorerCategory.Procedures)
            {
                yield return new(new(1, Guid.Empty, 10, DatabaseVersionedResourceKind.StoredProcedure, "dbo", "Zebra", "SP|dbo.Zebra", 100, 0, []), SqlDatabaseObjectKind.StoredProcedure);
                yield return new(new(2, Guid.Empty, 10, DatabaseVersionedResourceKind.StoredProcedure, "dbo", "Alpha", "SP|dbo.Alpha", 101, 1, []), SqlDatabaseObjectKind.StoredProcedure);
                if (IncludeDuplicateNames)
                    yield return new(new(4, Guid.Empty, 10, DatabaseVersionedResourceKind.StoredProcedure, "dbo", "Alpha", "SP|dbo.Alpha", 103, 3, []), SqlDatabaseObjectKind.StoredProcedure);
            }
            if (category == ExplorerCategory.Tables)
                yield return new(new(3, Guid.Empty, 10, DatabaseVersionedResourceKind.TableMetadata, "dbo", "Rows", "TableMetadata|dbo.Rows", 102, 2, []), null, true, 999);
        }
    }
    private class FakePhysical : IPhysicalExplorerQueries
    {
        internal int Reads;
        public Task<ExplorerAvailability> ProbeAsync(string path, bool directory, CancellationToken ct) => Task.FromResult(ExplorerAvailability.Present);
        public virtual Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string culture, ExplorerLimits limits, CancellationToken ct)
        { Reads++; return Task.FromResult(ImmutableArray<PhysicalExplorerEntry>.Empty); }
    }
    private sealed class DelayedPhysical : FakePhysical
    {
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SourceCancelled;
        public override async Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string culture, ExplorerLimits limits, CancellationToken ct)
        {
            Reads++; Started.TrySetResult(true);
            try { await Release.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { SourceCancelled = true; throw; }
            return [];
        }
    }
    private sealed class SearchPhysical : FakePhysical
    {
        public override Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string culture, ExplorerLimits limits, CancellationToken ct) =>
            Task.FromResult<ImmutableArray<PhysicalExplorerEntry>>(path == @"C:\pure" ?
                [new(@"C:\pure\bin", true, ExplorerAvailability.Present), new(@"C:\pure\visible.cs", false, ExplorerAvailability.Present)] :
                [new(@"C:\pure\bin\generated.cs", false, ExplorerAvailability.Present)]);
    }
    private sealed class FakeSources : IExplorerSearchSources
    {
        internal int ContentReads, TableReads, IndexCalls;
        internal bool UseIndex, ReturnNoIndexHits, PauseFirstContent;
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IndexFreshness IndexedFreshness = IndexFreshness.Indexed;
        private ImmutableArray<ExplorerIndexBinding> _bindings = [];
        internal IndexScanStatus TableStatus = IndexScanStatus.Matched;
        public Task<ImmutableArray<ExplorerIndexBinding>> BindIndexDocumentsAsync(ExplorerScope scope, ImmutableArray<ExplorerNodeSummary> nodes, CancellationToken ct)
        {
            _bindings = UseIndex ? nodes.Where(n => n.SourceRevisionKey.HasValue).Select(n => new ExplorerIndexBinding(n.OccurrenceKey,
                n.SourceRevisionKey!.Value, n.ScopeResourceKey!.Value, n.SourceRevisionKey, IndexedFreshness)).ToImmutableArray() : [];
            return Task.FromResult(_bindings);
        }
        public async IAsyncEnumerable<IndexSearchBatch> SearchIndexAsync(IndexSearchRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!UseIndex) await Task.FromException(new InvalidOperationException("Empty index bindings must use selected-source fallback."));
            ct.ThrowIfCancellationRequested(); IndexCalls++;
            var outcomes = _bindings.Where(b => request.Context.DocumentKeys.Contains(b.DocumentKey))
                .Select(b => new SearchSourceOutcome(b.DocumentKey, b.ScopeResourceKey,
                    !ReturnNoIndexHits && b.SourceRevisionKey == 101 ? IndexScanStatus.Matched : IndexScanStatus.NotMatched, IndexedFreshness)).ToImmutableArray();
            yield return new([], outcomes, new(outcomes.Length, outcomes.Length, outcomes.Count(o => o.Status == IndexScanStatus.Matched),
                0, IndexedFreshness == IndexFreshness.Indexed ? 0 : outcomes.Length, 0, request.Context.DiscoveryReconciled, true, true, false));
        }
        public async Task<string> ReadCurrentContentAsync(ExplorerNodeSummary node, int bound, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref ContentReads) == 1 && PauseFirstContent)
            { Started.TrySetResult(true); await Continue.Task.WaitAsync(ct).ConfigureAwait(false); }
            return node.ObjectName == "Alpha" ? "needle" : "other";
        }
        public Task<ExplorerTableSearch> SearchTableAsync(ExplorerNodeSummary node, IndexTextMatcher matcher, int bound, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); TableReads++; return Task.FromResult(new ExplorerTableSearch(false, [1], TableStatus, TableStatus == IndexScanStatus.Matched)); }
    }
}

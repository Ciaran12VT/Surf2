using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    // Separate entry point for the parent --runtime-state switch. Only SqlFixture-owned databases are changed.
    public static async Task RunRuntimeStateCommandChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        foreach (string name in StateRuntimeCommandsChecks.RunPure()) check(true, name);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        var connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        {
            ApplicationName = "Surf2_Regression_StateAccess_RuntimeState_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore(); var store = new RelationalStateStore(session, content);
        long firstSnapshot = 0, secondSnapshot = 0;
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            firstSnapshot = await writer.CreateSnapshotAsync(new("duplicate-runtime-snapshot", "First", "Fixture", EvidenceTime, 0));
            secondSnapshot = await writer.CreateSnapshotAsync(new("duplicate-runtime-snapshot", "Second", "Fixture", EvidenceTime, 1));
            await writer.PublishSnapshotAsync(firstSnapshot, null); await writer.PublishSnapshotAsync(secondSnapshot, null);
        });
        await fixture.MarkTestFixtureReadyAsync();
        check((await store.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Scopes)).Version.AsSpan().SequenceEqual(new byte[8]) &&
            (await store.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Diagrams)).Version.AsSpan().SequenceEqual(new byte[8]),
            "Unpublished catalogue generations use the same eight-byte zero token as the explorer catalogue");
        var snapshots = new RelationalSnapshotStore(session, content);
        var selectedSnapshot = Required(await snapshots.GetSnapshotAsync(secondSnapshot), "selected duplicate snapshot");
        var graph = Diagram("Runtime command selected graph");
        graph.Document.DiagramId = "duplicate-runtime-diagram";
        const string physicalPath = @"C:\runtime-state-fixture\selected.txt";
        graph.Document.Objects[0].Metadata.Link = physicalPath;
        var diagramToken = await store.CreateDiagramAsync(graph, 0, Guid.NewGuid());
        var otherGraph = Diagram("Runtime command independent graph"); otherGraph.Document.DiagramId = graph.Document.DiagramId;
        var otherDiagram = await store.CreateDiagramAsync(otherGraph, 1, Guid.NewGuid());
        long revision = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT CurrentRevisionKey FROM surf.Diagram WHERE DiagramKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, otherDiagram.Key)));
        var otherSummary = new DiagramSummary(otherDiagram, 1, otherGraph.Document.DiagramId, otherGraph.Document.Name, revision,
            otherGraph.Document.CreatedAtUtc, otherGraph.Document.UpdatedAtUtc);
        var source = new Scope { ScopeId = "runtime-command-source", Name = "Source" };
        source.Resources.Add(new() { ResourceId = "physical", Kind = ResourceKind.File, Path = physicalPath, DisplayNameOverride = "Alias", DetailsOverride = "Source details", IncludeChildren = false });
        source.Resources.Add(new() { ResourceId = "database", Kind = ResourceKind.DatabaseSnapshot, Path = selectedSnapshot.SnapshotId });
        source.Resources.Add(new() { ResourceId = "diagram", Kind = ResourceKind.Diagram, Path = otherGraph.Document.DiagramId });
        source.VirtualFolders.Add(new() { VirtualFolderId = "parent", Name = "Parent", ChildNodeKeys = ["surf2://virtual-folder/child", "physical", "surf2://virtual-folder/missing"] });
        source.VirtualFolders.Add(new() { VirtualFolderId = "child", Name = "Child", ParentNodeKey = "SURF2://VIRTUAL-FOLDER/PARENT", ChildNodeKeys = ["database", "diagram"] });
        var sourceToken = await store.CreateScopeAsync(source, 0, Guid.NewGuid());
        sourceToken = await store.SaveScopeWithSnapshotTargetAsync(source, sourceToken, 1,
            new(session.Epoch, secondSnapshot, selectedSnapshot.RowVersion), Guid.NewGuid());
        sourceToken = await StateAccessObservedAsync(connection,
            () => store.SaveScopeWithDiagramTargetAsync(source, sourceToken, 2, otherSummary, Guid.NewGuid()), commands =>
            {
                StateAccessNoReads(commands, check, "Selected logical diagram target validation", "DiagramRevision", "DiagramObject", "Workflow", "Workbench", "Asset");
                check(StateAccessReads(commands, "Diagram").Any(c => c.Text.Contains("DIAGRAMKEY=@TARGET", StringComparison.Ordinal) &&
                    c.Text.Contains("VERSION=@TARGETVERSION", StringComparison.Ordinal) && c.Text.Contains("CURRENTREVISIONKEY=@REVISION", StringComparison.Ordinal)),
                    "Selected diagram membership validates the logical owner key, rowversion and revision pointer, not the immutable scalar map");
            }, check, "selected logical diagram membership target");
        check((await store.ReadRuntimeScopeTargetsAsync(sourceToken)).Single(t => t.ResourceOrdinal == 2).DiagramKey == otherDiagram.Key,
            "Diagram target validation binds the selected logical head even when original diagram IDs repeat");
        var wrongRevisionSummary = otherSummary with { RevisionKey = revision + 1 };
        await ThrowsAsync<StateConflictException>(() => store.SaveScopeWithDiagramTargetAsync(source, sourceToken, 2, wrongRevisionSummary, Guid.NewGuid()), check,
            "A mismatched immutable revision pointer cannot masquerade as the selected logical diagram head");
        var wrongVersionSummary = otherSummary with { Token = new StateToken(otherDiagram.Key, session.Epoch, diagramToken.Version, Guid.Empty) };
        await ThrowsAsync<StateConflictException>(() => store.SaveScopeWithDiagramTargetAsync(source, sourceToken, 2, wrongVersionSummary, Guid.NewGuid()), check,
            "A selected diagram target with a stale or different logical head token rejects atomically");
        check((await store.ReadScopeTokenAsync(sourceToken.Key))!.Version.AsSpan().SequenceEqual(sourceToken.Version) &&
            (await store.ReadRuntimeScopeTargetsAsync(sourceToken)).Single(t => t.ResourceOrdinal == 2).DiagramKey == otherDiagram.Key,
            "Rejected logical target validation preserves the scope head and existing typed binding");
        var target = new Scope { ScopeId = "runtime-command-target", Name = "Target" };
        target.VirtualFolders.Add(new() { VirtualFolderId = "missing", Name = "Unrelated destination folder" });
        var targetToken = await store.CreateScopeAsync(target, 1, Guid.NewGuid());
        async Task<long> ResourceKey(long scopeKey, int ordinal) => Convert.ToInt64(await fixture.DestinationSqlAsync(
            "SELECT ScopeResourceKey FROM surf.ScopeResource WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, scopeKey), RelationalSession.Parameter("@Ordinal", SqlDbType.BigInt, ordinal)));
        async Task<RuntimeScopeResourceOrigin> Origin(long? scopeKey = null, long? memberKey = null, long? snapshotKey = null, long? diagramKey = null)
        {
            var scopes = await store.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Scopes);
            var diagrams = await store.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Diagrams);
            byte[] snapshotVersion = (byte[])Required(await fixture.DestinationSqlAsync("SELECT RowVersion FROM surf.SnapshotCatalogueHead WHERE UserKey=1;"), "snapshot catalogue version");
            check(scopes.Version.Length == 8 && snapshotVersion.Length == 8 && diagrams.Version.Length == 8,
                "Catalogue command origins encode three exact binary rowversion tokens, not SQL string conversion results");
            return new(session.Epoch, Convert.ToHexString(scopes.Version), Convert.ToHexString(snapshotVersion), Convert.ToHexString(diagrams.Version), scopeKey, memberKey, snapshotKey, diagramKey);
        }
        var physical = new ScopedResource { ResourceId = "new-physical", Kind = ResourceKind.File, Path = physicalPath,
            DisplayNameOverride = "Alias", DetailsOverride = "Source details", IncludeChildren = false };
        var physicalOrigin = await Origin(sourceToken.Key, await ResourceKey(sourceToken.Key, 0));
        var initialTargetToken = targetToken;
        targetToken = await store.AddScopeResourceFromCatalogueAsync(target, targetToken, physical, physicalOrigin, Guid.NewGuid());
        target = Required(await store.ReadScopeAsync(targetToken.Key), "added physical membership").Value;
        check(target.Resources.Count == 1 && target.Resources[0].ResourceId == "new-physical" && target.Resources[0].DetailsOverride == "Source details" && !target.Resources[0].IncludeChildren,
            "Catalogue addition preserves writable header scalars and publishes one fresh selected membership");
        check(!targetToken.Version.AsSpan().SequenceEqual(initialTargetToken.Version), "Catalogue addition returns the new expected scope head");
        SameModel(source, Required(await store.ReadScopeAsync(sourceToken.Key), "unchanged source scope").Value, check, "Catalogue addition never mutates its source scope");
        var freshOrigin = await Origin(sourceToken.Key, await ResourceKey(sourceToken.Key, 0));
        await ThrowsAsync<StateConflictException>(() => store.AddScopeResourceFromCatalogueAsync(target, initialTargetToken, physical, freshOrigin, Guid.NewGuid()), check,
            "Catalogue addition rejects a stale selected scope head");
        await ThrowsAsync<StateConflictException>(() => store.AddScopeResourceFromCatalogueAsync(target, targetToken, physical, physicalOrigin, Guid.NewGuid()), check,
            "Catalogue addition rejects a stale catalogue generation before publishing");
        SameModel(target, Required(await store.ReadScopeAsync(targetToken.Key), "failed addition rollback").Value, check, "Failed additions retain every selected scope row");
        check((await store.ReadScopeTokenAsync(targetToken.Key))!.Version.AsSpan().SequenceEqual(targetToken.Version), "Rejected additions do not advance the owner head");
        var bad = new ScopedResource { Kind = ResourceKind.File, Path = physicalPath, DetailsOverride = "forged source details", DisplayNameOverride = "Alias", IncludeChildren = false };
        await ThrowsAsync<StateConflictException>(() => store.AddScopeResourceFromCatalogueAsync(target, targetToken, bad, freshOrigin, Guid.NewGuid()), check,
            "Catalogue addition revalidates the exact source membership instead of trusting UI headers");
        var database = new ScopedResource { ResourceId = "new-database", Kind = ResourceKind.DatabaseSnapshot, Path = selectedSnapshot.SnapshotId };
        var databaseOrigin = await Origin(snapshotKey: secondSnapshot);
        targetToken = await store.AddScopeResourceFromCatalogueAsync(target, targetToken, database, databaseOrigin, Guid.NewGuid());
        target = Required(await store.ReadScopeAsync(targetToken.Key), "selected duplicate snapshot membership").Value;
        check((await store.ReadRuntimeScopeTargetsAsync(targetToken)).Single(t => t.ResourceOrdinal == 1).SnapshotKey == secondSnapshot && firstSnapshot != secondSnapshot,
            "Duplicate original snapshot IDs retain the explicitly selected typed target, never the first match");
        var unresolvedOrigin = await Origin(sourceToken.Key, await ResourceKey(sourceToken.Key, 1));
        await ThrowsAsync<ArgumentException>(() => store.AddScopeResourceFromCatalogueAsync(target, targetToken, database, unresolvedOrigin, Guid.NewGuid()), check,
            "A database catalogue item without a typed target cannot become an editable guessed membership");
        var diagramMember = new ScopedResource { ResourceId = "new-diagram", Kind = ResourceKind.Diagram, Path = otherSummary.DiagramId };
        targetToken = await store.AddScopeResourceFromCatalogueAsync(target, targetToken, diagramMember, await Origin(diagramKey: otherDiagram.Key), Guid.NewGuid());
        target = Required(await store.ReadScopeAsync(targetToken.Key), "selected duplicate diagram membership").Value;
        check((await store.ReadRuntimeScopeTargetsAsync(targetToken)).Single(t => t.ResourceOrdinal == 2).DiagramKey == otherDiagram.Key,
            "Duplicate original diagram IDs retain the chosen logical diagram FK");
        int offset = target.Resources.Count;
        var merged = await store.MergeScopeExpectedAsync(target, targetToken, sourceToken, Guid.NewGuid());
        target = merged.Scope; targetToken = merged.Token;
        var loadedMerge = Required(await store.ReadScopeAsync(targetToken.Key), "merged scope");
        SameModel(target, loadedMerge.Value, check, "Merge returns exactly its committed bounded selected graph");
        check(target.Resources.Skip(offset).Zip(source.Resources).All(p => p.First.ResourceId != p.Second.ResourceId) &&
            target.VirtualFolders[2].ParentNodeKey == "surf2://virtual-folder/" + target.VirtualFolders[1].VirtualFolderId &&
            target.VirtualFolders[1].ChildNodeKeys[0] == "surf2://virtual-folder/" + target.VirtualFolders[2].VirtualFolderId,
            "Merge atomically clones IDs and remaps nested parent/member relationships");
        check(target.VirtualFolders[1].ChildNodeKeys[2] != "surf2://virtual-folder/missing", "Missing source folders cannot bind an unrelated coincident destination ID");
        var mergedTargets = await store.ReadRuntimeScopeTargetsAsync(targetToken);
        check(mergedTargets.Single(t => t.ResourceOrdinal == offset + 1).SnapshotKey == secondSnapshot &&
            mergedTargets.Single(t => t.ResourceOrdinal == offset + 2).DiagramKey == otherDiagram.Key,
            "Merge copies exact typed source targets along with fresh membership identities");
        source.Name = "Changed source"; sourceToken = await store.SaveScopeAsync(source, sourceToken, Guid.NewGuid());
        var staleSource = new StateToken(sourceToken.Key, session.Epoch, initialTargetToken.Version, Guid.Empty);
        await ThrowsAsync<StateConflictException>(() => store.MergeScopeExpectedAsync(target, targetToken, staleSource, Guid.NewGuid()), check,
            "Merge rejects a stale source head without changing the destination");
        var ambiguous = new Scope { ScopeId = "runtime-command-ambiguous", Name = "Ambiguous source" };
        ambiguous.Resources.Add(new() { ResourceId = "duplicate" }); ambiguous.Resources.Add(new() { ResourceId = "DUPLICATE" });
        var ambiguousToken = await store.CreateScopeAsync(ambiguous, 2, Guid.NewGuid());
        await ThrowsAsync<System.IO.InvalidDataException>(() => store.MergeScopeExpectedAsync(target, targetToken, ambiguousToken, Guid.NewGuid()), check,
            "Merge rejects duplicate source IDs before any owner publication");
        SameModel(target, Required(await store.ReadScopeAsync(targetToken.Key), "failed merge rollback").Value, check, "Failed merges retain all destination children, aliases and folders");
        check((await store.ReadScopeTokenAsync(targetToken.Key))!.Version.AsSpan().SequenceEqual(targetToken.Version), "Failed merge retains the expected head token");
        var workbench = new WorkbenchAggregate(new WorkbenchState { WorkbenchId = "runtime-command-saved-copy", Name = "Saved independent copy",
            ScopeId = target.ScopeId, ActiveDiagramId = graph.Document.DiagramId, ActiveDiagramSnapshot = graph.Document }, graph.PastedImages, graph.PastedImageFallbacks);
        var workbenchToken = await store.CreateWorkbenchAsync(workbench, 0, Guid.NewGuid());
        var selectedGraph = Required(await store.ReadDiagramAsync(diagramToken.Key), "selected graph before removal").Value;
        selectedGraph.Document.Objects[0].Metadata.Link = string.Empty;
        long removedKey = await ResourceKey(targetToken.Key, 0);
        var wrongDiagramToken = new StateToken(diagramToken.Key, session.Epoch, otherDiagram.Version, Guid.Empty);
        await ThrowsAsync<StateConflictException>(() => store.RemoveScopeResourceExpectedAsync(target, targetToken, 0, removedKey, selectedGraph,
            wrongDiagramToken, Guid.NewGuid()), check, "Stale selected diagram publication rolls back the entire scope removal");
        SameModel(target, Required(await store.ReadScopeAsync(targetToken.Key), "atomic remove rollback").Value, check, "Rejected atomic removal retains selected scope membership and folders");
        check((await store.ReadScopeTokenAsync(targetToken.Key))!.Version.AsSpan().SequenceEqual(targetToken.Version), "Rollback does not advance the scope token after an attempted diagram publication");
        var removed = await store.RemoveScopeResourceExpectedAsync(target, targetToken, 0, removedKey, selectedGraph, diagramToken, Guid.NewGuid());
        var expectedScope = target; expectedScope.Resources.RemoveAt(0);
        SameModel(expectedScope, Required(await store.ReadScopeAsync(targetToken.Key), "scope after exact removal").Value, check,
            "Removal deletes the exact selected membership and retains all ordered surviving rows");
        SameModel(selectedGraph.Document, Required(await store.ReadDiagramAsync(diagramToken.Key), "selected graph after removal").Value.Document, check,
            "Atomic scope removal publishes selected link clearing while preserving all other graph fields");
        SameImages(selectedGraph.PastedImages, Required(await store.ReadDiagramAsync(diagramToken.Key), "selected PNGs after removal").Value.PastedImages, check,
            "Atomic selected link clearing retains pasted-image payloads independently of raw filenames");
        check(removed.Diagram != null && !removed.Diagram.Version.AsSpan().SequenceEqual(diagramToken.Version) &&
            !removed.Scope.Version.AsSpan().SequenceEqual(targetToken.Version), "Atomic removal returns both restamped expected owner tokens");
        SameModel(otherGraph.Document, Required(await store.ReadDiagramAsync(otherDiagram.Key), "independent current graph").Value.Document, check,
            "Selected removal never clears links in an unselected current graph");
        SameModel(workbench.Workbench, Required(await store.ReadWorkbenchAsync(workbenchToken.Key), "independent saved embedded graph").Value.Workbench, check,
            "Selected removal never changes a saved workbench's independent embedded diagram");
        SameImages(workbench.PastedImages, Required(await store.ReadWorkbenchAsync(workbenchToken.Key), "saved pasted assets").Value.PastedImages, check,
            "Saved workbench pasted bytes remain independent after current diagram link clearing");
        await ThrowsAsync<StateConflictException>(() => store.RemoveScopeResourceExpectedAsync(expectedScope, removed.Scope, 0, removedKey, null, null, Guid.NewGuid()), check,
            "An already removed typed occurrence cannot delete a different surviving membership");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    // Parent runtime-WPF suite can invoke this on its already initialized, selected fixture MainWindow.
    // No dialogs, writes, or guessed raw IDs: guards must reject before opening a command transaction.
    public static async Task RunRuntimeStateContextMenuGuardChecksAsync(Surf2.MainWindow main, Action<bool, string> check)
    {
        main.Dispatcher.VerifyAccess();
        var view = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var addition = main.RelationalScopeResourceAdditionHandler;
        var removal = main.RelationalScopeResourceRemovalHandler;
        check(addition != null && removal != null, "Selected scope application installs both explorer resource command delegates");
        if (addition == null || removal == null) throw new InvalidOperationException("Selected scope command delegates are unassigned.");
        var resources = await new ExplorerResourceCatalogue(runtime.Session).ListAsync(view);
        if (resources.Items.Length == 0) throw new InvalidOperationException("The context-menu fixture requires a candidate from another scope or catalogue.");
        var candidate = resources.Items[0];
        var edit = RuntimeWpfField<StateEditSession<Scope>>(main, "_relationalScopeEdit");
        var before = edit.ExpectedToken;
        RuntimeWpfSetField(main, "_relationalLayoutRestoring", true);
        try { await ThrowsAsync<InvalidOperationException>(() => addition(candidate, default), check, "Context-menu addition is blocked while saved layout restoration is active"); }
        finally { RuntimeWpfSetField(main, "_relationalLayoutRestoring", false); }
        RuntimeWpfSetField(main, "_relationalExplorerScope", view with { Context = view.Context with { RestrictDocumentKeys = true } });
        try { await ThrowsAsync<InvalidOperationException>(() => addition(candidate, default), check, "Context-menu subset/reference views cannot edit the authoritative scope"); }
        finally { RuntimeWpfSetField(main, "_relationalExplorerScope", view); }
        RuntimeWpfSetField(main, "_relationalExplorerScope", view with { Context = view.Context with { ScopeVersion = "0000000000000000" } });
        try { await ThrowsAsync<InvalidOperationException>(() => addition(candidate, default), check, "Context-menu stale scope heads reject before SQL publication"); }
        finally { RuntimeWpfSetField(main, "_relationalExplorerScope", view); }
        if (view.Resources.Length > 0)
        {
            var model = RuntimeWpfField<Scope>(main, "_activeScope");
            var duplicate = new ScopedResource { ResourceId = view.Resources[0].ResourceId, Kind = view.Resources[0].Kind, Path = view.Resources[0].Path };
            model.Resources.Add(duplicate);
            try { await ThrowsAsync<System.IO.InvalidDataException>(() => removal(view.Resources[0], default), check, "Context-menu removal never selects an arbitrary duplicate raw resource ID"); }
            finally { model.Resources.Remove(duplicate); }
        }
        check((await runtime.StateStore.ReadScopeTokenAsync(edit.SubjectKey))!.Version.AsSpan().SequenceEqual(before.Version),
            "All context-menu rejected operations preserve the stored owner head");
    }

    // Uses the actual explorer delegates and refresh path on a caller-owned Ready fixture; no picker or confirmation modal.
    public static async Task RunRuntimeStateContextMenuFlowChecksAsync(Surf2.MainWindow main, Action<bool, string> check)
    {
        await RunRuntimeStateContextMenuGuardChecksAsync(main, check);
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var view = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
        var catalogue = await new ExplorerResourceCatalogue(runtime.Session).ListAsync(view);
        var candidate = catalogue.Items.FirstOrDefault(c => c.Header.Kind is ResourceKind.File or ResourceKind.Folder ||
            c.Header.Kind == ResourceKind.DatabaseSnapshot && c.SnapshotKey.HasValue || c.Header.Kind == ResourceKind.Diagram && c.DiagramKey.HasValue)
            ?? throw new InvalidOperationException("The owned context-menu fixture requires one resolved or physical candidate.");
        var before = RuntimeWpfField<StateEditSession<Scope>>(main, "_relationalScopeEdit");
        var initial = before.ExpectedToken; int originalCount = before.Snapshot().Resources.Count;
        await main.RelationalScopeResourceAdditionHandler!(candidate, default);
        var after = RuntimeWpfField<StateEditSession<Scope>>(main, "_relationalScopeEdit");
        var added = after.Snapshot();
        check(!ReferenceEquals(before, after) && !after.ExpectedToken.Version.AsSpan().SequenceEqual(initial.Version) && added.Resources.Count == originalCount + 1,
            "Explorer Add Existing acknowledges a fresh loaded scope session and one new membership");
        SameModel(added, Required(await runtime.StateStore.ReadScopeAsync(after.SubjectKey), "context-menu added scope").Value, check,
            "Explorer Add Existing rich UI acknowledgement matches its committed selected scope");
        await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalRootsAsync");
        view = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
        var member = view.Resources.Single(r => r.SortOrdinal == originalCount);
        check(member.Snapshot?.SnapshotKey == candidate.SnapshotKey && member.DiagramKey == candidate.DiagramKey,
            "Refreshed explorer membership preserves the selected typed target and never resolves by original ID");
        var confirm = typeof(Surf2.MainWindow).GetProperty("RelationalScopeResourceRemovalConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing offscreen confirmation seam.");
        var previous = confirm.GetValue(main);
        confirm.SetValue(main, (Func<ExplorerResource, bool>)(_ => true));
        try { await main.RelationalScopeResourceRemovalHandler!(member, default); }
        finally { confirm.SetValue(main, previous); }
        var removed = RuntimeWpfField<StateEditSession<Scope>>(main, "_relationalScopeEdit");
        check(removed.Snapshot().Resources.Count == originalCount && !removed.ExpectedToken.Version.AsSpan().SequenceEqual(after.ExpectedToken.Version),
            "Explorer removal acknowledges its exact selected occurrence and a fresh expected head");
        SameModel(removed.Snapshot(), Required(await runtime.StateStore.ReadScopeAsync(removed.SubjectKey), "context-menu removed scope").Value, check,
            "Explorer removal updates the selected rich model only after the transaction commits");
        await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalRootsAsync");
        check(!RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope").Resources.Any(r => r.ScopeResourceKey == member.ScopeResourceKey),
            "Explorer roots no longer expose the removed typed occurrence");
    }

    // Consumes a dedicated fixture window: call at the end, before the parent's normal full disposal.
    public static async Task RunRuntimeStateStartupCancellationChecksAsync(Surf2.MainWindow main, Action<bool, string> check)
    {
        main.Dispatcher.VerifyAccess();
        var operation = (IDisposable)Required(RuntimeWpfInvoke(main, "BeginRelationalStateOperation"), "startup query lease");
        var token = (CancellationToken)Required(operation.GetType().GetProperty("Token")?.GetValue(operation), "startup query token");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var registration = token.Register(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); });
        try
        {
            var watch = Stopwatch.StartNew();
            RuntimeWpfInvoke(main, "CancelRelationalStateQueries"); watch.Stop();
            check(watch.Elapsed < TimeSpan.FromSeconds(1), "Unhydrated startup cancellation does not execute blocking query callbacks on the dispatcher");
            await Task.Run(() => { if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The retained first query cancellation batch did not start."); });
            var first = RuntimeWpfField<Task>(main, "_relationalStateFirstCancellation");
            RuntimeWpfInvoke(main, "CancelRelationalStateQueries");
            check(ReferenceEquals(first, RuntimeWpfField<Task>(main, "_relationalStateFirstCancellation")), "Repeated startup/shutdown signalling retains the first cancellation batch");
            check(token.IsCancellationRequested, "Startup operation tokens observe prompt close cancellation");
            var drain = (Task)Required(RuntimeWpfInvoke(main, "DrainRelationalStateQueriesAsync"), "state query drain");
            check(!drain.IsCompleted, "State drain waits for the actual startup body and its first callback batch before disposal");
            release.Set(); operation.Dispose();
            await drain.WaitAsync(TimeSpan.FromSeconds(15));
            check(drain.IsCompletedSuccessfully, "State drain finishes only after cancelled query work retires");
        }
        finally { release.Set(); operation.Dispose(); }
    }
}

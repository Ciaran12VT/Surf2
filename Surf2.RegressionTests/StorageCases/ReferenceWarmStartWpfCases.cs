using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Index;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckWarmReferencesAsync(Surf2.MainWindow main, string connection,
        QueryIntegrationFixture seeded, SqlFixture fixture, Action<bool, string> check)
    {
        main.Dispatcher.VerifyAccess();
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var originalRefresher = RuntimeWpfField<RelationalScopeIndexRefresher>(main, "_relationalIndexRefresher");
        var originalIndexRuntime = RuntimeWpfField<RelationalRuntime>(main, "_relationalIndexRuntime");
        string resourceId = "runtime-wpf-blocked-reference-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(fixture.OwnedDirectory, resourceId);
        var physical = new RuntimeWpfBlockedReferencePhysical(directory);
        var highlightRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var baselineWindows = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows");
        bool addedResource = false;
        string[] baselineResources = [];
        try
        {
            await RuntimeWpfDrainReferenceRefreshAsync(main);
            // The preceding scenario leaves a content search active. Its intentional source scan
            // is not background indexing, so isolate warm reference readiness from that work.
            RuntimeWpfNamed<System.Windows.Controls.TextBox>(main, "ObjectExplorerSearchTextBox").Text = string.Empty;
            await RuntimeWpfInvokeTaskAsync(main, "SearchRelationalExplorerAsync");
            // A fresh coordinator cannot use the previous coordinator's in-memory completion state.
            var warmRefresher = new RelationalScopeIndexRefresher(runtime.Session, runtime.Index, runtime.Snapshots);
            RuntimeWpfSetField(main, "_relationalIndexRuntime", runtime);
            RuntimeWpfSetField(main, "_relationalIndexRefresher", warmRefresher);
            using (var trace = new StateAccessSqlTrace(connection))
            {
                await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalLastActiveScopeAsync");
                var totalClock = RuntimeWpfField<Stopwatch>(main, "_relationalIndexElapsed");
                await RuntimeWpfDrainReferenceRefreshAsync(main);
                var progress = RuntimeWpfField<ExplorerIndexRefreshProgress>(main, "_relationalIndexProgress");
                var catalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
                var view = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
                RuntimeWpfAssert(ReferenceEquals(RuntimeWpfField<RelationalScopeIndexRefresher>(main, "_relationalIndexRefresher"), warmRefresher),
                    check, "Actual MainWindow warm reload preserves the fresh injected coordinator");
                RuntimeWpfAssert(progress.FullyPublished && progress.Considered == 0 && progress.Published == 0 &&
                    progress.ReusedResources == 3 && progress.Unchanged == 3 && catalogue.Coverage.FullyPublished &&
                    view.Resources.Count(r => r.IsLoaded) == 3,
                    check, "Actual MainWindow warm reload reuses three persisted captured-root checkpoints without checking documents");
                ReferenceWarmStartNoDocumentWork(trace.Commands, check, "actual MainWindow warm reload");
                RuntimeWpfAssert(trace.Commands.Count > 0, check, "MainWindow warm-reload assertions observe real owned-fixture SQL");
                RuntimeWpfAssert(!totalClock.IsRunning && progress.Elapsed >= totalClock.Elapsed,
                    check, "MainWindow warm completion elapsed includes the entire stopped UI index clock");
                var styles = (IReadOnlyDictionary<string, ReferenceHighlightStyleSetting>)
                    RuntimeWpfInvoke(main, "GetReferenceHighlightStylesForFile", "fixture.sql")!;
                RuntimeWpfAssert(styles.TryGetValue("NeedleProc", out var style) && RuntimeWpfIsCustomStyle(style),
                    check, "Warm readiness publishes the actual editor's prepared custom reference highlights");
            }

            Directory.CreateDirectory(directory);
            var selected = Required(await runtime.StateStore.ReadScopeAsync(seeded.ScopeToken.Key), "blocked reference scope owner");
            baselineResources = selected.Value.Resources.Select(r => r.ResourceId).ToArray();
            selected.Value.Resources.Add(new()
            {
                ResourceId = resourceId, Kind = ResourceKind.Folder, Path = directory,
                DisplayNameOverride = "Owned blocked reference folder", IncludeChildren = false
            });
            await runtime.StateStore.SaveScopeAsync(selected.Value, selected.Token, Guid.NewGuid());
            addedResource = true;
            var blockedRefresher = new RelationalScopeIndexRefresher(runtime.Session, runtime.Index, runtime.Snapshots, physical);
            RuntimeWpfSetField(main, "_relationalIndexRuntime", runtime);
            RuntimeWpfSetField(main, "_relationalIndexRefresher", blockedRefresher);
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalLastActiveScopeAsync");
            var operationClock = RuntimeWpfField<Stopwatch>(main, "_relationalIndexElapsed");
            var refresh = RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask");
            await physical.Entered.WaitAsync(TimeSpan.FromSeconds(60));
            await RuntimeWpfField<Task>(main, "_relationalHighlightTask").WaitAsync(TimeSpan.FromSeconds(60));
            var captured = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
            var capturedCatalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
            var physicalRoot = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope").Resources.Single(r => r.ResourceId == resourceId);
            RuntimeWpfAssert(!refresh.IsCompleted && physical.ReadCount == 1 &&
                ReferenceEquals(RuntimeWpfField<RelationalScopeIndexRefresher>(main, "_relationalIndexRefresher"), blockedRefresher),
                check, "The actual tracked MainWindow worker remains pending inside the injected owned-folder read");
            RuntimeWpfAssert(capturedCatalogue.Coverage.FullyPublished && captured.Resources.Count(r => r.IsLoaded) == 3 &&
                captured.Resources.Where(r => r.IsLoaded).All(r => r.Kind is ResourceKind.DatabaseSnapshot or ResourceKind.Diagram) &&
                !captured.Resources.Single(r => r.ResourceId == resourceId).IsLoaded &&
                captured.Context.UnloadedScopeResourceKeys.Contains(physicalRoot.ScopeResourceKey),
                check, "Blocked physical discovery leaves a verified captured-only reference view with physical roots explicitly excluded");
            RuntimeWpfAssert(RuntimeWpfInvoke(main, "HasPendingPhysicalReferences", captured) is true &&
                main.StatusText.Contains("Captured references ready", StringComparison.OrdinalIgnoreCase),
                check, "Actual captured-only readiness is labelled separately from pending physical references");

            string sourcePath = DatabaseDocumentService.CreateCanonicalObjectDocumentPath(
                new DatabaseMetadataSnapshot { SnapshotId = "query-snapshot", DisplayName = "Query snapshot" }, seeded.ProcedureValue);
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var request = new ReferenceNavigationRequestedEventArgs("NeedleProc", 1, 1, null);
                var resolution = await RuntimeWpfResolveReferenceAsync(main, request, sourcePath);
                captured = resolution.View;
                var candidates = resolution.Candidates;
                RuntimeWpfAssert(candidates.Length == 1 && candidates[0].Definition.Kind == ReferenceEntityKind.StoredProcedure &&
                    candidates[0].Definition.Name == "NeedleProc" && !refresh.IsCompleted,
                    check, "MainWindow's real reference-resolution entry point uses the captured view while physical discovery is blocked");
                StateAccessNoReads(trace.Commands, check, "Captured-only MainWindow resolution", "TextContent", "Asset");
            }
            capturedCatalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
            RuntimeWpfAssert(ReferenceEquals(captured, RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope")) &&
                await runtime.Index.IsContextCurrentAsync(captured.Context) && capturedCatalogue.Coverage.FullyPublished &&
                capturedCatalogue.Paint.Context.CatalogueGeneration == captured.Context.CatalogueGeneration &&
                RuntimeWpfInvoke(main, "HasPendingPhysicalReferences", captured) is true,
                check, "The actual current-view helper rebases captured resolution and paint without certifying the pending physical root");

            // Register metadata outside every selected root; no file is created, read or given scope membership.
            await runtime.Index.RegisterAsync(new(new FileDocumentOwner(Path.Combine(fixture.OwnedDirectory,
                "unrelated-reference-generation-" + Guid.NewGuid().ToString("N") + ".cs")),
                "Unrelated owned-fixture derived metadata", CodeWindowSettings.CSharpLanguage));
            var advanced = await runtime.Index.CaptureContextAsync(captured.Context.ScopeKey,
                captured.Context.UnloadedScopeResourceKeys);
            RuntimeWpfAssert(advanced.CatalogueGeneration > captured.Context.CatalogueGeneration &&
                advanced.ScopeVersion == captured.Context.ScopeVersion &&
                advanced.SnapshotCatalogueVersion == captured.Context.SnapshotCatalogueVersion &&
                advanced.DiagramCatalogueVersion == captured.Context.DiagramCatalogueVersion &&
                !await runtime.Index.IsContextCurrentAsync(captured.Context) && !refresh.IsCompleted && physical.ReadCount == 1,
                check, "An unrelated derived write advances only the owned fixture's global generation while physical discovery stays blocked");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync",
                new ReferenceNavigationRequestedEventArgs("NeedleProc", 1, 1, null), sourcePath, null);
            captured = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
            capturedCatalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
            var navigated = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows")
                .Where(w => !baselineWindows.Contains(w)).ToArray();
            RuntimeWpfAssert(navigated.Length == 1 && navigated[0].State.BoundSnapshotKey == seeded.SnapshotKey &&
                navigated[0].State.BoundResourceKey == seeded.Procedure.ResourceKey &&
                navigated[0].Text == seeded.ProcedureValue.Definition &&
                captured.Context.CatalogueGeneration == advanced.CatalogueGeneration &&
                capturedCatalogue.Paint.Context.CatalogueGeneration == advanced.CatalogueGeneration &&
                capturedCatalogue.Coverage.FullyPublished && await runtime.Index.IsContextCurrentAsync(captured.Context) &&
                captured.Resources.Count(r => r.IsLoaded) == 3 &&
                !captured.Resources.Single(r => r.ResourceId == resourceId).IsLoaded &&
                captured.Context.UnloadedScopeResourceKeys.Contains(physicalRoot.ScopeResourceKey) &&
                RuntimeWpfInvoke(main, "HasPendingPhysicalReferences", captured) is true &&
                !refresh.IsCompleted && physical.ReadCount == 1,
                check, "Actual navigation rebases captured view and paint after an unrelated generation change and opens the captured target while physical discovery remains blocked");
            await RuntimeWpfInvokeTaskAsync(main, "CloseOpenWindowAsync", navigated[0]);
            var missing = new ReferenceNavigationRequestedEventArgs("NoSuchOwnedPendingFileReference", 1, 1, null);
            bool qualifiedNegative = false;
            try { await RuntimeWpfResolveReferenceAsync(main, missing, sourcePath); }
            catch (InvalidOperationException ex) when (RuntimeWpfIsPendingPhysicalMessage(ex.Message)) { qualifiedNegative = true; }
            captured = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
            RuntimeWpfAssert(qualifiedNegative, check,
                "A missing token in the captured-only MainWindow resolver explicitly reports unready file references, not an authoritative miss");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", missing, sourcePath, null);
            RuntimeWpfAssert(RuntimeWpfIsPendingPhysicalMessage(main.StatusText) && !refresh.IsCompleted &&
                RuntimeWpfInvoke(main, "HasPendingPhysicalReferences", captured) is true &&
                RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").SequenceEqual(baselineWindows),
                check, "Actual MainWindow navigation publishes the explicit pending-file status without opening a target");

            // Hold the previous highlight job so the final real style builder cannot finish before the measured delay.
            await RuntimeWpfField<Task>(main, "_relationalHighlightTask").WaitAsync(TimeSpan.FromSeconds(60));
            RuntimeWpfSetField(main, "_relationalHighlightTask", highlightRelease.Task);
            TimeSpan elapsedBeforeRelease = operationClock.Elapsed;
            var highlightHoldClock = Stopwatch.StartNew();
            physical.Release();
            await RuntimeWpfWaitAsync(() =>
                !ReferenceEquals(RuntimeWpfField<Task>(main, "_relationalHighlightTask"), highlightRelease.Task),
                "blocked final real reference highlight builder");
            var finalHighlights = RuntimeWpfField<Task>(main, "_relationalHighlightTask");
            RuntimeWpfAssert(!finalHighlights.IsCompleted, check, "Final MainWindow highlight publication is held by the controlled predecessor task");
            await Task.Delay(200);
            TimeSpan minimumTotalElapsed = elapsedBeforeRelease + highlightHoldClock.Elapsed;
            highlightRelease.TrySetResult(true);
            await refresh.WaitAsync(TimeSpan.FromSeconds(120));
            await finalHighlights.WaitAsync(TimeSpan.FromSeconds(60));
            await RuntimeWpfDrainReferenceRefreshAsync(main);
            var completed = RuntimeWpfField<ExplorerIndexRefreshProgress>(main, "_relationalIndexProgress");
            var completeCatalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
            var completeView = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
            RuntimeWpfAssert(completed.FullyPublished && completeCatalogue.Coverage.FullyPublished &&
                completeView.Resources.Single(r => r.ResourceId == resourceId).IsLoaded &&
                RuntimeWpfInvoke(main, "HasPendingPhysicalReferences", completeView) is false,
                check, "Releasing physical discovery transitions actual MainWindow references from captured-only to complete");
            RuntimeWpfAssert(!operationClock.IsRunning && completed.Elapsed >= operationClock.Elapsed &&
                completed.Elapsed >= minimumTotalElapsed && completed.Elapsed >= elapsedBeforeRelease,
                check, "Terminal MainWindow elapsed is monotonic and includes physical waiting plus the delayed real highlight publication");

            // Inject watcher failure reporting, not source failure: this owned root remains readable.
            var watch = RuntimeWpfField<PhysicalReferenceWatch>(main, "_relationalPhysicalWatch");
            var failures = typeof(PhysicalReferenceWatch).GetField("_unwatchedRoots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("Missing watcher failure fixture hook.");
            failures.SetValue(watch, ImmutableArray.Create(new PhysicalReferenceWatchFailure(
                physicalRoot.ScopeResourceKey, directory, "OwnedFixtureWatchSetupFailed")));
            RuntimeWpfInvoke(main, "QueueRelationalPhysicalRefresh", new long[] { physicalRoot.ScopeResourceKey });
            await RuntimeWpfDrainReferenceRefreshAsync(main);
            var provisional = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
            RuntimeWpfAssert(RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope").Resources.Single(r => r.ResourceId == resourceId).IsLoaded &&
                !provisional.Resources.Single(r => r.ResourceId == resourceId).IsLoaded &&
                provisional.Context.UnloadedScopeResourceKeys.Contains(physicalRoot.ScopeResourceKey) &&
                RuntimeWpfInvoke(main, "HasPendingPhysicalReferences", provisional) is true &&
                main.StatusText.Contains("unwatched", StringComparison.OrdinalIgnoreCase) &&
                main.StatusText.Contains("provisional", StringComparison.OrdinalIgnoreCase),
                check, "Readable but unwatched roots remain provisional without changing the user's loaded Explorer selection");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", missing, sourcePath, null);
            RuntimeWpfAssert(RuntimeWpfIsPendingPhysicalMessage(main.StatusText), check,
                "Completed reconciliation cannot authorize a negative physical result when its watcher is unavailable");
            RuntimeWpfCheckEmptyLibraries(main, check);
        }
        finally
        {
            physical.Release();
            highlightRelease.TrySetResult(true);
            await RuntimeWpfInvokeTaskAsync(main, "StopRelationalExplorerAsync");
            foreach (var window in RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Where(w => !baselineWindows.Contains(w)))
                await RuntimeWpfInvokeTaskAsync(main, "CloseOpenWindowAsync", window);
            RuntimeWpfSetField(main, "_relationalIndexRefresher", originalRefresher);
            RuntimeWpfSetField(main, "_relationalIndexRuntime", originalIndexRuntime);
            if (addedResource)
            {
                var current = Required(await runtime.StateStore.ReadScopeAsync(seeded.ScopeToken.Key), "reference fixture cleanup owner");
                foreach (var added in current.Value.Resources.Where(r => r.ResourceId == resourceId).ToArray())
                    current.Value.Resources.Remove(added);
                await runtime.StateStore.SaveScopeAsync(current.Value, current.Token, Guid.NewGuid());
            }
            // Rebuild the GUI owner from persisted state; never leave a captured-only view or stopped cancellation owner behind.
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalLastActiveScopeAsync");
            await RuntimeWpfDrainReferenceRefreshAsync(main);
            if (addedResource)
            {
                var restored = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
                RuntimeWpfAssert(restored.Resources.Select(r => r.ResourceId).SequenceEqual(baselineResources) &&
                    RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask").IsCompleted,
                    check, "Warm-reference WPF checks restore the original resource list and leave no active worker for subsequent grid checks");
            }
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static async Task RuntimeWpfDrainReferenceRefreshAsync(Surf2.MainWindow main)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var refresh = RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask");
            await refresh.WaitAsync(TimeSpan.FromSeconds(120));
            await RuntimeWpfField<Task>(main, "_relationalHighlightTask").WaitAsync(TimeSpan.FromSeconds(60));
            await RuntimeWpfIdleAsync();
            if (ReferenceEquals(refresh, RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask"))) return;
        }
        throw new InvalidOperationException("The runtime reference fixture could not reach a stable tracked refresh task.");
    }

    private static async Task<(ExplorerScope View, ImmutableArray<SymbolSummary> Candidates)> RuntimeWpfResolveReferenceAsync(
        Surf2.MainWindow main, ReferenceNavigationRequestedEventArgs request, string sourcePath)
    {
        var currentView = RuntimeWpfInvoke(main, "CurrentRelationalReferenceViewAsync", CancellationToken.None)
            as Task<ExplorerScope> ?? throw new InvalidOperationException("Missing actual MainWindow current reference view hook.");
        var scope = await currentView.WaitAsync(TimeSpan.FromSeconds(60));
        var task = RuntimeWpfInvoke(main, "ResolveRelationalReferencesAsync", scope, request, sourcePath, CancellationToken.None)
            as Task<ReferenceResolutionBatch> ?? throw new InvalidOperationException("Missing actual MainWindow reference resolution hook.");
        return (scope, (await task.WaitAsync(TimeSpan.FromSeconds(60))).Targets.Single().Candidates);
    }

    private static bool RuntimeWpfIsPendingPhysicalMessage(string message) =>
        message.Contains("not ready", StringComparison.OrdinalIgnoreCase);

    private sealed class RuntimeWpfBlockedReferencePhysical(string root) : IPhysicalExplorerQueries
    {
        private readonly string _root = Path.GetFullPath(root);
        private readonly PhysicalExplorerQueries _physical = new();
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        public Task Entered => _entered.Task;
        public int ReadCount => Volatile.Read(ref _readCount);
        public void Release() => _release.TrySetResult(true);

        public Task<ExplorerAvailability> ProbeAsync(string path, bool isDirectory, CancellationToken ct)
        {
            RequireOwnedRoot(path);
            return _physical.ProbeAsync(path, isDirectory, ct);
        }

        public async Task<ImmutableArray<PhysicalExplorerEntry>> ReadDirectoryAsync(string path, string sortCultureName,
            ExplorerLimits limits, CancellationToken ct)
        {
            RequireOwnedRoot(path);
            Interlocked.Increment(ref _readCount);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return await _physical.ReadDirectoryAsync(path, sortCultureName, limits, ct).ConfigureAwait(false);
        }

        private void RequireOwnedRoot(string path)
        {
            if (!Path.GetFullPath(path).Equals(_root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The blocked physical fixture refuses paths outside its owned empty root.");
        }
    }
}

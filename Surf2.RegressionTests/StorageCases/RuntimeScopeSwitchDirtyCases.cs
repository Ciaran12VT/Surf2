using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalGrid;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    // Parent can register this entry point in a fresh process, separately from other WPF modes.
    // It changes only SqlFixture's generated databases and never calls App.OnStartup.
    public static Task RunRuntimeScopeSwitchDirtyChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                Surf2.App? app = null;
                Exception? failure = null;
                try
                {
                    if (Application.Current != null)
                        throw new InvalidOperationException("Scope-switch checks require a fresh WPF process.");
                    Type log = typeof(Surf2.App).Assembly.GetType("Surf2.Services.InternalLogService", true)!;
                    if (log.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not false)
                        throw new InvalidOperationException("Scope-switch checks require disabled user-file logging.");
                    app = new Surf2.App(); app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    await RunRuntimeScopeSwitchDirtyFixtureAsync(check);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { app?.Shutdown(); }
                    catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
                    dispatcher.InvokeShutdown();
                    if (failure == null) completion.TrySetResult(true);
                    else completion.TrySetException(failure);
                }
            }));
            try { Dispatcher.Run(); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Surf2 scope-switch dirty regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static async Task RunRuntimeScopeSwitchDirtyFixtureAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_ScopeDirty_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var store = new RelationalStateStore(session, new RelationalContentStore());
        var settings = new AppSettings { LoadMostRecentWorkbenchOnStartup = false,
            Diagnostics = new() { EnableInternalLogging = false } };
        settings.EnsureDefaults();
        const string firstId = "dirty-fixture-first", secondId = "dirty-fixture-second";
        string unavailable = System.IO.Path.Combine(fixture.OwnedDirectory, "unavailable-saved-grid.csv");
        await InTransactionAsync(session, async (c, t) =>
        {
            await store.ImportSettingsAsync(c, t, settings, Guid.NewGuid());
            await store.ImportWorkspaceAsync(c, t, new WorkspaceState
            {
                CanvasZoom = 1, ViewportHorizontalOffset = 250, ViewportVerticalOffset = 300,
                OpenDocuments = new ObservableCollection<OpenDocumentState>
                {
                    new() { FilePath = unavailable, DisplayName = "Retained grid", Left = 100, Top = 100,
                        SpreadsheetFilters = new() { [0] = "saved filter" } }
                }
            }, Guid.NewGuid());
            await store.ImportScopeSelectionAsync(c, t, 2, firstId, Guid.NewGuid());
        });
        await fixture.MarkTestFixtureReadyAsync();
        await store.CreateScopeAsync(new()
        {
            ScopeId = firstId, Name = "First dirty fixture",
            Resources = [new() { ResourceId = "dirty-resource", Kind = ResourceKind.File, Path = unavailable }],
            VirtualFolders = [new() { VirtualFolderId = "dirty-folder", Name = "Saved folder", ChildNodeKeys = ["dirty-resource"] }]
        }, 0, Guid.NewGuid());
        await store.CreateScopeAsync(new() { ScopeId = secondId, Name = "Second dirty fixture" }, 1, Guid.NewGuid());

        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        var root = new Grid { DataContext = main };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(main.Resources);
        var content = (FrameworkElement)main.Content; main.Content = null; root.Children.Add(content);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 scope dirty offscreen fixture")
        {
            PositionX = -32000, PositionY = -32000, Width = 1280, Height = 820,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080
        });
        host.RootVisual = root; RuntimeWpfArrange(root, 1280, 820);
        try
        {
            RuntimeWpfAssert(await ((Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!).WaitAsync(TimeSpan.FromSeconds(60)),
                check, "Actual MainWindow startup restores a selected scope without a default Workbench");
            await RuntimeWpfIdleAsync();
            await RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask").WaitAsync(TimeSpan.FromSeconds(120));
            await RuntimeWpfField<Task>(main, "_relationalHighlightTask").WaitAsync(TimeSpan.FromSeconds(60));
            await RunRuntimeScopeSwitchDirtyWindowChecksAsync(main, check, firstId, secondId, unavailable, connection);
            await RunRuntimeScopeSwitchGridOverlayChecksAsync(main, check, fixture.OwnedDirectory, secondId);
        }
        finally
        {
            try
            {
                await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync");
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true); main.Close();
            }
            finally { host.RootVisual = null; root.Children.Clear(); }
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task RunRuntimeScopeSwitchDirtyWindowChecksAsync(Surf2.MainWindow main, Action<bool, string> check,
        string firstId, string secondId, string unavailable, string connection)
    {
        main.Dispatcher.VerifyAccess();
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var confirmation = typeof(Surf2.MainWindow).GetProperty("RelationalStateSwitchConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing nonmodal scope-switch confirmation hook.");
        var previous = confirmation.GetValue(main);
        int prompts = 0; MessageBoxResult choice = MessageBoxResult.Cancel;
        confirmation.SetValue(main, (Func<MessageBoxResult>)(() => { prompts++; return choice; }));
        WorkbenchState Capture() => ((WorkbenchAggregate)RuntimeWpfInvoke(main, "CaptureRelationalWorkbench",
            RuntimeWpfOptionalField<StateEditSession<WorkbenchAggregate>>(main, "_relationalWorkbenchEdit")?.Snapshot().Workbench, true)!).Workbench;
        bool Dirty() => (bool)RuntimeWpfInvoke(main, "HasRelationalScopeSwitchWorkbenchChanges")!;
        Task<bool> Confirm(CancellationToken ct = default) => (Task<bool>)RuntimeWpfInvoke(main, "ConfirmRelationalStateSwitchAsync", ct)!;
        void SetLines(bool enabled) => RuntimeWpfInvoke(main, "SetReferenceConnectionLinesEnabled", enabled, false);
        Task<ExternalOpenResponse> Switch(string id) => (Task<ExternalOpenResponse>)RuntimeWpfInvoke(main,
            "OpenRelationalExternalResourceAsync", new ExternalOpenRequest { ScopeId = id, ResourcePath = unavailable })!;
        try
        {
            RuntimeWpfAssert(Capture().ScopeId == firstId && Capture().OpenDocuments.Single().SpreadsheetFilters[0] == "saved filter",
                check, "Startup retains unavailable saved grid layout and filters in the actual captured workspace");
            using (var trace = new StateAccessSqlTrace(connection))
            {
                RuntimeWpfAssert(!Dirty() && await Confirm() && await Confirm() && prompts == 0, check,
                    "Unchanged selected scope with meaningful restored layout does not ask to save on repeated switches");
                RuntimeWpfAssert(trace.Commands.Count == 0, check, "Dirty detection does not load a saved default owner or unrelated Workbench payloads");
            }
            bool lines = Capture().ReferenceConnectionLinesEnabled;
            SetLines(!lines);
            RuntimeWpfAssert(Dirty() && !await Confirm() && prompts == 1 && Dirty(), check,
                "Real Workbench edits ask to save and Cancel preserves the dirty baseline");
            choice = MessageBoxResult.No;
            RuntimeWpfAssert(await Confirm() && Dirty(), check, "No permits replacement without falsely acknowledging a save");
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalRootsAsync");
            RuntimeWpfAssert(Dirty(), check, "Explorer root refresh does not acknowledge unsaved Workbench changes");
            SetLines(lines);
            int before = prompts;
            RuntimeWpfAssert(!Dirty() && await Confirm() && prompts == before, check,
                "Returning the actual runtime state to its loaded baseline removes the save prompt");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ThrowsAsync<OperationCanceledException>(async () => { _ = await Confirm(cancelled.Token); }, check,
                    "Cancelled switch checking does not prompt or acknowledge a new baseline");
            }
            RuntimeWpfAssert(!Dirty() && prompts == before, check, "Cancelled checking retains the unchanged baseline");

            var activeScope = RuntimeWpfField<Scope>(main, "_activeScope");
            bool ScopeDirty() => (bool)RuntimeWpfInvoke(main, "HasRelationalScopeSwitchScopeChanges")!;
            var resource = activeScope.Resources.Single();
            DateTimeOffset added = resource.AddedAtUtc; resource.AddedAtUtc = added.AddDays(1);
            RuntimeWpfAssert(!ScopeDirty() && !Dirty(), check, "Incidental scope resource timestamps do not count as unsaved user changes");
            resource.AddedAtUtc = added;
            string details = resource.DetailsOverride; resource.DetailsOverride = "Unsaved resource details";
            choice = MessageBoxResult.Cancel;
            RuntimeWpfAssert(ScopeDirty() && !await Confirm(), check, "Genuine resource metadata edits retain scope-switch protection");
            resource.DetailsOverride = details;
            var extra = new ScopedResource { ResourceId = "extra-dirty-resource", Kind = ResourceKind.File, Path = unavailable };
            activeScope.Resources.Add(extra);
            RuntimeWpfAssert(ScopeDirty() && !await Confirm(), check, "Scope membership additions are detected independently of workspace layout");
            activeScope.Resources.Remove(extra);
            var folder = activeScope.VirtualFolders.Single(); string folderParent = folder.ParentNodeKey;
            folder.ParentNodeKey = "surf2://virtual-folder/other-parent";
            RuntimeWpfAssert(ScopeDirty() && !await Confirm(), check, "Virtual-folder reparenting is a genuine unsaved scope change");
            folder.ParentNodeKey = folderParent; folder.ChildNodeKeys.Add("extra-child");
            RuntimeWpfAssert(ScopeDirty(), check, "Virtual-folder membership edits are included in scope dirty detection");
            folder.ChildNodeKeys.Remove("extra-child");
            RuntimeWpfAssert(!ScopeDirty() && !Dirty(), check, "Reverting scope resources and virtual folders restores a clean baseline");
            var workspace = RuntimeWpfField<WorkspaceState>(main, "_workspaceState");
            workspace.UnloadedResourceIds.Add(resource.ResourceId);
            RuntimeWpfAssert(Dirty() && !await Confirm(), check, "User resource load-state changes remain part of Workbench dirty detection");
            workspace.UnloadedResourceIds.Remove(resource.ResourceId);
            var pan = RuntimeWpfNamed<ScrollViewer>(main, "WorkspaceScrollViewer");
            double horizontal = pan.HorizontalOffset, vertical = pan.VerticalOffset;
            pan.ScrollToHorizontalOffset(horizontal + 80); pan.ScrollToVerticalOffset(vertical + 60); pan.UpdateLayout();
            await RuntimeWpfIdleAsync();
            RuntimeWpfAssert(Dirty(), check, "User workspace panning remains part of the captured baseline comparison");
            pan.ScrollToHorizontalOffset(horizontal); pan.ScrollToVerticalOffset(vertical); pan.UpdateLayout(); await RuntimeWpfIdleAsync();
            double zoom = Capture().CodeCanvasZoom;
            RuntimeWpfSetField(main, "_canvasZoom", zoom + 0.25); RuntimeWpfInvoke(main, "ApplyCanvasZoom"); await RuntimeWpfIdleAsync();
            RuntimeWpfAssert(Dirty(), check, "User workspace zoom remains part of the captured baseline comparison");
            RuntimeWpfSetField(main, "_canvasZoom", zoom); RuntimeWpfInvoke(main, "ApplyCanvasZoom");
            pan.ScrollToHorizontalOffset(horizontal); pan.ScrollToVerticalOffset(vertical); pan.UpdateLayout(); await RuntimeWpfIdleAsync();
            RuntimeWpfAssert(!Dirty(), check, "Returning resource load state, pan and zoom to baseline removes Workbench dirtiness");
            string Comparison(WorkbenchState value) => (string)typeof(Surf2.MainWindow)
                .GetMethod("CreateRelationalWorkbenchComparisonKey", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [value])!;
            var timing = Capture(); string comparison = Comparison(timing);
            timing.CreatedAtUtc = timing.CreatedAtUtc.AddDays(1); timing.UpdatedAtUtc = timing.UpdatedAtUtc.AddDays(1);
            timing.SavedAtUtc = timing.SavedAtUtc.AddDays(1); timing.WorkbenchId = "incidental-owner-id"; timing.Name = "Incidental catalogue label";
            RuntimeWpfAssert(Comparison(timing) == comparison,
                check, "Publication timestamps and catalogue-only identity metadata do not enter the baseline string");

            var occurrences = RuntimeWpfField<System.Collections.IEnumerable>(main, "_relationalRetainedWindows").Cast<object>();
            var retained = (OpenDocumentState)occurrences.Single().GetType().GetProperty("Original")!.GetValue(occurrences.Single())!;
            double width = retained.Width, left = retained.Left;
            retained.Width += 40; retained.Left += 20;
            RuntimeWpfAssert(Dirty() && !await Confirm(), check, "Window resizing and positioning remain genuine Workbench changes");
            retained.Width = width; retained.Left = left;
            string filter = retained.SpreadsheetFilters[0]; retained.SpreadsheetFilters[0] = "unsaved grid filter";
            choice = MessageBoxResult.Cancel;
            RuntimeWpfAssert(Dirty() && !await Confirm(), check, "Real retained grid filter edits retain the scope-switch save protection");
            retained.SpreadsheetFilters[0] = filter;
            RuntimeWpfAssert(!Dirty(), check, "Returning grid filters to their restored values clears Workbench dirtiness");

            SetLines(!lines);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => RuntimeWpfInvokeTaskAsync(main, "SaveRelationalWorkbenchAsync", cancelled.Token),
                    check, "Cancelled Workbench save does not acknowledge unsaved runtime state");
            }
            RuntimeWpfAssert(Dirty(), check, "Real changes remain dirty after a cancelled save");
            choice = MessageBoxResult.Yes;
            RuntimeWpfAssert(await Confirm() && !Dirty(), check, "Yes publishes the real default Workbench and acknowledges only that saved capture");
            var scope = RuntimeWpfField<StateEditSession<Scope>>(main, "_relationalScopeEdit");
            var target = await runtime.StateStore.ReadDefaultWorkbenchTargetAsync(scope.SubjectKey, firstId);
            RuntimeWpfAssert(target.Summary != null, check, "Save confirmation creates the previously absent default owner");
            before = prompts;
            await Switch(secondId);
            RuntimeWpfAssert(main.ActiveScopeId == secondId && !Dirty() && await Confirm() && prompts == before,
                check, "Successful scope replacement establishes a clean scope-only baseline even without a saved default");
            bool secondLines = Capture().ReferenceConnectionLinesEnabled;
            SetLines(!secondLines); choice = MessageBoxResult.Cancel;
            var cancelledSwitch = await Switch(firstId);
            RuntimeWpfAssert(cancelledSwitch.WasCancelled && main.ActiveScopeId == secondId && Dirty(),
                check, "Cancel on the actual scope-switch path preserves the selected scope and real edits");
            SetLines(secondLines); before = prompts;
            await Switch(firstId);
            RuntimeWpfAssert(main.ActiveScopeId == firstId && !Dirty() && prompts == before,
                check, "Reverted scope state switches without prompting and resets the destination baseline");

            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalWorkbenchAsync", target.Summary!);
            await RuntimeWpfIdleAsync(); before = prompts;
            RuntimeWpfAssert(!Dirty() && await Confirm() && prompts == before && Capture().OpenDocuments.Count == 1,
                check, "Default Workbench load and deferred viewport restoration remain clean with unavailable documents");
            var named = (WorkbenchAggregate)RuntimeWpfInvoke(main, "CaptureRelationalWorkbench", null, false)!;
            named.Workbench.Name = "Independent named dirty fixture";
            named.Workbench.ReferenceConnectionLinesEnabled = !named.Workbench.ReferenceConnectionLinesEnabled;
            named.Workbench.OpenDocuments[0].SpreadsheetFilters[0] = "named saved filter";
            var namedToken = await runtime.StateStore.CreateWorkbenchAsync(named, 1, Guid.NewGuid());
            var namedSummary = (await runtime.StateStore.ListRecentWorkbenchesAsync()).Items.Single(w => w.Token.Key == namedToken.Key);
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalWorkbenchAsync", namedSummary);
            await RuntimeWpfIdleAsync(); before = prompts;
            RuntimeWpfAssert(!Dirty() && await Confirm() && prompts == before && Capture().OpenDocuments[0].SpreadsheetFilters[0] == "named saved filter",
                check, "An unchanged named Workbench is compared with what was loaded, not a different saved default");
            bool namedLines = Capture().ReferenceConnectionLinesEnabled;
            SetLines(!namedLines);
            RuntimeWpfAssert(Dirty() && !await Confirm(), check, "Named Workbench load still protects subsequent genuine changes");
            SetLines(namedLines);
            RuntimeWpfAssert(!Dirty(), check, "Reverting named Workbench changes restores its clean baseline");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => RuntimeWpfInvokeTaskAsync(main, "LoadRelationalWorkbenchAsync", target.Summary!, cancelled.Token),
                    check, "Cancelled Workbench loading never replaces the active named Workbench baseline");
            }
            RuntimeWpfAssert(!Dirty() && RuntimeWpfField<StateEditSession<WorkbenchAggregate>>(main, "_relationalWorkbenchEdit").SubjectKey == namedToken.Key,
                check, "Cancelled load leaves the selected owner and baseline intact");

            var graph = new DiagramState(new DiagramDocument { Name = "Dirty fixture diagram", Objects =
                [new() { Id = "dirty-shape", ObjectType = DiagramObjectType.Shape, ShapeKind = DiagramShapeKind.Rectangle,
                    LabelText = "Saved shape", Left = 200, Top = 200, Width = 160, Height = 100 }] }, new Dictionary<int, byte[]>());
            var diagramToken = await runtime.StateStore.CreateDiagramAsync(graph, 0, Guid.NewGuid());
            var diagramSummary = (await runtime.State.ListDiagramsAsync()).Items.Single(d => d.Token.Key == diagramToken.Key);
            var loadDiagram = typeof(Surf2.MainWindow).GetMethod("LoadRelationalDiagramAsync", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(DiagramSummary), typeof(CancellationToken)], null)
                ?? throw new InvalidOperationException("Missing typed diagram load hook.");
            await ((Task)loadDiagram.Invoke(main, [diagramSummary, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(60));
            await RuntimeWpfIdleAsync();
            RuntimeWpfAssert(RuntimeWpfInvoke(main, "HasRelationalUnsavedDiagramChanges") is false && Dirty(), check,
                "Loading a saved diagram is clean for the diagram owner but remains a genuine Workbench graph change");
            await RuntimeWpfInvokeTaskAsync(main, "SaveRelationalWorkbenchAsync");
            before = prompts;
            RuntimeWpfAssert(!Dirty() && await Confirm() && prompts == before, check,
                "Saving the Workbench acknowledges its diagram inclusion without disabling diagram protection");
            var shape = RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramShapeControl>().Single();
            string label = shape.LabelText; shape.ApplyDetails("Unsaved shape", shape.OutlineColorText, shape.BackColorText);
            RuntimeWpfAssert(RuntimeWpfInvoke(main, "HasRelationalUnsavedDiagramChanges") is true && Dirty() && !await Confirm(), check,
                "Real diagram edits retain the scope-switch save protection");
            shape.ApplyDetails(label, shape.OutlineColorText, shape.BackColorText);
            RuntimeWpfAssert(RuntimeWpfInvoke(main, "HasRelationalUnsavedDiagramChanges") is false && !Dirty(), check,
                "Returning diagram content to baseline clears only genuine diagram and Workbench dirtiness");
            shape.ApplyDetails("Saved diagram edit", shape.OutlineColorText, shape.BackColorText);
            await RuntimeWpfInvokeTaskAsync(main, "SaveRelationalDiagramAsync");
            RuntimeWpfAssert(RuntimeWpfInvoke(main, "HasRelationalUnsavedDiagramChanges") is false && Dirty(), check,
                "Diagram save does not falsely acknowledge the independent embedded Workbench graph");
            await RuntimeWpfInvokeTaskAsync(main, "SaveRelationalWorkbenchAsync");
            scope = RuntimeWpfField<StateEditSession<Scope>>(main, "_relationalScopeEdit");
            target = await runtime.StateStore.ReadDefaultWorkbenchTargetAsync(scope.SubjectKey, firstId);
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalWorkbenchAsync", target.Summary!);
            await RuntimeWpfIdleAsync(); before = prompts;
            RuntimeWpfAssert(RuntimeWpfInvoke(main, "HasRelationalUnsavedDiagramChanges") is false && !Dirty() && await Confirm() && prompts == before,
                check, "Loaded embedded diagram and retained grid layout form a clean actual runtime baseline");
        }
        finally { confirmation.SetValue(main, previous); }
    }

    // The dedicated runner supplies an initialized, caller-owned fixture MainWindow and directory.
    public static async Task RunRuntimeScopeSwitchGridOverlayChecksAsync(Surf2.MainWindow main, Action<bool, string> check,
        string ownedDirectory, string destinationScopeId)
    {
        main.Dispatcher.VerifyAccess();
        if (!System.IO.Path.IsPathFullyQualified(ownedDirectory))
            throw new ArgumentException("Grid overlay checks require an absolute fixture-owned directory.", nameof(ownedDirectory));
        System.IO.Directory.CreateDirectory(ownedDirectory);
        string path = System.IO.Path.Combine(ownedDirectory, "scope-dirty-overlay-" + Guid.NewGuid().ToString("N") + ".csv");
        await System.IO.File.WriteAllTextAsync(path, "Id,Value\r\n1,original\r\n2,second\r\n");
        IDataGridSource? source = await CsvGridSource.OpenAsync(path, limits: new() { StagingDirectory = ownedDirectory });
        FloatingSpreadsheetWindow? grid = null;
        var confirmation = typeof(Surf2.MainWindow).GetProperty("RelationalStateSwitchConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = confirmation.GetValue(main); var discard = main.RelationalGridDiscardEditsConfirmation;
        int workspacePrompts = 0, gridPrompts = 0;
        confirmation.SetValue(main, (Func<MessageBoxResult>)(() => { workspacePrompts++; return MessageBoxResult.Cancel; }));
        bool Dirty() => (bool)RuntimeWpfInvoke(main, "HasRelationalScopeSwitchWorkbenchChanges")!;
        Task<bool> Confirm() => (Task<bool>)RuntimeWpfInvoke(main, "ConfirmRelationalStateSwitchAsync")!;
        try
        {
            RuntimeWpfInvoke(main, "PresentRelationalGrid", new OpenDocumentState
                { FilePath = path, DisplayName = "Overlay fixture", Left = 350, Top = 350 }, source, null);
            grid = RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(w => w.State.FilePath == path);
            source = null; // The actual MainWindow presenter now owns disposal.
            var provider = RuntimeWpfField<IDataGridSource>(grid, "_gridSource");
            await RuntimeWpfIdleAsync(); await RuntimeWpfWaitQueryAsync(grid);
            await RuntimeWpfInvokeTaskAsync(main, "SaveRelationalWorkbenchAsync");
            RuntimeWpfAssert(!Dirty() && await Confirm(), check, "A clean live grid with saved workspace layout permits switching without confirmation");
            await using (var query = await provider.CreateQueryAsync())
            {
                await query.Completion;
                var row = (await query.ReadPageAsync(new(0, 1, 64 * 1024))).Rows.Single();
                provider.SetCell(row, 1, "unsaved overlay");
            }
            RuntimeWpfAssert(provider.OverlayBytes > 0 && !Dirty(), check,
                "An actual grid cell overlay can be dirty while its saved Workbench layout is unchanged");
            main.RelationalGridDiscardEditsConfirmation = (window, ct) =>
            { gridPrompts++; return Task.FromResult(false); };
            string scopeId = main.ActiveScopeId;
            var request = new ExternalOpenRequest { ScopeId = destinationScopeId, ResourcePath = path };
            var cancelled = await (Task<ExternalOpenResponse>)RuntimeWpfInvoke(main, "OpenRelationalExternalResourceAsync", request)!;
            RuntimeWpfAssert(cancelled.WasCancelled && main.ActiveScopeId == scopeId && provider.OverlayBytes > 0 && !grid.IsGridDisposed &&
                gridPrompts == 1 && workspacePrompts == 0, check,
                "Rejecting overlay discard on the actual scope-switch path preserves scope, grid owner and session edits");
            await RuntimeWpfInvokeTaskAsync(main, "SaveRelationalWorkbenchAsync");
            RuntimeWpfAssert(!Dirty() && provider.OverlayBytes > 0 && !await Confirm(), check,
                "Workbench save never acknowledges or discards an unsaved grid cell overlay");
            main.RelationalGridDiscardEditsConfirmation = (window, ct) =>
            { gridPrompts++; return Task.FromResult(true); };
            RuntimeWpfAssert(await Confirm() && provider.OverlayBytes > 0 && workspacePrompts == 0, check,
                "Explicit overlay-discard approval permits switching but does not discard until workspace replacement");
            string mode = ((WorkbenchAggregate)RuntimeWpfInvoke(main, "CaptureRelationalWorkbench", null, true)!).Workbench.CodeViewMode;
            RuntimeWpfSetMode(main, mode == "Canvas" ? "Tabs" : "Canvas"); await RuntimeWpfIdleAsync();
            RuntimeWpfSetMode(main, mode); await RuntimeWpfIdleAsync();
            RuntimeWpfAssert(!grid.IsGridDisposed && ReferenceEquals(provider, RuntimeWpfField<IDataGridSource>(grid, "_gridSource")) && provider.OverlayBytes > 0,
                check, "Code view reparenting preserves the same live grid provider and its unsaved overlay");
            await RuntimeWpfInvokeTaskAsync(main, "SaveRelationalWorkbenchAsync");
            provider.ClearEdits(); int before = gridPrompts;
            RuntimeWpfAssert(!Dirty() && await Confirm() && gridPrompts == before && workspacePrompts == 0,
                check, "Clearing the grid overlay returns to a clean scope-switch state without a sticky dirty flag");
        }
        finally
        {
            confirmation.SetValue(main, previous); main.RelationalGridDiscardEditsConfirmation = discard;
            if (source != null) await source.DisposeAsync();
        }
    }
}

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    // A separate process is required because WPF allows only one Application per process.
    public static Task RunV2WorkspaceChecksAsync(Action<bool, string> check, string outputDir)
    {
        if (!Path.IsPathFullyQualified(outputDir)) throw new ArgumentException("Supply an absolute evidence directory.", nameof(outputDir));
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
                    if (Application.Current != null) throw new InvalidOperationException("Run --v2-workspace in a fresh process.");
                    Type log = typeof(Surf2.App).Assembly.GetType("Surf2.Services.InternalLogService", true)!;
                    if (log.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not false)
                        throw new InvalidOperationException("Fixture logging must be disabled before application construction.");
                    app = new Surf2.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    Directory.CreateDirectory(outputDir);
                    await RunV2WorkspaceFixtureAsync(check, Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir)));
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { app?.Shutdown(); } catch (Exception ex) { failure ??= ex; }
                    dispatcher.InvokeShutdown();
                    if (failure == null) completion.TrySetResult(true); else completion.TrySetException(failure);
                }
            }));
            try { Dispatcher.Run(); } catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Surf2 V2 migrated workspace regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return JoinAsync();
        async Task JoinAsync()
        {
            try { await completion.Task; }
            finally
            {
                if (!await Task.Run(() => thread.Join(TimeSpan.FromSeconds(30))))
                    throw new TimeoutException("The V2 fixture dispatcher did not exit.");
            }
        }
    }

    private static async Task RunV2WorkspaceFixtureAsync(Action<bool, string> check, string outputDir)
    {
        var models = MigrationModels();
        models.Settings.LoadMostRecentWorkbenchOnStartup = false;
        models.Settings.Diagnostics.EnableInternalLogging = false;
        models.Settings.Appearance.Theme = AppearanceSettings.LightTheme;
        models.Workbenches.Workbenches.Clear(); models.Workspace.OpenDocuments.Clear();
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        VerifyDatabaseWorkflowChoices(check, fixture.OwnedDirectory);
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_V2_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var state = new RelationalStateStore(session);
        string folder = Path.Combine(fixture.OwnedDirectory, "definitions");
        Directory.CreateDirectory(folder);
        string callerPath = Path.Combine(folder, "Caller.cs"), targetPath = Path.Combine(folder, "Calculator.cs");
        await File.WriteAllTextAsync(callerPath, "class Caller { void Run() { new Calculator().Recalculate(); } } // current-first\n");
        await File.WriteAllTextAsync(targetPath, "class Calculator\n{\n    public void Recalculate() { }\n}\n");
        long scopeKey;
        await using (var sql = await session.OpenAsync())
        await using (var command = sql.CreateCommand())
        {
            command.CommandText = "SELECT ScopeKey FROM surf.Scope WHERE ScopeId=N'migration-scope';";
            scopeKey = Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        var selected = Required(await state.ReadScopeAsync(scopeKey), "migrated V2 scope");
        selected.Value.Resources.Add(new() { ResourceId = "v2-definitions", Kind = ResourceKind.Folder, Path = folder, IncludeChildren = true });
        await state.SaveScopeAsync(selected.Value, selected.Token, Guid.NewGuid());

        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeWpfSetField(main, "_relationalExplorerRetirement", gate.Task);
        var root = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey), DataContext = main };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources); root.Resources.MergedDictionaries.Add(main.Resources);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 V2 workspace offscreen host")
        { PositionX = -32000, PositionY = -32000, Width = 1280, Height = 820,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        try
        {
            using var trace = new StateAccessSqlTrace(connection);
            var startup = (Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!;
            RuntimeWpfAssert(await startup.WaitAsync(TimeSpan.FromSeconds(60)), check, "V2 opens a separately migrated master database");
            // This offscreen fixture calls the persistence stage directly. Mirror
            // Window_Loaded's finally block, which releases its startup overlay.
            RuntimeWpfInvoke(main, "HideMainCanvasLoading");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            var content = (FrameworkElement)main.Content; main.Content = null; root.Children.Add(content);
            host.RootVisual = root; RuntimeWpfArrange(root, 1280, 820); await RuntimeWpfIdleAsync();
            var scope = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
            VerifyV2ExplorerMenuParity(main, scope, folder, callerPath, check);
            var selector = RuntimeWpfNamed<ComboBox>(main, "ObjectExplorerSearchTargetComboBox");
            var footer = RuntimeWpfNamed<Border>(main, "RelationalIndexFooter");
            var index = RuntimeWpfField<ScopeReferenceIndex>(main, "_referenceIndex");
            RuntimeWpfAssert(scope.DatabaseCatalogue != null && !gate.Task.IsCompleted && index.Resolve("p").Count > 0,
                check, "Captured SQL names are navigable from metadata while background definitions are blocked");
            RuntimeWpfAssert(index.Resolve("Recalculate").Count == 0 && selector.SelectedIndex == 0 && !selector.IsEnabled && footer.Visibility == Visibility.Visible,
                check, "Name search is the locked default and a discreet loading footer is visible before definitions arrive");
            RuntimeWpfAssert(!(bool)RuntimeWpfField<object>(main, "_relationalSnapshotMemoryStarted"), check,
                "Snapshot history preload waits until the scope's definition and content-search load finishes");
            RuntimeWpfAssert(RuntimeWpfNamed<FrameworkElement>(main, "ObjectExplorerLoadingOverlay").Visibility == Visibility.Collapsed &&
                RuntimeWpfNamed<FrameworkElement>(main, "MainCanvasLoadingOverlay").Visibility == Visibility.Collapsed,
                check, "Background definition loading leaves the workspace unobstructed");
            var searchBox = RuntimeWpfNamed<TextBox>(main, "ObjectExplorerSearchTextBox");
            var unfilteredRootKeys = main.RootNodes.Select(node => node.NodeKey).ToArray();
            int sqlBeforeNameSearch = trace.Commands.Count;
            searchBox.Text = "p";
            await RuntimeWpfInvokeTaskAsync(main, "SearchRelationalExplorerAsync");
            RuntimeWpfAssert(main.RootNodes.Count > 0 && !selector.IsEnabled, check, "Name search returns resources before the definition job starts");
            RuntimeWpfAssert(main.StatusText.Contains("Search complete.", StringComparison.Ordinal), check,
                "Name search reports completion independently of the pending definition index");
            RuntimeWpfAssert(trace.Commands.Count == sqlBeforeNameSearch, check,
                "Name search uses the prepared scope inventory without SQL reads");
            VerifyExpandedSearchBranches(main.RootNodes, "Name");
            searchBox.Clear();
            await RuntimeWpfInvokeTaskAsync(main, "SearchRelationalExplorerAsync");
            RuntimeWpfAssert(trace.Commands.Count == sqlBeforeNameSearch && main.RootNodes.Select(node => node.NodeKey).SequenceEqual(unfilteredRootKeys),
                check, "Clearing Name search restores the cached scope roots without SQL reads");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", new ReferenceNavigationRequestedEventArgs("p", 1, 1, null), callerPath, null);
            RuntimeWpfAssert(RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(window => window.Text == "current-first"),
                check, "A metadata reference lazily opens the selected SQL body before physical definitions load");
            RuntimeWpfArrange(root, 1280, 820); await RuntimeWpfIdleAsync();
            RuntimeWpfSaveRender(root, outputDir, "v2-workspace-loading", 1280, 820, check);

            gate.TrySetResult(true);
            await RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask").WaitAsync(TimeSpan.FromSeconds(60));
            RuntimeWpfAssert(selector.IsEnabled && selector.SelectedIndex == 0,
                check, "Completing the in-memory definition index unlocks Content search without changing Name selection");
            RuntimeWpfAssert((bool)RuntimeWpfField<object>(main, "_relationalSnapshotMemoryStarted"), check,
                "History and comparison preload begins automatically after the definition index completes");
            index = RuntimeWpfField<ScopeReferenceIndex>(main, "_referenceIndex");
            RuntimeWpfAssert(index.Resolve("Recalculate").Any(reference => reference.FilePath == targetPath && reference.LineNumber == 3) &&
                index.Resolve("current-a1").Any(reference => reference.Kind == ReferenceEntityKind.Field),
                check, "The memory index contains parsed method locations and batched captured table fields");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", new ReferenceNavigationRequestedEventArgs("Recalculate", 1, 44, 0), callerPath, null);
            RuntimeWpfAssert(RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(window => window.State.FilePath == targetPath && window.Text.Contains("public void Recalculate")),
                check, "Method navigation uses the in-memory definition and opens its physical source");
            selector.SelectedIndex = 1; RuntimeWpfNamed<TextBox>(main, "ObjectExplorerSearchTextBox").Text = "current-first";
            await RuntimeWpfInvokeTaskAsync(main, "SearchRelationalExplorerAsync");
            var hits = RuntimeWpfField<System.Collections.IDictionary>(main, "_relationalExplorerNodes").Values.Cast<ExplorerNodeSummary>().ToArray();
            RuntimeWpfAssert(hits.Any(hit => hit.IsVirtualDocument) && hits.Any(hit => hit.FullPath == callerPath),
                check, "Content search returns both the scoped SQL body and physical file containing the literal substring");
            VerifyExpandedSearchBranches(main.RootNodes, "Content");
            RuntimeWpfAssert(main.StatusText.Contains("Search complete.", StringComparison.Ordinal) &&
                !main.StatusText.Contains("index", StringComparison.OrdinalIgnoreCase) &&
                !main.StatusText.Contains("discovery", StringComparison.OrdinalIgnoreCase), check,
                "Completed V2 source searches report useful counts without obsolete SQL-index warnings");
            string partialStatus = Surf2.MainWindow.RelationalMemorySearchStatus(
                new(11, 2, 1, 8, false, true, false, PhysicalDiscoveryNotFrozen: true), hasUnloadedResources: true);
            RuntimeWpfAssert(partialStatus.Contains("Search incomplete.", StringComparison.Ordinal) &&
                partialStatus.Contains("1 resource could not be fully searched.", StringComparison.Ordinal) &&
                partialStatus.Contains("Unloaded resources excluded.", StringComparison.Ordinal), check,
                "Actual unreadable resources remain explicit in V2 status, with unloaded resources identified separately");
            StateAccessNoReads(trace.Commands, check, "V2 navigation and search avoid persisted definition-index reads", "SymbolDefinition", "SymbolOccurrence");
            RuntimeWpfAssert(trace.Commands.All(command => !command.Text.Contains("INSERT INTO SURF.INDEXDOCUMENT", StringComparison.Ordinal)),
                check, "V2 startup builds no per-file SQL index documents");
            await RuntimeWpfInvokeTaskAsync(main, "EnsureRelationalSnapshotMemoryReadyAsync", CancellationToken.None);
            var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
            RuntimeWpfAssert(runtime.IsSnapshotMemoryReady(scope) && selector.IsEnabled && footer.Visibility == Visibility.Collapsed,
                check, "Completed history preload keeps content search available and removes the loading footer");
            int beforeWarmHistoryGate = trace.Commands.Count;
            await RuntimeWpfInvokeTaskAsync(main, "EnsureRelationalSnapshotMemoryReadyAsync", CancellationToken.None);
            RuntimeWpfAssert(trace.Commands.Count == beforeWarmHistoryGate, check,
                "Opening already loaded history does not revalidate or read the database");
            RuntimeWpfArrange(root, 1280, 820); await RuntimeWpfIdleAsync();
            RuntimeWpfSaveRender(root, outputDir, "v2-workspace-ready", 1280, 820, check);
            await VerifyV2HistoryUiParityAsync(main, scope, models.Snapshots, outputDir, check);
            await fixture.VerifySourceUnchangedAsync(check);
        }
        finally
        {
            gate.TrySetResult(true);
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            try { await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync"); }
            finally
            {
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                host.RootVisual = null; root.Children.Clear(); root.Resources.MergedDictionaries.Clear(); main.Close();
            }
        }

        void VerifyExpandedSearchBranches(IEnumerable<FileSystemNode> nodes, string target)
        {
            var branches = SearchBranches(nodes).ToArray();
            RuntimeWpfAssert(branches.Length > 0 && branches.All(node => node.IsExpanded && node.IsLoaded), check,
                $"{target} search automatically expands populated result branches without loading unfiltered children");
        }

        static IEnumerable<FileSystemNode> SearchBranches(IEnumerable<FileSystemNode> nodes)
        {
            foreach (var node in nodes.Where(node => node.Children.Count > 0))
            {
                yield return node;
                foreach (var child in SearchBranches(node.Children)) yield return child;
            }
        }
    }
}

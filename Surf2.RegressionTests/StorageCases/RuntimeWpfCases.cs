using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Controls.RelationalGrid;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalGrid;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private const int RuntimeWpfRowCount = 2048;
    private const string RuntimeWpfEditedValue = "edited,\r\n\"quoted\"";

    // Register in a separate console invocation: WPF permits only one Application per process.
    // This creates/drops ONLY SqlFixture's randomly named databases; it never calls App.OnStartup.
    public static Task RunRuntimeWpfChecksAsync(Action<bool, string> check, string outputDir, bool startupOnly = false)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        if (!Path.IsPathFullyQualified(outputDir))
            throw new ArgumentException("Runtime WPF evidence requires an explicit absolute output directory.", nameof(outputDir));
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
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
                        throw new InvalidOperationException("Run runtime WPF checks in a fresh process, separately from --visual.");
                    Type log = typeof(Surf2.App).Assembly.GetType("Surf2.Services.InternalLogService", true)!;
                    if (log.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not false)
                        throw new InvalidOperationException("Runtime WPF checks require disabled logging; no user-file initialization is permitted.");
                    app = new Surf2.App();
                    app.InitializeComponent();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    Directory.CreateDirectory(directory);
                    await RuntimeWpfCheckCancellationAsync(check);
                    await RunRuntimeWpfFixtureAsync(check, directory, startupOnly);
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
        }) { IsBackground = true, Name = "Surf2 runtime offscreen WPF regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WaitForThreadExitAsync();
        async Task WaitForThreadExitAsync()
        {
            try { await completion.Task; }
            finally
            {
                if (!await Task.Run(() => thread.Join(TimeSpan.FromSeconds(30))))
                    throw new TimeoutException("The regression WPF dispatcher did not exit.");
            }
        }
    }

    private static async Task RunRuntimeWpfFixtureAsync(Action<bool, string> check, string outputDir, bool startupOnly)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_RuntimeWpf_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var state = new RelationalStateStore(session, content);
        var capture = new RelationalCaptureStore(session, new CaptureLimits
        {
            WriteBatchRows = 128, WriteBatchBytes = 256 * 1024,
            ReadBatchRows = 128, ReadBatchBytes = 128 * 1024, MaxPageRows = 128, MaxPageBytes = 128 * 1024
        });
        var settings = new AppSettings
        {
            LoadMostRecentWorkbenchOnStartup = false,
            Diagnostics = new() { EnableInternalLogging = false },
            Appearance = new() { Theme = AppearanceSettings.LightTheme }
        };
        settings.EnsureDefaults();
        settings.CodeWindows.BackcolorsByExtension.Insert(0, new()
        { Extension = ".runtime-fixture", Backcolor = "#DCEFE3", Language = CodeWindowSettings.JavaScriptLanguage });
        settings.ReferenceHighlights.Styles.RemoveAll(s => s.Language == "SQL Server" && s.Kind == ReferenceEntityKind.StoredProcedure);
        settings.ReferenceHighlights.Styles.Insert(0, new()
        {
            Language = "SQL Server", Kind = ReferenceEntityKind.StoredProcedure, Foreground = "#7F1351",
            IsBold = false, IsItalic = true, IsUnderline = true
        });
        await InTransactionAsync(session, async (c, t) =>
        {
            await state.ImportSettingsAsync(c, t, settings, Guid.NewGuid());
            await state.ImportWorkspaceAsync(c, t, new WorkspaceState { CanvasZoom = 1 }, Guid.NewGuid());
            await state.ImportScopeSelectionAsync(c, t, 2, null, Guid.NewGuid());
        });
        // Two scopes and no selected scope avoid an intentional index worker during the startup-read assertion.
        var seeded = await SeedQueryIntegrationAsync(fixture, session, content, capture, state);
        var otherScope = Required(await state.ReadScopeAsync(seeded.OtherScopeKey), "context-menu source fixture");
        otherScope.Value.Resources.Add(new()
        {
            ResourceId = "unrequested-context-menu-file", Kind = ResourceKind.File,
            Path = Path.Combine(fixture.OwnedDirectory, "unrequested-context-menu-file.sql"),
            DisplayNameOverride = "Context menu fixture", DetailsOverride = "Metadata-only unselected scope member"
        });
        await state.SaveScopeAsync(otherScope.Value, otherScope.Token, Guid.NewGuid());
        long revision = 0;
        await InTransactionAsync(session, async (c, t) =>
        {
            revision = await new RelationalSnapshotWriter(c, t, content).InsertTableDataRevisionAsync(seeded.DataResourceKey);
        });
        var data = await capture.CreateDataSetAsync(new(revision,
            [new CaptureColumnDefinition("Id"), new("Value"), new("Flag")], 123456, EvidenceTime, RuntimeWpfRowCount));
        await capture.AppendRowsAsync(data, RuntimeWpfRowsAsync(), 0);
        await capture.CompleteDataSetAsync(data);
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            await writer.SealRevisionAsync(revision);
            await writer.SetCurrentRevisionAsync(seeded.DataResourceKey, revision, 2);
            await writer.PublishSnapshotAsync(seeded.SnapshotKey, null);
        });
        var probe = await new PersistenceFormatProbe().ProbeAsync(connection);
        if (probe.Format != PersistenceFormat.Relational)
            throw new InvalidOperationException("Refusing runtime startup on anything other than the owned Ready fixture.");
        await session.RequireReadyAsync();

        if (startupOnly)
        {
            await RuntimeWpfCheckStartupResilienceAsync(fixture, session, state, seeded, connection, check);
            return;
        }

        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        FloatingSpreadsheetWindow? grid = null;
        var root = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey), DataContext = main };
        // Application resource invalidation normally visits registered Windows. This
        // detached HWND tree must also own the dictionary to receive live theme updates.
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(main.Resources);
        // A nonactivating offscreen HWND supplies real Loaded/layout/virtualization. MainWindow is never shown.
        using var host = new HwndSource(new HwndSourceParameters("Surf2 regression offscreen host")
        {
            PositionX = -32000, PositionY = -32000, Width = 1400, Height = 1000,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080
        });
        try
        {
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var startup = RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync") as Task<bool>
                    ?? throw new InvalidOperationException("Missing asynchronous relational startup hook.");
                RuntimeWpfAssert(await startup.WaitAsync(TimeSpan.FromSeconds(60)), check, "Runtime Ready startup succeeds without a migration dialog");
                RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
                StateAccessNoReads(trace.Commands, check, "Runtime startup payload exclusion", "TextContent",
                    "DatabaseObjectRevision", "TableColumnRevision", "DataValueException", "DiagramObject", "Workflow");
                long workspace = RuntimeWpfField<StateEditSession<WorkspaceState>>(main, "_relationalWorkspaceEdit").ExpectedToken.Key;
                var layoutReads = StateAccessReads(trace.Commands, "DocumentWindowState");
                RuntimeWpfAssert(layoutReads.Count > 0 && layoutReads.All(c => c.Text.Contains("TOP (@LIMIT)", StringComparison.Ordinal) &&
                    Regex.IsMatch(c.Text, @"\bWHERE\s+(?:[A-Z_][A-Z0-9_]*\.)?WORKSPACESESSIONKEY\s*=\s*@KEY\b",
                        RegexOptions.CultureInvariant) && c.Number("@Key") == workspace),
                    check, "Runtime startup reads only bounded window descriptors for the selected workspace owner");
                RuntimeWpfAssert(trace.Commands.All(c => !Regex.IsMatch(c.Text,
                    @"\b(?:BYTES|ORIGINALBASE64|INLINEBASE64|PAYLOADJSON)\b", RegexOptions.CultureInvariant)),
                    check, "Startup permits asset summaries and layout heads, never asset/base64/library payload projections");
                RuntimeWpfAssert(trace.Commands.Count != 0, check, "Runtime startup executes observed fixture SQL, not a mocked loader");
            }
            RuntimeWpfAssert(RuntimeWpfField<object>(main, "_relationalDocumentOpener") != null &&
                RuntimeWpfField<object>(main, "_relationalDocuments") != null &&
                main.RelationalGridDocumentPresenter != null, check, "Ready startup installs document access and the actual grid presenter");
            RuntimeWpfCheckEmptyLibraries(main, check);
            RuntimeWpfCheckStartupAppearance(main, connection, check);
            var mainContent = main.Content as FrameworkElement
                ?? throw new InvalidOperationException("MainWindow has no renderable content.");
            main.Content = null;
            root.Children.Add(mainContent);
            RuntimeWpfArrange(root, 1280, 820);
            host.RootVisual = root;
            await RuntimeWpfIdleAsync();
            await RuntimeWpfCheckSelectedScopeAsync(main, connection, seeded, check);
            await RuntimeWpfCheckWarmReferencesAsync(main, connection, seeded, fixture, check);

            var snapshotHeader = new DatabaseMetadataSnapshot { SnapshotId = "query-snapshot", DisplayName = "Query snapshot" };
            string codePath = DatabaseDocumentService.CreateCanonicalObjectDocumentPath(snapshotHeader, seeded.ProcedureValue);
            string dataPath = DatabaseDocumentService.CreateCanonicalTableDataDocumentPath(snapshotHeader,
                new SqlTable { SchemaName = "dbo", TableName = "Evidence" });
            using (var trace = new StateAccessSqlTrace(connection))
            {
                await RuntimeWpfInvokeTaskAsync(main, "OpenFileAsync", codePath);
                var code = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Single();
                RuntimeWpfAssert(code.State.BoundResourceKey == seeded.Procedure.ResourceKey &&
                    code.Text == seeded.ProcedureValue.Definition, check, "OpenFileAsync loads the exact selected relational definition into the real editor");
                StateAccessNoReads(trace.Commands, check, "Selected code isolation", "Asset", "DiagramObject", "TableColumnRevision", "DataValueException");
            }
            using (var trace = new StateAccessSqlTrace(connection))
            {
                try
                {
                    await RuntimeWpfInvokeTaskAsync(main, "OpenFileAsync", dataPath);
                    grid = RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single();
                    RuntimeWpfAssert(grid.UsesGridProvider && grid.State.BoundResourceKey == seeded.DataResourceKey &&
                        !grid.IsGridDisposed, check, "OpenFileAsync routes captured data to the opt-in provider constructor");
                    RuntimeWpfCheckSelectedGridSql(trace.Commands, seeded, check);
                }
                catch { RuntimeWpfLogSqlFailure(trace.Commands, check, "Selected grid isolation"); throw; }
            }
            RuntimeWpfCheckEmptyLibraries(main, check);
            IDataGridSource source = RuntimeWpfField<IDataGridSource>(grid, "_gridSource");
            var items = RuntimeWpfField<GridViewportItems>(grid, "_gridItems");
            var table = RuntimeWpfNamed<DataGrid>(grid, "SpreadsheetGrid");
            int loads = 0, unloads = 0;
            grid.Loaded += (_, _) => loads++;
            grid.Unloaded += (_, _) => unloads++;
            RuntimeWpfSetMode(main, "Tabs");
            TabItem tab = RuntimeWpfSelectGridTab(main, grid);
            RuntimeWpfArrange(root, 1280, 820);
            await RuntimeWpfWaitQueryAsync(grid);
            RuntimeWpfAssert(await source.DisplayRowCount.WaitAsync(TimeSpan.FromSeconds(60)) == RuntimeWpfRowCount &&
                source.Descriptor.ReportedRowCount == 123456 && table.Items.Count == RuntimeWpfRowCount,
                check, "Grid publishes the real dataset scroll extent separately from the reported count");
            RuntimeWpfAssert(table.Columns.Count == 3 &&
                source.Descriptor.Columns.Select(c => c.Header).SequenceEqual(new[] { "Id", "Value", "Flag" }),
                check, "Runtime grid preserves the supplied metadata header order");
            var first = await RuntimeWpfScrollAsync(grid, 0);
            RuntimeWpfAssert(first[0] == "0000" && first[1] == "viewport row 0000" && first[2] == "True",
                check, "Actual DataGrid viewport hydrates display strings without CSV or a whole row collection");
            RuntimeWpfAssert((RuntimeWpfField<ICollection>(grid, "_rows")).Count == 0,
                check, "Provider control leaves the legacy resident CSV row list empty");

            using (var trace = new StateAccessSqlTrace(connection))
            {
                var editableCell = new DataGridCellInfo(first, table.Columns[1]);
                table.SelectedCells.Clear();
                table.SelectedCells.Add(editableCell);
                table.CurrentCell = editableCell;
                RuntimeWpfAssert(table.BeginEdit(), check, "Actual DataGrid starts an editor on a hydrated cell");
                table.UpdateLayout();
                var editor = table.Columns[1].GetCellContent(first) as TextBox;
                RuntimeWpfAssert(editor != null, check, "Actual cell editing template exposes its TextBox");
                editor!.Text = RuntimeWpfEditedValue;
                RuntimeWpfAssert(RuntimeWpfInvoke(grid, "CommitGridEditor") is true,
                    check, "Grid commit hook commits the active WPF editor before operations");
                await RuntimeWpfWaitAsync(() => source.OverlayGeneration > 0, "committed sparse edit", grid);
                await RuntimeWpfWaitQueryAsync(grid, source.OverlayGeneration);
                RuntimeWpfAssert(trace.Commands.Count == 0, check, "Cell commit and edited query refresh perform no SQL writes or reads");
            }

            long editGeneration = source.OverlayGeneration;
            RuntimeWpfSetMode(main, "Canvas");
            RuntimeWpfArrange(root, 1280, 820);
            await RuntimeWpfIdleAsync();
            RuntimeWpfAssert(ReferenceEquals(RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(), grid) &&
                ReferenceEquals(RuntimeWpfField<IDataGridSource>(grid, "_gridSource"), source) &&
                !grid.IsGridDisposed && source.OverlayGeneration == editGeneration, check,
                "Canvas reparenting preserves the same provider control and sparse edits");
            RuntimeWpfSetMode(main, "Tabs");
            tab = RuntimeWpfSelectGridTab(main, grid);
            RuntimeWpfArrange(root, 1280, 820);
            first = await RuntimeWpfScrollAsync(grid, 0);
            RuntimeWpfAssert(first[1] == RuntimeWpfEditedValue && loads > 0 && unloads > 0,
                check, "Actual Loaded/Unloaded reparenting resumes the edited row without logical disposal");

            // Render the same presented control at two explicit allocations, not a replacement test grid.
            tab.Content = null;
            root.Children.Remove(mainContent);
            root.Children.Add(grid);
            var hashes = new Dictionary<(string Theme, int Width), string>();
            Brush? configuredGridBackground = table.Background;
            Brush? configuredRowBackground = table.RowBackground;
            try
            {
                foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
                foreach (var size in new[] { (Width: 960, Height: 560), (Width: 360, Height: 420) })
                {
                    AppThemeService.Apply(theme);
                    RuntimeWpfField<AppSettings>(main, "_appSettings").Appearance.Theme = theme;
                    RuntimeWpfInvoke(main, "ApplyThemeToRuntimeSurfaces");
                    root.Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey);
                    RuntimeWpfArrange(root, size.Width, size.Height);
                    await RuntimeWpfScrollAsync(grid, 0);
                    await RuntimeWpfIdleAsync();
                    root.UpdateLayout();
                    RuntimeWpfCheckThemeBrushes(grid, table, root, theme,
                        configuredGridBackground, configuredRowBackground, check);
                    using var trace = new StateAccessSqlTrace(connection);
                    var before = RuntimeWpfSaveRender(root, outputDir,
                        $"runtime-grid-{theme.ToLowerInvariant()}-{size.Width}x{size.Height}-first", size.Width, size.Height, check);
                    GridViewportRow deep = await RuntimeWpfScrollAsync(grid, 1800);
                    RuntimeWpfAssert(deep[0] == "1800" && deep[1] == "viewport row 1800", check,
                        $"{theme} {size.Width}: scrollbar seek hydrates the actual deep viewport");
                    var after = RuntimeWpfSaveRender(root, outputDir,
                        $"runtime-grid-{theme.ToLowerInvariant()}-{size.Width}x{size.Height}-deep", size.Width, size.Height, check);
                    hashes[(theme, size.Width)] = after;
                    RuntimeWpfAssert(before != after && trace.Commands.Count == 0, check,
                        $"{theme} {size.Width}: scrolling changes real rendered pixels without SQL in paint, getters or cache pages");
                    RuntimeWpfCheckViewport(grid, table, items, root, check, theme + " " + size.Width);
                }
                foreach (int width in new[] { 960, 360 })
                    RuntimeWpfAssert(hashes[(AppearanceSettings.LightTheme, width)] != hashes[(AppearanceSettings.DarkTheme, width)],
                        check, $"Runtime {width}: Light/Dark real-grid pixels differ");
            }
            finally
            {
                root.Children.Remove(grid);
                root.Children.Add(mainContent);
                tab.Content = grid;
                RuntimeWpfArrange(root, 1280, 820);
            }

            await RuntimeWpfCheckPinnedRetryAsync(grid, source, check);
            await RuntimeWpfCheckOutputAsync(grid, source, fixture.OwnedDirectory, check);
            await RuntimeWpfInvokeTaskAsync(main, "CloseSpreadsheetWindowAsync", grid);
            var retirements = RuntimeWpfField<List<Task>>(main, "_relationalGridRetirements").ToArray();
            await Task.WhenAll(retirements).WaitAsync(TimeSpan.FromSeconds(60));
            RuntimeWpfAssert(grid.IsGridDisposed && items.ResidentItems == 0 &&
                RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Count == 0,
                check, "Parent logical-close hook awaits provider disposal and clears bounded viewport items");
            await grid.DisposeGridAsync();
            await ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await using var unexpected = await source.CreateQueryAsync();
            }, check, "Closed provider rejects new sessions; disposal is idempotent");
            var raw = await capture.ReadPageAsync(data.DataSetKey, maxRows: 1, maxBytes: 128 * 1024);
            RuntimeWpfAssert(raw.Rows.Count == 1 && raw.Rows[0].RowOrdinal == 0 &&
                raw.Rows[0].Value.GetProperty("Value").GetString() == "viewport row 0000",
                check, "Runtime session edits and output do not mutate authoritative captured rows");
            await RunRuntimeStateContextMenuFlowChecksAsync(main, check);
            await RuntimeWpfCheckTypedAliasClickAsync(main, session, content, seeded, connection, root, check);
            await RuntimeWpfCheckReopenAsync(main, fixture.OwnedDirectory, root, check);
            RuntimeWpfCheckImageFilenameOwnership(main, check);
            await fixture.VerifySourceUnchangedAsync(check);
            RuntimeWpfAssert(!main.IsVisible && new WindowInteropHelper(main).Handle == IntPtr.Zero,
                check, "MainWindow remains unshown; offscreen host does not execute normal Window.Loaded startup");
            // This helper cancels the fixture's state-query lifetime, so no further UI operations follow it.
            await RunRuntimeStateStartupCancellationChecksAsync(main, check);
        }
        finally
        {
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            try
            {
                if (grid != null) await grid.DisposeGridAsync();
                await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync");
            }
            finally
            {
                // Cleanup already awaited the real relational lifecycle. Do not start async shutdown saves or prompts.
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                host.RootVisual = null;
                root.Children.Clear();
                root.Resources.MergedDictionaries.Clear();
                main.Close();
            }
        }
        await RuntimeWpfCheckStartupCloseAsync(session, connection, check);
    }

    private static async Task RuntimeWpfCheckStartupCloseAsync(RelationalSession session, string connection, Action<bool, string> check)
    {
        await session.RequireReadyAsync();
        await using var blocker = await session.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText = "SELECT Version FROM surf.ApplicationPreference WITH (TABLOCKX,HOLDLOCK) WHERE ProfileKey=1;";
        var before = await lockCommand.ExecuteScalarAsync() as byte[]
            ?? throw new InvalidOperationException("Startup-close fixture preferences were not seeded.");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        main.PersistenceConnectionChanged += (_, _) => RuntimeWpfField<RelationalRuntime>(main, "_relational").Session.Metrics.Enable();
        main.Closed += (_, _) => closed.TrySetResult(true);
        var root = new Grid { DataContext = main };
        using var host = new HwndSource(new HwndSourceParameters("Surf2 startup-close regression offscreen host")
        {
            PositionX = -32000, PositionY = -32000, Width = 1280, Height = 820,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080
        });
        var content = main.Content as FrameworkElement ?? throw new InvalidOperationException("Missing startup-close content.");
        main.Content = null;
        root.Children.Add(content);
        host.RootVisual = root;
        RuntimeWpfArrange(root, 1280, 820);
        var startupReady = RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion");
        bool rollbackCompleted = false;
        using var trace = new StateAccessSqlTrace(connection, command =>
        {
            if (Regex.IsMatch(StateAccessSql(command.CommandText), @"\b(?:FROM|JOIN)\s+SURF\.APPLICATIONPREFERENCE\b",
                RegexOptions.CultureInvariant)) entered.TrySetResult(true);
        });
        try
        {
            // Ready destination only. Exercise the real Loaded/Closing handlers, not reflected startup with a synthetic ready TCS.
            RuntimeWpfInvoke(main, "Window_Loaded", main, new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            RuntimeWpfAssert(!startupReady.Task.IsCompleted, check, "Real startup is pending on the fixture's read-only preference lock");
            main.Close();
            bool dispatcherResponsive = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => dispatcherResponsive = true, DispatcherPriority.Background);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
            var metrics = runtime.Session.Metrics.Snapshot();
            object lifetime = RuntimeWpfField<object>(main, "_relationalStartupCancellation");
            var startupToken = (CancellationToken)lifetime.GetType().GetProperty("Token", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(lifetime)!;
            RuntimeWpfAssert(dispatcherResponsive && startupReady.Task.IsCompletedSuccessfully &&
                metrics.ActiveCommands == 0 && metrics.Started > 0 && startupToken.IsCancellationRequested &&
                typeof(Surf2.MainWindow).GetField("_isPersistenceHydrated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) is false &&
                RuntimeWpfOptionalField<StateEditSession<StartupPreferences>>(main, "_relationalPreferenceEdit") == null &&
                RuntimeWpfOptionalField<StateEditSession<WorkspaceState>>(main, "_relationalWorkspaceEdit") == null &&
                RuntimeWpfOptionalField<StateEditSession<ScopeSelection>>(main, "_relationalSelectionEdit") == null,
                check, "Actual Close cancels/drains startup before the SQL lock is released, keeps the dispatcher responsive and publishes no replacement owners");
            RuntimeWpfAssert(trace.Commands.All(c => !Regex.IsMatch(c.Text,
                @"\b(?:INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE)\b", RegexOptions.CultureInvariant)),
                check, "Cancelled startup and actual Closing issue no persistence/default replacement mutation commands");
            RuntimeWpfCheckEmptyLibraries(main, check);
            await transaction.RollbackAsync();
            rollbackCompleted = true;
            await using var read = blocker.CreateCommand();
            read.CommandText = "SELECT Version FROM surf.ApplicationPreference WHERE ProfileKey=1;";
            var after = await read.ExecuteScalarAsync() as byte[];
            RuntimeWpfAssert(after != null && before.AsSpan().SequenceEqual(after), check,
                "Startup-close cancellation leaves the authoritative preference revision token unchanged");
        }
        catch { RuntimeWpfLogSqlFailure(trace.Commands, check, "Actual startup-close cancellation"); throw; }
        finally
        {
            try
            {
                if (!closed.Task.IsCompleted && !startupReady.Task.IsCompleted) main.Close();
                // Release the read-only lock on failure too, so missing cancellation cannot hang fixture cleanup.
                if (!rollbackCompleted) await transaction.RollbackAsync();
                await startupReady.Task.WaitAsync(TimeSpan.FromSeconds(60));
                if (!closed.Task.IsCompleted &&
                    typeof(Surf2.MainWindow).GetField("_shutdownRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) is not true)
                {
                    await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync");
                    RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                    main.Close();
                }
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally { host.RootVisual = null; root.Children.Clear(); }
        }
    }

    private static async Task RuntimeWpfCheckOutputAsync(FloatingSpreadsheetWindow grid, IDataGridSource source,
        string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        grid.ApplyColumnFiltersByName(new Dictionary<string, string> { ["Id"] = "0", ["Flag"] = "True" });
        var table = RuntimeWpfNamed<DataGrid>(grid, "SpreadsheetGrid");
        RuntimeWpfInvoke(grid, "GridSorting", table, new DataGridSortingEventArgs(table.Columns[0]));
        RuntimeWpfInvoke(grid, "GridSorting", table, new DataGridSortingEventArgs(table.Columns[0]));
        await RuntimeWpfWaitQueryAsync(grid, source.OverlayGeneration);
        RuntimeWpfAssert(table.Columns[0].SortDirection == ListSortDirection.Descending, check,
            "Actual Sorting handler selects stable descending display-string order");
        table.Columns[1].DisplayIndex = 0;
        table.Columns[2].Visibility = Visibility.Collapsed;
        RuntimeWpfAssert(RuntimeWpfInvoke(grid, "CommitGridEditor") is true, check,
            "Output operation commits the current editor before capturing filters and projection");
        GridQuery specification = (GridQuery)RuntimeWpfInvoke(grid, "CaptureGridQuery")!;
        var columns = (List<(int ColumnIndex, string Header)>)RuntimeWpfInvoke(grid, "GetVisibleColumnDescriptors")!;
        int[] projection = columns.Select(c => c.ColumnIndex).ToArray();
        RuntimeWpfAssert(projection.SequenceEqual(new[] { 1, 0 }), check,
            "Toolbar projection captures display order and excludes hidden columns");
        int[] expected = Enumerable.Range(0, RuntimeWpfRowCount)
            .Where(i => i % 2 == 0 && RuntimeWpfId(i).Contains('0')).OrderByDescending(i => i).ToArray();
        await using var pinned = await source.CreateQueryAsync(specification);
        var copied = await GridOutput.CopyAsync(pinned, projection);
        string[] expectedLines = new[] { "Value\tId" }.Concat(expected.Select(i =>
            RuntimeWpfQuote(i == 0 ? RuntimeWpfEditedValue : "viewport row " + RuntimeWpfId(i), '\t') + "\t" + RuntimeWpfId(i))).ToArray();
        RuntimeWpfAssert(copied.RowCount == expected.Length && copied.ColumnCount == 2 &&
            copied.Text == string.Join(Environment.NewLine, expectedLines) + Environment.NewLine &&
            copied.RowCount > 100, check,
            "Copy stream contains ALL matching rows, edited display values and exact quoting, not the resident viewport");
        check(true, "Copy envelope verified without changing the OS clipboard; native clipboard delivery is deliberately not exercised.");

        // Invoke the toolbar's post-dialog worker with an owned path; no SaveFileDialog or Clipboard call.
        string export = Path.Combine(directory, "runtime-all-matching.csv");
        object cancellation = RuntimeWpfNewCancellation();
        RuntimeWpfSetField(grid, "_gridOutputCancellation", cancellation);
        RuntimeWpfNamed<Button>(grid, "CopyGridButton").IsEnabled = false;
        RuntimeWpfNamed<Button>(grid, "ExportGridButton").IsEnabled = false;
        await RuntimeWpfInvokeTaskAsync(grid, "OutputGridAsync", specification, projection, export, cancellation);
        byte[] bytes = await File.ReadAllBytesAsync(export);
        string expectedCsv = "Value,Id" + Environment.NewLine + string.Join(Environment.NewLine, expected.Select(i =>
            RuntimeWpfQuote(i == 0 ? RuntimeWpfEditedValue : "viewport row " + RuntimeWpfId(i), ',') + "," + RuntimeWpfId(i))) + Environment.NewLine;
        RuntimeWpfAssert(bytes.AsSpan().SequenceEqual(new UTF8Encoding(false).GetBytes(expectedCsv)) &&
            RuntimeWpfNamed<TextBlock>(grid, "StatusText").Text.StartsWith("Exported ", StringComparison.Ordinal) &&
            RuntimeWpfNamed<Button>(grid, "CopyGridButton").IsEnabled &&
            RuntimeWpfNamed<Button>(grid, "ExportGridButton").IsEnabled, check,
            "Actual toolbar export worker publishes exact all-matching UTF-8 CSV and restores operation controls");

        grid.ApplyColumnFiltersByName(new Dictionary<string, string> { ["Value"] = "not-a-match" });
        await RuntimeWpfWaitQueryAsync(grid, source.OverlayGeneration);
        RuntimeWpfAssert(await RuntimeWpfField<IGridQuerySession>(grid, "_gridQuery").Completion == 0 &&
            (await GridOutput.CopyAsync(pinned, projection)).Text == copied.Text, check,
            "Interactive query replacement cannot invalidate a pinned all-matching output session");
        object cancelled = RuntimeWpfNewCancellation();
        await RuntimeWpfInvokeTaskAsync(cancelled, "Cancel");
        RuntimeWpfSetField(grid, "_gridOutputCancellation", cancelled);
        await RuntimeWpfInvokeTaskAsync(grid, "OutputGridAsync", specification, projection, export, cancelled);
        RuntimeWpfAssert((await File.ReadAllBytesAsync(export)).AsSpan().SequenceEqual(bytes) &&
            RuntimeWpfNamed<TextBlock>(grid, "StatusText").Text.StartsWith("Output canceled", StringComparison.Ordinal) &&
            !Directory.EnumerateFiles(directory).Any(p => Path.GetFileName(p).StartsWith(".surf-", StringComparison.Ordinal) &&
                p.EndsWith(".grid-pending", StringComparison.Ordinal)),
            check, "Canceled toolbar export preserves the existing destination and leaves no pending publication");
        grid.ApplyColumnFiltersByName(new Dictionary<string, string>());
    }

    private static void RuntimeWpfCheckStartupAppearance(Surf2.MainWindow main, string connection, Action<bool, string> check)
    {
        var loaded = RuntimeWpfField<AppSettings>(main, "_appSettings");
        var extension = loaded.CodeWindows.BackcolorsByExtension.SingleOrDefault(s => s.Extension == ".runtime-fixture");
        RuntimeWpfAssert(extension?.Backcolor == "#DCEFE3" && extension.Language == CodeWindowSettings.JavaScriptLanguage, check,
            "Startup loads custom extension color/language metadata before opening any document");
        var style = loaded.ReferenceHighlights.Styles.SingleOrDefault(s => s.Language == "SQL Server" &&
            s.Kind == ReferenceEntityKind.StoredProcedure);
        RuntimeWpfAssert(RuntimeWpfIsCustomStyle(style), check,
            "Startup preserves nondefault reference foreground/bold/italic/underline metadata before document opening");
        using var trace = new StateAccessSqlTrace(connection);
        for (int i = 0; i < 20; i++)
        {
            var color = RuntimeWpfInvoke(main, "GetCodeWindowBackcolor", "fixture.runtime-fixture") as SolidColorBrush;
            RuntimeWpfAssert(color?.Color == Color.FromRgb(0xDC, 0xEF, 0xE3) &&
                loaded.CodeWindows.GetLanguageForFile("fixture.runtime-fixture") == CodeWindowSettings.JavaScriptLanguage, check,
                "Startup extension rendering lookup uses the preserved in-memory metadata");
            _ = RuntimeWpfInvoke(main, "GetReferenceHighlightStylesForFile", "fixture.sql");
        }
        RuntimeWpfAssert(trace.Commands.Count == 0, check, "Startup color/language/reference paint lookups execute no SQL");
    }

    private static bool RuntimeWpfIsCustomStyle(ReferenceHighlightStyleSetting? style) => style is
        { Foreground: "#7F1351", IsBold: false, IsItalic: true, IsUnderline: true };

    private static async Task RuntimeWpfCheckSelectedScopeAsync(Surf2.MainWindow main, string connection,
        QueryIntegrationFixture seeded, Action<bool, string> check)
    {
        var selection = RuntimeWpfField<StateEditSession<ScopeSelection>>(main, "_relationalSelectionEdit");
        selection.Replace(selection.Snapshot() with { LastActiveScopeId = "query-scope" });
        await selection.SaveAsync();
        await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalLastActiveScopeAsync");
        var scope = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
        RuntimeWpfAssert(scope.ScopeId == "query-scope" && scope.Resources.Length == 3 &&
            scope.Resources.Count(r => r.Snapshot?.SnapshotKey == seeded.SnapshotKey) == 2 &&
            main.RootNodes.Count == 3 && !main.RootNodes.Any(n => n.ScopeResourceId == "excluded-scope"), check,
            "Actual selected-scope loader binds only selected metadata and publishes independent database alias roots");
        var databaseRoot = main.RootNodes.Single(n => n.ScopeResourceId == "db-a");
        RuntimeWpfAssert(!databaseRoot.IsLoaded, check, "Selected database root begins with unloaded children, not a snapshot model");
        // Indexing is an intentional source reader, distinct from startup or paint. Await its actual tracked publication.
        await RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask").WaitAsync(TimeSpan.FromSeconds(120));
        await RuntimeWpfField<Task>(main, "_relationalHighlightTask").WaitAsync(TimeSpan.FromSeconds(60));
        var coverage = RuntimeWpfField<ExplorerIndexRefreshProgress>(main, "_relationalIndexProgress");
        var catalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
        RuntimeWpfAssert(coverage.FullyPublished && coverage.Considered >= 3 && catalogue.Coverage.FullyPublished &&
            catalogue.Paint.TryGetStyle("NeedleProc", "SQL Server", out var key) && key?.Kind == ReferenceEntityKind.StoredProcedure,
            check, "Actual scoped background refresh completes discovery/publication and installs the derived reference catalogue");
        RuntimeWpfCheckEmptyLibraries(main, check);
        using (var trace = new StateAccessSqlTrace(connection))
        {
            try
            {
                await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", databaseRoot);
                RuntimeWpfAssert(databaseRoot.IsLoaded && databaseRoot.Children.Count == 5, check,
                    "Actual database-root expansion binds five metadata categories");
                var summaries = RuntimeWpfField<Dictionary<FileSystemNode, ExplorerNodeSummary>>(main, "_relationalExplorerNodes");
                var procedures = databaseRoot.Children.Single(n => summaries[n].Category == ExplorerCategory.Procedures);
                await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", procedures);
                RuntimeWpfAssert(procedures.IsLoaded && procedures.Children.Count == 1 &&
                    summaries[procedures.Children.Single()].SourceRevisionKey == seeded.Procedure.RevisionKey, check,
                    "Actual category expansion pins selected procedure metadata in the WPF explorer tree");
                RuntimeWpfCheckExpansionSql(trace.Commands, seeded.SnapshotKey, check);
            }
            catch
            {
                RuntimeWpfLogSqlFailure(trace.Commands, check, "Scoped explorer expansion");
                throw;
            }
        }
        using (var trace = new StateAccessSqlTrace(connection))
        {
            for (int i = 0; i < 100; i++)
            {
                var paint = (IReadOnlyDictionary<string, ReferenceHighlightStyleSetting>)
                    RuntimeWpfInvoke(main, "GetReferenceHighlightStylesForFile", "fixture.sql")!;
                RuntimeWpfAssert(paint.TryGetValue("NeedleProc", out var style) && RuntimeWpfIsCustomStyle(style), check,
                    "Prepared runtime reference paint lookup uses the nondefault persisted style");
            }
            RuntimeWpfAssert(trace.Commands.Count == 0, check, "Scoped prepared reference rendering lookup executes no SQL");
        }
        MethodInfo target = RuntimeWpfMethod(main, "SetObjectExplorerSearchTarget");
        RuntimeWpfInvoke(main, target.Name, Enum.Parse(target.GetParameters()[0].ParameterType, "Content"));
        RuntimeWpfNamed<TextBox>(main, "ObjectExplorerSearchTextBox").Text = "code-only-marker";
        using (var trace = new StateAccessSqlTrace(connection))
        {
            await RuntimeWpfInvokeTaskAsync(main, "SearchRelationalExplorerAsync");
            var hits = RuntimeWpfField<Dictionary<FileSystemNode, ExplorerNodeSummary>>(main, "_relationalExplorerNodes")
                .Where(p => !p.Key.IsDirectory && p.Value.SourceRevisionKey == seeded.Procedure.RevisionKey).ToArray();
            RuntimeWpfAssert(hits.Length == 2 && hits.Select(p => p.Value.ScopeResourceKey).Distinct().Count() == 2 &&
                hits.All(p => p.Key.ContentSearchPattern == "code-only-marker") &&
                main.StatusText.Contains("Search complete.", StringComparison.Ordinal) && trace.Commands.Count > 0, check,
                "Actual runtime Content search publishes both aliases with current scoped coverage, not global in-memory snapshot search");
            StateAccessNoReads(trace.Commands, check, "Scoped content search asset exclusion", "Asset");
        }
        RuntimeWpfCheckEmptyLibraries(main, check);
    }

    private static void RuntimeWpfCheckExpansionSql(IReadOnlyList<StateAccessCommand> commands, long snapshotKey,
        Action<bool, string> check)
    {
        string[] excluded = ["TextContent", "Asset", "TableColumnRevision", "DataValueException", "DiagramObject"];
        RuntimeWpfAssert(excluded.All(table => StateAccessReads(commands, table).Count == 0) &&
            commands.All(c => !Regex.IsMatch(c.Text, @"\b(?:FROM|JOIN)\s+CAPTURE\.", RegexOptions.CultureInvariant)),
            check, "Scoped explorer expansion reads no text, assets, table columns, exceptional cells, diagram objects or generated captured rows");

        // ListObjectsAsync joins immutable revisions for scalar metadata. Definitions require a separate chosen-document reader.
        var revisions = StateAccessReads(commands, "DatabaseObjectRevision");
        RuntimeWpfAssert(revisions.Count > 0 && revisions.All(c => RuntimeWpfIsNarrowObjectMetadata(c, snapshotKey)), check,
            "Scoped explorer expansion permits only bounded selected-snapshot/category scalar object-revision metadata projections");
    }

    private static void RuntimeWpfCheckSelectedGridSql(IReadOnlyList<StateAccessCommand> commands, QueryIntegrationFixture seeded,
        Action<bool, string> check)
    {
        RuntimeWpfAssert(new[] { "Asset", "DiagramObject", "TextContent" }
            .All(table => StateAccessReads(commands, table).Count == 0) &&
            commands.All(c => !Regex.IsMatch(c.Text, @"\b(?:DEFINITIONCONTENTKEY|DEFINITION)\b", RegexOptions.CultureInvariant)),
            check, "Selected grid isolation reads no assets, diagram objects or code-definition text/content references");
        var revisions = StateAccessReads(commands, "DatabaseObjectRevision");
        RuntimeWpfAssert(revisions.All(c =>
            RuntimeWpfIsNarrowObjectMetadata(c, seeded.SnapshotKey, DatabaseVersionedResourceKind.TableMetadata) ||
            RuntimeWpfIsNarrowObjectMetadata(c, seeded.SnapshotKey, DatabaseVersionedResourceKind.TableData) ||
            RuntimeWpfIsChosenTableResourceMetadata(c, seeded)), check,
            "Selected grid permits only bounded table catalogue pages or chosen table-resource scalar metadata, never unrelated code payloads");
    }

    private static bool RuntimeWpfIsNarrowObjectMetadata(StateAccessCommand command, long snapshotKey,
        DatabaseVersionedResourceKind kind = DatabaseVersionedResourceKind.StoredProcedure) =>
        command.Text.StartsWith("SELECT TOP (@TAKE) ", StringComparison.Ordinal) && command.Number("@Take") is > 0 and <= 65 &&
        command.Number("@Owner") == snapshotKey && command.Number("@Kind") == (long)kind &&
        command.Text.Contains("WHERE R.SNAPSHOTKEY=@OWNER AND (@KIND IS NULL OR R.KIND=@KIND)", StringComparison.Ordinal) &&
        command.Text.Contains("ORDER BY R.CURRENTSORTORDINAL, R.RESOURCEKEY", StringComparison.Ordinal) &&
        RuntimeWpfHasOnlyScalarObjectMetadata(command);

    private static bool RuntimeWpfIsChosenTableResourceMetadata(StateAccessCommand command, QueryIntegrationFixture seeded) =>
        command.Text.StartsWith("SELECT ", StringComparison.Ordinal) && command.Number("@Owner") == seeded.SnapshotKey &&
        (command.Number("@Resource") == seeded.DataResourceKey || command.Number("@Resource") == seeded.Table.ResourceKey) &&
        command.Text.Contains("WHERE R.RESOURCEKEY=@RESOURCE AND CHOSEN.REVISIONKEY IS NOT NULL AND RR.ISSEALED=1", StringComparison.Ordinal) &&
        RuntimeWpfHasOnlyScalarObjectMetadata(command);

    private static bool RuntimeWpfHasOnlyScalarObjectMetadata(StateAccessCommand command)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { "REVISIONKEY", "SCHEMANAME", "OBJECTNAME", "OBJECTKIND", "TYPEDESCRIPTION", "PARENTSCHEMANAME", "PARENTOBJECTNAME" };
        var members = Regex.Matches(command.Text, @"\bO\.([A-Z_][A-Z0-9_]*)\b", RegexOptions.CultureInvariant);
        return members.Count > 0 && members.Cast<Match>().All(m => allowed.Contains(m.Groups[1].Value)) &&
            !Regex.IsMatch(command.Text, @"\.\s*\*|\bSELECT\s+(?:TOP\s*\([^)]*\)\s*)?\*|\b(?:DEFINITIONCONTENTKEY|DEFINITION|TEXT|BYTES|PAYLOADJSON|SCALARTOKEN|SHAPE)\b",
                RegexOptions.CultureInvariant);
    }

    private static void RuntimeWpfLogSqlFailure(IReadOnlyList<StateAccessCommand> commands, Action<bool, string> check, string scenario)
    {
        const int commandLimit = 64, characterLimit = 96 * 1024;
        int emitted = 0, characters = 0;
        foreach (var command in commands.Take(commandLimit))
        {
            string numbers = string.Join(", ", command.Numbers.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
            string message = scenario + " fixture SQL " + (emitted + 1) + "/" + commands.Count + ": " + command.Text +
                (numbers.Length == 0 ? string.Empty : " | Numeric parameters: " + numbers);
            if (message.Length > characterLimit - characters) break;
            check(true, message); // Owned-fixture SQL and numeric parameters only; never connection or text/binary parameter values.
            characters += message.Length;
            emitted++;
        }
        if (emitted != commands.Count)
            check(true, scenario + " fixture SQL diagnostics bounded at " + commandLimit + " commands/" + characterLimit +
                " characters; " + (commands.Count - emitted) + " additional command(s) omitted.");
    }

    private static async Task RuntimeWpfCheckTypedAliasClickAsync(Surf2.MainWindow main, RelationalSession session,
        RelationalContentStore content, QueryIntegrationFixture seeded, string connection, Grid root, Action<bool, string> check)
    {
        foreach (var window in RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows"))
            await RuntimeWpfInvokeTaskAsync(main, "CloseOpenWindowAsync", window);
        var duplicateValue = new SqlDatabaseObject
        {
            SchemaName = seeded.ProcedureValue.SchemaName, ObjectName = seeded.ProcedureValue.ObjectName,
            Kind = seeded.ProcedureValue.Kind,
            Definition = "CREATE PROCEDURE [dbo].[NeedleProc] AS SELECT N'clicked-duplicate-resource-marker';\r\n"
        };
        SnapshotRevisionHandle duplicate = null!;
        // Add ambiguity only after the original startup, selected-code and captured-grid fixture checks.
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            duplicate = await writer.InsertCurrentObjectAsync(seeded.SnapshotKey, duplicateValue, 3);
            await writer.PublishSnapshotAsync(seeded.SnapshotKey, null);
        });
        RuntimeWpfNamed<TextBox>(main, "ObjectExplorerSearchTextBox").Text = string.Empty;
        await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalLastActiveScopeAsync");
        await RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask").WaitAsync(TimeSpan.FromSeconds(120));
        await RuntimeWpfField<Task>(main, "_relationalHighlightTask").WaitAsync(TimeSpan.FromSeconds(60));
        var scope = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
        var secondRoot = main.RootNodes.Single(n => n.ScopeResourceId == "db-b");
        await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", secondRoot);
        var summaries = RuntimeWpfField<Dictionary<FileSystemNode, ExplorerNodeSummary>>(main, "_relationalExplorerNodes");
        var procedures = secondRoot.Children.Single(n => summaries[n].Category == ExplorerCategory.Procedures);
        await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", procedures);
        var duplicateLeaf = procedures.Children.Single(n => summaries[n].SnapshotResourceKey == duplicate.ResourceKey);
        var originalLeaf = procedures.Children.Single(n => summaries[n].SnapshotResourceKey == seeded.Procedure.ResourceKey);
        long secondKey = scope.Resources.Single(r => r.ResourceId == "db-b").ScopeResourceKey;
        RuntimeWpfAssert(procedures.Children.Count == 2 && duplicateLeaf.Name == originalLeaf.Name &&
            summaries[duplicateLeaf].ScopeResourceKey == secondKey && summaries[originalLeaf].ScopeResourceKey == secondKey &&
            summaries[duplicateLeaf].SourceRevisionKey == duplicate.RevisionKey,
            check, "Second-alias explorer contains distinct typed occurrences for duplicate object names and exact revisions");

        try
        {
            foreach (var target in new[]
            {
                (Node: duplicateLeaf, Handle: duplicate, Definition: duplicateValue.Definition),
                (Node: originalLeaf, Handle: seeded.Procedure, Definition: seeded.ProcedureValue.Definition)
            })
            {
                var item = await RuntimeWpfExplorerItemAsync(main, root, [secondRoot, procedures, target.Node]);
                var tree = RuntimeWpfNamed<TreeView>(main, "ObjectExplorer");
                using (var trace = new StateAccessSqlTrace(connection))
                {
                    try
                    {
                        // Exercise the real XAML-wired activation handler with a real leaf OriginalSource; no direct document-open API.
                        var clicked = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = Control.PreviewMouseDoubleClickEvent, Source = item };
                        tree.RaiseEvent(clicked);
                        await RuntimeWpfWaitAsync(() => clicked.Handled &&
                            RuntimeWpfField<HashSet<string>>(main, "_openingObjectExplorerNodeKeys").Count == 0,
                            "actual second-alias explorer activation completion");
                        var opened = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows")
                            .SingleOrDefault(w => w.State.BoundResourceKey == target.Handle.ResourceKey);
                        RuntimeWpfAssert(opened != null && opened.State.BoundSnapshotKey == seeded.SnapshotKey &&
                            opened.Text == target.Definition && opened.ClipboardTextFileNameSeedProvider != null,
                            check, "Actual second-alias activation opens the clicked duplicate resource and exact definition: " + target.Handle.ResourceKey);
                        var definitions = StateAccessReads(trace.Commands, "TextContent");
                        RuntimeWpfAssert(definitions.Count > 0 && definitions.All(c => c.Number("@Revision") == target.Handle.RevisionKey),
                            check, "Explorer activation reads only the clicked immutable definition revision, not its same-name sibling");
                        RuntimeWpfAssert(new[] { "Asset", "DataSet", "DataValueException", "TableColumnRevision", "DiagramObject" }
                            .All(table => StateAccessReads(trace.Commands, table).Count == 0) &&
                            trace.Commands.All(c => !Regex.IsMatch(c.Text, @"\b(?:FROM|JOIN)\s+CAPTURE\.", RegexOptions.CultureInvariant)),
                            check, "Typed explorer code activation reads no unrelated assets, captured rows or table/diagram payloads");
                    }
                    catch { RuntimeWpfLogSqlFailure(trace.Commands, check, "Second-alias typed explorer activation"); throw; }
                }
            }
            var openedWindows = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows");
            RuntimeWpfAssert(openedWindows.Count == 2 && openedWindows.Select(w => w.State.FilePath).Distinct(StringComparer.Ordinal).Count() == 2 &&
                openedWindows.Select(w => w.State.BoundResourceKey).ToHashSet().SetEquals([seeded.Procedure.ResourceKey, duplicate.ResourceKey]),
                check, "Same-name clicked resources retain separate identity-bound editors/tabs without overwriting one another");
            using (var trace = new StateAccessSqlTrace(connection))
            {
                foreach (var window in openedWindows)
                for (int i = 0; i < 20; i++)
                    RuntimeWpfAssert(window.ClipboardTextFileNameSeedProvider?.Invoke() == "Second alias_Stored Procedures_dbo.NeedleProc",
                        check, "Actual opened editor TXT seed preserves the clicked Second alias hierarchy instead of the first matching alias");
                RuntimeWpfAssert(trace.Commands.Count == 0, check, "Actual opened-editor TXT seed lookup performs no SQL");
            }
            RuntimeWpfCheckEmptyLibraries(main, check);
        }
        finally
        {
            foreach (var window in RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows"))
                await RuntimeWpfInvokeTaskAsync(main, "CloseOpenWindowAsync", window);
        }
    }

    private static async Task<TreeViewItem> RuntimeWpfExplorerItemAsync(Surf2.MainWindow main, Grid root, FileSystemNode[] path)
    {
        ItemsControl owner = RuntimeWpfNamed<TreeView>(main, "ObjectExplorer");
        TreeViewItem? item = null;
        for (int i = 0; i < path.Length; i++)
        {
            FileSystemNode node = path[i];
            RuntimeWpfArrange(root, 1280, 820);
            owner.UpdateLayout();
            await RuntimeWpfWaitAsync(() => owner.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem,
                "real explorer item container for " + node.Name);
            item = (TreeViewItem)owner.ItemContainerGenerator.ContainerFromItem(node);
            item.BringIntoView();
            if (i < path.Length - 1) item.IsExpanded = true;
            owner = item;
            await RuntimeWpfIdleAsync();
        }
        return item ?? throw new InvalidOperationException("Explorer activation requires an actual leaf container.");
    }

    private static void RuntimeWpfCheckImageFilenameOwnership(Surf2.MainWindow main, Action<bool, string> check)
    {
        var orphan = RuntimeWpfRegisterImageOwnershipFixture(main, "unowned-image-fixture.png", retainSnapshot: false);
        var owned = RuntimeWpfRegisterImageOwnershipFixture(main, "owned-image-fixture.png", retainSnapshot: true);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        RuntimeWpfAssert(!orphan.Payload.TryGetTarget(out _) && owned.Payload.TryGetTarget(out _), check,
            "Filename image lookup does not retain orphaned byte payloads; a live undo/clipboard snapshot still owns its bytes");
        object selected = RuntimeWpfInvoke(main, "FindRelationalImagePayload", owned.Snapshot!)
            ?? throw new InvalidOperationException("Live image snapshot lost its occurrence-owned payload.");
        for (int i = 0; i < 32; i++) RuntimeWpfInvoke(main, "RegisterRelationalImageFilename", selected);
        var registry = RuntimeWpfField<IDictionary>(main, "_relationalFilenameImages");
        RuntimeWpfAssert(!registry.Contains("unowned-image-fixture.png") &&
            ReferenceEquals(selected, RuntimeWpfInvoke(main, "FindRelationalImagePayload", owned.Snapshot!)), check,
            "Image filename metadata retires dead names while exact occurrence bindings remain valid");
        GC.KeepAlive(owned.Snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object> Payload, DiagramObjectSnapshot? Snapshot) RuntimeWpfRegisterImageOwnershipFixture(
        Surf2.MainWindow main, string filename, bool retainSnapshot)
    {
        Type type = typeof(Surf2.MainWindow).GetNestedType("RelationalImagePayload", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing image ownership record.");
        byte[] bytes = [1, 2, 3, 4];
        object payload = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [filename, bytes, bytes, null], culture: null)
            ?? throw new InvalidOperationException("Image ownership fixture construction failed.");
        RuntimeWpfInvoke(main, "RegisterRelationalImageFilename", payload);
        var snapshot = retainSnapshot ? new DiagramObjectSnapshot
            { ObjectType = DiagramObjectType.Image, PastedImageFileName = filename } : null;
        var copy = snapshot == null ? null : (DiagramObjectSnapshot?)RuntimeWpfInvoke(main, "CloneRelationalDiagramSnapshot", snapshot);
        return (new(payload), copy);
    }

    private static async Task RuntimeWpfCheckPinnedRetryAsync(FloatingSpreadsheetWindow grid, IDataGridSource source,
        Action<bool, string> check)
    {
        long generation = RuntimeWpfField<IGridQuerySession>(grid, "_gridQuery").Generation;
        long overlay = source.OverlayGeneration;
        int reopenEvents = 0;
        bool requiresReopen = false;
        EventHandler reopen = (_, _) => reopenEvents++;
        EventHandler<GridReadErrorEventArgs> failed = (_, e) => requiresReopen |= e.RequiresReopen;
        grid.GridReopenRequested += reopen;
        grid.GridReadFailed += failed;
        try
        {
            // Controlled transient viewport error, not a changed-source or real SQL fault injection.
            RuntimeWpfInvoke(grid, "ShowGridReadError", new IOException("Owned regression transient viewport read error."));
            RuntimeWpfNamed<Button>(grid, "RetryGridButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var first = await RuntimeWpfScrollAsync(grid, 0);
            RuntimeWpfAssert(reopenEvents == 0 && !requiresReopen && grid.GridReadError == null &&
                ReferenceEquals(RuntimeWpfField<IDataGridSource>(grid, "_gridSource"), source) &&
                RuntimeWpfField<IGridQuerySession>(grid, "_gridQuery").Generation == generation &&
                source.OverlayGeneration == overlay && first[1] == RuntimeWpfEditedValue, check,
                "Transient pinned-capture Retry reuses its query/source and sparse edits without reopening or discard confirmation");
        }
        finally { grid.GridReopenRequested -= reopen; grid.GridReadFailed -= failed; }
    }

    private static async Task RuntimeWpfCheckReopenAsync(Surf2.MainWindow main, string directory, Grid root,
        Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "runtime-reopen.csv");
        await File.WriteAllTextAsync(path, "Id,Value\r\n0,original zero\r\n1,original one\r\n", new UTF8Encoding(false));
        var previousConfirmation = main.RelationalGridDiscardEditsConfirmation;
        FloatingSpreadsheetWindow? current = null;
        try
        {
            await RuntimeWpfInvokeTaskAsync(main, "OpenFileAsync", path);
            current = RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(w => w.State.FilePath == path);
            var original = current;
            OpenDocumentState layoutIdentity = original.State;
            IDataGridSource source = RuntimeWpfField<IDataGridSource>(original, "_gridSource");
            RuntimeWpfAssert(RuntimeWpfField<IDictionary>(main, "_relationalGridLifecycleOwners").Contains(original),
                check, "Presenter attaches the real grid lifecycle/error/reopen handlers");
            RuntimeWpfSelectGridTab(main, original);
            RuntimeWpfArrange(root, 1280, 820);
            await RuntimeWpfWaitQueryAsync(original);
            var row = await RuntimeWpfScrollAsync(original, 0);
            row[1] = "unsaved CSV session edit";
            RuntimeWpfInvoke(original, "RequestGridQuery", true);
            await RuntimeWpfWaitQueryAsync(original, source.OverlayGeneration);
            row = await RuntimeWpfScrollAsync(original, 0);
            main.RelationalGridDiscardEditsConfirmation = (_, _) =>
            {
                row[1] = "newer edit during reopen confirmation";
                return Task.FromResult(true);
            };
            await main.ReopenRelationalGridAsync(original);
            RuntimeWpfAssert(!original.IsGridDisposed &&
                ReferenceEquals(RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(), original) &&
                main.StatusText.StartsWith("The table view or edits changed", StringComparison.Ordinal), check,
                "An overlay-generation change during confirmed reopen fences prepared publication and retains the newer edit");
            RuntimeWpfInvoke(original, "RequestGridQuery", true);
            await RuntimeWpfWaitQueryAsync(original, source.OverlayGeneration);
            long edits = source.OverlayBytes, generation = source.OverlayGeneration;
            int readErrors = 0, reopenEvents = 0;
            bool requiresReopen = false;
            original.GridReadFailed += (_, error) => { readErrors++; requiresReopen |= error.RequiresReopen; };
            original.GridReopenRequested += (_, _) => reopenEvents++;
            // Only this locally created fixture CSV is changed; the original source database remains read-only.
            await File.WriteAllTextAsync(path, "Id,Value\r\n0,replacement zero\r\n1,replacement one\r\n2,new third row\r\n", new UTF8Encoding(false));
            RuntimeWpfInvoke(original, "RequestGridQuery", true);
            await RuntimeWpfWaitAsync(() => original.GridReadError != null, "actual changed CSV read failure");
            RuntimeWpfAssert(readErrors > 0 && requiresReopen && source.IsInvalidated && edits > 0 &&
                RuntimeWpfNamed<Button>(original, "RetryGridButton").Visibility == Visibility.Visible, check,
                "Actual changed-CSV query failure raises RequiresReopen and leaves an actionable Retry control");

            int confirmations = 0;
            main.RelationalGridDiscardEditsConfirmation = (_, _) => { confirmations++; return Task.FromResult(false); };
            RuntimeWpfNamed<Button>(original, "RetryGridButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RuntimeWpfWaitAsync(() => confirmations == 1 && main.StatusText.StartsWith("Reopen canceled", StringComparison.Ordinal),
                "cancelled actual Retry confirmation");
            RuntimeWpfAssert(reopenEvents == 1 && ReferenceEquals(RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(), original) &&
                !original.IsGridDisposed && source.OverlayGeneration == generation && source.OverlayBytes == edits,
                check, "Actual Retry handler confirms discard; Cancel retains the original control/source and every sparse edit");

            main.RelationalGridDiscardEditsConfirmation = (_, _) => Task.FromResult(true);
            string missing = path + ".owned-missing";
            File.Move(path, missing);
            try { await main.ReopenRelationalGridAsync(original); }
            finally { File.Move(missing, path); }
            RuntimeWpfAssert(!original.IsGridDisposed && source.OverlayBytes == edits &&
                ReferenceEquals(RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(), original) &&
                main.StatusText.StartsWith("Could not reopen table", StringComparison.Ordinal), check,
                "Confirmed reopen read failure retains the old grid and sparse edits, rather than publishing an empty replacement");

            main.RelationalGridDiscardEditsConfirmation = (_, _) =>
            {
                RuntimeWpfInvoke(main, "InvalidateRelationalDocumentContext");
                return Task.FromResult(true);
            };
            await main.ReopenRelationalGridAsync(original);
            RuntimeWpfAssert(!original.IsGridDisposed && source.OverlayBytes == edits &&
                ReferenceEquals(RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single(), original), check,
                "A document-context change during confirmation fences reopen publication and preserves edits");

            main.RelationalGridDiscardEditsConfirmation = (_, _) => Task.FromResult(true);
            await main.ReopenRelationalGridAsync(original);
            current = RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Single();
            IDataGridSource replacement = RuntimeWpfField<IDataGridSource>(current, "_gridSource");
            RuntimeWpfAssert(!ReferenceEquals(current, original) && original.IsGridDisposed &&
                ReferenceEquals(current.State, layoutIdentity) && replacement.Descriptor.SourceId != source.Descriptor.SourceId &&
                replacement.OverlayBytes == 0 && await replacement.DisplayRowCount == 3, check,
                "Confirmed reopen validates first-page/source before atomically replacing the control and retaining its layout identity");
            RuntimeWpfSelectGridTab(main, current);
            RuntimeWpfArrange(root, 1280, 820);
            var fresh = await RuntimeWpfScrollAsync(current, 0);
            RuntimeWpfAssert(fresh[1] == "replacement zero" && current.GridReadError == null &&
                RuntimeWpfField<IDictionary>(main, "_relationalGridLifecycleOwners").Contains(current) &&
                !RuntimeWpfField<IDictionary>(main, "_relationalGridLifecycleOwners").Contains(original), check,
                "Prepared replacement renders fresh source values and transfers the lifecycle handlers without stale ownership");
            RuntimeWpfCheckEmptyLibraries(main, check);
        }
        finally
        {
            main.RelationalGridDiscardEditsConfirmation = previousConfirmation;
            if (current != null)
            {
                await RuntimeWpfInvokeTaskAsync(main, "CloseSpreadsheetWindowAsync", current);
                await current.DisposeGridAsync();
            }
        }
    }

    private static string RuntimeWpfQuote(string value, char delimiter) =>
        value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static object RuntimeWpfNewCancellation(CancellationToken parent = default)
    {
        Type type = typeof(FloatingSpreadsheetWindow).Assembly.GetType("Surf2.Controls.RelationalGrid.GridCancellation", true)!;
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [parent], CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Cannot construct the grid cancellation owner.");
    }

    private static async Task RuntimeWpfCheckCancellationAsync(Action<bool, string> check)
    {
        object parent = RuntimeWpfNewCancellation();
        var token = (CancellationToken)parent.GetType().GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parent)!;
        object child = RuntimeWpfNewCancellation(token);
        var childToken = (CancellationToken)child.GetType().GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child)!;
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var callback = childToken.Register(() =>
        {
            entered.TrySetResult(Environment.CurrentManagedThreadId);
            if (!release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Fixture cancellation callback was not released.");
        });
        try
        {
            int dispatcherThread = Environment.CurrentManagedThreadId;
            Task firstParent = (Task)RuntimeWpfInvoke(parent, "Cancel")!;
            RuntimeWpfAssert(token.IsCancellationRequested, check, "Grid Cancel marks its token before returning to the dispatcher");
            int callbackThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task firstChild = (Task)RuntimeWpfInvoke(child, "Cancel")!;
            Task repeated = (Task)RuntimeWpfInvoke(child, "Cancel")!;
            Task retirement = (Task)RuntimeWpfInvoke(child, "RetireAsync")!;
            bool dispatched = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => dispatched = true, DispatcherPriority.Background);
            RuntimeWpfAssert(callbackThread != dispatcherThread && dispatched && ReferenceEquals(firstChild, repeated) &&
                !firstChild.IsCompleted && !retirement.IsCompleted, check,
                "Parent/child cancellation runs callbacks off-dispatcher, retains the FIRST task and defers disposal until callbacks finish");
            release.Set();
            await Task.WhenAll(firstParent, firstChild, retirement).WaitAsync(TimeSpan.FromSeconds(10));
            RuntimeWpfAssert(ReferenceEquals(retirement, RuntimeWpfInvoke(child, "RetireAsync")), check,
                "Cancellation-owner retirement remains idempotent after callback completion");
        }
        finally
        {
            release.Set();
            await RuntimeWpfInvokeTaskAsync(child, "RetireAsync");
            await RuntimeWpfInvokeTaskAsync(parent, "RetireAsync");
        }
    }

    private static string RuntimeWpfId(int i) => i.ToString("D4", CultureInfo.InvariantCulture);

    private static async IAsyncEnumerable<JsonElement> RuntimeWpfRowsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < RuntimeWpfRowCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JsonSerializer.SerializeToElement(new { Id = RuntimeWpfId(i), Value = "viewport row " + RuntimeWpfId(i), Flag = i % 2 == 0 });
            if (i % 128 == 0) await Task.Yield();
        }
    }

    private static async Task RuntimeWpfWaitQueryAsync(FloatingSpreadsheetWindow grid, long? overlay = null)
    {
        await RuntimeWpfWaitAsync(() => RuntimeWpfOptionalField<IGridQuerySession>(grid, "_gridQuery") is { } query &&
            query.Completion.IsCompleted && RuntimeWpfField<Task>(grid, "_gridQueryWork").IsCompleted &&
            (overlay == null || query.OverlayGeneration == overlay), "query completion", grid);
        var current = RuntimeWpfField<IGridQuerySession>(grid, "_gridQuery");
        await current.Completion;
        RuntimeWpfInvoke(grid, "PollGridCount");
        await RuntimeWpfIdleAsync();
    }

    private static async Task<GridViewportRow> RuntimeWpfScrollAsync(FloatingSpreadsheetWindow grid, int position)
    {
        var table = RuntimeWpfNamed<DataGrid>(grid, "SpreadsheetGrid");
        table.UpdateLayout();
        var scroll = RuntimeWpfVisuals<ScrollViewer>(table).FirstOrDefault()
            ?? throw new InvalidOperationException("Actual DataGrid has no scrolling viewport.");
        scroll.ScrollToVerticalOffset(position);
        table.UpdateLayout();
        RuntimeWpfInvoke(grid, "QueueGridViewport");
        await RuntimeWpfWaitAsync(() => table.ItemContainerGenerator.ContainerFromIndex(position) is DataGridRow
            { Item: GridViewportRow { IsLoaded: true } row } && row.Position == position,
            "hydrated visible row " + position, grid);
        return (GridViewportRow)((DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(position)).Item;
    }

    private static async Task RuntimeWpfWaitAsync(Func<bool> condition, string name, FloatingSpreadsheetWindow? grid = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (grid?.GridReadError is { } error) throw new InvalidOperationException("Runtime grid read failed while waiting for " + name, error);
            if (clock.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("Runtime offscreen fixture timed out waiting for " + name + ".");
            await Task.Delay(25);
            grid?.UpdateLayout();
        }
    }

    private static async Task RuntimeWpfIdleAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(50);
    }

    private static void RuntimeWpfArrange(Grid root, int width, int height)
    {
        root.Width = width; root.Height = height;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void RuntimeWpfCheckThemeBrushes(FloatingSpreadsheetWindow grid, DataGrid table, Grid root,
        string theme, Brush? configuredGridBackground, Brush? configuredRowBackground, Action<bool, string> check)
    {
        static Color BrushColor(Brush? brush) => brush is SolidColorBrush solid ? solid.Color
            : throw new InvalidOperationException("Runtime theme evidence requires a solid resource/control brush.");
        bool dark = theme == AppearanceSettings.DarkTheme;
        Color surfaceAlt = BrushColor(AppThemeService.GetBrush(AppThemeService.SurfaceAltBrushKey));
        Color text = BrushColor(AppThemeService.GetBrush(AppThemeService.TextBrushKey));
        Color expectedSurfaceAlt = (Color)ColorConverter.ConvertFromString(dark ? "#273244" : "#F8FAFC");
        Color expectedText = (Color)ColorConverter.ConvertFromString(dark ? "#F9FAFB" : "#111827");
        RuntimeWpfAssert(AppThemeService.CurrentTheme == theme && surfaceAlt == expectedSurfaceAlt && text == expectedText &&
            BrushColor(root.Background) == BrushColor(AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey)),
            check, theme + ": the actual application palette and offscreen root use the requested theme");
        var headers = RuntimeWpfVisuals<DataGridColumnHeader>(table).Where(h => h.IsVisible && h.Column != null).ToArray();
        RuntimeWpfAssert(headers.Length > 0 && headers.All(h => BrushColor(h.Background) == surfaceAlt && BrushColor(h.Foreground) == text) &&
            BrushColor(RuntimeWpfNamed<Border>(grid, "FooterBorder").Background) == surfaceAlt &&
            BrushColor(RuntimeWpfNamed<Border>(grid, "LoadingOverlay").Background) == surfaceAlt &&
            BrushColor(RuntimeWpfNamed<TextBlock>(grid, "StatusText").Foreground) ==
                BrushColor(AppThemeService.GetBrush(AppThemeService.SubtleTextBrushKey)) &&
            BrushColor(RuntimeWpfNamed<Grid>(grid, "HeaderBar").Background) ==
                BrushColor(AppThemeService.GetBrush(AppThemeService.PanelBrushKey)) &&
            BrushColor(RuntimeWpfNamed<TextBlock>(grid, "TitleText").Foreground) == text &&
            BrushColor(RuntimeWpfNamed<Border>(grid, "OuterBorder").Background) ==
                BrushColor(AppThemeService.GetBrush(AppThemeService.SurfaceBrushKey)),
            check, theme + ": real header/footer/loading chrome resolves current theme brushes and contrast");
        RuntimeWpfAssert(ReferenceEquals(table.Background, configuredGridBackground) && ReferenceEquals(table.RowBackground, configuredRowBackground),
            check, theme + ": changing chrome theme preserves the configured grid/row background palette");
    }

    private static string RuntimeWpfSaveRender(Grid root, string directory, string name, int width, int height,
        Action<bool, string> check)
    {
        string path = Path.Combine(directory, name + ".png");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Runtime PNG escaped its explicit directory.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Color background = (root.Background as SolidColorBrush)?.Color
            ?? throw new InvalidOperationException("Runtime evidence requires an opaque theme background.");
        long opaque = 0, different = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255) opaque++;
            if (pixels[i + 3] != 0 && (Math.Abs(pixels[i] - background.B) > 2 ||
                Math.Abs(pixels[i + 1] - background.G) > 2 || Math.Abs(pixels[i + 2] - background.R) > 2)) different++;
        }
        string hash = Convert.ToHexString(SHA256.HashData(pixels));
        RuntimeWpfAssert(opaque >= (long)width * height * 95 / 100 && different > (long)width * height / 100,
            check, name + ": real grid CPU pixels are opaque and nonblank");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) encoder.Save(file);
        check(true, $"Offscreen runtime grid PNG: {path}; {width}x{height}; opaque={opaque}; nonbackground={different}; sha256={hash}. " +
            "Actual presented control at an explicit allocation, not a native MainWindow screenshot.");
        return hash;
    }

    private static void RuntimeWpfCheckViewport(FloatingSpreadsheetWindow grid, DataGrid table,
        GridViewportItems items, Grid root, Action<bool, string> check, string scenario)
    {
        var rows = RuntimeWpfVisuals<DataGridRow>(table).Where(r => r.IsVisible).ToArray();
        RuntimeWpfAssert(rows.Length > 0 && rows.Length < items.Limits.MaxItems &&
            items.ResidentItems <= items.Limits.MaxItems && items.ResidentBytes <= items.Limits.MaxBytes &&
            items.ResidentItems < RuntimeWpfRowCount / 2, check,
            scenario + ": actual row containers and requested item/byte residency stay bounded independently of dataset count");
        var viewport = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        foreach (string name in new[] { "SpreadsheetGrid", "StatusText", "CopyGridButton", "ExportGridButton" })
        {
            var element = RuntimeWpfNamed<FrameworkElement>(grid, name);
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));
            RuntimeWpfAssert(MigrationContains(viewport, bounds, 1), check, scenario + ": " + name + " fits the explicit viewport");
        }
        RuntimeWpfAssert(RuntimeWpfVisuals<TextBlock>(table).Any(t => t.IsVisible && t.Text == "viewport row 1800") &&
            RuntimeWpfNamed<Border>(grid, "LoadingOverlay").Visibility == Visibility.Collapsed && grid.GridReadError == null,
            check, scenario + ": real cell binding renders the expected deep-row text, with no loading/error overlay");
    }

    private static void RuntimeWpfCheckEmptyLibraries(Surf2.MainWindow main, Action<bool, string> check)
    {
        foreach (var entry in new (string Field, string Collection)[]
        {
            (Field: "_scopeLibrary", Collection: "Scopes"), ("_databaseSnapshots", "Snapshots"),
            ("_databaseSnapshots", "Histories"), ("_diagramLibrary", "Diagrams"), ("_workbenchLibrary", "Workbenches")
        })
        {
            object owner = RuntimeWpfField<object>(main, entry.Field);
            var collection = owner.GetType().GetProperty(entry.Collection)?.GetValue(owner) as ICollection
                ?? throw new InvalidOperationException("Missing runtime library collection: " + entry.Field + "." + entry.Collection);
            RuntimeWpfAssert(collection.Count == 0, check, "Runtime leaves legacy full library empty: " + entry.Field + "." + entry.Collection);
        }
    }

    private static TabItem RuntimeWpfSelectGridTab(Surf2.MainWindow main, FloatingSpreadsheetWindow grid)
    {
        var tabs = RuntimeWpfNamed<TabControl>(main, "CodeDocumentsTabControl");
        var tab = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Tag, grid.State.FilePath));
        tabs.SelectedItem = tab;
        return tab;
    }

    private static void RuntimeWpfSetMode(Surf2.MainWindow main, string mode)
    {
        MethodInfo method = RuntimeWpfMethod(main, "SetCodeViewMode");
        RuntimeWpfInvoke(main, method.Name, Enum.Parse(method.GetParameters()[0].ParameterType, mode));
    }

    private static List<T> RuntimeWpfWindows<T>(object main, string name) where T : class =>
        RuntimeWpfField<IDictionary>(main, name).Values.Cast<T>().ToList();

    private static T RuntimeWpfNamed<T>(FrameworkElement owner, string name) where T : FrameworkElement =>
        owner.FindName(name) as T ?? throw new InvalidOperationException("Missing runtime control: " + name);

    private static IEnumerable<T> RuntimeWpfVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (T descendant in RuntimeWpfVisuals<T>(child)) yield return descendant;
        }
    }

    private static T RuntimeWpfField<T>(object owner, string name) where T : class =>
        RuntimeWpfOptionalField<T>(owner, name) ?? throw new InvalidOperationException("Missing initialized runtime field: " + name);

    private static T? RuntimeWpfOptionalField<T>(object owner, string name) where T : class =>
        (owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing runtime field: " + name)).GetValue(owner) as T;

    private static void RuntimeWpfSetField(object owner, string name, object value) =>
        (owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing runtime field: " + name)).SetValue(owner, value);

    private static MethodInfo RuntimeWpfMethod(object owner, string name) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("Missing runtime integration hook: " + name);

    private static object? RuntimeWpfInvoke(object owner, string name, params object?[] supplied)
    {
        var method = RuntimeWpfMethod(owner, name);
        var parameters = method.GetParameters();
        if (supplied.Length > parameters.Length) throw new ArgumentException("Too many runtime hook arguments.");
        object?[] values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            values[i] = i < supplied.Length ? supplied[i] : parameters[i].HasDefaultValue ? parameters[i].DefaultValue
                : throw new InvalidOperationException("Missing required runtime hook argument: " + name + "." + parameters[i].Name);
            if (i >= supplied.Length && values[i] == null && parameters[i].ParameterType.IsValueType)
                values[i] = Activator.CreateInstance(parameters[i].ParameterType);
        }
        try { return method.Invoke(owner, values); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static async Task RuntimeWpfInvokeTaskAsync(object owner, string name, params object?[] arguments)
    {
        var task = RuntimeWpfInvoke(owner, name, arguments) as Task
            ?? throw new InvalidOperationException("Runtime hook is no longer asynchronous: " + name);
        await task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void RuntimeWpfAssert(bool condition, Action<bool, string> check, string name)
    {
        check(condition, name);
        if (!condition) throw new InvalidOperationException(name);
    }
}

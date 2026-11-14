using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using Surf2;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.LiveSql;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.SourceResources;
using Surf2.Storage;
using Surf2.Storage.Relational.Index;

public static partial class StorageRegressionSuite
{
    public static Task RunSqlQueryWindowChecksAsync(Action<bool, string> check, string outputDir)
    {
        if (!Path.IsPathFullyQualified(outputDir)) throw new ArgumentException("Supply an absolute render directory.", nameof(outputDir));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                App? app = null; Exception? failure = null;
                try
                {
                    if (Application.Current != null) throw new InvalidOperationException("Run --sql-query-ui in a fresh process.");
                    var log = typeof(App).Assembly.GetType("Surf2.Services.InternalLogService", true)!;
                    if (log.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not false)
                        throw new InvalidOperationException("Query UI checks require disabled fixture logging.");
                    app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    Directory.CreateDirectory(outputDir);
                    await VerifySqlQueryWindowAsync(check, Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir)));
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { app?.Shutdown(); } catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                    if (failure == null) completion.TrySetResult(); else completion.TrySetException(failure);
                }
            }));
            try { Dispatcher.Run(); } catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Surf2 query window offscreen regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return JoinAsync();
        async Task JoinAsync()
        {
            try { await completion.Task; }
            finally
            {
                if (!await Task.Run(() => thread.Join(TimeSpan.FromSeconds(30))))
                    throw new TimeoutException("Query UI fixture dispatcher did not exit.");
            }
        }
    }

    private static async Task VerifySqlQueryWindowAsync(Action<bool, string> check, string outputDir)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await fixture.DestinationSqlAsync("""
            CREATE TABLE dbo.[Query ]] Items](Id int NOT NULL PRIMARY KEY,RowVersion rowversion NOT NULL,Title nvarchar(100) NULL);
            ;WITH n AS(SELECT 1 AS Id UNION ALL SELECT Id+1 FROM n WHERE Id<1005)
            INSERT dbo.[Query ]] Items](Id,Title) SELECT Id,CASE WHEN Id=2 THEN NULL ELSE N'Item '+CONVERT(nvarchar(12),Id)+N' — 測試' END FROM n
            OPTION(MAXRECURSION 0);
            """);
        await fixture.DestinationSqlAsync("CREATE VIEW dbo.[Query View] AS SELECT Id,RowVersion,Title FROM dbo.[Query ]] Items] WHERE Id<=3;");
        string expectedRowVersion;
        await using (var connection = new SqlConnection(fixture.DestinationConnectionString))
        {
            await connection.OpenAsync();
            using var command = new SqlCommand("SELECT RowVersion FROM dbo.[Query ]] Items] WHERE Id=1;", connection);
            expectedRowVersion = "0x" + Convert.ToHexString((byte[])(await command.ExecuteScalarAsync())!);
        }
        var service = new LiveSqlService(fixture.DestinationConnectionString);
        var qualification = await service.QualifyAsync(); qualification.RequireQualified();
        AppThemeService.Apply(AppearanceSettings.LightTheme);
        var window = new SqlQueryWindow(service);
        var root = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey) };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 query UI offscreen host")
        { PositionX = -32000, PositionY = -32000, Width = 1150, Height = 780,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        try
        {
            var content = (FrameworkElement)window.Content; window.Content = null; root.Children.Add(content); host.RootVisual = root;
            RuntimeWpfArrange(root, 1150, 780); await RuntimeWpfIdleAsync();
            var editor = RuntimeWpfNamed<TextBox>(window, "SqlEditor");
            var execute = RuntimeWpfNamed<Button>(window, "ExecuteButton");
            var cancel = RuntimeWpfNamed<Button>(window, "CancelQueryButton");
            var results = RuntimeWpfNamed<DataGrid>(window, "ResultsGrid");
            var status = RuntimeWpfNamed<TextBlock>(window, "QueryStatusTextBlock");
            var parameters = RuntimeWpfNamed<Expander>(window, "ParametersExpander");
            var splitter = RuntimeWpfNamed<GridSplitter>(window, "QueryResultsSplitter");
            check(editor.Text.Length == 0 && results.Items.Count == 0 && window.CurrentOperation.IsCompleted,
                "Database Query starts with an editable blank query and no automatic execution");
            check(Equals(execute.Content, "Execute") && editor.AcceptsReturn && editor.AcceptsTab && !parameters.IsExpanded && results.IsReadOnly,
                "Query form exposes Execute, multiline SQL, optional parameters and read-only results");
            check(editor.ActualHeight > 100 && results.ActualHeight > 150 && splitter.ActualHeight > 0 &&
                editor.TransformToAncestor(root).Transform(new Point()).Y < splitter.TransformToAncestor(root).Transform(new Point()).Y &&
                splitter.TransformToAncestor(root).Transform(new Point()).Y < results.TransformToAncestor(root).Transform(new Point()).Y,
                "SQL editor and results occupy resizable sections above and below the splitter");
            check(SqlQueryWindow.InitialQuery("schema]quoted", "object]quoted") == "SELECT TOP (1000) *\nFROM [schema]]quoted].[object]]quoted];",
                "Table/view query template quotes identifiers safely and selects at most 1000 rows");

            editor.Text = SqlQueryWindow.InitialQuery("dbo", "Query ] Items");
            await ExecuteAsync();
            var firstResult = RuntimeWpfField<LiveSqlResult>(window, "_result");
            check(firstResult.RowCount == 1000 && results.Columns.Count == 3 && results.Items.Count == 100 && execute.IsEnabled,
                "Execute runs the table TOP1000 query and displays the first page below the editor");
            check(results.Items.Cast<IReadOnlyList<string>>().Any(row => row[0] == "1" && row[1] == expectedRowVersion),
                "Table query displays the exact rowversion bytes as hexadecimal");
            check(results.Items.Cast<IReadOnlyList<string>>().Any(row => row[0] == "2" && row[2] == "NULL") &&
                results.Items.Cast<IReadOnlyList<string>>().Any(row => row[2].Contains("測試", StringComparison.Ordinal)),
                "Results preserve visible NULL and Unicode values");

            editor.Text = SqlQueryWindow.InitialQuery("dbo", "Query View");
            await ExecuteAsync();
            var viewResult = RuntimeWpfField<LiveSqlResult>(window, "_result");
            check(viewResult.RowCount == 3 && results.Items.Count == 3 && editor.Text.Contains("[dbo].[Query View]", StringComparison.Ordinal),
                "A view can be queried through the same editable TOP1000 form");
            check(results.Items.Cast<IReadOnlyList<string>>().Any(row => row[0] == "1" && row[1] == expectedRowVersion),
                "View query preserves rowversion bytes and subsequent columns");
            RuntimeWpfArrange(root, 1150, 780); await RuntimeWpfIdleAsync();
            RuntimeWpfSaveRender(root, outputDir, "sql-query-results", 1150, 780, check);
            AppThemeService.Apply(AppearanceSettings.DarkTheme);
            root.Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey);
            RuntimeWpfArrange(root, 1150, 780); await RuntimeWpfIdleAsync();
            RuntimeWpfSaveRender(root, outputDir, "sql-query-results-dark", 1150, 780, check);
            AppThemeService.Apply(AppearanceSettings.LightTheme);
            root.Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey);

            var previousItems = results.ItemsSource;
            editor.Text = "SELECT MissingColumn FROM [dbo].[Query ]] Items];";
            await ExecuteAsync();
            check(ReferenceEquals(previousItems, results.ItemsSource) && ReferenceEquals(viewResult, RuntimeWpfField<LiveSqlResult>(window, "_result")) &&
                status.Text.Length > 0 && execute.IsEnabled,
                "SQL execution errors retain the previous result and allow correction and retry");

            await using var blocker = new SqlConnection(fixture.DestinationConnectionString); await blocker.OpenAsync();
            using var transaction = blocker.BeginTransaction();
            using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "UPDATE dbo.[Query ]] Items] WITH(TABLOCKX) SET Title=Title WHERE Id=1;";
                await command.ExecuteNonQueryAsync();
            }
            editor.Text = SqlQueryWindow.InitialQuery("dbo", "Query ] Items");
            execute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!window.CurrentOperation.IsCompleted && !execute.IsEnabled && cancel.IsEnabled,
                "Executing a query exposes cancellation and prevents overlapping executions");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(15));
            check(ReferenceEquals(previousItems, results.ItemsSource) && execute.IsEnabled && status.Text.Contains("cancel", StringComparison.OrdinalIgnoreCase),
                "Cancelling a blocked query retains the previous result and re-enables Execute");
            transaction.Rollback();

            await VerifySqlQueryExplorerMenusAsync(fixture, service, qualification, window, check);
            check(window.CloseCompletion.IsCompletedSuccessfully, "Unloading a direct source closes and drains its query window");
        }
        finally
        {
            window.Close(); await window.CloseCompletion.WaitAsync(TimeSpan.FromSeconds(30));
            host.RootVisual = null; root.Children.Clear(); root.Resources.MergedDictionaries.Clear();
        }
        await fixture.VerifySourceUnchangedAsync(check);

        async Task ExecuteAsync()
        {
            RuntimeWpfNamed<Button>(window, "ExecuteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(60));
            await RuntimeWpfIdleAsync();
        }
    }

    private static async Task VerifySqlQueryExplorerMenusAsync(SqlFixture fixture, LiveSqlService service,
        SqlAccessQualification qualification, SqlQueryWindow query, Action<bool, string> check)
    {
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        var main = new MainWindow(runtime.Options); RuntimeWpfSetField(main, "_relational", runtime);
        var selections = typeof(ResourceWorkspace).GetField("_selection", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previousSelections = selections.GetValue(null); selections.SetValue(null, new Dictionary<Guid, Guid>());
        var live = new ScopedResource { Kind = ResourceKind.LiveDatabase, Path = "sql://query-fixture", DisplayNameOverride = "Query fixture",
            Source = new() { Provider = SourceProvider.LiveSql, Endpoint = service.Server, DatabaseName = service.Database } };
        var snapshot = new ScopedResource { Kind = ResourceKind.DatabaseSnapshot, Path = "snapshot://query-fixture", DisplayNameOverride = "Snapshot" };
        var model = new Scope { Name = "Query menus", Resources = [live, snapshot] };
        ResourceWorkspace.Register(model); ResourceWorkspace.Admit(live); ResourceWorkspace.Admit(snapshot);
        LiveSourceWorkspace.Register(live, service, qualification);
        var resources = model.Resources.Select((member, index) => new ExplorerResource(index + 1, index, member.ResourceId,
            member.Kind, member.Path, member.DisplayName, true, true, null, null, null, null, false)
        { PublicId = member.PublicId, ContainerId = member.ContainerId, Source = member.Source }).ToImmutableArray();
        var scope = new ExplorerScope(new IndexRequestContext(runtime.Session.Epoch, 1, 1, "scope", "snapshot", "diagram", true, [], [], false),
            model.ScopeId, model.Name, resources, [], "en-IE");
        RuntimeWpfSetField(main, "_activeScope", model); RuntimeWpfSetField(main, "_relationalExplorerScope", scope);
        try
        {
            var root = Summary(live, 1, ExplorerNodeRole.Resource, null, true);
            var table = Summary(live, 1, ExplorerNodeRole.LiveDatabaseDocument, ExplorerCategory.Tables);
            var view = Summary(live, 1, ExplorerNodeRole.LiveDatabaseDocument, ExplorerCategory.Views);
            foreach (var (label, summary) in new[] { ("database", root), ("table", table), ("view", view) })
                check(QueryItem(summary) is { IsEnabled: true }, "Direct " + label + " context menu offers Query");
            check(QueryItem(Summary(live, 1, ExplorerNodeRole.LiveDatabaseDocument, ExplorerCategory.Procedures)) == null &&
                QueryItem(Summary(snapshot, 2, ExplorerNodeRole.Resource, null, true)) == null,
                "Query is not offered on snapshot resources or stored-procedure nodes");
            ResourceWorkspace.Block(live);
            check(QueryItem(root with { IsScopeResourceLoaded = false }) is { IsEnabled: false } &&
                QueryItem(table) is { IsEnabled: false }, "Unavailable live sources cannot open query windows from stale menus");
            ResourceWorkspace.Admit(live);
            var result = RuntimeWpfField<LiveSqlResult>(query, "_result");
            string scratchPath = RuntimeWpfField<FileStream>(result, "_file").Name;
            RuntimeWpfInvoke(main, "TrackSourceWindow", live, query);
            RuntimeWpfInvoke(main, "CloseResourceViews", live);
            await query.CloseCompletion.WaitAsync(TimeSpan.FromSeconds(30));
            check(!File.Exists(scratchPath), "Closing a source query releases its temporary result file");
        }
        finally
        {
            foreach (var member in model.Resources) { RuntimeWpfInvoke(main, "CloseResourceViews", member); ResourceWorkspace.Block(member, release: true); }
            ResourceWorkspace.ReleaseExcept(); LiveSourceWorkspace.ReleaseExcept(); selections.SetValue(null, previousSelections);
            await RuntimeWpfInvokeTaskAsync(main, "DrainRelationalStateQueriesAsync");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            RuntimeWpfSetField(main, "_shutdownSaveCompleted", true); main.Close();
        }

        MenuItem? QueryItem(ExplorerNodeSummary summary)
        {
            var node = (FileSystemNode)RuntimeWpfInvoke(main, "CreateRelationalExplorerNode", summary)!;
            var menu = (ContextMenu?)RuntimeWpfInvoke(main, "CreateRelationalObjectExplorerContextMenu", node);
            return menu?.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "Query"));
        }
        static ExplorerNodeSummary Summary(ScopedResource member, long key, ExplorerNodeRole role, ExplorerCategory? category, bool root = false) =>
            new("query:" + Guid.NewGuid().ToString("N"), member.DisplayName, member.DisplayName, member.Path, member.Path,
                FileSystemNode.RootParentKey, FileSystemNode.RootParentKey, role, member.Kind, FileSystemNodeIconKind.LiveDatabase,
                ExplorerAvailability.Present, root, !root, root, true, ExplorerChildrenState.Loaded, key, member.ResourceId,
                Category: category, SchemaName: "dbo", ObjectName: category == ExplorerCategory.Views ? "Query View" : "Query ] Items");
    }
}

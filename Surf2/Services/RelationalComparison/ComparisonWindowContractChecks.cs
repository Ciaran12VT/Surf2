using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Services.RelationalComparison;

// Run in a separate test process. Owns one STA dispatcher and no Application.
public static class ComparisonWindowContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(string? appXamlPath = null)
    {
        if (Application.Current != null)
            throw new InvalidOperationException("Comparison window checks require a process without a shared WPF Application.");
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completion.SetResult(await CheckWindowsAsync(appXamlPath)); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Surf comparison lifecycle checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { return await completion.Task.ConfigureAwait(false); }
        finally { await Task.Run(thread.Join).ConfigureAwait(false); }
    }

    private static async Task<IReadOnlyList<string>> CheckWindowsAsync(string? appXamlPath)
    {
        var passed = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "Surf2ComparisonWindowChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stores = new List<ComparisonResultStore>();
        var sessions = new List<RelationalComparisonResultSession>();
        var leftTable = Resource("Current rows", "Table Data", ComparisonResourceKind.TableData);
        var rightTable = Resource("Saved rows", "Table Data", ComparisonResourceKind.TableData);
        var leftCollection = Resource("Current database", "Database", ComparisonResourceKind.DatabaseSnapshot, collection: true);
        var rightCollection = Resource("Saved version", "Database Version", ComparisonResourceKind.DatabaseSnapshot, collection: true);
        var rebuildStarted = Signal();
        var releaseRebuild = new TaskCompletionSource<ComparisonResultStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationStarted = Signal();
        var callbackStarted = Signal();
        var releaseOperation = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        try
        {
            Check(Application.Current == null, "WPF checks create no shared Application");
            var old = await ResultAsync("old");
            var replacement = await ResultAsync("stale");
            CancellationToken rebuildToken = default;
            var retired = new RelationalComparisonResultSession(old, leftCollection, rightCollection,
                (_, _) => Task.CompletedTask,
                (_, ct) =>
                {
                    rebuildToken = ct; rebuildStarted.TrySetResult();
                    return releaseRebuild.Task; // Deliberately ignores cancellation like a late provider.
                }, new());
            sessions.Add(retired);
            ((ResourceCollectionDiffWindow)retired.View).IgnoreWhitespaceCheckBox.IsChecked = true;
            Start(retired, Method(retired, "RebuildAsync").CreateDelegate<Func<CancellationToken, Task>>(retired));
            await rebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long generation = Field<long>(retired, "_generation");
            Task retirement = retired.CloseAndDisposeAsync();
            Check(Field<bool>(retired, "_closing") && Field<long>(retired, "_generation") > generation && rebuildToken.IsCancellationRequested,
                "Closing synchronously generation-fences and cancels delayed comparison work");
            Check(!retirement.IsCompleted, "Window retirement waits for a provider that ignores cancellation");
            var freshResult = await ResultAsync("fresh");
            var fresh = new RelationalComparisonResultSession(freshResult, leftTable, rightTable, ["Id"]);
            sessions.Add(fresh);
            foreach (string field in new[] { "_previous", "_next" })
                Check(DependencyPropertyHelper.GetValueSource(Field<Button>(fresh, field), Control.StyleProperty).IsExpression,
                    "Comparison paging resolves its Surf style dynamically: " + field);
            if (appXamlPath != null)
            {
                var styles = ReadButtonStyles(appXamlPath);
                fresh.View.Resources.MergedDictionaries.Add(styles);
                foreach (string field in new[] { "_previous", "_next" })
                {
                    var button = Field<Button>(fresh, field);
                    Check(ReferenceEquals(button.Style, styles["SurfPrimaryButtonStyle"]) && button.Foreground is SolidColorBrush foreground &&
                        foreground.Color == Colors.White && button.Background is SolidColorBrush background &&
                        Contrast(foreground.Color, background.Color) >= 4.5,
                        "Actual Surf paging style uses readable white text: " + field);
                }
            }
            releaseRebuild.TrySetResult(replacement);
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            Check(ReferenceEquals(Field<ComparisonResultStore>(retired, "_result"), old) &&
                Field<Selector>(retired, "_rows").ItemsSource == null,
                "Late rebuilt results are never bound to a retired WPF window");
            await DisposedAsync(old, "Retired window disposes its original spool");
            await DisposedAsync(replacement, "Retired window disposes the late replacement spool");
            Check(!Field<bool>(fresh, "_closing") && (await freshResult.ReadPageAsync()).Rows.Single().Key == "fresh" &&
                Directory.EnumerateDirectories(root).Count() == 1,
                "Completing old retirement leaves a subsequent new-context window and spool intact");
            var table = (TableDataDiffWindow)fresh.View;
            Check(table.Title == "Compare Current rows and Saved rows" &&
                table.HeaderText.Text == "Table data: Current rows  <->  Saved rows" &&
                table.RowsDataGrid.Columns.Select(column => column.Header as string)
                    .SequenceEqual(new[] { "Key (Id)", "Status" }) &&
                table.RightRowsDataGrid.Columns.Select(column => column.Header as string)
                    .SequenceEqual(new[] { "Key (Id)", "Status" }),
                "Paired table results display the selected key heading and status on both sides");
            await RefreshAsync(fresh);
            Check(table.StatusTextBlock.Text == "Key: Id. 1 identical, 0 altered, 0 added, 0 removed." &&
                ReferenceEquals(table.RowsDataGrid.ItemsSource, table.RightRowsDataGrid.ItemsSource) &&
                Field<StackPanel>(fresh, "_paging").Visibility == Visibility.Collapsed && table.FindPanel.Visibility == Visibility.Collapsed &&
                Field<MenuItem>(fresh, "_export").IsEnabled,
                "Both grids share the same page and retain the table summary, Find and CSV export");
            // Two competing refreshes exercise cancellation/generation of actual result pages.
            Start(fresh, Method(fresh, "RefreshAsync").CreateDelegate<Func<CancellationToken, Task>>(fresh));
            Field<TextBox>(fresh, "_search").Text = "no-match";
            await WaitForTasksAsync(fresh);
            Check(Field<Selector>(fresh, "_rows").ItemsSource is IReadOnlyList<ComparisonResultRow> { Count: 0 },
                "Newer WPF search refresh wins over the cancelled earlier page");
            await fresh.CloseAndDisposeAsync();

            await CheckCollectionAsync();
            PairedTableDataUiChecks.Run(Check);

            var callbacks = new RelationalComparisonResultSession(await ResultAsync("callbacks"), leftTable, rightTable, ["Id"]);
            sessions.Add(callbacks);
            CancellationToken operationToken = default;
            Start(callbacks, async ct =>
            {
                operationToken = ct;
                using var registration = ct.Register(() =>
                {
                    callbackStarted.TrySetResult();
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Callback release timed out.");
                });
                operationStarted.TrySetResult();
                await releaseOperation.Task;
            });
            await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task cleanup = callbacks.CloseAndDisposeAsync();
            Check(operationToken.IsCancellationRequested && !cleanup.IsCompleted,
                "Window close flags cancellation immediately without blocking its STA on provider callbacks");
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseOperation.TrySetResult();
            Check(!cleanup.IsCompleted, "Cleanup retains ownership of in-flight cancellation callbacks");
            releaseCallback.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Directory.EnumerateFileSystemEntries(root).Any() == false,
                "WPF retirement drains work and removes every owned scratch directory");
            Check(Application.Current == null, "WPF lifecycle checks leave no Application behind");
            return passed;
        }
        finally
        {
            if (stores.Count > 1) releaseRebuild.TrySetResult(stores[1]);
            releaseOperation.TrySetResult(); releaseCallback.Set();
            foreach (var session in sessions) await session.CloseAndDisposeAsync();
            foreach (var store in stores) await store.DisposeAsync();
            Directory.Delete(root); // Exact UUID-owned directory, now empty; never recursive.
        }

        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
        async Task DisposedAsync(ComparisonResultStore store, string name)
        {
            try { await store.ReadPageAsync(); }
            catch (ObjectDisposedException) { Check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        async Task<ComparisonResultStore> ResultAsync(string key)
        {
            var input = new ComparisonTableInput(["Id"], ["Id"], 1, Rows(key));
            var result = await ComparisonTableEngine.BuildAsync(input, input, ["Id"], new() { StagingDirectory = root });
            stores.Add(result); return result;
        }

        async Task CheckCollectionAsync()
        {
            Guid epoch = Guid.NewGuid();
            const string folderName = "Stored Procedures";
            var folder = new RelationalComparisonTarget(Resource(folderName, "Folder", ComparisonResourceKind.DatabaseObjectFolder, collection: true), epoch);
            var before = new RelationalComparisonTarget(Resource("inspect.sql", "Stored Procedure", ComparisonResourceKind.StoredProcedure),
                epoch, SnapshotKey: 1, ResourceKey: 2, RevisionKey: 3, VersionKey: 4, HistoricalEntryKey: 5);
            var after = before with { SnapshotKey = 6, ResourceKey = 7, RevisionKey = 8, VersionKey = null, HistoricalEntryKey = null };
            var result = ComparisonResultStore.FromMemory(
            [
                new(folderName, "Different", "", folderName, folderName, IsCollection: true, Left: folder, Right: folder),
                new(folderName + "/inspect.sql", "Different", "", "inspect.sql", "inspect.sql", Left: before, Right: after),
                new(folderName + "/verify.sql", "Missing right", "", "verify.sql", "", Left: before)
            ], new() { PageSize = 2 });
            stores.Add(result);
            ComparisonResultRow? opened = null;
            var session = new RelationalComparisonResultSession(result, leftCollection, rightCollection,
                (row, _) => { opened = row; return Task.CompletedTask; },
                (_, _) => throw new InvalidOperationException("The collection fixture does not change comparison options."), new());
            sessions.Add(session);
            var collection = (ResourceCollectionDiffWindow)session.View;
            var list = collection.RowsListView;
            Check(collection.Title == "Compare Current database and Saved version" &&
                collection.LeftHeaderText.Text == "Database: Current database" && collection.RightHeaderText.Text == "Database Version: Saved version" &&
                ((GridView)list.View).Columns.Select(column => column.Header as string).SequenceEqual(new[] { "Left", "Status", "Right" }) &&
                list.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header as string).SequenceEqual(new[] { "Compare with...", "Exclude" }),
                "Relational collection results use the master headings, side-by-side columns and row actions");
            Check(collection.IgnoreCaseCheckBox.Parent is WrapPanel toolbar &&
                toolbar.Children.IndexOf(collection.DifferencesOnlyCheckBox) == toolbar.Children.IndexOf(collection.IgnoreCaseCheckBox) + 1 &&
                toolbar.Children.IndexOf(collection.ClearExclusionsButton) == toolbar.Children.IndexOf(collection.DifferencesOnlyCheckBox) + 1 &&
                toolbar.Children.IndexOf(collection.ExportCsvButton) == toolbar.Children.IndexOf(collection.ClearExclusionsButton) + 1 &&
                collection.DifferencesOnlyCheckBox.Visibility == Visibility.Visible && collection.ClearExclusionsButton.Visibility == Visibility.Visible &&
                collection.ExportCsvButton.Visibility == Visibility.Visible,
                "Comparison filters and CSV actions are inline after Ignore case");
            await RefreshAsync(session);
            var rows = (ResourceCollectionDiffRow[])list.ItemsSource;
            Check(rows.Single() is { IsFolder: true, IsExpanded: false }, "Database entity groups start collapsed");
            session.Toggle(rows[0]);
            await WaitForTasksAsync(session);
            rows = (ResourceCollectionDiffRow[])list.ItemsSource;
            var previous = Field<Button>(session, "_previous");
            var next = Field<Button>(session, "_next");
            var paging = Field<StackPanel>(session, "_paging");
            Check(rows.Length == 2 && rows[0].IsFolder && rows[0].IsExpanded && rows[1].Indent == 18 &&
                rows[1].DisplayStatus == "Different" && paging.Visibility == Visibility.Visible && !previous.IsEnabled && next.IsEnabled,
                "Collection results show only one page of colored, indented master rows at a time");

            session.Open(rows[1]);
            await WaitForTasksAsync(session);
            Check(opened?.Left == before && opened?.Right == after,
                "Opening a rendered row retains its exact historical and current targets");
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitForTasksAsync(session);
            Check(((ResourceCollectionDiffRow[])list.ItemsSource).Single().DisplayStatus == "Missing right" &&
                previous.IsEnabled && !next.IsEnabled, "Collection paging reaches the final row without materializing both pages");
            previous.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitForTasksAsync(session);

            rows = (ResourceCollectionDiffRow[])list.ItemsSource;
            list.SelectedItem = rows[0];
            list.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Exclude")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(rows.All(row => row.DisplayStatus == "Excluded" && !row.CanOpenDiff) && list.Items.Count == 2,
                "Excluding a folder marks it and its descendants while keeping their rows visible");
            session.Toggle(rows[0]);
            await WaitForTasksAsync(session);
            Check(((ResourceCollectionDiffRow[])list.ItemsSource).Single() is { IsFolder: true, IsExpanded: false, IsExcluded: true } &&
                paging.Visibility == Visibility.Collapsed, "The inline folder toggle collapses descendants and removes unnecessary paging");
            collection.SearchTextBox.Text = " inspect ";
            await WaitForTasksAsync(session);
            Check(((ResourceCollectionDiffRow[])list.ItemsSource).Single() is { RelativePath: folderName + "/inspect.sql", IsExcluded: true },
                "Find reaches descendants of collapsed folders and retains exclusion styling");
            collection.ClearExclusionsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!((ResourceCollectionDiffRow[])list.ItemsSource).Single().IsExcluded && !collection.ClearExclusionsButton.IsEnabled,
                "Clear Exclusions restores the selected comparison result colors");
            await session.CloseAndDisposeAsync();
        }
    }

    private static async IAsyncEnumerable<CaptureRow> Rows(string key)
    {
        await Task.CompletedTask;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { Id = key }));
        yield return new(0, document.RootElement.Clone(), document.RootElement.GetRawText().Length * 2L);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ComparisonResource Resource(string name, string type, ComparisonResourceKind kind, bool collection = false) =>
        new(name, type, name, kind, kind.ToString(), collection, kind == ComparisonResourceKind.StoredProcedure,
            kind == ComparisonResourceKind.TableData, name);
    private static ResourceDictionary ReadButtonStyles(string path)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] keys = ["SurfActionButtonStyle", "SurfPrimaryButtonStyle"];
        var source = XDocument.Load(path);
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName),
            source.Descendants(presentation + "Style").Where(style => keys.Contains((string?)style.Attribute(xaml + "Key")))
                .Select(style => new XElement(style)));
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
    private static double Contrast(Color left, Color right)
    {
        double a = Luminance(left), b = Luminance(right);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        static double Channel(byte value)
        { double scaled = value / 255d; return scaled <= 0.04045 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4); }
    }
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name);
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) ?? throw new MissingFieldException(name));
    private static void Start(RelationalComparisonResultSession session, Func<CancellationToken, Task> action) =>
        Method(session, "Start").Invoke(session, [action]);
    private static Task RefreshAsync(RelationalComparisonResultSession session)
    {
        Start(session, Method(session, "RefreshAsync").CreateDelegate<Func<CancellationToken, Task>>(session));
        return WaitForTasksAsync(session);
    }
    private static async Task WaitForTasksAsync(RelationalComparisonResultSession session)
    {
        while (Field<HashSet<Task>>(session, "_tasks").Count != 0)
        {
            await Task.WhenAll(Field<HashSet<Task>>(session, "_tasks").ToArray()).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }
    }
}

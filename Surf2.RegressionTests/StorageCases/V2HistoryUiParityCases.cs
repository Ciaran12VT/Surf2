using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Surf2;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyV2HistoryUiParityAsync(MainWindow main, ExplorerScope scope,
        DatabaseSnapshotLibrary legacy, string outputDir, Action<bool, string> check)
    {
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var snapshot = scope.Resources.First(resource => resource.Snapshot != null).Snapshot!;
        var history = legacy.Histories.Single(item => item.SnapshotId == snapshot.SnapshotId);
        // The original window is the presentation oracle, using the exact same
        // logical fixture that was migrated to the isolated relational database.
        var original = new DatabaseSnapshotHistoryWindow(legacy.Snapshots.Single(item => item.SnapshotId == snapshot.SnapshotId),
            legacy, history, new DatabaseSnapshotHistoryService(new DatabaseDocumentService()));
        var window = new RelationalSnapshotHistoryWindow(runtime, snapshot);
        var root = new Grid { Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey) };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(window.Resources);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 history parity offscreen host")
        { PositionX = -32000, PositionY = -32000, Width = 1120, Height = 620,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        try
        {
            await RuntimeWpfInvokeTaskAsync(window, "InitializeAsync");
            var content = (FrameworkElement)window.Content;
            window.Content = null; root.Children.Add(content); host.RootVisual = root;
            RuntimeWpfArrange(root, 1120, 620); await RuntimeWpfIdleAsync();

            var rows = RuntimeWpfNamed<ListView>(window, "RowsListView");
            var oldRows = RuntimeWpfNamed<ListView>(original, "RowsListView");
            var find = RuntimeWpfNamed<TextBox>(window, "HistoryFilterTextBox");
            var oldFind = RuntimeWpfNamed<TextBox>(original, "HistoryFilterTextBox");
            var status = RuntimeWpfNamed<TextBlock>(window, "StatusTextBlock");
            var oldStatus = RuntimeWpfNamed<TextBlock>(original, "StatusTextBlock");
            var versions = HistoryDataRows(rows).ToArray();
            var oldVersions = oldRows.Items.Cast<DatabaseSnapshotHistoryWindow.DatabaseHistoryVersionRow>().ToArray();

            RuntimeWpfAssert(window.Title == original.Title &&
                window.Width == original.Width && window.Height == original.Height && window.MinWidth == original.MinWidth && window.MinHeight == original.MinHeight,
                check, "History retains master's title and window dimensions");
            var header = RuntimeWpfNamed<TextBlock>(window, "HeaderTextBlock");
            var display = await runtime.Snapshots.ReadRefreshDisplayAsync(snapshot);
            RuntimeWpfAssert(header.Text == display.Label(snapshot.DisplayName) && Equals(header.ToolTip, header.Text), check,
                "History top-left heading shows version, name, capture and refresh dates with a full-text tooltip");
            var columns = ((GridView)rows.View).Columns;
            var expectedColumns = ((GridView)oldRows.View).Columns;
            RuntimeWpfAssert(columns.Select(column => column.Width).SequenceEqual(expectedColumns.Select(column => column.Width)) &&
                !RuntimeWpfVisuals<DataGrid>(root).Any() && !RuntimeWpfVisuals<TabControl>(root).Any() &&
                !RuntimeWpfVisuals<ComboBox>(root).Any(), check,
                "History uses a single five-column hierarchy with Find");
            RuntimeWpfAssert(versions.Length == history.Versions.Count && versions.All(row => HistoryValue<bool>(row, "IsVersion")) &&
                versions.All(row => !HistoryValue<bool>(row, "IsExpanded")) &&
                versions.Select(row => HistoryValue<SnapshotVersionSummary>(row, "Version").VersionNumber)
                    .SequenceEqual(history.Versions.OrderByDescending(version => version.VersionNumber).Select(version => version.VersionNumber)) &&
                status.Text == oldStatus.Text, check,
                "History starts with descending, collapsed versions and the master version-count status");

            await VerifyHistoryActionsDuringCountsAsync(window, runtime, versions[0], check);
            for (int i = 0; i < versions.Length; i++)
            {
                await RuntimeWpfInvokeTaskAsync(window, "ToggleRowAsync", versions[i]);
                ToggleHistoryRow(original, oldVersions[i]);
            }
            await RuntimeWpfWaitAsync(() => HistoryRowsReady(rows, oldRows.Items.Count), "expanded history line counts");
            RuntimeWpfAssert(HistorySignatures(rows).SequenceEqual(HistorySignatures(oldRows)), check,
                "Expanded migrated history matches master's resource types, names, order, timestamps and added/removed line counts");
            RuntimeWpfArrange(root, 1120, 620); await RuntimeWpfIdleAsync();
            VerifyHistoryColumnHeadings(rows, check);
            RuntimeWpfAssert(versions.All(row => HistoryValue<string>(row, "ToggleGlyph") == "▲" &&
                    HistoryValue<string>(row, "ToggleToolTip") == "Collapse"), check,
                "History expand buttons expose master's glyph and Collapse tooltip");

            var version = versions[0];
            var file = HistoryValue<IEnumerable>(version, "Children").Cast<object>()
                .First(row => HistoryValue<string>(row, "ResourceTypeColumn") == "Stored Procedure");
            var versionMenu = (ContextMenu)RuntimeWpfInvoke(window, "CreateVersionContextMenu", version)!;
            var fileMenu = (ContextMenu)RuntimeWpfInvoke(window, "CreateFileContextMenu", file)!;
            RuntimeWpfAssert(MenuSignature(versionMenu).SequenceEqual(new[] { "Compare With...", "Restore", "Restore To..." }) &&
                MenuSignature(fileMenu).SequenceEqual(new[] { "View", "Compare With...", "Restore", "Restore To..." }) &&
                versionMenu.Items.OfType<MenuItem>().Concat(fileMenu.Items.OfType<MenuItem>()).All(item => item.IsEnabled && item.Items.Count == 0),
                check, "History version and file menus match master's exact labels and order without extra export items or submenus");
            var openedTarget = await VerifyHistoryActionTargetsAsync(window, snapshot, version, file, versionMenu, fileMenu, check);
            await VerifyV2HistoryFilePickerAsync(runtime, snapshot, openedTarget, outputDir, check);

            RuntimeWpfArrange(root, 1120, 620); await RuntimeWpfIdleAsync();
            RuntimeWpfSaveRender(root, outputDir, "v2-history-expanded", 1120, 620, check);

            // Collapse first, then search; matching files must still be exposed.
            foreach (var row in versions) await RuntimeWpfInvokeTaskAsync(window, "ToggleRowAsync", row);
            foreach (var row in oldVersions) ToggleHistoryRow(original, row);
            foreach (string query in new[] { "  MIXED P  ", "mixed/A", "Stored Procedure", "Version early", "no-such-history-resource" })
            {
                find.Text = query; oldFind.Text = query;
                await RuntimeWpfWaitAsync(() => HistorySignatures(rows).SequenceEqual(HistorySignatures(oldRows)) && status.Text == oldStatus.Text,
                    "history Find parity for " + query);
                RuntimeWpfAssert(HistorySignatures(rows).SequenceEqual(HistorySignatures(oldRows)) && status.Text == oldStatus.Text &&
                    versions.All(row => !HistoryValue<bool>(row, "IsExpanded")), check,
                    $"History Find '{query.Trim()}' matches master and reveals matching children without changing expansion state");
            }
            find.Text = "mixed/A"; oldFind.Text = "mixed/A";
            await RuntimeWpfWaitAsync(() => HistorySignatures(rows).SequenceEqual(HistorySignatures(oldRows)), "history filtered screenshot rows");
            RuntimeWpfArrange(root, 1120, 620); await RuntimeWpfIdleAsync();
            RuntimeWpfSaveRender(root, outputDir, "v2-history-find", 1120, 620, check);
            find.Clear(); oldFind.Clear();
            await RuntimeWpfWaitAsync(() => HistoryDataRows(rows).Count() == versions.Length && status.Text == oldStatus.Text, "cleared history Find");
            RuntimeWpfAssert(HistoryDataRows(rows).Count() == versions.Length && status.Text == oldStatus.Text, check,
                "Clearing History Find returns to collapsed versions and normal status");
        }
        finally
        {
            host.RootVisual = null; root.Children.Clear(); root.Resources.MergedDictionaries.Clear();
            original.Close();
            await window.CloseAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    private static void VerifyHistoryColumnHeadings(ListView rows, Action<bool, string> check)
    {
        var displayed = rows.Items.OfType<RelationalSnapshotHistoryWindow.HistoryRow>().ToArray();
        var versionHeading = displayed[0];
        var version = (RelationalSnapshotHistoryWindow.HistoryVersionRow)displayed[1];
        var fileHeading = displayed[2];
        var file = (RelationalSnapshotHistoryWindow.HistoryFileRow)displayed[3];
        RuntimeWpfAssert(versionHeading.IsHeading && versionHeading.VersionColumn == "Version" &&
            versionHeading.AddedColumn == "Timestamp" && versionHeading.RemovedColumn == "Files" &&
            fileHeading.IsHeading && fileHeading.ResourceTypeColumn == "Resource Type" && fileHeading.NameColumn == "Name" &&
            fileHeading.AddedColumn == "Lines Added" && fileHeading.RemovedColumn == "Lines Removed" &&
            displayed.Count(row => row.IsHeading) == displayed.Count(row => row.IsVersion) + 1 &&
            displayed.Select((row, index) => (row, index)).Where(pair => pair.row.IsVersion)
                .All(pair => displayed[pair.index + 1].IsHeading), check,
            "History displays a version heading and one file heading inside each expanded version");

        RuntimeWpfAssert(Aligned(versionHeading, "Version", version, version.VersionColumn) &&
            Aligned(versionHeading, "Timestamp", version, version.AddedColumn) &&
            Aligned(versionHeading, "Files", version, version.RemovedColumn) &&
            Aligned(fileHeading, "Resource Type", file, file.ResourceTypeColumn) &&
            Aligned(fileHeading, "Name", file, file.NameColumn) &&
            Aligned(fileHeading, "Lines Added", file, file.AddedColumn) &&
            Aligned(fileHeading, "Lines Removed", file, file.RemovedColumn), check,
            "History headings align with the rendered text in their data columns");
        RuntimeWpfAssert(file.HasAddedLines && file.HasRemovedLines &&
            Cell(file, file.AddedColumn).Foreground is SolidColorBrush added && added.Color.G > added.Color.R &&
            Cell(file, file.RemovedColumn).Foreground is SolidColorBrush removed && removed.Color.R > removed.Color.G, check,
            "History renders positive added counts in green and removed counts in red");

        TextBlock Cell(object data, string text) => RuntimeWpfVisuals<TextBlock>(
            rows.ItemContainerGenerator.ContainerFromItem(data) ?? throw new InvalidOperationException("Missing history row visual."))
            .Single(block => block.Text == text);
        double Left(object data, string text) => Cell(data, text).TransformToAncestor(rows).Transform(new Point()).X;
        bool Aligned(object heading, string label, object data, string value) => Math.Abs(Left(heading, label) - Left(data, value)) < 1;
    }

    private static async Task<RelationalComparisonTarget> VerifyHistoryActionTargetsAsync(RelationalSnapshotHistoryWindow window, SnapshotSummary snapshot,
        object version, object file, ContextMenu versionMenu, ContextMenu fileMenu, Action<bool, string> check)
    {
        RelationalComparisonTarget? opened = null, compared = null, copied = null;
        window.OpenRequested += (_, e) => opened = e.Target;
        window.CopyRequested += (_, e) => copied = e.Target;
        EventHandler<RelationalHistoryOpenEventArgs> compareHandler = (_, e) => compared = e.Target;
        (window.GetType().GetEvent("CompareWithRequested") ?? throw new InvalidOperationException("Missing History Compare With route."))
            .AddEventHandler(window, compareHandler);
        long versionKey = HistoryValue<SnapshotVersionSummary>(version, "Version").VersionKey;
        Click(versionMenu, "Compare With...");
        await RuntimeWpfWaitAsync(() => compared != null, "History version Compare With event");
        RuntimeWpfAssert(compared!.SnapshotKey == snapshot.SnapshotKey && compared.VersionKey == versionKey &&
            compared.Resource.Kind == ComparisonResourceKind.DatabaseSnapshot, check,
            "History Compare With carries the selected immutable database version to the comparison picker");
        compared = null;
        Click(versionMenu, "Restore To...");
        await RuntimeWpfWaitAsync(() => copied != null, "History version Restore To event");
        RuntimeWpfAssert(copied!.SnapshotKey == snapshot.SnapshotKey && copied.VersionKey == versionKey &&
            copied.Resource.Kind == ComparisonResourceKind.DatabaseSnapshot, check,
            "History Restore To carries the selected database version without changing the current snapshot");
        copied = null;
        Click(fileMenu, "View");
        await RuntimeWpfWaitAsync(() => opened != null, "History file View event");
        RuntimeWpfAssert(opened!.SnapshotKey == snapshot.SnapshotKey && opened.VersionKey == versionKey &&
            opened.ResourceKey.HasValue && opened.Resource.Kind == ComparisonResourceKind.StoredProcedure, check,
            "History View routes the selected historical file with its snapshot, version and resource identity");
        Click(fileMenu, "Compare With...");
        await RuntimeWpfWaitAsync(() => compared != null, "History file Compare With event");
        Click(fileMenu, "Restore To...");
        await RuntimeWpfWaitAsync(() => copied != null, "History file Restore To event");
        RuntimeWpfAssert(compared!.SnapshotKey == opened.SnapshotKey && compared.VersionKey == opened.VersionKey &&
            compared.ResourceKey == opened.ResourceKey && copied!.SnapshotKey == opened.SnapshotKey &&
            copied.VersionKey == opened.VersionKey && copied.ResourceKey == opened.ResourceKey, check,
            "History file Compare With and Restore To preserve the exact selected historical target");
        return opened;
        static void Click(ContextMenu menu, string label) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void ToggleHistoryRow(Window window, object row) => RuntimeWpfInvoke(window, "ToggleRowButton_Click",
        new Button { DataContext = row }, new RoutedEventArgs(Button.ClickEvent));

    private static T HistoryValue<T>(object row, string property) =>
        (T)(row.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(row)
            ?? throw new InvalidOperationException("Missing history row property: " + property));

    private static IEnumerable<object> HistoryDataRows(ListView rows) => rows.Items.Cast<object>()
        .Where(row => row is not RelationalSnapshotHistoryWindow.HistoryRow { IsHeading: true });

    private static string[] HistorySignatures(ListView rows) => HistoryDataRows(rows).Select(row => string.Join("\u001F",
        new[] { "VersionColumn", "ResourceTypeColumn", "NameColumn", "AddedColumn", "RemovedColumn" }
            .Select(property => HistoryValue<string>(row, property)))).ToArray();

    private static bool HistoryRowsReady(ListView rows, int expectedCount) => HistoryDataRows(rows).Count() == expectedCount &&
        HistoryDataRows(rows).Where(row => !HistoryValue<bool>(row, "IsVersion"))
            .All(row => HistoryValue<string>(row, "AddedColumn").StartsWith('+') && HistoryValue<string>(row, "RemovedColumn").StartsWith('-'));
}

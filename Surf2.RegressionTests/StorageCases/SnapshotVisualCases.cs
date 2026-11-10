using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalGrid;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    // Called inside the existing --visual resource-only STA, before its single Application shuts down.
    private static void RenderSnapshotWorkflowCasesOnSta(Action<bool, string> check, string outputDir)
    {
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(
            "Server=Surf2_Regression_Offscreen;Database=Surf2_Regression_" + Guid.NewGuid().ToString("N") +
            ";Integrated Security=true;TrustServerCertificate=true;Pooling=false;Connect Timeout=1"));
        var token = new StateToken(1, runtime.Session.Epoch, new byte[8], Guid.NewGuid());
        var scope = new Scope { ScopeId = "offscreen-only", Name = "Offscreen scope" };
        var snapshot = new SnapshotSummary(1, Guid.NewGuid(), "offscreen-snapshot", "Offscreen published snapshot",
            "FixtureOnly", DateTimeOffset.UnixEpoch, 0, 1, new byte[8]);
        var hashes = new Dictionary<(string Case, bool Minimum, string Theme), string>();
        foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
        {
            AppThemeService.Apply(theme);
            foreach (bool minimum in new[] { false, true })
            foreach (string name in new[] { "capture-fresh", "capture-replacement", "history", "history-changes", "copy-code", "copy-table", "historical-grid" })
            {
                SnapshotRenderOnlySource? source = name == "historical-grid" ? new() : null;
                Window window = name switch
                {
                    "capture-fresh" => new Surf2.RelationalDatabaseCaptureWindow(runtime, scope, token),
                    "capture-replacement" => new Surf2.RelationalDatabaseCaptureWindow(runtime, scope, token, snapshot),
                    "history" or "history-changes" => new Surf2.RelationalSnapshotHistoryWindow(runtime, snapshot),
                    "copy-code" => SnapshotVisualInternalWindow("SnapshotCopyTargetWindow", runtime, "dbo", "copied_procedure", "SELECT N'offscreen fixture';"),
                    "copy-table" => SnapshotVisualInternalWindow("SnapshotCopyTargetWindow", runtime, "dbo", "copied_table", null),
                    _ => SnapshotVisualInternalWindow("HistoricalGridWindow", "Offscreen historical captured dataset", source!)
                };
                Task closeCompletion = SnapshotVisualCloseCompletion(window);
                check(!closeCompletion.IsCompleted && ReferenceEquals(closeCompletion, SnapshotVisualCloseCompletion(window)),
                    "Offscreen " + name + ": stable close completion remains pending until the window actually closes");
                FrameworkElement? content = null;
                try
                {
                    PopulateSnapshotVisualWindow(window, snapshot);
                    if (name == "history-changes")
                        SnapshotVisualDescendants<TabControl>(window.Content as DependencyObject
                            ?? throw new InvalidOperationException("History changes render lost its content.")).Single().SelectedIndex = 1;
                    int width = MigrationDeclaredDimension(minimum ? window.MinWidth : window.Width) - 16;
                    int height = MigrationDeclaredDimension(minimum ? window.MinHeight : window.Height) - 40;
                    string scenario = "snapshot-" + name + "-" + theme.ToLowerInvariant() + "-" + width + "x" + height + "-client-approx";
                    content = window.Content as FrameworkElement ?? throw new InvalidOperationException("Snapshot window lost its content root.");
                    window.Content = null;
                    var surface = new Grid { Width = width, Height = height,
                        Background = AppThemeService.GetBrush(AppThemeService.WindowBackgroundBrushKey) };
                    surface.Children.Add(content);
                    var size = new Size(width, height);
                    surface.Measure(size); surface.Arrange(new Rect(new Point(), size)); surface.UpdateLayout();
                    string path = System.IO.Path.Combine(outputDir, scenario + ".png");
                    var pixels = SaveMigrationRender(surface, path, width, height);
                    hashes.Add((name, minimum, theme), pixels.Hash);
                    check(pixels.OpaquePixels >= (long)width * height * 95 / 100 && pixels.NonBackgroundPixels > (long)width * height / 100,
                        scenario + ": CPU pixels show nonblank offscreen content with an opaque theme background");
                    check(true, "Offscreen WPF PNG: " + path + "; sha256=" + pixels.Hash + ". Client deductions are approximate, not a native screenshot.");
                    CheckSnapshotVisualBounds(content, surface, width, height, scenario, check);
                    CheckSnapshotVisualButtons(content, scenario, check);
                    CheckSnapshotVisualColumns(window, content, scenario, check);
                    check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero &&
                        SnapshotVisualDescendants<ComboBox>(content).All(c => !c.IsDropDownOpen),
                        scenario + ": no Window.Show, HWND, popup, application startup or production query is used");
                    if (source != null) check(source.QueryCalls == 0, scenario + ": render-only grid source issues no data/query requests");
                    surface.Children.Remove(content);
                }
                finally
                {
                    // Real idle close handlers retire these never-loaded instances without a dispatcher pump.
                    Task returned = SnapshotVisualCloseAndDrain(window);
                    check(ReferenceEquals(returned, closeCompletion) && closeCompletion.IsCompletedSuccessfully,
                        "Offscreen " + name + ": idle CloseAndDrainAsync returns its stable task only after real close and cleanup");
                    if (!closeCompletion.IsCompleted)
                        throw new InvalidOperationException("Offscreen idle close unexpectedly requires dispatcher work.");
                    closeCompletion.GetAwaiter().GetResult();
                    if (source != null) check(source.Disposed && source.QueryCalls == 0, "Offscreen historical grid retires only its render stub without SQL or file access");
                }
            }
        }
        foreach (var name in hashes.Keys.Select(k => (k.Case, k.Minimum)).Distinct())
            check(hashes[(name.Case, name.Minimum, AppearanceSettings.LightTheme)] != hashes[(name.Case, name.Minimum, AppearanceSettings.DarkTheme)],
                "Offscreen " + name.Case + " " + (name.Minimum ? "minimum" : "default") + ": Light and Dark rendered pixels differ");
        CheckSnapshotVisualCleanupFailure(check);
    }

    private static Task SnapshotVisualCloseCompletion(Window window) =>
        window.GetType().GetProperty("CloseCompletion")?.GetValue(window) as Task
        ?? throw new InvalidOperationException("Snapshot window lacks its stable close-completion task.");
    private static Task SnapshotVisualCloseAndDrain(Window window) =>
        window.GetType().GetMethod("CloseAndDrainAsync")?.Invoke(window, null) as Task
        ?? throw new InvalidOperationException("Snapshot window lacks its close-and-drain contract.");
    private static void CheckSnapshotVisualCleanupFailure(Action<bool, string> check)
    {
        const string marker = "offscreen-owned-source-disposal-failure";
        var source = new SnapshotRenderOnlySource(new System.IO.IOException(marker));
        var window = SnapshotVisualInternalWindow("HistoricalGridWindow", "Offscreen cleanup failure", source);
        Task completion = SnapshotVisualCloseCompletion(window);
        check(!completion.IsCompleted, "Offscreen failing source has no premature close-completion success");
        Task returned = SnapshotVisualCloseAndDrain(window);
        check(ReferenceEquals(returned, completion) && completion.IsFaulted && source.Disposed && source.QueryCalls == 0,
            "Offscreen real idle grid close surfaces disposal failure on its stable completion task without queries");
        if (!completion.IsCompleted) throw new InvalidOperationException("Offscreen failing close unexpectedly needs dispatcher work.");
        try { completion.GetAwaiter().GetResult(); }
        catch (AggregateException error)
        {
            check(error.Flatten().InnerExceptions.Any(e => e is System.IO.IOException && e.Message == marker),
                "Offscreen failed grid cleanup retains the exact owned-source error rather than reporting success");
            return;
        }
        throw new InvalidOperationException("Offscreen failed grid cleanup lost its expected aggregate exception.");
    }

    private static Window SnapshotVisualInternalWindow(string name, params object?[] arguments)
    {
        var type = typeof(RelationalSnapshotHistoryService).Assembly.GetType("Surf2.Services.RelationalSnapshots." + name, true)!;
        return (Window)(Activator.CreateInstance(type, arguments) ?? throw new InvalidOperationException("Cannot construct offscreen " + name));
    }
    private static T SnapshotVisualField<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as T
        ?? throw new InvalidOperationException("Missing offscreen field: " + name);
    private static void PopulateSnapshotVisualWindow(Window window, SnapshotSummary snapshot)
    {
        if (window is Surf2.RelationalDatabaseCaptureWindow capture)
        {
            SnapshotVisualField<TextBox>(capture, "_name").Text = "Offscreen fixture capture";
            SnapshotVisualField<TextBox>(capture, "_version").Text = "Selected fixture version";
            SnapshotVisualField<PasswordBox>(capture, "_connection").Password = "fixture-only";
            var choices = SnapshotVisualField<IList>(capture, "_tablePage");
            var choice = capture.GetType().GetNestedType("TableChoice", BindingFlags.NonPublic)!;
            choices.Add(Activator.CreateInstance(choice, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [new RelationalSourceTable(1, "dbo", "FixtureRows"), true, new Action<bool>(_ => { })], null)!);
        }
        else if (window is Surf2.RelationalSnapshotHistoryWindow history)
        {
            // Prevent selection event handlers from issuing SQL; only detached display DTOs are populated.
            history.GetType().GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(history, true);
            var version = new SnapshotVersionSummary(1, 1, "fixture-version", "Selected fixture version", 1, DateTimeOffset.UnixEpoch, true, 0, 1);
            SnapshotVisualField<ComboBox>(history, "_histories").ItemsSource = new[] { new SnapshotHistorySummary(1, 1, snapshot.SnapshotId, 2, 0, new byte[8]) };
            SnapshotVisualField<ComboBox>(history, "_histories").SelectedIndex = 0;
            SnapshotVisualField<DataGrid>(history, "_versions").ItemsSource = new[] { version };
            SnapshotVisualField<DataGrid>(history, "_versions").SelectedIndex = 0;
            history.GetType().GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(history, version);
            SnapshotVisualField<DataGrid>(history, "_resources").ItemsSource = new[]
            { new HistoricalSnapshotEntry(1, HistoricalCollection.Objects, 0, 1, 1, null, "dbo", "fixture_procedure", SqlDatabaseObjectKind.StoredProcedure) };
            SnapshotVisualField<DataGrid>(history, "_resources").SelectedIndex = 0;
            SnapshotVisualField<DataGrid>(history, "_changes").ItemsSource = new[]
            {
                new SnapshotChangeSummary(1, version.VersionKey, 1, DatabaseVersionedResourceKind.StoredProcedure,
                    DatabaseSnapshotResourceChangeKind.Modified, "fixture-resource", "fixture_procedure", "dbo/fixture_procedure.sql", 0, 1)
            };
            SnapshotVisualField<DataGrid>(history, "_changes").SelectedIndex = 0;
            SnapshotVisualField<TextBox>(history, "_preview").Text = "SELECT N'offscreen selected definition';";
            history.GetType().GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(history, false);
            history.GetType().GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history, [false]);
        }
        else if (window.GetType().Name == "SnapshotCopyTargetWindow")
        {
            SnapshotVisualField<ComboBox>(window, "_snapshots").ItemsSource = new[] { snapshot };
            SnapshotVisualField<ComboBox>(window, "_snapshots").SelectedIndex = 0;
        }
        if (window.GetType().GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is TextBlock status)
            status.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 30).Select(i => "Fixture progress " + i + ": source preserved; selected immutable rows remain bounded."));
    }

    private static IEnumerable<T> SnapshotVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in SnapshotVisualDescendants<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }
    private static bool SnapshotVisualVisible(FrameworkElement element)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return element.RenderSize.Width > 0 && element.RenderSize.Height > 0;
    }
    private static void CheckSnapshotVisualBounds(FrameworkElement content, Grid surface, int width, int height,
        string scenario, Action<bool, string> check)
    {
        var client = new Rect(0, 0, width, height);
        var buttons = SnapshotVisualDescendants<Button>(content).Where(SnapshotVisualVisible).ToArray();
        var inputs = SnapshotVisualDescendants<Control>(content).Where(c => c is TextBox or PasswordBox or ComboBox)
            .Where(SnapshotVisualVisible).ToArray();
        check(MigrationContains(client, MigrationBounds(content, surface), 1) &&
            buttons.Cast<FrameworkElement>().Concat(inputs).All(c => MigrationContains(client, MigrationBounds(c, surface), 1)),
            scenario + ": content, input and visible action bounds fit the reduced client allocation");
        bool separated = true;
        for (int i = 0; i < buttons.Length; i++)
        for (int j = i + 1; j < buttons.Length; j++)
            separated &= !MigrationOverlaps(MigrationBounds(buttons[i], surface), MigrationBounds(buttons[j], surface));
        foreach (var button in buttons)
        foreach (var input in inputs)
            separated &= !MigrationOverlaps(MigrationBounds(button, surface), MigrationBounds(input, surface));
        check(separated, scenario + ": visible action buttons do not overlap");
    }
    private static void CheckSnapshotVisualButtons(FrameworkElement content, string scenario, Action<bool, string> check)
    {
        var buttons = SnapshotVisualDescendants<Button>(content).Where(b => SnapshotVisualVisible(b) && b.Content is string).ToArray();
        check(buttons.Length > 0 && buttons.All(b => SnapshotVisualSurfStyle(b.Style) &&
            MigrationControlTextFits(b, (string)b.Content)), scenario + ": themed action and icon captions fit their chrome");
        check(buttons.All(b => b.Foreground is SolidColorBrush foreground && b.Background is SolidColorBrush background &&
            SnapshotVisualContrast(foreground.Color, background.Color) >= 4.5), scenario + ": action foreground/background contrast is at least 4.5:1");
    }
    private static void CheckSnapshotVisualColumns(Window window, FrameworkElement content, string scenario, Action<bool, string> check)
    {
        if (window is Surf2.RelationalDatabaseCaptureWindow)
        {
            CheckSnapshotVisualNameGrid(SnapshotVisualField<DataGrid>(window, "_tables"), 1, 160, [(0, 90)], scenario + " capture", check);
            var replace = SnapshotVisualField<CheckBox>(window, "_replace");
            var target = replace.Parent as Grid ?? throw new InvalidOperationException("Capture replacement label lost its target row.");
            var selector = SnapshotVisualField<ComboBox>(window, "_snapshots");
            var label = replace.Content as TextBlock ?? throw new InvalidOperationException("Capture replacement label must wrap.");
            var probe = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize,
                FontStyle = label.FontStyle, FontStretch = label.FontStretch, FontWeight = label.FontWeight, TextWrapping = TextWrapping.Wrap };
            probe.Measure(new Size(label.ActualWidth, double.PositiveInfinity));
            Rect slot = new(0, 0, target.ColumnDefinitions[0].ActualWidth, target.ActualHeight);
            check(label.TextWrapping == TextWrapping.Wrap && target.ColumnDefinitions[0].ActualWidth == 160 &&
                MigrationContains(slot, MigrationBounds(label, target), 1) &&
                probe.DesiredSize.Height <= label.ActualHeight + 1 &&
                !MigrationOverlaps(MigrationBounds(replace, content), MigrationBounds(selector, content)),
                scenario + ": wrapped replacement label fits the 160-DIP slot without touching its snapshot selector");
        }
        else if (window is Surf2.RelationalSnapshotHistoryWindow)
        {
            CheckSnapshotVisualNameGrid(SnapshotVisualField<DataGrid>(window, "_versions"), 0, 120, [(1, 60), (2, 65)], scenario + " versions", check);
            CheckSnapshotVisualNameGrid(SnapshotVisualField<DataGrid>(window, "_resources"), 0, 120, [(1, 100), (2, 100)], scenario + " resources", check);
            CheckSnapshotVisualNameGrid(SnapshotVisualField<DataGrid>(window, "_changes"), 0, 120, [(1, 100)], scenario + " changes", check);
        }
    }
    private static void CheckSnapshotVisualNameGrid(DataGrid grid, int nameIndex, double minimumNameWidth,
        (int Index, double Width)[] compact, string scenario, Action<bool, string> check)
    {
        DataGridColumn name = grid.Columns[nameIndex];
        check(name.Width.IsStar && name.MinWidth >= minimumNameWidth && compact.All(item =>
            grid.Columns[item.Index].Width.IsAbsolute && grid.Columns[item.Index].Width.Value == item.Width &&
            grid.Columns[item.Index].MinWidth == item.Width && grid.Columns[item.Index].MaxWidth == item.Width),
            scenario + ": name uses a minimum-bounded star column and metadata columns have explicit compact widths");
        if (!SnapshotVisualVisible(grid)) return;
        var headers = SnapshotVisualDescendants<DataGridColumnHeader>(grid).Where(h => h.Column != null && SnapshotVisualVisible(h)).ToArray();
        var nameHeader = headers.SingleOrDefault(h => ReferenceEquals(h.Column, name));
        var cells = SnapshotVisualDescendants<DataGridCell>(grid).Where(c => ReferenceEquals(c.Column, name) && SnapshotVisualVisible(c)).ToArray();
        check(name.ActualWidth + 1 >= minimumNameWidth && nameHeader != null && nameHeader.ActualWidth + 1 >= minimumNameWidth &&
            cells.Length > 0 && cells.All(cell => cell.ActualWidth + 1 >= minimumNameWidth &&
                SnapshotVisualDescendants<TextBlock>(cell).Any(text => text.Text.Length >= 6 && text.ActualWidth + 1 >= minimumNameWidth - 8)),
            scenario + ": realized name header and fixture cells remain readable, not tiny ellipsis-only columns; " +
            "actual_name_width=" + name.ActualWidth.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        check(headers.Length == grid.Columns.Count && headers.All(header => header.Content is string caption && MigrationControlTextFits(header, caption)) &&
            compact.All(item => Math.Abs(grid.Columns[item.Index].ActualWidth - item.Width) <= 1),
            scenario + ": every realized header caption fits and fixed metadata columns do not consume spare name space");
    }
    private static bool SnapshotVisualSurfStyle(Style? style)
    {
        var expected = new[] { "SurfPrimaryButtonStyle", "SurfSaveButtonStyle", "SurfCloseButtonStyle" }
            .Select(key => Application.Current.FindResource(key)).ToArray();
        for (var current = style; current != null; current = current.BasedOn)
            if (expected.Any(candidate => ReferenceEquals(candidate, current))) return true;
        return false;
    }
    private static double SnapshotVisualContrast(Color first, Color second)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private sealed class SnapshotRenderOnlySource(Exception? disposalFailure = null) : IDataGridSource
    {
        public GridDescriptor Descriptor { get; } = new(Guid.NewGuid(), "offscreen-only", new[] { new GridColumn(0, "Id", "Id"), new GridColumn(1, "Name", "Name") }, 0, 0, 1);
        public Task<long> DisplayRowCount => Task.FromResult(0L);
        public bool IsInvalidated => false;
        public long OverlayGeneration => 0;
        public long OverlayBytes => 0;
        public int QueryCalls { get; private set; }
        public bool Disposed { get; private set; }
        public void SetCell(GridRow row, int columnOrdinal, string? value) => throw new InvalidOperationException("Render-only source cannot edit rows.");
        public void ClearEdits() { }
        public Task<IGridQuerySession> CreateQueryAsync(GridQuery? query = null, CancellationToken cancellationToken = default)
        { ++QueryCalls; throw new InvalidOperationException("Offscreen rendering must not issue queries."); }
        public ValueTask DisposeAsync()
        { Disposed = true; return disposalFailure == null ? ValueTask.CompletedTask : ValueTask.FromException(disposalFailure); }
    }
}

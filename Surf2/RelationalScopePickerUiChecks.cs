using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using Surf2.Storage.Relational.State;

namespace Surf2;

/// <summary>Resource-only STA checks: no Window.Show, SQL, connection settings or application startup.</summary>
public static class RelationalScopePickerUiChecks
{
    private static readonly string[] ResourceCommands = ["Add Folder", "Add File", "Add Database", "Edit Database", "Add Snapshot", "Add Diagram", "Remove"];
    private static readonly string[] ScopeCommands = ["New", "Save", "Delete", "Add Scope Resources", .. ResourceCommands, "Open", "Close"];

    // Hook: call on an STA with App.InitializeComponent(), without Application.Run/OnStartup.
    public static async Task<IReadOnlyList<string>> RunOffscreenAsync(string outputDirectory, CancellationToken ct = default)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Load App resources before rendering picker fixtures.");
        app.Dispatcher.VerifyAccess();
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("An absolute owned fixture output directory is required.", nameof(outputDirectory));
        string directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        string previous = AppThemeService.CurrentTheme;
        var passed = new List<string>();
        var hashes = new Dictionary<(string Theme, int Width), string>();
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString("Server=unused.invalid;Database=OffscreenFixture;Integrated Security=True"));
        try
        {
            foreach (string theme in new[] { AppearanceSettings.LightTheme, AppearanceSettings.DarkTheme })
            {
                ct.ThrowIfCancellationRequested(); AppThemeService.Apply(theme);
                foreach (var (width, height) in new[] { (1300, 560), (1000, 560), (800, 460), (640, 380) })
                foreach (bool large in new[] { false, true })
                {
                    var scopes = new RelationalScopePickerWindow(runtime);
                    try
                    {
                        var fixture = PopulateScope(scopes, large);
                        // Frame deductions approximate a reduced client area, not a native/DPI measurement.
                        using var surface = new OffscreenSurface(scopes, width - 16, height - 40);
                        surface.Layout();
                        CheckScopeLayout(surface, width >= 1000);
                        if (large) CheckResourceVirtualization(surface, fixture.Resources.Count);
                        CheckScopeEditing(scopes, fixture, surface);
                        CheckButtons(surface.Root, "scopes", ScopeCommands);
                        string path = Path.Combine(directory, $"scopes-{theme.ToLowerInvariant()}-{width}x{height}-{(large ? "600-resources" : "5-resources")}-client-approx.png");
                        string hash = surface.Save(path);
                        if (!large) hashes.Add((theme, width), hash);
                        Require(!scopes.IsVisible && new WindowInteropHelper(scopes).Handle == IntPtr.Zero,
                            "Scope fixture must remain offscreen without an HWND.");
                        passed.Add($"Scopes {theme} {width}x{height} {fixture.Resources.Count} resources: columns, toolbar, long text, edits, identity, finite viewport and pixels: {path}");
                    }
                    finally { await scopes.DrainQueriesAsync(); scopes.Close(); }
                }
                await CheckPaginationAsync(passed, theme, ct);
            }
            foreach (int width in new[] { 1300, 1000, 800, 640 })
                Require(hashes[(AppearanceSettings.LightTheme, width)] != hashes[(AppearanceSettings.DarkTheme, width)],
                    "Light and Dark scope pixels must differ at " + width);
            Require(runtime.Session.Metrics.Snapshot().Started == 0, "Offscreen scope fixtures must not issue SQL commands.");
        }
        finally { AppThemeService.Apply(previous); }
        return passed;
    }

    private static Scope PopulateScope(RelationalScopePickerWindow window, bool large)
    {
        var scope = new Scope { Name = "Scope with a long descriptive name for the selected development workspace",
            Description = "Development sources, database snapshots and diagrams with long resource names." };
        scope.Resources.Add(new() { ResourceId = "folder-membership", Kind = ResourceKind.Folder, Path = @"C:\Workspace\Long project directory\Source\Nested folder", DisplayNameOverride = "Source alias with a deliberately long name", AddedAtUtc = DateTimeOffset.UnixEpoch });
        scope.Resources.Add(new() { ResourceId = "file-membership", Kind = ResourceKind.File, Path = @"C:\Workspace\Long project directory\Source\An exceptionally long resource filename.cs", IncludeChildren = false, AddedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1) });
        scope.Resources.Add(new() { ResourceId = "snapshot-membership", Kind = ResourceKind.DatabaseSnapshot, Path = "same-original-snapshot-id", DisplayNameOverride = "Database snapshot alias", DetailsOverride = "Published database resource", AddedAtUtc = DateTimeOffset.UnixEpoch.AddDays(2) });
        scope.Resources.Add(new() { ResourceId = "other-snapshot-membership", Kind = ResourceKind.DatabaseSnapshot, Path = "same-original-snapshot-id", DisplayNameOverride = "Another explicitly targeted snapshot", DetailsOverride = "Other database resource", IncludeChildren = false, AddedAtUtc = DateTimeOffset.UnixEpoch.AddDays(3) });
        scope.Resources.Add(new() { ResourceId = "diagram-membership", Kind = ResourceKind.Diagram, Path = "diagram-original-id", DisplayNameOverride = "Diagram alias", AddedAtUtc = DateTimeOffset.UnixEpoch.AddDays(4) });
        if (large)
            for (int i = scope.Resources.Count; i < 600; i++)
                scope.Resources.Add(new() { ResourceId = "large-membership-" + i, Kind = ResourceKind.File, Path = @"C:\Workspace\Source\File" + i + ".cs", AddedAtUtc = DateTimeOffset.UnixEpoch.AddDays(i) });
        Field<TextBox>(window, "_name").Text = scope.Name;
        Field<TextBox>(window, "_description").Text = scope.Description;
        Field<Grid>(window, "Details").IsEnabled = true;
        var grid = Field<DataGrid>(window, "_resources"); grid.ItemsSource = scope.Resources; grid.SelectedIndex = 0;
        return scope;
    }

    private static void CheckScopeLayout(OffscreenSurface surface, bool singleRow)
    {
        var root = surface.Root;
        var list = Descendants(root).OfType<ListBox>().Single();
        Require(Math.Abs(list.ActualWidth - 220) < 1, "The scope list must remain narrow.");
        var toolbar = Descendants(root).OfType<WrapPanel>().Single();
        var buttons = toolbar.Children.OfType<Button>().ToArray();
        Require(buttons.Select(b => b.Content as string).SequenceEqual(ResourceCommands), "Resource toolbar commands/order changed.");
        var bounds = buttons.Select(b => Bounds(b, toolbar)).ToArray();
        for (int i = 0; i < bounds.Length; i++)
        {
            Require(bounds[i].Left >= -1 && bounds[i].Right <= toolbar.ActualWidth + 1 && bounds[i].Top >= -1 && bounds[i].Bottom <= toolbar.ActualHeight + 1,
                "Resource toolbar button is outside its layout: " + buttons[i].Content);
            for (int j = i + 1; j < bounds.Length; j++)
                Require(!bounds[i].IntersectsWith(bounds[j]), "Resource toolbar buttons overlap.");
        }
        if (singleRow) Require(bounds.All(b => Math.Abs(b.Top - bounds[0].Top) < 1), "Resource toolbar must fit one row at a useful desktop width.");
        Require(toolbar.ActualWidth > list.ActualWidth, "Details must get the remaining width, not a fixed 520px.");
        foreach (var button in Descendants(root).OfType<Button>().Where(b => ScopeCommands.Contains(b.Content as string)))
        {
            var rect = Bounds(button, root);
            Require(rect.Left >= -1 && rect.Right <= root.ActualWidth + 1, "Scope command is clipped horizontally: " + button.Content);
        }
        var grid = Descendants(root).OfType<DataGrid>().Single();
        Require(grid.Columns.Select(c => Header(c.Header)).SequenceEqual(new[] { "Alias", "Type", "Path/Resource", "Added at" }), "Scope resource columns/order are incorrect.");
        Require(grid.Columns[0] is DataGridTemplateColumn { CellTemplate: not null, CellEditingTemplate: not null }, "Alias needs separate display-name and alias-edit templates.");
        string[] paths = [nameof(ScopedResource.ResourceTypeDisplay), nameof(ScopedResource.Details), nameof(ScopedResource.AddedDisplay)];
        for (int i = 0; i < paths.Length; i++)
        {
            Require(grid.Columns[i + 1] is DataGridTextColumn column && column.Binding is Binding binding && binding.Path.Path == paths[i], "Scope column has the wrong model binding.");
            Require(grid.Columns[i + 1].IsReadOnly, "Only the alias column should be editable.");
        }
        Require(!grid.Columns[0].IsReadOnly, "Alias must remain editable.");
        Require(grid.Columns[3].SortMemberPath == nameof(ScopedResource.AddedAtUtc), "Added-at sorting must use the actual timestamp, not its formatted string.");
        var unaliased = (ScopedResource)grid.Items[1];
        var display = grid.Columns[0].GetCellContent(unaliased);
        Require(display != null && Descendants(display).OfType<TextBlock>().Any(t => t.Text == unaliased.DisplayName && t.ToolTip as string == unaliased.DisplayName),
            "An empty alias must display the legacy resource-name fallback.");
        var added = (TextBlock)grid.Columns[3].Header;
        Require(added.ToolTip is string tooltip && tooltip.Contains("AddedAtUtc", StringComparison.Ordinal), "Added-at header must describe the actual timestamp.");
        Require(grid.Columns.All(c => c is not DataGridCheckBoxColumn), "IncludeChildren must not be an unexplained main-grid column.");
        var headers = Descendants(grid).OfType<DataGridColumnHeader>().Where(h => h.Column != null).ToArray();
        Require(headers.Length == 4, "The resource column headers were not rendered.");
        foreach (var header in headers)
        {
            CheckTextFits(header, Header(header.Content), 8);
            Require(Contrast(ColorOf(header.Background), ColorOf(header.Foreground)) >= 4.5, "Resource header contrast failed.");
        }
        foreach (var row in Descendants(grid).OfType<DataGridRow>())
            Require(Contrast(ColorOf(row.Background), ColorOf(row.Foreground)) >= 4.5, "Resource row contrast failed.");
        foreach (var cell in Descendants(grid).OfType<DataGridCell>())
            Require(Contrast(ColorOf(cell.Background), ColorOf(cell.Foreground)) >= 4.5, "Resource cell/selection contrast failed.");
        var horizontal = Descendants(grid).OfType<ScrollViewer>().FirstOrDefault(v => v.Name == "DG_ScrollViewer")
            ?? Descendants(grid).OfType<ScrollViewer>().First();
        if (!singleRow) Require(horizontal.ScrollableWidth > 0, "Reduced resource grid must expose horizontal scrolling rather than clip columns.");
        var detailsScroll = Descendants(root).OfType<ScrollViewer>().Single(v => v.Content is Grid);
        Require(detailsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Small layouts must retain vertical access to resource options.");
        detailsScroll.ScrollToBottom(); surface.Layout();
        var children = Descendants(root).OfType<CheckBox>().Single(c => c.Content as string == "Include children");
        var optionBounds = Bounds(children, detailsScroll);
        Require(optionBounds.Top >= -1 && optionBounds.Bottom <= detailsScroll.ActualHeight + 1, "Selected-resource options cannot be reached by scrolling.");
        detailsScroll.ScrollToTop(); surface.Layout();
        foreach (var text in Descendants(grid).OfType<TextBlock>().Where(t => t.DataContext is ScopedResource && !string.IsNullOrEmpty(t.Text)))
            Require(text.TextTrimming == TextTrimming.CharacterEllipsis && text.ToolTip as string == text.Text, "Long resource text must retain a full-value tooltip.");
    }

    private static void CheckResourceVirtualization(OffscreenSurface surface, int total)
    {
        var grid = Descendants(surface.Root).OfType<DataGrid>().Single();
        int realized = Descendants(grid).OfType<DataGridRow>().Count();
        Require(total > 500 && grid.Items.Count == total && double.IsFinite(grid.Height) && grid.ActualHeight >= 140 && grid.ActualHeight < surface.Root.ActualHeight,
            "Large resource sets must retain a finite DataGrid viewport.");
        Require(grid.EnableRowVirtualization && realized > 0 && realized < 80 && grid.ItemContainerGenerator.ContainerFromIndex(total - 1) == null,
            $"Large resource grid realized {realized}/{total} rows instead of virtualizing.");
    }

    private static void CheckScopeEditing(RelationalScopePickerWindow window, Scope scope, OffscreenSurface surface)
    {
        var baseline = StateCopies.Scope(scope, new(new StateLimits()));
        var grid = Field<DataGrid>(window, "_resources");
        var children = Field<CheckBox>(window, "_includeChildren");
        grid.SelectedIndex = 0; surface.Layout();
        Require(children.IsEnabled && children.IsChecked == true, "IncludeChildren must reflect the selected membership.");
        children.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        Require(!scope.Resources[0].IncludeChildren && !scope.Resources[1].IncludeChildren, "IncludeChildren edit must affect the selected membership only.");
        grid.SelectedIndex = 2; surface.Layout();
        Require(children.IsChecked == true && !children.IsEnabled && Field<Button>(window, "_editDatabase").IsEnabled,
            "Database selection must retain child metadata without offering an ineffective checkbox edit.");
        grid.SelectedIndex = 3; surface.Layout();
        Require(children.IsChecked == false && !children.IsEnabled, "Duplicate original IDs must retain each selected membership's own child metadata.");
        grid.SelectedIndex = 1; surface.Layout();
        Require(children.IsChecked == false && !children.IsEnabled, "File selection must retain child metadata without offering an ineffective checkbox edit.");
        grid.SelectedIndex = 4; surface.Layout();
        Require(!Field<Button>(window, "_editDatabase").IsEnabled && children.IsChecked == true && !children.IsEnabled,
            "Diagram selection must retain child metadata and disable database/child editing.");
        grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(scope.Resources[0], grid.Columns[0]); surface.Layout();
        grid.ScrollIntoView(scope.Resources[0], grid.Columns[0]); surface.Layout();
        Require(grid.BeginEdit(), "The alias cell could not enter its real edit control.");
        surface.Layout();
        var editor = Descendants(grid).OfType<TextBox>().Single();
        editor.Text = "Edited alias with a long value";
        editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Require(grid.CommitEdit(DataGridEditingUnit.Cell, true) && grid.CommitEdit(DataGridEditingUnit.Row, true), "Alias edit could not commit.");
        Require(scope.Resources[0].DisplayNameOverride == editor.Text, "Alias edit did not update the selected model.");
        surface.Layout();
        var displayed = grid.Columns[0].GetCellContent(scope.Resources[0]);
        Require(displayed != null && Descendants(displayed).OfType<TextBlock>().Any(t => t.Text == scope.Resources[0].DisplayName),
            "Committed alias must refresh the displayed resource name.");
        var copy = StateCopies.Scope(scope, new(new StateLimits()));
        Require(copy.Resources[0].DisplayNameOverride == "Edited alias with a long value" && !copy.Resources[0].IncludeChildren && copy.Resources[2].IncludeChildren,
            "Alias and child-resource edits must survive the existing state-copy boundary.");
        for (int i = 0; i < baseline.Resources.Count; i++)
        {
            var before = baseline.Resources[i]; var after = copy.Resources[i];
            Require(before.ResourceId == after.ResourceId && before.Kind == after.Kind && before.Path == after.Path &&
                before.DetailsOverride == after.DetailsOverride && before.AddedAtUtc == after.AddedAtUtc,
                "Resource edits must not rewrite identity, target address, details or added time.");
            if (i != 0) Require(before.DisplayNameOverride == after.DisplayNameOverride && before.IncludeChildren == after.IncludeChildren,
                "Resource editing changed another membership's alias or preserved child metadata.");
        }
        grid.SelectedItem = null; surface.Layout();
        Require(!children.IsEnabled && !Field<Button>(window, "_editDatabase").IsEnabled, "Resource edit controls must disable without a selection.");
        grid.SelectedIndex = 0; surface.Layout();
    }

    private static async Task CheckPaginationAsync(List<string> passed, string theme, CancellationToken ct)
    {
        var source = new PageSource();
        var picker = new PagingFixture(source);
        try
        {
            using var surface = new OffscreenSurface(picker, 784, 520);
            surface.Layout();
            var list = Descendants(surface.Root).OfType<ListBox>().Single();
            var back = Descendants(surface.Root).OfType<Button>().Single(b => b.ToolTip as string == "Previous page");
            var next = Descendants(surface.Root).OfType<Button>().Single(b => b.ToolTip as string == "Next page");
            var paging = (StackPanel)back.Parent;
            Require(paging.Visibility == Visibility.Collapsed && !back.IsEnabled && !next.IsEnabled, "Unloaded pager must not expose empty arrows.");
            foreach (int total in new[] { 0, 1, 100 })
            {
                ct.ThrowIfCancellationRequested(); source.Total = total; await picker.ReadFirstAsync(); surface.Layout();
                Require(list.Items.Count == total && paging.Visibility == Visibility.Collapsed, "At most 100 items must not show a pager.");
            }
            source.Total = 101; await picker.ReadFirstAsync(); surface.Layout();
            Require(list.Items.Count == 100 && paging.Visibility == Visibility.Visible && !back.IsEnabled && next.IsEnabled, "101 items must retain a bounded first page with Next.");
            Require(AutomationProperties.GetName(back) == "Previous page" && AutomationProperties.GetName(next) == "Next page" &&
                !string.IsNullOrEmpty(AutomationProperties.GetHelpText(next)) && ToolTipService.GetShowOnDisabled(back), "Pager labels/tooltips must be accessible even at page boundaries.");
            picker.CommandBusy(true);
            Require(!back.IsEnabled && !next.IsEnabled && paging.Visibility == Visibility.Visible, "Busy commands must disable, not remove, useful paging.");
            picker.CommandBusy(false);
            list.SelectedIndex = 0;
            Require(picker.SelectedSummary?.Name == "Item 0", "Page selection was not accepted.");
            int calls = source.Calls;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); surface.Layout();
            Require(source.Calls == calls + 1 && list.Items.Count == 1 && picker.SelectedSummary == null && back.IsEnabled && !next.IsEnabled,
                "Next must retain only the last page and clear selection.");
            Require(ReferenceEquals(source.LastCursor, source.Continuation), "Next lost its opaque continuation.");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); surface.Layout();
            Require(list.Items.Count == 100 && !back.IsEnabled && next.IsEnabled && source.LastCursor == null, "Back must restore the bounded first page.");
            source.Fail = true;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); surface.Layout();
            Require(list.Items.Count == 100 && next.IsEnabled && paging.Visibility == Visibility.Visible, "A failed page query must retain the current page and its pager.");
            source.Fail = false; source.Total = 100; await picker.ReadFirstAsync(); surface.Layout();
            Require(list.Items.Count == 100 && paging.Visibility == Visibility.Collapsed && !back.IsEnabled && !next.IsEnabled,
                "Reload to one page must clear stale page history and hide the pager.");
            source.Hold = true;
            Task loading = picker.ReadFirstAsync();
            Require(!loading.IsCompleted && !back.IsEnabled && !next.IsEnabled && !list.IsEnabled, "Loading must guard all navigation controls.");
            source.Complete(); await loading; surface.Layout();
            Require(list.Items.Count == 100 && paging.Visibility == Visibility.Collapsed && source.SawCancellationToken, "Held page query must complete using the picker lifetime.");
            CheckButtons(surface.Root, "paging", ["Open", "Close"]);
            Require(!picker.IsVisible && new WindowInteropHelper(picker).Handle == IntPtr.Zero, "Paging fixture must not show a window.");
            calls = source.Calls;
            Task cleanup = picker.DrainQueriesAsync(); await cleanup;
            Require(ReferenceEquals(cleanup, picker.DrainQueriesAsync()), "Picker cleanup must retain its stable task.");
            await picker.ReadFirstAsync();
            Require(source.Calls == calls, "A closed picker must not start another query.");
            passed.Add("Paging " + theme + ": 0/1/100/101 items, next/back, bounded pages, accessible labels, busy/loading/error/reload/close.");
        }
        finally { await picker.DrainQueriesAsync(); picker.Close(); }
    }

    private sealed record Item(string Name);
    private sealed class PageSource
    {
        public int Total, Calls;
        public bool Fail, Hold, SawCancellationToken;
        public object Continuation { get; } = new();
        public object? LastCursor;
        private TaskCompletionSource<RelationalPickerPage<Item>>? _pending;
        public Task<RelationalPickerPage<Item>> Read(object? cursor, CancellationToken ct)
        {
            Calls++; LastCursor = cursor; SawCancellationToken |= ct.CanBeCanceled; ct.ThrowIfCancellationRequested();
            if (Fail) return Task.FromException<RelationalPickerPage<Item>>(new InvalidOperationException("Fixture page failure."));
            if (Hold) { _pending = new(); return _pending.Task; }
            return Task.FromResult(Page(cursor));
        }
        private RelationalPickerPage<Item> Page(object? cursor)
        {
            int start = cursor == null ? 0 : 100;
            return new(Enumerable.Range(start, Math.Min(100, Math.Max(0, Total - start))).Select(i => new Item("Item " + i)).ToArray(),
                cursor == null && Total > 100 ? Continuation : null);
        }
        public void Complete() { Hold = false; _pending!.SetResult(Page(LastCursor)); }
    }
    private sealed class PagingFixture(PageSource source) : RelationalQueryPickerWindow<Item>("Fixture items", source.Read, i => i.Name)
    {
        public Task ReadFirstAsync() => ReloadAsync();
        public void CommandBusy(bool busy) => SetPickerCommandBusy(busy);
    }

    private sealed class OffscreenSurface : IDisposable
    {
        private readonly Window _window;
        public FrameworkElement Root { get; }
        private readonly Grid _surface;
        public OffscreenSurface(Window window, int width, int height)
        {
            _window = window; Root = window.Content as FrameworkElement ?? throw new InvalidOperationException("The fixture has no visual root.");
            window.Content = null;
            _surface = new Grid { Width = width, Height = height, Background = window.Background, Resources = window.Resources };
            _surface.Children.Add(Root);
        }
        public void Layout()
        {
            _surface.Measure(new(_surface.Width, _surface.Height));
            _surface.Arrange(new Rect(0, 0, _surface.Width, _surface.Height)); _surface.UpdateLayout();
        }
        public string Save(string path)
        {
            int width = (int)_surface.Width, height = (int)_surface.Height;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(_surface);
            byte[] pixels = new byte[checked(width * height * 4)]; bitmap.CopyPixels(pixels, width * 4, 0);
            Require(pixels.Where((_, i) => i % 4 != 3).Any(b => b != pixels[0]), "The picker render is blank.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(stream);
            return Convert.ToHexString(SHA256.HashData(pixels));
        }
        public void Dispose() { _surface.Children.Remove(Root); _window.Content = Root; }
    }

    private static void CheckButtons(FrameworkElement root, string name, string[] required)
    {
        var buttons = Descendants(root).OfType<Button>().ToArray();
        foreach (string label in required)
        {
            var button = buttons.FirstOrDefault(b => b.Content as string == label)
                ?? throw new InvalidOperationException(name + " fixture is missing button: " + label);
            Require(button.Style != null && Contrast(ColorOf(button.Background), ColorOf(button.Foreground)) >= 4.5,
                name + " button has no conforming style/contrast: " + label);
            CheckTextFits(button, label, button.Padding.Left + button.Padding.Right);
        }
    }
    private static void CheckTextFits(Control control, string label, double inset)
    {
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch), control.FontSize, control.Foreground, 1);
        Require(control.ActualWidth > 0 && text.Width <= control.ActualWidth - inset + 1, "Control text is clipped: " + label);
    }
    private static string Header(object header) => header is TextBlock text ? text.Text : header as string ?? string.Empty;
    private static Color ColorOf(Brush brush) => (brush as SolidColorBrush)?.Color ?? throw new InvalidOperationException("A fixture brush is not a solid theme color.");
    private static Rect Bounds(FrameworkElement child, Visual parent) => child.TransformToAncestor(parent).TransformBounds(new Rect(new Point(), child.RenderSize));
    private static T Field<T>(object owner, string name)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) is T value) return value;
        throw new InvalidOperationException("Missing fixture field: " + name);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Light(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        double a = Light(first), b = Light(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}

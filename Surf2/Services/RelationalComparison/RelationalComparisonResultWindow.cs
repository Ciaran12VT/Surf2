using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Storage.Relational.Access;

namespace Surf2.Services.RelationalComparison;

internal sealed class RelationalComparisonResultWindow : Window
{
    private ComparisonResultStore _result;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false };
    private readonly TextBox _search = new() { Width = 220, Margin = new(8, 0, 8, 0) };
    private readonly CheckBox _different = new() { Content = "Differences only", VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 8, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous = Button("<", "Previous page"), _next = Button(">", "Next page"),
        _export = Button("Export", "Export all matching comparison rows", "SurfSaveButtonStyle");
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _tasks = [];
    private readonly AccessCancellation _lifetime = new();
    private AccessCancellation? _pageCancellation;
    private readonly Func<ComparisonResultRow, CancellationToken, Task>? _open;
    private readonly Func<ComparisonOptions, CancellationToken, Task<ComparisonResultStore>>? _rebuild;
    private CheckBox? _whitespace, _case;
    private long _offset, _generation;
    private bool _closing, _rebuilding;
    private Task? _cleanup;
    public Task Disposal => _cleanup ?? Task.CompletedTask;

    public RelationalComparisonResultWindow(ComparisonResultStore result, string title,
        Func<ComparisonResultRow, CancellationToken, Task>? open = null,
        Func<ComparisonOptions, CancellationToken, Task<ComparisonResultStore>>? rebuild = null,
        ComparisonOptions? options = null)
    {
        _result = result; _open = open; _rebuild = rebuild;
        Title = title; Width = 1180; Height = 720; MinWidth = 680; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new(12) };
        var toolbar = new WrapPanel { Margin = new(0, 0, 0, 10) }; DockPanel.SetDock(toolbar, Dock.Top);
        toolbar.Children.Add(_previous); toolbar.Children.Add(_next); toolbar.Children.Add(_search); toolbar.Children.Add(_different); toolbar.Children.Add(_export);
        if (rebuild != null)
        {
            _whitespace = new() { Content = "Ignore whitespace", IsChecked = options?.IgnoreWhitespace == true, Margin = new(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _case = new() { Content = "Ignore case", IsChecked = options?.IgnoreCase == true, Margin = new(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            toolbar.Children.Add(_whitespace); toolbar.Children.Add(_case);
            _whitespace.Click += (_, _) => Start(RebuildAsync); _case.Click += (_, _) => Start(RebuildAsync);
        }
        layout.Children.Add(toolbar); DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status); layout.Children.Add(_grid); Content = layout;
        AddColumn("Key / Path", nameof(ComparisonResultRow.Key), 240); AddColumn("Status", nameof(ComparisonResultRow.Status), 110);
        AddColumn("Changed Columns", nameof(ComparisonResultRow.ChangedColumns), 190);
        AddColumn("Left", nameof(ComparisonResultRow.LeftPreview), double.NaN); AddColumn("Right", nameof(ComparisonResultRow.RightPreview), double.NaN);
        _grid.Sorting += (_, e) => e.Handled = true; // Sorting one resident page would falsely imply a global sort.
        if (open != null)
        {
            _grid.MouseDoubleClick += (_, _) => Start(OpenAsync);
            var menu = new ContextMenu();
            var compare = new MenuItem { Header = "Open Comparison" }; compare.Click += (_, _) => Start(OpenAsync); menu.Items.Add(compare);
            var with = new MenuItem { Header = "Compare With..." }; with.Click += (_, _) => Start(CompareWithAsync); menu.Items.Add(with);
            var toggle = new MenuItem { Header = "Collapse" }; toggle.Click += (_, _) =>
            {
                if (_grid.SelectedItem is not ComparisonResultRow { IsCollection: true } row) return;
                if (!_collapsed.Remove(row.Key))
                {
                    if (_collapsed.Count == 4096) { _status.Text = "The collapse limit has been reached."; return; }
                    _collapsed.Add(row.Key);
                }
                _offset = 0; Start(RefreshAsync);
            }; menu.Items.Add(toggle);
            menu.Opened += (_, _) =>
            {
                toggle.IsEnabled = _grid.SelectedItem is ComparisonResultRow { IsCollection: true };
                toggle.Header = _grid.SelectedItem is ComparisonResultRow r && _collapsed.Contains(r.Key) ? "Expand" : "Collapse";
            };
            var exclude = new MenuItem { Header = "Exclude" }; exclude.Click += (_, _) =>
            {
                if (_grid.SelectedItem is not ComparisonResultRow row) return;
                if (_excluded.Count == 4096) { _status.Text = "The exclusion limit has been reached."; return; }
                _excluded.Add(row.Key); _offset = 0; Start(RefreshAsync);
            }; menu.Items.Add(exclude);
            var reset = new MenuItem { Header = "Clear Exclusions" }; reset.Click += (_, _) => { _excluded.Clear(); _offset = 0; Start(RefreshAsync); }; menu.Items.Add(reset);
            _grid.ContextMenu = menu;
            _grid.PreviewMouseRightButtonDown += (_, e) =>
            {
                if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is DataGridRow row) row.IsSelected = true;
            };
        }
        _previous.Click += (_, _) => { _offset = Math.Max(0, _offset - _result.PageSize); Start(RefreshAsync); };
        _next.Click += (_, _) => { _offset += _result.PageSize; Start(RefreshAsync); };
        _search.TextChanged += (_, _) => { _offset = 0; Start(RefreshAsync); };
        _different.Click += (_, _) => { _offset = 0; Start(RefreshAsync); };
        _export.Click += (_, _) => Start(ExportAsync);
        Loaded += (_, _) => Start(RefreshAsync);
        Closed += (_, _) => _cleanup ??= CleanupAsync();
    }
    private void AddColumn(string title, string property, double width) => _grid.Columns.Add(new DataGridTextColumn
    { Header = title, Binding = new Binding(property), Width = double.IsNaN(width) ? new(1, DataGridLengthUnitType.Star) : new(width) });
    private static Button Button(string content, string tooltip, string style = "SurfPrimaryButtonStyle")
    {
        var button = new Button { Content = content, ToolTip = tooltip, MinWidth = 34, Padding = new(8, 5, 8, 5), Margin = new(0, 0, 4, 0) };
        button.SetResourceReference(StyleProperty, style);
        return button;
    }
    private void Start(Func<CancellationToken, Task> action)
    {
        if (_closing) return;
        Task task = RunAsync(); _tasks.Add(task);
        async Task RunAsync()
        {
            await Task.Yield();
            try { _lifetime.Token.ThrowIfCancellationRequested(); await action(_lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closing) _status.Text = "Comparison failed: " + ex.Message; }
        }
        _ = RetireAsync(task);
        async Task RetireAsync(Task work) { await work; _tasks.Remove(work); }
    }
    private async Task RefreshAsync(CancellationToken ct)
    {
        _ = _pageCancellation?.Cancel();
        var owner = new AccessCancellation(); _pageCancellation = owner;
        using var registration = ct.Register(() => _ = owner.Cancel());
        try
        {
        long generation = ++_generation; string search = _search.Text; bool differences = _different.IsChecked == true;
        long offset = _offset; string[] excluded = _excluded.ToArray(); string[] collapsed = _collapsed.ToArray();
        await Task.Delay(150, owner.Token);
        if (generation != _generation || _rebuilding) return;
        var page = await _result.ReadPageAsync(offset, search, differences, excluded.ToHashSet(StringComparer.OrdinalIgnoreCase), owner.Token,
            collapsed.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (_closing || generation != _generation) return;
        _grid.ItemsSource = page.Rows; _previous.IsEnabled = offset > 0; _next.IsEnabled = page.HasNext;
        _status.Text = $"{page.MatchingCount:N0} matching of {_result.Count:N0} results. Showing {page.Rows.Count:N0} from {offset + 1:N0}. " +
            string.Join(", ", _result.StatusCounts.Select(x => $"{x.Value:N0} {x.Key.ToLowerInvariant()}"));
        }
        finally { if (ReferenceEquals(_pageCancellation, owner)) _pageCancellation = null; await owner.RetireAsync(); }
    }
    private async Task OpenAsync(CancellationToken ct)
    {
        if (_rebuilding || _open == null || _grid.SelectedItem is not ComparisonResultRow row || row.IsCollection) return;
        await _open(row, ct);
    }
    private async Task CompareWithAsync(CancellationToken ct)
    {
        if (_rebuilding || _open == null) return;
        var choices = await _result.ReadDocumentChoicesAsync(_search.Text, _different.IsChecked == true, _excluded, ct);
        if (_closing || _rebuilding) return;
        if (!choices.Complete)
            MessageBox.Show(this, "The resource choices reached their metadata limit. Narrow the comparison search for further choices.",
                "Comparison choices", MessageBoxButton.OK, MessageBoxImage.Information);
        if (choices.Left.Count == 0 && choices.Right.Count == 0) { _status.Text = "There are no comparable documents."; return; }
        var selected = _grid.SelectedItem as ComparisonResultRow;
        var dialog = new ResourceCompareWithWindow("Left", "Right", choices.Left.Select(x => x.Document).ToArray(),
            choices.Right.Select(x => x.Document).ToArray(),
            choices.Left.FirstOrDefault(x => x.Target.Resource.IdentityKey == selected?.Left?.Resource.IdentityKey)?.Document,
            choices.Right.FirstOrDefault(x => x.Target.Resource.IdentityKey == selected?.Right?.Resource.IdentityKey)?.Document) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedDocument1 == null || dialog.SelectedDocument2 == null || _closing) return;
        var all = choices.Left.Concat(choices.Right);
        var left = all.Single(x => ReferenceEquals(x.Document, dialog.SelectedDocument1)).Target;
        var right = all.Single(x => ReferenceEquals(x.Document, dialog.SelectedDocument2)).Target;
        await _open(new("", "Different", "", "", "", Left: left, Right: right), ct);
    }
    private async Task ExportAsync(CancellationToken ct)
    {
        if (_rebuilding) return;
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", FileName = "Comparison.csv" };
        if (dialog.ShowDialog(this) != true) return;
        _export.IsEnabled = false;
        try
        {
            await _result.ExportAsync(dialog.FileName, _search.Text, _different.IsChecked == true, _excluded, ct);
            if (!_closing) _status.Text = "Exported all matching comparison rows.";
        }
        finally { if (!_closing) _export.IsEnabled = true; }
    }
    private async Task RebuildAsync(CancellationToken ct)
    {
        if (_rebuilding || _rebuild == null) return;
        var whitespace = _whitespace ?? throw new InvalidOperationException("Collection options are unavailable.");
        var ignoreCase = _case ?? throw new InvalidOperationException("Collection options are unavailable.");
        _rebuilding = true; _grid.IsEnabled = _export.IsEnabled = false;
        whitespace.IsEnabled = ignoreCase.IsEnabled = false;
        try
        {
            _status.Text = "Comparing collections...";
            var replacement = await _rebuild(new(whitespace.IsChecked == true, ignoreCase.IsChecked == true), ct);
            if (_closing) { await replacement.DisposeAsync(); return; }
            var old = _result; _result = replacement; await old.DisposeAsync(); _offset = 0;
        }
        finally
        {
            _rebuilding = false;
            if (!_closing) { _grid.IsEnabled = _export.IsEnabled = whitespace.IsEnabled = ignoreCase.IsEnabled = true; await RefreshAsync(ct); }
        }
    }
    public Task CloseAndDisposeAsync()
    {
        if (!_closing) Close();
        return _cleanup ??= CleanupAsync();
    }
    private async Task CleanupAsync()
    {
        _closing = true; ++_generation;
        try
        {
            await _lifetime.Cancel();
            await Task.WhenAll(_tasks.ToArray());
        }
        finally
        {
            try { await _result.DisposeAsync(); }
            finally { await _lifetime.RetireAsync(); }
        }
    }
}

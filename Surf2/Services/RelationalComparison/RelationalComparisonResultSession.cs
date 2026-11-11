using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access;

namespace Surf2.Services.RelationalComparison;

// Presents bounded relational pages in the same forms used for legacy comparisons.
internal sealed class RelationalComparisonResultSession
{
    private ComparisonResultStore _result;
    private readonly Selector _rows;
    private readonly TextBox _search;
    private readonly TextBlock _status;
    private readonly Button _previous = Button("<", "Previous page"), _next = Button(">", "Next page");
    private readonly StackPanel _paging = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Control _export;
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<long> _previousOffsets = [];
    private readonly HashSet<Task> _tasks = [];
    private readonly AccessCancellation _lifetime = new();
    private readonly ComparisonResource _left, _right;
    private readonly IReadOnlyList<string> _keyColumns = [];
    private readonly ResourceCollectionDiffWindow? _collection;
    private readonly TableDataDiffWindow? _table;
    private readonly Func<ComparisonResultRow, CancellationToken, Task>? _open;
    private readonly Func<ComparisonOptions, CancellationToken, Task<ComparisonResultStore>>? _rebuild;
    private IReadOnlyDictionary<string, ComparisonResultRow> _pageRows = new Dictionary<string, ComparisonResultRow>();
    private ComparisonOptions? _options;
    private AccessCancellation? _pageCancellation;
    private long _offset, _nextOffset, _generation;
    private bool _closing, _rebuilding, _differencesOnly;
    private Task? _cleanup;

    public Window View { get; }
    public Task Disposal => _cleanup ?? Task.CompletedTask;

    public RelationalComparisonResultSession(ComparisonResultStore result, ComparisonResource left, ComparisonResource right,
        Func<ComparisonResultRow, CancellationToken, Task> open,
        Func<ComparisonOptions, CancellationToken, Task<ComparisonResultStore>> rebuild, ComparisonOptions options)
    {
        _result = result;
        _left = left;
        _right = right;
        _open = open;
        _rebuild = rebuild;
        _options = options;
        _collection = new ResourceCollectionDiffWindow(left, right, options, this);
        View = _collection;
        _rows = _collection.RowsListView;
        _search = _collection.SearchTextBox;
        _status = _collection.StatusTextBlock;
        _export = _collection.ExportCsvButton;
        _collection.DifferencesOnlyCheckBox.Visibility = _collection.ClearExclusionsButton.Visibility = _export.Visibility = Visibility.Visible;
        _collection.DifferencesOnlyCheckBox.Click += (_, _) => SetDifferencesOnly(_collection.DifferencesOnlyCheckBox.IsChecked == true);
        _collection.ClearExclusionsButton.Click += (_, _) => { _excluded.Clear(); ApplyExclusions(); };
        _collection.ExportCsvButton.Click += (_, _) => Start(ExportAsync);
        _collection.IgnoreWhitespaceCheckBox.Click += (_, _) => Start(RebuildAsync);
        _collection.IgnoreCaseCheckBox.Click += (_, _) => Start(RebuildAsync);
        if (left.Kind == ComparisonResourceKind.DatabaseSnapshot && right.Kind == ComparisonResourceKind.DatabaseSnapshot)
        {
            _collapsed.UnionWith(Enum.GetValues<ExplorerCategory>().Select(ExplorerCompatibility.CategoryName));
            _collapsed.Add("Full Table Data");
        }
        ConfigurePaging(_collection.ResultCommands);
    }

    public RelationalComparisonResultSession(ComparisonResultStore result, ComparisonResource left, ComparisonResource right,
        IReadOnlyList<string> keyColumns)
    {
        _result = result;
        _left = left;
        _right = right;
        _keyColumns = keyColumns;
        var layout = result.TableLayout ?? throw new InvalidOperationException("Table comparison column metadata is unavailable.");
        var view = new TableDataDiffWindow(left, right, layout);
        _table = view;
        View = view;
        _rows = view.RowsDataGrid;
        _search = view.SearchTextBox;
        _status = view.StatusTextBlock;
        // A column sort on the resident page would imply a global sort.
        view.RowsDataGrid.CanUserSortColumns = false;
        var export = new MenuItem { Header = "Export CSV…" };
        export.Click += (_, _) => Start(ExportAsync);
        _export = export;
        ConfigurePaging(view.ResultCommands, CreateTableOptions(view.FindPanel, export));
    }

    private void ConfigurePaging(ContentControl host, Button? options = null)
    {
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(8, 0, 0, 0) };
        _previous.IsEnabled = _next.IsEnabled = false;
        _paging.Children.Add(_previous);
        _paging.Children.Add(_next);
        footer.Children.Add(_paging);
        if (options != null) footer.Children.Add(options);
        host.Content = footer;

        _previous.Click += (_, _) =>
        {
            if (_previous.IsEnabled && _previousOffsets.TryPop(out long offset)) RequestPage(offset);
        };
        _next.Click += (_, _) =>
        {
            if (!_next.IsEnabled) return;
            _previousOffsets.Push(_offset);
            RequestPage(_nextOffset);
        };
        _search.TextChanged += (_, _) => RequestFirstPage();
        View.Loaded += (_, _) => RequestFirstPage();
        View.Closed += (_, _) => _cleanup ??= CleanupAsync();
    }

    private Button CreateTableOptions(FrameworkElement findPanel, MenuItem export)
    {
        var options = Button("…", "Comparison options");
        var menu = new ContextMenu { PlacementTarget = options, Placement = PlacementMode.Bottom };
        var find = new MenuItem { Header = "Find…", IsCheckable = true };
        find.Click += (_, _) =>
        {
            findPanel.Visibility = find.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            if (find.IsChecked) _search.Focus();
            else _search.Clear();
        };
        var different = new MenuItem { Header = "Differences Only", IsCheckable = true, StaysOpenOnClick = true };
        different.Click += (_, _) => SetDifferencesOnly(different.IsChecked);
        menu.Items.Add(find);
        menu.Items.Add(different);
        menu.Items.Add(new Separator());
        menu.Items.Add(export);
        options.ContextMenu = menu;
        options.Click += (_, _) => menu.IsOpen = true;
        return options;
    }

    private void SetDifferencesOnly(bool enabled)
    {
        _differencesOnly = enabled;
        RequestFirstPage();
    }

    private void RequestFirstPage()
    {
        _previousOffsets.Clear();
        RequestPage(0);
    }

    private static Button Button(string content, string tooltip)
    {
        var button = new Button { Content = content, ToolTip = tooltip, MinWidth = 34, Padding = new(8, 5, 8, 5), Margin = new(0, 0, 4, 0) };
        AutomationProperties.SetName(button, tooltip);
        button.SetResourceReference(FrameworkElement.StyleProperty, "SurfPrimaryButtonStyle");
        return button;
    }

    public void Open(ResourceCollectionDiffRow row)
    {
        if (row.CanOpenDiff && _pageRows.TryGetValue(row.RelativePath, out var target))
            Start(ct => OpenAsync(target, ct));
    }

    public void Toggle(ResourceCollectionDiffRow row)
    {
        if (!row.IsFolder || _closing || _rebuilding) return;
        if (!_collapsed.Remove(row.RelativePath))
        {
            if (_collapsed.Count >= 4096) { _status.Text = "The collapse limit has been reached."; return; }
            _collapsed.Add(row.RelativePath);
        }
        row.IsExpanded = !_collapsed.Contains(row.RelativePath);
        RequestPage(_offset);
    }

    public void Exclude(ResourceCollectionDiffRow row)
    {
        if (_closing || _rebuilding || _excluded.Contains(row.RelativePath)) return;
        if (_excluded.Count >= 4096) { _status.Text = "The exclusion limit has been reached."; return; }
        _excluded.Add(row.RelativePath);
        ApplyExclusions();
    }

    public void CompareWith() => Start(CompareWithAsync);

    private void ApplyExclusions()
    {
        if (_collection != null) _collection.ClearExclusionsButton.IsEnabled = _excluded.Count > 0 && !_rebuilding;
        if (_rows.ItemsSource is not ResourceCollectionDiffRow[] rows) return;
        foreach (var row in rows)
        {
            string path = row.RelativePath;
            bool excluded = _excluded.Contains(path);
            for (int separator = path.LastIndexOf('/'); !excluded && separator > 0; separator = path.LastIndexOf('/', separator - 1))
                excluded = _excluded.Contains(path[..separator]);
            row.IsExcluded = excluded;
        }
    }

    private void RequestPage(long offset)
    {
        if (_closing) return;
        _offset = offset;
        ++_generation;
        _ = _pageCancellation?.Cancel();
        _previous.IsEnabled = _next.IsEnabled = false;
        Start(RefreshAsync);
    }

    private void Start(Func<CancellationToken, Task> action)
    {
        if (_closing) return;
        Task task = RunAsync();
        _tasks.Add(task);
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
        var owner = new AccessCancellation();
        _pageCancellation = owner;
        using var registration = ct.Register(() => _ = owner.Cancel());
        try
        {
            long generation = ++_generation;
            string search = _search.Text.Trim();
            bool differences = _differencesOnly;
            long offset = _offset;
            // Find can reveal results inside collapsed folders, as in the original form.
            var collapsed = search.Length == 0 ? _collapsed.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            await Task.Delay(150, owner.Token);
            if (generation != _generation || _rebuilding) return;
            var page = await _result.ReadPageAsync(offset, search, differences, ct: owner.Token, collapsed: collapsed);
            if (_closing || generation != _generation || _rebuilding) return;
            if (_collection != null)
            {
                _pageRows = page.Rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);
                _rows.ItemsSource = page.Rows.Select(CreateCollectionRow).ToArray();
                ApplyExclusions();
            }
            else
            {
                _table!.SetComparisonPage(page.Rows);
            }
            _nextOffset = page.Offset + page.Rows.Count;
            _previous.IsEnabled = _previousOffsets.Count > 0;
            _next.IsEnabled = page.HasNext;
            _paging.Visibility = offset > 0 || page.HasNext ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatus(page, search.Length > 0 || differences);
        }
        finally
        {
            if (ReferenceEquals(_pageCancellation, owner)) _pageCancellation = null;
            await owner.RetireAsync();
        }
    }

    private ResourceCollectionDiffRow CreateCollectionRow(ComparisonResultRow row)
    {
        var status = row.Status switch
        {
            "Identical" => ResourceComparisonStatus.Identical,
            "Missing left" => ResourceComparisonStatus.MissingLeft,
            "Missing right" => ResourceComparisonStatus.MissingRight,
            _ => ResourceComparisonStatus.Different
        };
        return new ResourceCollectionDiffRow(row.Key.Count(c => c == '/'), row.Key, row.LeftPreview, row.RightPreview, status,
            Document(row.Left), Document(row.Right)) { IsExpanded = !_collapsed.Contains(row.Key) };

        ResourceComparisonDocument? Document(RelationalComparisonTarget? target) => target == null ? null :
            new(row.Key, target.Resource.DisplayName, target.Resource.Kind, target.Resource.IsCollection, "", target.Resource.SyntaxPath, target.Resource);
    }

    private void UpdateStatus(ComparisonResultPage page, bool filtered)
    {
        string shown = page.HasNext || page.Offset > 0
            ? $" Showing {page.Offset + 1:N0}–{page.Offset + page.Rows.Count:N0} of {page.MatchingCount:N0}."
            : filtered ? $" Showing {page.Rows.Count:N0} of {_result.Count:N0}." : "";
        if (_collection != null)
        {
            _status.Text = $"{_result.Count:N0} item(s).{shown} Double-click a changed file, database object, table, or table data row to inspect details.";
            return;
        }
        long Count(string status) => _result.StatusCounts.GetValueOrDefault(status);
        _status.Text = $"Key: {string.Join(", ", _keyColumns)}. {Count("Identical"):N0} identical, {Count("Altered"):N0} altered, " +
            $"{Count("Added"):N0} added, {Count("Removed"):N0} removed.{shown}";
    }

    private async Task OpenAsync(ComparisonResultRow row, CancellationToken ct)
    {
        if (!_rebuilding && _open != null && !row.IsCollection) await _open(row, ct);
    }

    private async Task CompareWithAsync(CancellationToken ct)
    {
        if (_rebuilding || _open == null) return;
        var choices = await _result.ReadDocumentChoicesAsync(_search.Text.Trim(), _differencesOnly, _excluded, ct);
        if (_closing || _rebuilding) return;
        if (!choices.Complete)
            MessageBox.Show(View, "The resource choices reached their metadata limit. Narrow the comparison search for further choices.",
                "Comparison choices", MessageBoxButton.OK, MessageBoxImage.Information);
        if (_closing || _rebuilding) return;
        ct.ThrowIfCancellationRequested();
        if (choices.Left.Count == 0 && choices.Right.Count == 0) { _status.Text = "There are no comparable documents."; return; }
        ComparisonResultRow? selected = _rows.SelectedItem is ResourceCollectionDiffRow selectedRow
            ? _pageRows.GetValueOrDefault(selectedRow.RelativePath) : null;
        var defaultLeft = choices.Left.FirstOrDefault(x => x.Target.Resource.IdentityKey == selected?.Left?.Resource.IdentityKey)?.Document
            ?? choices.Left.FirstOrDefault()?.Document;
        var defaultRight = choices.Right.FirstOrDefault(x => x.Target.Resource.IdentityKey == selected?.Right?.Resource.IdentityKey)?.Document
            ?? choices.Right.FirstOrDefault(x => string.Equals(x.Document.RelativePath, defaultLeft?.RelativePath, StringComparison.OrdinalIgnoreCase))?.Document;
        var dialog = new ResourceCompareWithWindow(_left.DisplayName, _right.DisplayName,
            choices.Left.Select(x => x.Document).ToArray(), choices.Right.Select(x => x.Document).ToArray(), defaultLeft, defaultRight) { Owner = View };
        if (dialog.ShowDialog() != true || dialog.SelectedDocument1 == null || dialog.SelectedDocument2 == null || _closing) return;
        var all = choices.Left.Concat(choices.Right);
        var left = all.Single(x => ReferenceEquals(x.Document, dialog.SelectedDocument1)).Target;
        var right = all.Single(x => ReferenceEquals(x.Document, dialog.SelectedDocument2)).Target;
        await _open(new("", "Different", "", "", "", Left: left, Right: right), ct);
    }

    private async Task ExportAsync(CancellationToken ct)
    {
        if (_rebuilding || !_export.IsEnabled) return;
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", FileName = "Comparison.csv" };
        if (dialog.ShowDialog(View) != true) return;
        _export.IsEnabled = false;
        try
        {
            await _result.ExportAsync(dialog.FileName, _search.Text.Trim(), _differencesOnly, _excluded, ct);
            if (!_closing) _status.Text = "Exported all matching comparison rows.";
        }
        finally { if (!_closing) _export.IsEnabled = true; }
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        if (_rebuilding || _rebuild == null || _collection == null) return;
        var whitespace = _collection.IgnoreWhitespaceCheckBox;
        var ignoreCase = _collection.IgnoreCaseCheckBox;
        var requested = new ComparisonOptions(whitespace.IsChecked == true, ignoreCase.IsChecked == true);
        if (requested == _options) return;
        _rebuilding = true;
        ++_generation;
        _ = _pageCancellation?.Cancel();
        _rows.IsEnabled = _export.IsEnabled = whitespace.IsEnabled = ignoreCase.IsEnabled = false;
        _collection.ClearExclusionsButton.IsEnabled = false;
        _previous.IsEnabled = _next.IsEnabled = false;
        try
        {
            _status.Text = "Comparing collections...";
            var replacement = await _rebuild(requested, ct);
            if (_closing) { await replacement.DisposeAsync(); return; }
            var old = _result;
            _result = replacement;
            _options = requested;
            await old.DisposeAsync();
            _offset = 0;
            _previousOffsets.Clear();
        }
        finally
        {
            _rebuilding = false;
            if (!_closing)
            {
                whitespace.IsChecked = _options!.IgnoreWhitespace;
                ignoreCase.IsChecked = _options.IgnoreCase;
                _rows.IsEnabled = _export.IsEnabled = whitespace.IsEnabled = ignoreCase.IsEnabled = true;
                _collection.ClearExclusionsButton.IsEnabled = _excluded.Count > 0;
                await RefreshAsync(ct);
            }
        }
    }

    public Task CloseAndDisposeAsync()
    {
        if (!_closing) View.Close();
        return _cleanup ??= CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        _closing = true;
        ++_generation;
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

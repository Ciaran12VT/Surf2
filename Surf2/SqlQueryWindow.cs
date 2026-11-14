using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.LiveSql;
using Surf2.Services.RelationalGrid;
using Surf2.Services.RelationalSnapshots;

namespace Surf2;

public sealed class SqlQueryWindow : Window
{
    public sealed class ParameterRow
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "NVarChar";
        public string Value { get; set; } = "";
        public bool IsNull { get; set; }
        public LiveSqlParameter ToParameter()
        {
            if (!Enum.TryParse<SqlDbType>(Type, true, out var type)) throw new ArgumentException("Unknown parameter SQL type.");
            object? value = IsNull ? null : type switch
            {
                SqlDbType.Int => int.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.BigInt => long.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.SmallInt => short.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.TinyInt => byte.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.Decimal => System.Data.SqlTypes.SqlDecimal.Parse(Value),
                SqlDbType.Float => double.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.Real => float.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.Bit => bool.Parse(Value),
                SqlDbType.Date or SqlDbType.DateTime2 => DateTime.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.DateTimeOffset => DateTimeOffset.Parse(Value, CultureInfo.InvariantCulture),
                SqlDbType.UniqueIdentifier => Guid.Parse(Value),
                SqlDbType.VarBinary => Convert.FromHexString(Value),
                SqlDbType.NVarChar or SqlDbType.VarChar => Value,
                _ => throw new ArgumentException("Unsupported parameter type: " + type)
            };
            return new(Name, type, value, type is SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.VarBinary ? -1 : 0);
        }
    }
    private readonly LiveSqlService _service;
    private readonly TextBox _sql = new() { Name = "SqlEditor", AcceptsReturn = true, AcceptsTab = true,
        FontFamily = new("Consolas"), FontSize = 14, Padding = new(8),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DataGrid _parameters = new() { Name = "QueryParametersGrid", AutoGenerateColumns = false, Height = 110, CanUserAddRows = true };
    private readonly ObservableCollection<ParameterRow> _parameterRows = [];
    private readonly DataGrid _grid = new() { Name = "ResultsGrid", IsReadOnly = true, AutoGenerateColumns = false,
        CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true };
    private readonly TextBlock _status = new() { Name = "QueryStatusTextBlock", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock _emptyResults = new() { Text = "Execute a query to see results.",
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBox _rowLimit = new() { Name = "MaximumRowsTextBox", Text = "10000", Width = 80, Margin = new(6, 3, 0, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _run = SnapshotWindowStyles.Command("Execute");
    private readonly Button _cancel = SnapshotWindowStyles.Command("Cancel");
    private readonly Button _previous = SnapshotWindowStyles.Command("Previous page");
    private readonly Button _next = SnapshotWindowStyles.Command("Next page");
    private readonly WrapPanel _resultTools = new() { Margin = new(0, 2, 0, 6) };
    private LiveSqlResult? _result;
    private LiveSqlGridSource? _adapter;
    private IGridQuerySession? _view;
    private readonly ComboBox _filterColumn = new() { Name = "ResultFilterColumn", Width = 160, Margin = new(8, 3, 3, 3), DisplayMemberPath = nameof(LiveSqlColumn.Name) };
    private readonly TextBox _filter = new() { Name = "ResultFilterTextBox", Width = 150, Margin = new(3), VerticalContentAlignment = VerticalAlignment.Center };
    private GridFilter[] _filters = [];
    private GridSort[] _sorts = [];
    private CancellationTokenSource? _execution;
    private readonly CancellationTokenSource _lifetime = new();
    private int _page, _generation;
    private bool _closed;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public Task CloseCompletion { get; private set; } = Task.CompletedTask;

    internal static string InitialQuery(string? schema = null, string? objectName = null) => objectName == null
        ? string.Empty : "SELECT TOP (1000) *\nFROM " + SqlName.FormatMultipartName(schema ?? "dbo", objectName) + ";";

    public SqlQueryWindow(LiveSqlService service, string? sql = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Title = "SQL Query — " + service.Server + "/" + service.Database;
        Width = 1150; Height = 780; MinWidth = 720; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SnapshotWindowStyles.ThemeWindow(this);
        foreach (var input in new Control[] { _sql, _rowLimit, _filter, _filterColumn }) SnapshotWindowStyles.ThemeInput(input);
        _sql.Text = sql ?? InitialQuery();
        _run.Name = "ExecuteButton"; _run.ToolTip = "Execute query (F5 or Ctrl+Enter)";
        _cancel.Name = "CancelQueryButton";
        _emptyResults.SetResourceReference(TextBlock.ForegroundProperty, AppThemeService.MutedTextBrushKey);
        if (_sql.Text.Contains("@limit", StringComparison.Ordinal)) _parameterRows.Add(new() { Name = "@limit", Type = "Int", Value = "1000" });
        _parameters.Columns.Add(new DataGridTextColumn { Header = "Parameter", Binding = new Binding(nameof(ParameterRow.Name)), Width = 170 });
        _parameters.Columns.Add(new DataGridComboBoxColumn { Header = "SQL type", ItemsSource = new[] { "Int", "BigInt", "Decimal", "Float", "Bit", "NVarChar", "VarChar", "DateTime2", "Date", "DateTimeOffset", "UniqueIdentifier", "VarBinary" }, SelectedItemBinding = new Binding(nameof(ParameterRow.Type)), Width = 140 });
        _parameters.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(ParameterRow.Value)), Width = new(1, DataGridLengthUnitType.Star) });
        _parameters.Columns.Add(new DataGridCheckBoxColumn { Header = "NULL", Binding = new Binding(nameof(ParameterRow.IsNull)) }); _parameters.ItemsSource = _parameterRows;
        SnapshotWindowStyles.ThemeGrid(_parameters); SnapshotWindowStyles.ThemeGrid(_grid);
        var root = new Grid { Margin = new(12) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(2, GridUnitType.Star), MinHeight = 140 });
        root.RowDefinitions.Add(new() { Height = new(8) });
        root.RowDefinitions.Add(new() { Height = new(3, GridUnitType.Star), MinHeight = 140 });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapPanel { Margin = new(0, 0, 0, 6) };
        toolbar.Children.Add(new TextBlock { Text = "SQL query", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) });
        _run.Click += async (_, _) => await RunAsync(); toolbar.Children.Add(_run);
        _cancel.Click += (_, _) => { SetStatus("Cancelling…"); _execution?.Cancel(); }; toolbar.Children.Add(_cancel);
        toolbar.Children.Add(new TextBlock { Text = "Maximum rows", VerticalAlignment = VerticalAlignment.Center, Margin = new(16, 0, 0, 0) });
        toolbar.Children.Add(_rowLimit); root.Children.Add(toolbar);
        var parameters = new Expander { Name = "ParametersExpander", Header = "Parameters", Content = _parameters,
            IsExpanded = _parameterRows.Count > 0, Margin = new(0, 6, 0, 0) };
        var editor = new DockPanel(); DockPanel.SetDock(parameters, Dock.Bottom);
        editor.Children.Add(parameters); editor.Children.Add(_sql); Grid.SetRow(editor, 1); root.Children.Add(editor);
        var splitter = new GridSplitter { Name = "QueryResultsSplitter", Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.SetResourceReference(Control.BackgroundProperty, AppThemeService.BorderBrushKey);
        Grid.SetRow(splitter, 2); root.Children.Add(splitter);
        var results = new Grid(); results.RowDefinitions.Add(new() { Height = GridLength.Auto });
        results.RowDefinitions.Add(new() { Height = GridLength.Auto }); results.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        results.Children.Add(new TextBlock { Text = "Results", FontWeight = FontWeights.SemiBold, Margin = new(0, 2, 0, 2) });
        _previous.Click += async (_, _) => await RunOperationAsync(async token => { if (_page > 0) await ShowPageAsync(_page - 1, token); }); _resultTools.Children.Add(_previous);
        _next.Click += async (_, _) => await RunOperationAsync(async token => { if (_view != null && (_page + 1) * 100 < _view.Count.AvailableRows) await ShowPageAsync(_page + 1, token); }); _resultTools.Children.Add(_next);
        _resultTools.Children.Add(_filterColumn); _resultTools.Children.Add(_filter);
        var applyFilter = SnapshotWindowStyles.Command("Filter fetched rows");
        applyFilter.Click += async (_, _) =>
        {
            if (_adapter == null || _filterColumn.SelectedItem is not LiveSqlColumn column) return;
            await RunOperationAsync(token => RebuildViewAsync(_filter.Text.Length == 0 ? [] : [new(column.Ordinal, _filter.Text)], _sorts, token));
        }; _resultTools.Children.Add(applyFilter);
        var export = SnapshotWindowStyles.Command("Export result…"); export.Click += async (_, _) =>
        {
            if (_result == null) return; var dialog = new SaveFileDialog { Filter = "Typed SQL result (*.json)|*.json", AddExtension = true, OverwritePrompt = false };
            if (dialog.ShowDialog(this) != true) return;
            await RunOperationAsync(async token => { await _result.ExportAsync(dialog.FileName, token); SetStatus("Exported typed result."); });
        }; _resultTools.Children.Add(export);
        Grid.SetRow(_resultTools, 1); results.Children.Add(_resultTools);
        var resultBody = new Grid(); resultBody.Children.Add(_grid); resultBody.Children.Add(_emptyResults);
        Grid.SetRow(resultBody, 2); results.Children.Add(resultBody); Grid.SetRow(results, 3); root.Children.Add(results);
        var status = SnapshotWindowStyles.Status(_status); Grid.SetRow(status, 4); root.Children.Add(status); Content = root;
        NameScope.SetNameScope(this, new NameScope());
        foreach (var element in new FrameworkElement[] { _sql, _run, _cancel, _grid, _status, _rowLimit, _parameters, parameters, splitter, _filterColumn, _filter })
            RegisterName(element.Name, element);
        _sql.TextChanged += (_, _) => UpdateControls();
        Loaded += (_, _) => _sql.Focus();
        PreviewKeyDown += async (_, e) =>
        {
            if ((e.Key == Key.F5 || e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) && _run.IsEnabled)
            { e.Handled = true; await RunAsync(); }
        };
        _grid.Sorting += async (_, e) =>
        {
            e.Handled = true; if (_result == null) return;
            int ordinal = int.Parse(e.Column.SortMemberPath, CultureInfo.InvariantCulture);
            bool descending = _sorts.Length != 0 && _sorts[0].ColumnOrdinal == ordinal && !_sorts[0].Descending;
            await RunOperationAsync(token => RebuildViewAsync(_filters, [new(ordinal, descending)], token));
        };
        Closed += (_, _) => { _closed = true; _generation++; _lifetime.Cancel(); _execution?.Cancel(); CloseCompletion = CloseAsync(); };
        SetStatus("Enter a SELECT query and choose Execute."); UpdateControls();
    }

    private Task RunAsync() => RunOperationAsync(ExecuteCoreAsync);
    private Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_execution != null || _closed) return CurrentOperation;
        return CurrentOperation = RunOperationCoreAsync(action);
    }
    private async Task RunOperationCoreAsync(Func<CancellationToken, Task> action)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _execution = execution; UpdateControls();
        try { await action(execution.Token); }
        catch (OperationCanceledException) { SetStatus(_result == null ? "Cancelled." : "Cancelled. Displayed result retained."); }
        catch (Exception e) { SetStatus(e.Message, error: true); }
        finally { _execution = null; UpdateControls(); }
    }
    private async Task ExecuteCoreAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_sql.Text)) { SetStatus("Enter a SELECT query first."); return; }
        if (!_parameters.CommitEdit(DataGridEditingUnit.Cell, true) || !_parameters.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Complete the parameter edit before executing the query.");
        SetStatus("Executing query…");
        var limits = new LiveSqlQueryLimits(MaximumRows: int.Parse(_rowLimit.Text, CultureInfo.InvariantCulture));
        var result = await _service.QueryAsync(_sql.Text, _parameterRows.Where(p => p.Name.Length != 0).Select(p => p.ToParameter()).ToArray(), limits, token);
        LiveSqlGridSource? adapter = new(result); IGridQuerySession? view = null;
        try
        {
            view = await adapter.CreateQueryAsync(cancellationToken: token);
            var page = await view.ReadPageAsync(new(0, 100, 128 * 1024 * 1024), token);
            token.ThrowIfCancellationRequested();
            var oldView = _view; var oldAdapter = _adapter;
            _generation++; _result = result; _adapter = adapter; _view = view; adapter = null; view = null;
            _filters = []; _sorts = []; _page = 0; _filter.Clear(); _grid.Columns.Clear();
            _filterColumn.ItemsSource = result.Columns; _filterColumn.SelectedIndex = result.Columns.Count == 0 ? -1 : 0;
            foreach (var column in result.Columns) _grid.Columns.Add(new DataGridTextColumn { Header = column.Name + " (" + column.SqlType + ")", Binding = new Binding("[" + column.Ordinal + "]"), SortMemberPath = column.Ordinal.ToString(CultureInfo.InvariantCulture) });
            SnapshotWindowStyles.ThemeGrid(_grid); PublishPage(page);
            if (oldView != null) await oldView.DisposeAsync(); if (oldAdapter != null) await oldAdapter.DisposeAsync();
        }
        finally { if (view != null) await view.DisposeAsync(); if (adapter != null) await adapter.DisposeAsync(); }
    }
    private async Task ShowPageAsync(int pageIndex, CancellationToken token)
    {
        if (_result == null || _view == null) return;
        int generation = _generation;
        var page = await _view.ReadPageAsync(new(pageIndex * 100, 100, 128 * 1024 * 1024), token);
        token.ThrowIfCancellationRequested();
        if (generation == _generation && !_closed) { _page = pageIndex; PublishPage(page); }
    }
    private void PublishPage(GridPage page)
    {
        _grid.ItemsSource = page.Rows.Select(row => row.Cells).ToArray();
        _emptyResults.Text = _result!.RowCount == 0 ? "No rows returned." : "No rows match the filter.";
        _emptyResults.Visibility = page.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus($"{_result.Status} — {_result.RowCount:N0} fetched rows, {_result.Elapsed.TotalSeconds:F2}s. Page {_page + 1}; {_view!.Count.AvailableRows:N0} matching rows. Sorting/filtering applies only to fetched rows.");
    }
    private async Task RebuildViewAsync(GridFilter[] filters, GridSort[] sorts, CancellationToken token)
    {
        if (_adapter == null) return;
        IGridQuerySession? view = await _adapter.CreateQueryAsync(new(Filters: filters, Sorts: sorts), token);
        try
        {
            var page = await view.ReadPageAsync(new(0, 100, 128 * 1024 * 1024), token);
            token.ThrowIfCancellationRequested();
            var old = _view; _view = view; view = null; _filters = filters; _sorts = sorts; _page = 0;
            foreach (var column in _grid.Columns)
                column.SortDirection = sorts.Length != 0 && column.SortMemberPath == sorts[0].ColumnOrdinal.ToString(CultureInfo.InvariantCulture)
                    ? sorts[0].Descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
            PublishPage(page); if (old != null) await old.DisposeAsync();
        }
        finally { if (view != null) await view.DisposeAsync(); }
    }
    private void SetStatus(string text, bool error = false)
    {
        if (_closed) return;
        _status.Text = text; _status.SetResourceReference(TextBlock.ForegroundProperty, error ? AppThemeService.ErrorTextBrushKey : AppThemeService.SubtleTextBrushKey);
    }
    private void UpdateControls()
    {
        bool busy = _execution != null || _closed;
        _run.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_sql.Text); _cancel.IsEnabled = _execution != null && !_closed;
        _sql.IsReadOnly = busy; _parameters.IsEnabled = !busy; _rowLimit.IsEnabled = !busy;
        _resultTools.IsEnabled = !busy && _result != null; _grid.IsEnabled = !busy && _result != null;
        _previous.IsEnabled = _page > 0; _next.IsEnabled = _view != null && (_page + 1) * 100 < _view.Count.AvailableRows;
    }
    private async Task CloseAsync()
    {
        await CurrentOperation;
        if (_view != null) await _view.DisposeAsync(); if (_adapter != null) await _adapter.DisposeAsync();
        _view = null; _adapter = null; _result = null; _lifetime.Dispose();
    }
}

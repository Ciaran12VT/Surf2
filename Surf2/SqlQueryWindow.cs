using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Surf2.Services.LiveSql;
using Surf2.Services.RelationalGrid;

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
    private readonly TextBox _sql = new() { AcceptsReturn = true, FontFamily = new("Consolas"), Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DataGrid _parameters = new() { AutoGenerateColumns = false, Height = 110, CanUserAddRows = true };
    private readonly ObservableCollection<ParameterRow> _parameterRows = [];
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, EnableRowVirtualization = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _rowLimit = new() { Text = "10000", Width = 80 };
    private readonly Button _run = new() { Content = "Run", Margin = new(4) };
    private LiveSqlResult? _result;
    private LiveSqlGridSource? _adapter;
    private IGridQuerySession? _view;
    private readonly ComboBox _filterColumn = new() { Width = 180, DisplayMemberPath = nameof(LiveSqlColumn.Name) };
    private readonly TextBox _filter = new() { Width = 180 };
    private GridFilter[] _filters = [];
    private GridSort[] _sorts = [];
    private CancellationTokenSource? _execution;
    private readonly CancellationTokenSource _lifetime = new();
    private int _page, _generation;
    private bool _descending;
    public SqlQueryWindow(LiveSqlService service, string? sql = null)
    {
        _service = service; Title = "SQL Query — " + service.Server + "/" + service.Database; Width = 1150; Height = 780;
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeWindow(this);
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeInput(_sql);
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeGrid(_grid);
        Services.RelationalSnapshots.SnapshotWindowStyles.ThemeGrid(_parameters);
        _sql.Text = sql ?? "SELECT 1 AS Value";
        if (_sql.Text.Contains("@limit", StringComparison.Ordinal)) _parameterRows.Add(new() { Name = "@limit", Type = "Int", Value = "1000" });
        _parameters.Columns.Add(new DataGridTextColumn { Header = "Parameter", Binding = new Binding(nameof(ParameterRow.Name)), Width = 170 });
        _parameters.Columns.Add(new DataGridComboBoxColumn { Header = "SQL type", ItemsSource = new[] { "Int", "BigInt", "Decimal", "Float", "Bit", "NVarChar", "VarChar", "DateTime2", "Date", "DateTimeOffset", "UniqueIdentifier", "VarBinary" }, SelectedItemBinding = new Binding(nameof(ParameterRow.Type)), Width = 140 });
        _parameters.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(ParameterRow.Value)), Width = new(1, DataGridLengthUnitType.Star) });
        _parameters.Columns.Add(new DataGridCheckBoxColumn { Header = "NULL", Binding = new Binding(nameof(ParameterRow.IsNull)) }); _parameters.ItemsSource = _parameterRows;
        var root = new DockPanel { Margin = new(12) }; var top = new StackPanel();
        top.Children.Add(_sql); top.Children.Add(_parameters); var buttons = new WrapPanel();
        _run.Click += async (_, _) => await RunAsync(); buttons.Children.Add(_run);
        var cancel = new Button { Content = "Cancel", Margin = new(4) }; cancel.Click += (_, _) => { _status.Text = "Cancelling…"; _execution?.Cancel(); }; buttons.Children.Add(cancel);
        buttons.Children.Add(new TextBlock { Text = "Maximum rows", VerticalAlignment = VerticalAlignment.Center }); buttons.Children.Add(_rowLimit);
        var previous = new Button { Content = "Previous page", Margin = new(4) }; previous.Click += async (_, _) => { if (_page > 0) { _page--; await ShowPageAsync(); } }; buttons.Children.Add(previous);
        var next = new Button { Content = "Next page", Margin = new(4) }; next.Click += async (_, _) => { if (_view != null && (_page + 1) * 100 < _view.Count.AvailableRows) { _page++; await ShowPageAsync(); } }; buttons.Children.Add(next);
        buttons.Children.Add(_filterColumn); buttons.Children.Add(_filter);
        var applyFilter = new Button { Content = "Filter fetched rows", Margin = new(4) };
        applyFilter.Click += async (_, _) =>
        {
            if (_adapter == null || _filterColumn.SelectedItem is not LiveSqlColumn column) return;
            try { _filters = _filter.Text.Length == 0 ? [] : [new(column.Ordinal, _filter.Text)]; _page = 0; await RebuildViewAsync(); }
            catch (Exception e) { _status.Text = e.Message; }
        }; buttons.Children.Add(applyFilter);
        var export = new Button { Content = "Export typed result…", Margin = new(4) }; export.Click += async (_, _) =>
        {
            if (_result == null) return; var dialog = new SaveFileDialog { Filter = "Typed SQL result (*.json)|*.json", AddExtension = true, OverwritePrompt = false };
            if (dialog.ShowDialog(this) != true) return;
            try { await _result.ExportAsync(dialog.FileName, _lifetime.Token); _status.Text = "Exported typed result."; } catch (Exception e) { _status.Text = e.Message; }
        }; buttons.Children.Add(export); top.Children.Add(buttons); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status); root.Children.Add(_grid); Content = root;
        _grid.Sorting += async (_, e) =>
        {
            e.Handled = true; if (_result == null) return; int ordinal = _grid.Columns.IndexOf(e.Column); int generation = _generation;
            try
            {
                _status.Text = "Sorting fetched result…"; _descending = !_descending; _sorts = [new(ordinal, _descending)];
                if (generation != _generation) return; _page = 0; await RebuildViewAsync();
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        Closed += async (_, _) => { _generation++; _lifetime.Cancel(); _execution?.Cancel(); if (_view != null) await _view.DisposeAsync(); if (_adapter != null) await _adapter.DisposeAsync(); };
    }
    private async Task RunAsync()
    {
        if (_execution != null) return;
        _parameters.CommitEdit(DataGridEditingUnit.Cell, true); _parameters.CommitEdit(DataGridEditingUnit.Row, true);
        _execution = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _run.IsEnabled = false; _status.Text = "Qualifying source and executing…";
        int generation = ++_generation;
        try
        {
            var limits = new LiveSqlQueryLimits(MaximumRows: int.Parse(_rowLimit.Text, CultureInfo.InvariantCulture));
            var result = await _service.QueryAsync(_sql.Text, _parameterRows.Where(p => p.Name.Length != 0).Select(p => p.ToParameter()).ToArray(), limits, _execution.Token);
            if (_lifetime.IsCancellationRequested || generation != _generation) { await result.DisposeAsync(); return; }
            if (_view != null) await _view.DisposeAsync(); if (_adapter != null) await _adapter.DisposeAsync();
            _result = result; _adapter = new(result); _filters = []; _sorts = []; _page = 0; _grid.Columns.Clear();
            _filterColumn.ItemsSource = result.Columns; _filterColumn.SelectedIndex = result.Columns.Count == 0 ? -1 : 0;
            foreach (var column in result.Columns) _grid.Columns.Add(new DataGridTextColumn { Header = column.Name + " (" + column.SqlType + ")", Binding = new Binding("[" + column.Ordinal + "]"), SortMemberPath = column.Ordinal.ToString(CultureInfo.InvariantCulture) });
            await RebuildViewAsync();
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled. Previous displayed result retained."; }
        catch (Exception e) { _status.Text = e.Message; }
        finally { _execution.Dispose(); _execution = null; _run.IsEnabled = true; }
    }
    private async Task ShowPageAsync()
    {
        try
        {
            if (_result == null || _view == null) return; var result = _result; var view = _view; int generation = _generation;
            var page = await view.ReadPageAsync(new(_page * 100, 100, 128 * 1024 * 1024), _lifetime.Token);
            if (generation != _generation || _lifetime.IsCancellationRequested || !ReferenceEquals(view, _view)) return;
            _grid.ItemsSource = page.Rows.Select(row => row.Cells).ToArray(); _status.Text = $"{result.Status} — {result.RowCount:N0} fetched rows, {result.Elapsed.TotalSeconds:F2}s. Page {_page + 1}; {view.Count.AvailableRows:N0} matching rows. Sorting/filtering applies only to fetched rows.";
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) _status.Text = error.Message; }
    }
    private async Task RebuildViewAsync()
    {
        if (_adapter == null) return; var adapter = _adapter; int generation = _generation;
        var view = await adapter.CreateQueryAsync(new(Filters: _filters, Sorts: _sorts), _lifetime.Token);
        if (_lifetime.IsCancellationRequested || generation != _generation || !ReferenceEquals(adapter, _adapter))
        { await view.DisposeAsync(); return; }
        if (_view != null) await _view.DisposeAsync(); _view = view; await ShowPageAsync();
    }
}

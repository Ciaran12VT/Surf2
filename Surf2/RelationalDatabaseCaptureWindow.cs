using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;
using static Surf2.Services.RelationalSnapshots.SnapshotWindowStyles;

namespace Surf2;

public sealed class RelationalDatabaseCaptureWindow : Window
{
    private readonly RelationalRuntime _runtime;
    private readonly RelationalDatabaseCaptureService _service;
    private readonly Scope _scope;
    private readonly StateToken _scopeToken;
    private readonly PasswordBox _connection = new();
    private readonly TextBox _name = new(), _version = new();
    private readonly CheckBox _replace = new()
    {
        Content = new TextBlock { Text = "Replace selected snapshot", TextWrapping = TextWrapping.Wrap, MaxWidth = 128 },
        VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0)
    };
    private readonly CheckBox _blockingConsent = new() { Content = new TextBlock { Text = "Allow blocking capture when source snapshot isolation is unavailable", TextWrapping = TextWrapping.Wrap } };
    private readonly ComboBox _snapshots = new() { DisplayMemberPath = nameof(SnapshotSummary.DisplayName), MinWidth = 180 };
    private readonly DataGrid _tables = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Single, EnableRowVirtualization = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 4, IsIndeterminate = false };
    private readonly Button _capture = Command("Capture", "SurfSaveButtonStyle"), _cancel = Command("Cancel", "SurfCloseButtonStyle"), _readTables = Command("Read Tables"),
        _nextTables = ToolButton("\uE72A", "Next table page"), _previousTables = ToolButton("\uE72B", "Previous table page"),
        _nextSnapshots = ToolButton("\uE72A", "Next snapshot page");
    private readonly ObservableCollection<TableChoice> _tablePage = [];
    private readonly Dictionary<(string, string), RelationalSourceTable> _selectedTables = new(RuntimeTableComparer.Instance);
    private readonly Stack<int> _previousTablePages = new();
    private CancellationTokenSource? _operation;
    private Task _cancellation = Task.CompletedTask;
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SnapshotCursor? _snapshotNext;
    private int _tableStart; private int? _tableNext;
    private bool _busy, _closingAfterCancel, _closed;
    public RelationalCaptureResult? Result { get; private set; }
    public bool RequiresReload { get; private set; }
    public Exception? CleanupFailure { get; private set; }
    public Task CloseCompletion => _closeCompletion.Task;
    public Task CloseAndDrainAsync() { Dispatcher.VerifyAccess(); Close(); return CloseCompletion; }

    public RelationalDatabaseCaptureWindow(RelationalRuntime runtime, Scope selected, StateToken expectedScope)
        : this(runtime, selected, expectedScope, null) { }
    public RelationalDatabaseCaptureWindow(RelationalRuntime runtime, Scope selected, StateToken expectedScope, SnapshotSummary? replacement)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scope = selected ?? throw new ArgumentNullException(nameof(selected)); _scopeToken = expectedScope;
        if (expectedScope.Epoch != runtime.Session.Epoch) throw new ArgumentException("The selected scope belongs to another epoch.");
        _service = new(runtime); Title = "Database Capture"; Width = 760; Height = 620; MinWidth = 620; MinHeight = 520;
        ThemeWindow(this); ThemeInput(_connection);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new(16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto,
            new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        var fields = new Grid(); fields.ColumnDefinitions.Add(new() { Width = new(160) }); fields.ColumnDefinitions.Add(new());
        for (int i = 0; i < 3; i++) fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Field(fields, "Source connection", _connection, 0); Field(fields, "Snapshot name", _name, 1); Field(fields, "Version name", _version, 2);
        root.Children.Add(fields);
        var target = new Grid { Margin = new(0, 8, 0, 8) }; target.ColumnDefinitions.Add(new() { Width = new(160) });
        target.ColumnDefinitions.Add(new()); target.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        target.Children.Add(_replace); Grid.SetColumn(_snapshots, 1); target.Children.Add(_snapshots);
        _snapshots.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_nextSnapshots, 2); target.Children.Add(_nextSnapshots); Add(root, target, 1);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 8) };
        toolbar.Children.Add(_readTables); toolbar.Children.Add(_previousTables); toolbar.Children.Add(_nextTables); Add(root, toolbar, 2);
        var selectionHeader = new StackPanel { Margin = new(0, 0, 0, 6) };
        selectionHeader.Children.Add(_blockingConsent);
        selectionHeader.Children.Add(new TextBlock { Text = "Full-data tables", Margin = new(0, 6, 0, 0) }); Add(root, selectionHeader, 3);
        _tables.Columns.Add(new DataGridCheckBoxColumn { Header = "Capture rows", Binding = new Binding(nameof(TableChoice.Capture))
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 90, MinWidth = 90, MaxWidth = 90 });
        _tables.Columns.Add(new DataGridTextColumn { Header = "Table", Binding = new Binding(nameof(TableChoice.Name)), IsReadOnly = true,
            Width = new(1, DataGridLengthUnitType.Star), MinWidth = 160 }); ThemeGrid(_tables); _tables.ItemsSource = _tablePage; Add(root, _tables, 4);
        var status = new StackPanel { Margin = new(0, 8, 0, 8) }; status.Children.Add(_progress); status.Children.Add(Status(_status)); Add(root, status, 5);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(_capture); footer.Children.Add(_cancel); Add(root, footer, 6); Content = root;
        _previousTables.IsEnabled = _nextTables.IsEnabled = false;
        _connection.PasswordChanged += (_, _) => { _selectedTables.Clear(); _tablePage.Clear(); _previousTablePages.Clear();
            _tableNext = null; _previousTables.IsEnabled = _nextTables.IsEnabled = false; };
        if (replacement != null) { _replace.IsChecked = true; _snapshots.Items.Add(replacement); _snapshots.SelectedItem = replacement; _name.Text = replacement.DisplayName; }
        else _snapshots.IsEnabled = false;
        _replace.Checked += (_, _) => _snapshots.IsEnabled = !_busy; _replace.Unchecked += (_, _) => _snapshots.IsEnabled = false;
        _snapshots.SelectionChanged += (_, _) => { if (_replace.IsChecked == true && _snapshots.SelectedItem is SnapshotSummary head) _name.Text = head.DisplayName; };
        _nextSnapshots.Click += async (_, _) => await RunAsync(() => ReadSnapshotsAsync(_snapshotNext));
        _readTables.Click += async (_, _) => await RunAsync(async () =>
        { _selectedTables.Clear(); _previousTablePages.Clear(); await ReadTablesAsync(0); });
        _nextTables.Click += async (_, _) => await RunAsync(async () =>
        { if (_tableNext is { } next) { _previousTablePages.Push(_tableStart); await ReadTablesAsync(next); } });
        _previousTables.Click += async (_, _) => await RunAsync(async () =>
        { if (_previousTablePages.Count != 0) await ReadTablesAsync(_previousTablePages.Pop()); });
        _capture.Click += async (_, _) => await RunAsync(CaptureAsync);
        _cancel.Click += (_, _) => { if (_busy) RequestCancellation(); else Close(); };
        Loaded += async (_, _) => { if (replacement == null) await RunAsync(() => ReadSnapshotsAsync(null)); };
        Closing += OnClosing; Closed += (_, _) => { _closed = true; if (!_busy) CompleteClose(); };
    }

    private async Task ReadSnapshotsAsync(SnapshotCursor? cursor)
    {
        var page = await _runtime.Snapshots.ListSnapshotsAsync(pageSize: 50, cursor: cursor, ct: _operation!.Token);
        _snapshots.Items.Clear(); foreach (var item in page.Items) _snapshots.Items.Add(item);
        _snapshotNext = page.Next; _nextSnapshots.IsEnabled = page.Next != null;
    }
    private async Task ReadTablesAsync(int after)
    {
        _tables.CommitEdit(DataGridEditingUnit.Cell, true); _tables.CommitEdit(DataGridEditingUnit.Row, true);
        var page = await _service.ListSourceTablesAsync(_connection.Password, afterObjectId: after, ct: _operation!.Token);
        _tablePage.Clear(); _tableStart = after; _tableNext = page.NextObjectId;
        foreach (var table in page.Items)
            _tablePage.Add(new(table, _selectedTables.ContainsKey((table.SchemaName, table.TableName)), selected =>
            {
                if (selected) _selectedTables[(table.SchemaName, table.TableName)] = table;
                else _selectedTables.Remove((table.SchemaName, table.TableName));
            }));
        _status.Text = _selectedTables.Count + " full-data table(s) selected.";
    }
    private async Task CaptureAsync()
    {
        _tables.CommitEdit(DataGridEditingUnit.Cell, true); _tables.CommitEdit(DataGridEditingUnit.Row, true);
        if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException("Enter a snapshot name.");
        SnapshotRuntimeToken? expected = null;
        if (_replace.IsChecked == true)
        {
            if (_snapshots.SelectedItem is not SnapshotSummary snapshot) throw new ArgumentException("Select the snapshot to replace.");
            expected = new(_runtime.Session.Epoch, snapshot.SnapshotKey, snapshot.RowVersion);
        }
        var request = new RelationalDatabaseCaptureRequest(_connection.Password, _name.Text.Trim(), _selectedTables.Values.ToArray(),
            _scope, _scopeToken, expected, _version.Text.Trim(), _blockingConsent.IsChecked == true);
        var progress = new Progress<RelationalCaptureProgress>(value =>
            _status.Text = value.Phase + ": " + value.MetadataUnits + " metadata units, " + value.CapturedRows + " rows.");
        var token = _operation!.Token;
        Result = await Task.Run(() => _service.CaptureAsync(request, progress, token), token);
        _connection.Clear(); DialogResult = true;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return; _busy = true; _operation = new(); _cancellation = Task.CompletedTask;
        SetBusy(true);
        try { await action(); }
        catch (SnapshotPublicationOutcomeUnknownException error) { RequiresReload = true; _status.Text = error.Message; }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            try { await _cancellation; }
            catch (Exception error) { CleanupFailure ??= error; _status.Text = "Cancellation failed: " + error.Message; }
            _operation.Dispose(); _operation = null; _busy = false; SetBusy(false);
            if (_closingAfterCancel) Close();
            if (_closed) CompleteClose();
        }
    }
    private void CompleteClose()
    {
        if (CleanupFailure is { } failure) _closeCompletion.TrySetException(failure);
        else _closeCompletion.TrySetResult();
    }
    private void SetBusy(bool value)
    {
        _connection.IsEnabled = _name.IsEnabled = _version.IsEnabled = _replace.IsEnabled = _tables.IsEnabled =
            _readTables.IsEnabled = _capture.IsEnabled = _blockingConsent.IsEnabled = !value;
        _snapshots.IsEnabled = !value && _replace.IsChecked == true; _nextSnapshots.IsEnabled = !value && _snapshotNext != null;
        _previousTables.IsEnabled = !value && _previousTablePages.Count != 0; _nextTables.IsEnabled = !value && _tableNext != null;
        _progress.IsIndeterminate = value;
        if (RequiresReload) _capture.IsEnabled = false;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (_busy && Result == null) { e.Cancel = true; RequestCancellation(); } }
    private void RequestCancellation()
    {
        if (_closingAfterCancel) return;
        _closingAfterCancel = true; _status.Text = "Cancelling..."; _cancel.IsEnabled = false;
        _cancellation = _operation?.CancelAsync() ?? Task.CompletedTask;
    }
    private static void Add(Grid grid, UIElement value, int row) { Grid.SetRow(value, row); grid.Children.Add(value); }
    private static void Field(Grid grid, string label, Control value, int row)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 4, 8, 4) };
        Add(grid, text, row); Grid.SetColumn(value, 1); value.Margin = new(0, 4, 0, 4); Add(grid, value, row);
    }
    private sealed class TableChoice(RelationalSourceTable source, bool capture, Action<bool> changed) : INotifyPropertyChanged
    {
        private bool _capture = capture;
        public string Name => source.DisplayName;
        public bool Capture { get => _capture; set { if (_capture == value) return; _capture = value; changed(value); PropertyChanged?.Invoke(this, new(nameof(Capture))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Snapshots;
using static Surf2.Services.RelationalSnapshots.SnapshotWindowStyles;

namespace Surf2;

public sealed class RelationalHistoryOpenEventArgs(RelationalComparisonTarget target) : EventArgs
{ public RelationalComparisonTarget Target { get; } = target; }
public sealed class RelationalHistoryCompareEventArgs(RelationalComparisonTarget left, RelationalComparisonTarget right) : EventArgs
{ public RelationalComparisonTarget Left { get; } = left; public RelationalComparisonTarget Right { get; } = right; }

public sealed class RelationalSnapshotHistoryWindow : Window
{
    private readonly RelationalRuntime _runtime;
    private readonly RelationalSnapshotHistoryService _service;
    private SnapshotSummary _snapshot;
    private readonly ComboBox _histories = new() { DisplayMemberPath = nameof(SnapshotHistorySummary.HistoryKey), MinWidth = 130 };
    private readonly DataGrid _versions = ListGrid(DataGridSelectionMode.Extended), _changes = ListGrid(), _resources = ListGrid();
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _historyNext = ToolButton("\uE72A", "Next history page"), _versionNext = ToolButton("\uE72A", "Next version page"),
        _changeNext = ToolButton("\uE72A", "Next change page"), _resourceNext = ToolButton("\uE72A", "Next resource page");
    private readonly Button _open = Command("Open"), _compareResource = Command("Compare Current"), _compareVersions = Command("Compare Versions"),
        _restoreVersion = Command("Restore Version", "SurfSaveButtonStyle"), _restoreResource = Command("Restore Resource", "SurfSaveButtonStyle"), _export = ToolButton("\uE74E", "Export selected resource"),
        _exportVersion = Command("Export Version"), _copyVersion = Command("Copy Version", "SurfSaveButtonStyle"), _copyResource = Command("Copy Resource", "SurfSaveButtonStyle"),
        _close = Command("Close", "SurfCloseButtonStyle"), _reload = ToolButton("\uE72C", "Reload selected snapshot");
    private SnapshotCursor? _historyCursor, _versionCursor, _changeCursor, _resourceCursor;
    private SnapshotVersionSummary? _version;
    private HistoricalSnapshotContext? _context;
    private CancellationTokenSource? _operation;
    private Task _cancellation = Task.CompletedTask, _closingTask = Task.CompletedTask;
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _busy, _closing, _allowClose, _closed;
    public SnapshotSummary? UpdatedSnapshot { get; private set; }
    public bool RequiresReload { get; private set; }
    public Exception? CleanupFailure { get; private set; }
    public Task CloseCompletion => _closeCompletion.Task;
    public Task CloseAndDrainAsync() { Dispatcher.VerifyAccess(); Close(); return CloseCompletion; }
    public event EventHandler<RelationalHistoryOpenEventArgs>? OpenRequested;
    public event EventHandler<RelationalHistoryCompareEventArgs>? CompareRequested;
    public event EventHandler<RelationalHistoryOpenEventArgs>? CopyRequested;
    public event Action<SnapshotSummary>? SnapshotChanged;

    public RelationalSnapshotHistoryWindow(RelationalRuntime runtime, SnapshotSummary snapshot)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _service = new(runtime); Title = "Snapshot History: " + snapshot.DisplayName; Width = 1000; Height = 680; MinWidth = 800; MinHeight = 560;
        ThemeWindow(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = new(130) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "History", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
        header.Children.Add(_histories); header.Children.Add(_historyNext); header.Children.Add(_reload); Add(root, header, 0);
        var lists = new Grid(); lists.ColumnDefinitions.Add(new() { Width = new(290) }); lists.ColumnDefinitions.Add(new());
        _versions.Columns.Add(Column("Version", nameof(SnapshotVersionSummary.VersionName)));
        _versions.Columns.Add(Column("Number", nameof(SnapshotVersionSummary.VersionNumber), 60));
        _versions.Columns.Add(Column("Changes", nameof(SnapshotVersionSummary.ChangeCount), 65));
        _changes.Columns.Add(Column("Resource", nameof(SnapshotChangeSummary.DisplayName)));
        _changes.Columns.Add(Column("Kind", nameof(SnapshotChangeSummary.ChangeKind), 100));
        _resources.Columns.Add(Column("Resource", nameof(HistoricalSnapshotEntry.Name)));
        _resources.Columns.Add(Column("Schema", nameof(HistoricalSnapshotEntry.SchemaName), 100));
        _resources.Columns.Add(Column("Kind", nameof(HistoricalSnapshotEntry.Collection), 100));
        ThemeGrid(_versions); ThemeGrid(_changes); ThemeGrid(_resources);
        var versions = PagedList(_versions, _versionNext); versions.Margin = new(0, 0, 10, 0); lists.Children.Add(versions);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Resources", Content = PagedList(_resources, _resourceNext) });
        tabs.Items.Add(new TabItem { Header = "Changes", Content = PagedList(_changes, _changeNext) });
        Grid.SetColumn(tabs, 1); lists.Children.Add(tabs); Add(root, lists, 1);
        _preview.Margin = new(0, 8, 0, 8); Add(root, _preview, 2);
        var footer = new StackPanel(); footer.Children.Add(Status(_status));
        var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _open, _compareResource, _compareVersions, _restoreResource, _restoreVersion,
            _copyResource, _copyVersion, _export, _exportVersion, _close }) commands.Children.Add(button);
        footer.Children.Add(commands); Add(root, footer, 3); Content = root;
        _histories.SelectionChanged += async (_, _) => await RunAsync(() => ReadVersionsAsync(null));
        _versions.SelectionChanged += async (_, e) =>
        {
            if (e.AddedItems.Count != 0 && e.AddedItems[0] is SnapshotVersionSummary selected) await RunAsync(() => SelectVersionAsync(selected));
            if (!_busy) SetBusy(false);
        };
        _resources.SelectionChanged += async (_, _) => await RunAsync(PreviewResourceAsync);
        _changes.SelectionChanged += async (_, _) => await RunAsync(PreviewChangeAsync);
        _historyNext.Click += async (_, _) => await RunAsync(() => ReadHistoriesAsync(_historyCursor));
        _versionNext.Click += async (_, _) => await RunAsync(() => ReadVersionsAsync(_versionCursor));
        _changeNext.Click += async (_, _) => await RunAsync(() => ReadChangesAsync(_changeCursor));
        _resourceNext.Click += async (_, _) => await RunAsync(() => ReadResourcesAsync(_resourceCursor));
        _reload.Click += async (_, _) => await RunAsync(ReloadAsync);
        _open.Click += async (_, _) => await RunAsync(async () => OpenRequested?.Invoke(this,
            new(await _service.ResourceTargetAsync(SelectedContext(), SelectedResource(), Token))));
        _compareResource.Click += async (_, _) => await RunAsync(CompareResourceAsync);
        _compareVersions.Click += (_, _) => CompareVersions();
        _restoreVersion.Click += async (_, _) => await RunAsync(() => RestoreAsync(false));
        _restoreResource.Click += async (_, _) => await RunAsync(() => RestoreAsync(true));
        _copyResource.Click += async (_, _) => await RunAsync(async () => CopyRequested?.Invoke(this,
            new(await _service.ResourceTargetAsync(SelectedContext(), SelectedResource(), Token))));
        _copyVersion.Click += (_, _) => { if (_version != null) CopyRequested?.Invoke(this,
            new(_service.VersionTarget(_snapshot, _version.VersionKey, _version.VersionName))); };
        _export.Click += async (_, _) => await ExportAsync(false); _exportVersion.Click += async (_, _) => await ExportAsync(true);
        _close.Click += (_, _) => Close(); Closing += OnClosing; Closed += (_, _) =>
        {
            _closed = true;
            if (CleanupFailure is { } failure) _closeCompletion.TrySetException(failure);
            else _closeCompletion.TrySetResult();
        };
        Loaded += async (_, _) => await RunAsync(() => ReadHistoriesAsync(null)); SetBusy(false);
    }
    private CancellationToken Token => _operation!.Token;
    private HistoricalSnapshotContext SelectedContext() => _context ?? throw new InvalidOperationException("Select a version.");
    private HistoricalSnapshotEntry SelectedResource() => _resources.SelectedItem as HistoricalSnapshotEntry ?? throw new InvalidOperationException("Select a resource.");
    private async Task ReadHistoriesAsync(SnapshotCursor? cursor)
    {
        var page = await _service.ListHistoriesAsync(_snapshot.SnapshotKey, cursor: cursor, ct: Token);
        _historyCursor = page.Next; _histories.ItemsSource = page.Items; _histories.SelectedIndex = page.Items.Count == 0 ? -1 : 0;
        await ReadVersionsAsync(null);
    }
    private async Task ReadVersionsAsync(SnapshotCursor? cursor)
    {
        if (_histories.SelectedItem is not SnapshotHistorySummary history) { _versions.ItemsSource = null; await ClearVersionAsync(); return; }
        var page = await _service.ListVersionsAsync(_snapshot.SnapshotKey, history.HistoryKey, cursor: cursor, ct: Token);
        _versionCursor = page.Next; _versions.ItemsSource = page.Items; _versions.SelectedIndex = page.Items.Count == 0 ? -1 : 0;
        if (page.Items.Count != 0) await SelectVersionAsync(page.Items[0]); else await ClearVersionAsync();
    }
    private async Task ClearVersionAsync()
    {
        if (_context != null) { await _context.DisposeAsync(); _context = null; }
        _version = null; _resourceCursor = _changeCursor = null; _resources.ItemsSource = _changes.ItemsSource = null; _preview.Clear();
    }
    private async Task SelectVersionAsync(SnapshotVersionSummary version)
    {
        await ClearVersionAsync();
        _context = await _service.OpenVersionAsync(_snapshot.SnapshotKey, version.VersionKey, Token); _version = version;
        await ReadResourcesAsync(null); await ReadChangesAsync(null);
        _status.Text = version.VersionName + " / " + version.VersionNumber + " / " + version.ChangeCount + " change(s).";
    }
    private async Task ReadResourcesAsync(SnapshotCursor? cursor)
    {
        var page = await SelectedContext().ReadResources(null, null, 50, cursor, Token);
        _resources.ItemsSource = page.Items; _resourceCursor = page.Next; _preview.Clear();
    }
    private async Task ReadChangesAsync(SnapshotCursor? cursor)
    {
        if (_version == null) return;
        var page = await _service.ListChangesAsync(_snapshot.SnapshotKey, _version.VersionKey, cursor: cursor, ct: Token);
        _changes.ItemsSource = page.Items; _changeCursor = page.Next;
    }
    private async Task PreviewResourceAsync()
    {
        if (_resources.SelectedItem is not HistoricalSnapshotEntry entry || _context == null) return;
        DisplayPreview(await Task.Run(() => _service.ReadPreviewAsync(_context, entry, Token), Token));
    }
    private async Task PreviewChangeAsync()
    {
        if (_changes.SelectedItem is not SnapshotChangeSummary change) return;
        DisplayPreview(await Task.Run(() => _service.ReadPreviousPayloadAsync(_snapshot.SnapshotKey, change, Token), Token));
    }
    private void DisplayPreview(RelationalHistoryPreview preview)
    {
        _preview.Text = preview.Text is { Length: > 32768 } text ? text[..32768] : preview.Text ?? "";
        if (preview.Text is { Length: > 32768 }) _status.Text = "Text preview limited to 32,768 characters. The selected export remains complete.";
        else if (preview.CapturedData is { } data) _status.Text = data.Summary.ActualRowCount + " captured row(s), " + data.Columns.Count + " column(s).";
        else if (preview.Text == null) _status.Text = "This change has no previous payload.";
    }
    private async Task CompareResourceAsync()
    {
        var historical = await _service.ResourceTargetAsync(SelectedContext(), SelectedResource(), Token);
        var current = await _runtime.Snapshots.ResolveResourceAsync(historical.ResourceKey!.Value, ct: Token)
            ?? throw new InvalidOperationException("The resource is absent from the current snapshot. Compare versions instead.");
        CompareRequested?.Invoke(this, new(historical, RelationalComparisonService.FromResource(current, _runtime.Session.Epoch)));
    }
    private void CompareVersions()
    {
        var selected = _versions.SelectedItems.Cast<SnapshotVersionSummary>().ToArray();
        if (selected.Length != 2) { _status.Text = "Select two versions."; return; }
        CompareRequested?.Invoke(this, new(_service.VersionTarget(_snapshot, selected[0].VersionKey, selected[0].VersionName),
            _service.VersionTarget(_snapshot, selected[1].VersionKey, selected[1].VersionName)));
    }
    private async Task RestoreAsync(bool resourceOnly)
    {
        if (_version == null) throw new InvalidOperationException("Select a version.");
        var expected = new SnapshotRuntimeToken(_runtime.Session.Epoch, _snapshot.SnapshotKey, _snapshot.RowVersion);
        long version = _version.VersionKey; long? resource = resourceOnly ? SelectedResource().ResourceKey : null;
        var token = Token;
        var result = await Task.Run(() => resource is { } key
            ? _service.RestoreResourceAsync(expected, version, key, ct: token)
            : _service.RestoreVersionAsync(expected, version, ct: token), token);
        _snapshot = result.Snapshot; UpdatedSnapshot = result.Snapshot;
        SnapshotChanged?.Invoke(result.Snapshot);
        await ClearVersionAsync(); await ReadHistoriesAsync(null); _status.Text = "Restored as a new version.";
    }
    private async Task ExportAsync(bool wholeVersion)
    {
        if (_busy || _version == null) return;
        if (!wholeVersion && _resources.SelectedItem is not HistoricalSnapshotEntry) return;
        bool data = !wholeVersion && SelectedResource().Collection == HistoricalCollection.TableDataSets;
        var dialog = new SaveFileDialog { FileName = wholeVersion ? "snapshot-version.zip" : data ? "captured-table.csv" : "resource.sql",
            Filter = wholeVersion ? "ZIP archive|*.zip" : data ? "CSV|*.csv" : "SQL text|*.sql", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async () =>
        {
            var token = Token;
            if (wholeVersion) await Task.Run(() => _service.ExportVersionAsync(_snapshot.SnapshotKey, _version.VersionKey, dialog.FileName, ct: token), token);
            else
            {
                var context = SelectedContext(); var resource = SelectedResource();
                await Task.Run(() => _service.ExportResourceAsync(context, resource, dialog.FileName, token), token);
            }
            _status.Text = "Export complete.";
        });
    }
    private async Task ReloadAsync()
    {
        _snapshot = await _runtime.Snapshots.GetSnapshotAsync(_snapshot.SnapshotKey, Token) ?? throw new SnapshotConcurrencyException();
        RequiresReload = false; await ClearVersionAsync(); await ReadHistoriesAsync(null);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed || _closing) return;
        _busy = true; _operation = new(); _cancellation = Task.CompletedTask; SetBusy(true);
        try { await action(); }
        catch (SnapshotPublicationOutcomeUnknownException error) { RequiresReload = true; _status.Text = error.Message; }
        catch (SnapshotConcurrencyException error) { RequiresReload = true; _status.Text = error.Message; }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception error) { _status.Text = error.Message; }
        finally
        {
            try { await _cancellation; }
            catch (Exception error) { CleanupFailure ??= error; _status.Text = "Cancellation failed: " + error.Message; }
            _operation.Dispose(); _operation = null; _busy = false; SetBusy(false);
            if (_closing) _closingTask = DrainAndCloseAsync(ClearVersionAsync());
        }
    }
    private void SetBusy(bool busy)
    {
        _histories.IsEnabled = _versions.IsEnabled = _changes.IsEnabled = _resources.IsEnabled = !busy;
        _historyNext.IsEnabled = !busy && _historyCursor != null; _versionNext.IsEnabled = !busy && _versionCursor != null;
        _changeNext.IsEnabled = !busy && _changeCursor != null; _resourceNext.IsEnabled = !busy && _resourceCursor != null;
        _reload.IsEnabled = !busy;
        foreach (var button in new[] { _open, _compareResource, _restoreResource, _copyResource, _export }) button.IsEnabled = !busy && _resources.SelectedItem != null;
        _compareVersions.IsEnabled = !busy && _versions.SelectedItems.Count == 2;
        _restoreVersion.IsEnabled = !busy && _version != null && !RequiresReload;
        _restoreResource.IsEnabled &= !RequiresReload; _exportVersion.IsEnabled = !busy && _version != null;
        _copyVersion.IsEnabled = !busy && _version != null;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return; e.Cancel = true;
        if (_closing || !_closingTask.IsCompleted) return; _closing = true; _close.IsEnabled = false;
        if (_busy) { _status.Text = "Cancelling..."; _cancellation = _operation!.CancelAsync(); }
        else
        {
            var cleanup = ClearVersionAsync();
            if (cleanup.IsCompleted)
            {
                try { cleanup.GetAwaiter().GetResult(); }
                catch (Exception error) { CleanupFailure ??= error; }
                _allowClose = true; e.Cancel = false;
            }
            else _closingTask = DrainAndCloseAsync(cleanup);
        }
    }
    private async Task DrainAndCloseAsync(Task cleanup)
    {
        try { await cleanup; }
        catch (Exception error) { CleanupFailure ??= error; _status.Text = error.Message; }
        finally
        {
            _allowClose = true;
            // Queue the final close even if cleanup completed between the check and await.
            await Dispatcher.InvokeAsync(Close);
        }
    }
    private static DataGrid ListGrid(DataGridSelectionMode selection = DataGridSelectionMode.Single) => new()
    { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = selection,
        EnableRowVirtualization = true, SelectionUnit = DataGridSelectionUnit.FullRow };
    private static DataGridTextColumn Column(string header, string property, double? width = null) => new()
    {
        Header = header, Binding = new Binding(property),
        Width = width is { } fixedWidth ? new(fixedWidth) : new(1, DataGridLengthUnitType.Star),
        MinWidth = width ?? 120, MaxWidth = width ?? double.PositiveInfinity
    };
    private static Grid PagedList(DataGrid list, Button next)
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(list); next.HorizontalAlignment = HorizontalAlignment.Right; Add(grid, next, 1); return grid;
    }
    private static void Add(Grid grid, UIElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
}

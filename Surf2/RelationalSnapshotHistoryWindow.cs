using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public sealed class RelationalHistoryOpenEventArgs(RelationalComparisonTarget target) : EventArgs
{ public RelationalComparisonTarget Target { get; } = target; }

/// <summary>The master's history presentation, backed by immutable SQL version locators.</summary>
public sealed partial class RelationalSnapshotHistoryWindow : Window
{
    private readonly RelationalRuntime _runtime;
    private readonly RelationalSnapshotHistoryService _service;
    private SnapshotSummary _snapshot;
    private readonly List<HistoryVersionRow> _versionRows = [];
    private readonly ObservableCollection<HistoryRow> _visibleRows = [];
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _operation;
    private Task _cancellation = Task.CompletedTask;
    private Task? _initialization;
    private bool _busy, _closing, _allowClose, _closed, _filterPending;
    private long _filterGeneration, _metadataBytes;
    private int _metadataUnits;
    private string? _lineCountFailure;
    public SnapshotSummary? UpdatedSnapshot { get; private set; }
    public bool RequiresReload { get; private set; }
    public Exception? CleanupFailure { get; private set; }
    public Action? VerifyWriteAccess { get; set; }
    public Task CloseCompletion => _closeCompletion.Task;
    public Task Initialization => _initialization ?? Task.CompletedTask;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public Task CloseAndDrainAsync() { Dispatcher.VerifyAccess(); Close(); return CloseCompletion; }
    public event EventHandler<RelationalHistoryOpenEventArgs>? OpenRequested;
    public event EventHandler<RelationalHistoryOpenEventArgs>? CompareWithRequested;
    public event EventHandler<RelationalHistoryOpenEventArgs>? CopyRequested;
    public event Action<SnapshotSummary>? SnapshotChanged;

    public RelationalSnapshotHistoryWindow(RelationalRuntime runtime, SnapshotSummary snapshot)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _service = new(runtime);
        InitializeComponent();
        Title = "History - " + snapshot.DisplayName;
        HeaderTextBlock.Text = "Database History: " + snapshot.DisplayName;
        RowsListView.ItemsSource = _visibleRows;
        RowsListView.ContextMenu = new ContextMenu();
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            if (_busy) { _filterPending = true; return; }
            await RunAsync(ApplyFilterAsync);
        };
        Loaded += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true; _filterTimer.Stop();
            if (CleanupFailure is { } failure) _closeCompletion.TrySetException(failure);
            else _closeCompletion.TrySetResult();
        };
    }
    public Task InitializeAsync()
    {
        Dispatcher.VerifyAccess();
        return _initialization ??= RunAsync(ReadVersionsAsync);
    }
    public Task ReloadRowsAsync() => RunAsync(async () =>
    {
        _snapshot = await _runtime.Snapshots.GetSnapshotAsync(_snapshot.SnapshotKey, Token) ?? throw new SnapshotConcurrencyException();
        RequiresReload = false; await ReadVersionsAsync();
    });
    private CancellationToken Token => _operation!.Token;
    private async Task ReadVersionsAsync()
    {
        _versionRows.Clear(); _metadataBytes = 0; _metadataUnits = 0; _lineCountFailure = null;
        _visibleRows.Clear(); StatusTextBlock.Text = "Loading history…";
        var token = Token;
        var versions = await Task.Run(() => _service.GetDisplayedVersionsAsync(_snapshot, token), token);
        foreach (var version in versions)
        {
            ReserveMetadata(version.VersionName.Length * 2L + 256);
            var row = new HistoryVersionRow(version); _versionRows.Add(row);
            // An initial version's file count is its reconstructed resource count, not ChangeCount.
            // This reads identity metadata only; definitions and captured rows remain unloaded.
            if (version.IsInitial) await LoadChildrenAsync(row);
        }
        RefreshVisibleRows();
        if (!string.IsNullOrWhiteSpace(HistoryFilterTextBox.Text)) await ApplyFilterAsync();
    }
    private void ReserveMetadata(long bytes)
    {
        _metadataBytes = checked(_metadataBytes + bytes);
        if (++_metadataUnits > 100_000 || _metadataBytes > 32L * 1024 * 1024)
            throw new InvalidDataException("This history exceeds the window's metadata budget. Open a smaller snapshot history.");
    }
    private async Task LoadChildrenAsync(HistoryVersionRow row)
    {
        if (row.ChildrenLoaded) return;
        StatusTextBlock.Text = "Loading " + row.VersionColumn + "…";
        var token = Token;
        var files = await Task.Run(() => _service.ListFileRowsAsync(row.Version, token), token);
        foreach (var file in files) ReserveMetadata(384 + 2L * (file.DisplayName.Length + file.RelativePath.Length + file.OriginalResourceKey.Length));
        row.Children.AddRange(files.OrderBy(f => DatabaseHistoryDisplay.GetResourceTypeSortOrder(f.Kind))
            .ThenBy(f => DatabaseHistoryDisplay.GetFileNameDisplay(f.DisplayName, f.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(file => new HistoryFileRow(row, file)));
        row.ChildrenLoaded = true;
    }
    private void RefreshVisibleRows()
    {
        if (_closed) return;
        var selected = RowsListView.SelectedItem as HistoryRow;
        string filter = HistoryFilterTextBox.Text.Trim(); bool filtered = filter.Length != 0;
        int files = 0, versions = 0; _visibleRows.Clear();
        foreach (var version in _versionRows)
        {
            var children = filtered ? version.Children.Where(c => c.Matches(filter)).ToList() : version.Children;
            if (filtered && !version.Matches(filter) && children.Count == 0) continue;
            _visibleRows.Add(version); versions++;
            if (!version.IsExpanded && !filtered) continue;
            foreach (var child in children) { _visibleRows.Add(child); files++; }
        }
        if (selected != null && _visibleRows.Contains(selected)) RowsListView.SelectedItem = selected;
        StatusTextBlock.Text = filtered ? $"{files} matching file(s) in {versions} version(s)." : $"{_versionRows.Count} version(s).";
        if (_lineCountFailure != null) StatusTextBlock.Text += " Some line counts are unavailable: " + _lineCountFailure;
    }
    private async Task ApplyFilterAsync()
    {
        _filterPending = false; long generation = _filterGeneration;
        if (!string.IsNullOrWhiteSpace(HistoryFilterTextBox.Text))
            foreach (var version in _versionRows)
            {
                if (generation != _filterGeneration) { _filterPending = true; return; }
                await LoadChildrenAsync(version);
            }
        string filter = HistoryFilterTextBox.Text.Trim();
        if (filter.Length > 0 && filter.All(c => char.IsDigit(c) || c is '+' or '-'))
            await LoadCountsAsync(_versionRows.SelectMany(v => v.Children).ToArray(), generation);
        RefreshVisibleRows(); await LoadVisibleCountsAsync(generation);
    }
    private Task LoadVisibleCountsAsync(long generation) => LoadCountsAsync(_visibleRows.OfType<HistoryFileRow>().ToArray(), generation);
    private async Task LoadCountsAsync(IReadOnlyList<HistoryFileRow> rows, long generation)
    {
        foreach (var group in rows.Where(row => !row.CountsLoaded).GroupBy(row => row.File.Version.VersionKey))
        {
            if (generation != _filterGeneration) { _filterPending = true; return; }
            var byFile = group.ToDictionary(row => row.File);
            var files = byFile.Keys.ToArray(); var token = Token;
            // One worker reuses the selected/previous SQL projections. Each UI update is
            // awaited, so close waits for the iterator and its contexts to retire.
            await Task.Run(async () =>
            {
                await foreach (var counts in _service.CountFileLinesAsync(files, token).ConfigureAwait(false))
                {
                    bool keepGoing = await Dispatcher.InvokeAsync(() =>
                    {
                        if (generation != _filterGeneration) { _filterPending = true; return false; }
                        var row = byFile[counts.File];
                        if (counts.Error is { } error) { row.SetCountError(error); _lineCountFailure ??= error; }
                        else row.SetCounts(counts.Added, counts.Removed);
                        return !_closing;
                    });
                    if (!keepGoing) break;
                }
            }, token);
        }
        RefreshVisibleRows();
    }
    private void HistoryFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ++_filterGeneration;
        if (_initialization == null || _closing || _closed) return;
        if (string.IsNullOrWhiteSpace(HistoryFilterTextBox.Text)) RefreshVisibleRows();
        _filterPending = true; _filterTimer.Stop(); _filterTimer.Start();
    }
    public async Task ToggleRowAsync(HistoryVersionRow row)
    {
        if (_busy || _closing || _closed) return;
        if (row.IsExpanded) { row.IsExpanded = false; RefreshVisibleRows(); return; }
        await RunAsync(async () =>
        {
            await LoadChildrenAsync(row); row.IsExpanded = true; RefreshVisibleRows();
            await LoadVisibleCountsAsync(_filterGeneration);
        });
    }
    private async void RowsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (RowsListView.SelectedItem is HistoryVersionRow row) await ToggleRowAsync(row);
    }
    private async void ToggleRowButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is HistoryVersionRow row) await ToggleRowAsync(row);
    }
    private void RowsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item) { item.IsSelected = true; item.Focus(); }
    }
    private void RowsListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ContextMenu? menu = RowsListView.SelectedItem switch
        {
            HistoryVersionRow version => CreateVersionContextMenu(version),
            HistoryFileRow file => CreateFileContextMenu(file), _ => null
        };
        if (menu == null || _busy || _closing) { e.Handled = true; return; }
        RowsListView.ContextMenu = menu;
    }
    private ContextMenu CreateVersionContextMenu(HistoryVersionRow row)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "Compare With...", () => { CompareWithRequested?.Invoke(this, new(VersionTarget(row))); return Task.CompletedTask; });
        AddMenuItem(menu, "Restore", () => RestoreVersionAsync(row), write: true);
        AddMenuItem(menu, "Restore To...", () => { VerifyWriteAccess?.Invoke(); CopyRequested?.Invoke(this, new(VersionTarget(row))); return Task.CompletedTask; }, write: true);
        return menu;
    }
    private ContextMenu CreateFileContextMenu(HistoryFileRow row)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "View", () => ViewFileAsync(row));
        AddMenuItem(menu, "Compare With...", async () => CompareWithRequested?.Invoke(this, new(await FileTargetAsync(row))));
        AddMenuItem(menu, "Restore", () => RestoreFileAsync(row), write: true);
        AddMenuItem(menu, "Restore To...", async () => { VerifyWriteAccess?.Invoke(); CopyRequested?.Invoke(this, new(await FileTargetAsync(row))); }, write: true);
        return menu;
    }
    private void AddMenuItem(ContextMenu menu, string header, Func<Task> action, bool write = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = !_busy && !_closing && (!write || !RequiresReload) };
        item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item);
    }
    private RelationalComparisonTarget VersionTarget(HistoryVersionRow row)
    {
        var target = _service.VersionTarget(_snapshot, row.Version.VersionKey, row.Version.VersionName);
        return target with { Resource = target.Resource with { DisplayName = _snapshot.DisplayName + " " + row.Version.VersionName } };
    }
    private Task<RelationalComparisonTarget> FileTargetAsync(HistoryFileRow row) => _service.FileTargetAsync(row.File, Token);
    private async Task ViewFileAsync(HistoryFileRow row)
    {
        if (row.File.Entry != null) { OpenRequested?.Invoke(this, new(await FileTargetAsync(row))); return; }
        string text = row.File.RelativePath + " does not exist in " + row.File.Version.VersionName + ".";
        string label = row.NameColumn + " (" + row.File.Version.VersionName + ")";
        var resource = new ComparisonResource(label, "Database Resource", label, ComparisonResourceKind.StoredProcedure,
            "DatabaseResource", false, true, false, label);
        var window = new ResourceFileDiffWindow(resource, resource, text, text, false, false, startInUnifiedMode: true)
        { Owner = this, Title = row.NameColumn + " - " + row.File.Version.VersionName };
        window.Show();
    }
    private async Task RestoreVersionAsync(HistoryVersionRow row)
    {
        VerifyWriteAccess?.Invoke(); RequireRestoreReady();
        if (MessageBox.Show(this, $"Restore {_snapshot.DisplayName} to {row.Version.VersionName}? This will create a new version.",
            "Restore Database Version", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var token = Token;
        var restored = await Task.Run(() => _service.RestoreVersionAsync(ExpectedSnapshot(), row.Version.VersionKey, ct: token), token);
        await AdoptRestoredSnapshotAsync(restored);
    }
    private async Task RestoreFileAsync(HistoryFileRow row)
    {
        VerifyWriteAccess?.Invoke(); RequireRestoreReady(); var token = Token;
        var restored = await Task.Run(() => _service.RestoreResourceAsync(ExpectedSnapshot(), row.File.Version.VersionKey, row.File.ResourceKey, ct: token), token);
        await AdoptRestoredSnapshotAsync(restored);
    }
    private void RequireRestoreReady()
    { if (RequiresReload) throw new InvalidOperationException("Close and reopen history before restoring after a snapshot conflict."); }
    private SnapshotRuntimeToken ExpectedSnapshot() => new(_runtime.Session.Epoch, _snapshot.SnapshotKey, _snapshot.RowVersion);
    private async Task AdoptRestoredSnapshotAsync(RelationalHistoryRestoreResult restored)
    {
        _snapshot = restored.Snapshot; UpdatedSnapshot = restored.Snapshot;
        SnapshotChanged?.Invoke(restored.Snapshot); await ReadVersionsAsync();
    }
    private Task RunAsync(Func<Task> action)
    {
        if (_busy || _closing || _closed) return Task.CompletedTask;
        return CurrentOperation = RunCoreAsync(action);
    }
    private async Task RunCoreAsync(Func<Task> action)
    {
        _busy = true; _operation = new(); _cancellation = Task.CompletedTask;
        try { await action(); }
        catch (SnapshotPublicationOutcomeUnknownException error) { RequiresReload = true; StatusTextBlock.Text = error.Message + " Close and reopen history before restoring."; }
        catch (SnapshotConcurrencyException error) { RequiresReload = true; StatusTextBlock.Text = error.Message + " Close and reopen history before restoring."; }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { StatusTextBlock.Text = "Cancelled."; }
        catch (Exception error) { if (_closing) CleanupFailure ??= error; StatusTextBlock.Text = error.Message; }
        finally
        {
            try { await _cancellation; }
            catch (Exception error) { CleanupFailure ??= error; StatusTextBlock.Text = "Cancellation failed: " + error.Message; }
            _operation.Dispose(); _operation = null; _busy = false; RowsListView.IsEnabled = !_closing;
            if (_closing) { _allowClose = true; await Dispatcher.InvokeAsync(Close); }
            else if (_filterPending) { _filterTimer.Stop(); _filterTimer.Start(); }
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _filterTimer.Stop();
        if (_allowClose) return;
        if (_closing) { e.Cancel = true; return; }
        _closing = true; HistoryFilterTextBox.IsEnabled = false; RowsListView.IsEnabled = false;
        if (_busy) { e.Cancel = true; StatusTextBlock.Text = "Cancelling…"; _cancellation = _operation!.CancelAsync(); }
        else _allowClose = true;
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T value) return value;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
    public abstract class HistoryRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public abstract bool IsVersion { get; }
        public abstract string VersionColumn { get; }
        public abstract string ResourceTypeColumn { get; }
        public abstract string NameColumn { get; }
        public abstract string AddedColumn { get; }
        public abstract string RemovedColumn { get; }
        public virtual Thickness IndentMargin => new(0);
        public virtual Visibility ToggleVisibility => Visibility.Hidden;
        public virtual string ToggleGlyph => "";
        public virtual string ToggleToolTip => "";
        public virtual bool Matches(string filter) => new[] { VersionColumn, ResourceTypeColumn, NameColumn, AddedColumn, RemovedColumn }
            .Any(value => DatabaseHistoryDisplay.ContainsText(value, filter));
        protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
    public sealed class HistoryVersionRow(SnapshotVersionSummary version) : HistoryRow
    {
        private bool _expanded, _childrenLoaded;
        public SnapshotVersionSummary Version { get; } = version;
        public List<HistoryFileRow> Children { get; } = [];
        public bool ChildrenLoaded { get => _childrenLoaded; set { _childrenLoaded = value; Notify(); Notify(nameof(RemovedColumn)); Notify(nameof(ToggleVisibility)); } }
        public bool IsExpanded { get => _expanded; set { _expanded = value; Notify(); Notify(nameof(ToggleGlyph)); Notify(nameof(ToggleToolTip)); } }
        public override bool IsVersion => true;
        public override string VersionColumn => "Version " + Version.VersionName;
        public override string ResourceTypeColumn => "";
        public override string NameColumn => "";
        public override string AddedColumn => Version.CreatedAtUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        public override string RemovedColumn => (ChildrenLoaded ? Children.Count.ToString(CultureInfo.CurrentCulture) : Version.IsInitial ? "…" : Version.ChangeCount.ToString(CultureInfo.CurrentCulture)) + " file(s)";
        public override Visibility ToggleVisibility => (ChildrenLoaded ? Children.Count != 0 : Version.IsInitial || Version.ChangeCount != 0) ? Visibility.Visible : Visibility.Hidden;
        public override string ToggleGlyph => IsExpanded ? "▲" : "▼";
        public override string ToggleToolTip => IsExpanded ? "Collapse" : "Expand";
    }
    public sealed class HistoryFileRow(HistoryVersionRow version, RelationalHistoryFileSummary file) : HistoryRow
    {
        private int? _added, _removed;
        public HistoryVersionRow VersionRow { get; } = version;
        public RelationalHistoryFileSummary File { get; } = file;
        public bool CountsLoaded { get; private set; }
        public string? CountError { get; private set; }
        public override bool IsVersion => false;
        public override string VersionColumn => "";
        public override string ResourceTypeColumn => DatabaseHistoryDisplay.GetResourceTypeDisplay(File.Kind);
        public override string NameColumn => DatabaseHistoryDisplay.GetFileNameDisplay(File.DisplayName, File.RelativePath);
        public override string AddedColumn => _added.HasValue ? "+" + _added.Value.ToString(CultureInfo.CurrentCulture) : CountError == null ? "…" : "—";
        public override string RemovedColumn => _removed.HasValue ? "-" + _removed.Value.ToString(CultureInfo.CurrentCulture) : CountError == null ? "…" : "—";
        public override Thickness IndentMargin => new(28, 0, 0, 0);
        public override bool Matches(string filter) => base.Matches(filter) || DatabaseHistoryDisplay.ContainsText(File.DisplayName, filter) || DatabaseHistoryDisplay.ContainsText(File.RelativePath, filter);
        public void SetCounts(int added, int removed) { _added = added; _removed = removed; CountsLoaded = true; Notify(nameof(AddedColumn)); Notify(nameof(RemovedColumn)); }
        public void SetCountError(string message) { CountError = message; CountsLoaded = true; Notify(nameof(AddedColumn)); Notify(nameof(RemovedColumn)); }
    }
}

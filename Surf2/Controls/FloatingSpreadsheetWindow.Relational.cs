using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Surf2.Controls.RelationalGrid;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalGrid;

namespace Surf2.Controls;

public sealed class GridReadErrorEventArgs(Exception error, Guid sourceId, long queryGeneration, bool requiresReopen) : EventArgs
{
    public Exception Error { get; } = error;
    public Guid SourceId { get; } = sourceId;
    public long QueryGeneration { get; } = queryGeneration;
    public bool RequiresReopen { get; } = requiresReopen;
}

internal sealed record GridReopenColumn(int Ordinal, int DisplayIndex, Visibility Visibility, DataGridLength Width);
internal sealed record GridReopenView(GridQuery Query, GridReopenColumn[] Columns, GridBindingLimits Limits,
    long OverlayGeneration, long OverlayBytes, string Fingerprint);

public partial class FloatingSpreadsheetWindow
{
    private IDataGridSource? _gridSource;
    private GridViewportItems? _gridItems;
    private GridPagedCollectionView? _gridView;
    private IGridQuerySession? _gridQuery;
    private readonly List<GridSort> _gridSorts = [];
    private readonly HashSet<Task> _gridTasks = [];
    private readonly List<Exception> _gridBackgroundErrors = [];
    private GridCancellation? _gridLifetime;
    private GridCancellation? _gridQueryCancellation;
    private GridCancellation? _gridPageCancellation;
    private GridCancellation? _gridOutputCancellation;
    private DispatcherTimer? _gridCountTimer;
    private DispatcherTimer? _gridViewportTimer;
    private ScrollViewer? _gridScroll;
    private Task _gridQueryWork = Task.CompletedTask;
    private Task _gridPageWork = Task.CompletedTask;
    private Task? _gridCloseTask;
    private long _gridRequestGeneration;
    private long _gridActiveRequestGeneration;
    private string? _gridSignature;
    private string? _gridOutputStatus;
    private Exception? _gridReadError;
    private bool _gridStarted;
    private bool _gridClosing;
    private bool _gridEditRefresh;
    private bool _gridSourceCountStarted;
    private long? _gridDisplayCount;

    // Ownership of source transfers after this constructor succeeds. The parent
    // must await DisposeGridAsync on logical close, never on Unloaded.
    public FloatingSpreadsheetWindow(OpenDocumentState state, IDataGridSource source) : this(state, source, new()) { }

    public FloatingSpreadsheetWindow(OpenDocumentState state, IDataGridSource source, GridBindingLimits limits) : this(state)
    {
        ArgumentNullException.ThrowIfNull(source);
        _gridItems = new(source, limits);
        _gridView = new(_gridItems);
        _gridSource = source;
        _gridLifetime = new();
        _isCsvLoaded = true;
        _columnCount = source.Descriptor.Columns.Count;
        BuildColumns(source.Descriptor.Columns.Select(column => column.Header).ToArray());
        foreach (var column in SpreadsheetGrid.Columns.OfType<DataGridTextColumn>())
        {
            column.Binding = new Binding($"[{GetColumnIndex(column)}]")
            {
                Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
                ValidatesOnExceptions = true, NotifyOnValidationError = true
            };
        }
        SpreadsheetGrid.ItemsSource = _gridView;
        SpreadsheetGrid.CanUserResizeRows = false;
        SpreadsheetGrid.SelectionUnit = DataGridSelectionUnit.Cell;
        // Selection is deliberately bounded. All-matching operations use a
        // separate provider stream, not SelectedItems or collection enumeration.
        SpreadsheetGrid.SelectionMode = DataGridSelectionMode.Single;
        SpreadsheetGrid.RowHeight = Math.Max(26, SpreadsheetGrid.FontSize * 1.6 + 8);
        UpdateGridHeaderHeight();
        ScrollViewer.SetCanContentScroll(SpreadsheetGrid, true);
        VirtualizingPanel.SetScrollUnit(SpreadsheetGrid, ScrollUnit.Item);
        VirtualizingPanel.SetCacheLengthUnit(SpreadsheetGrid, VirtualizationCacheLengthUnit.Item);
        VirtualizingPanel.SetCacheLength(SpreadsheetGrid, new VirtualizationCacheLength(2));
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(GridViewportRow.Status))));
        var loading = new DataTrigger { Binding = new Binding(nameof(GridViewportRow.IsLoading)), Value = true };
        loading.Setters.Add(new Setter(OpacityProperty, 0.55));
        style.Triggers.Add(loading);
        SpreadsheetGrid.RowStyle = style;
        SpreadsheetGrid.LoadingRow += GridLoadingRow;
        SpreadsheetGrid.UnloadingRow += GridUnloadingRow;
        SpreadsheetGrid.Sorting += GridSorting;
        SpreadsheetGrid.BeginningEdit += GridBeginningEdit;
        SpreadsheetGrid.CellEditEnding += GridCellEditEnding;
        SpreadsheetGrid.RowEditEnding += GridRowEditEnding;
        SpreadsheetGrid.SizeChanged += GridSizeChanged;
        SpreadsheetGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(GridScrollChanged));
        SpreadsheetGrid.CommandBindings.Add(new CommandBinding(DataGrid.SelectAllCommand, GridSelectAll, GridCanSelectAll));
        _gridItems.CellCommitted += GridCellCommitted;
        _gridCountTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, GridCountTick, Dispatcher);
        _gridCountTimer.Stop();
        _gridViewportTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, GridViewportTick, Dispatcher);
        _gridViewportTimer.Stop();
        LoadingText.Text = "Preparing table...";
        if (!ApplyPendingColumnFilters()) { RestoreFiltersFromStateIfNeeded(); RefreshFilterTextBoxes(); }
    }

    public event EventHandler<GridReadErrorEventArgs>? GridReadFailed;
    public event EventHandler? GridReopenRequested;
    public bool UsesGridProvider => _gridSource != null;
    public bool IsGridDisposed => _gridClosing;
    public Exception? GridReadError => _gridReadError;
    internal IDataGridSource PreparedGridSource => _gridSource ?? throw new ObjectDisposedException(nameof(FloatingSpreadsheetWindow));

    internal bool TryCaptureGridReopenView(out GridReopenView? view)
    {
        view = null;
        if (_gridSource == null || _gridClosing || !CommitGridEditor()) return false;
        if (_gridOutputCancellation != null)
        { StatusText.Text = "Finish or cancel output before reopening the source."; return false; }
        var query = CaptureGridQuery();
        var columns = SpreadsheetGrid.Columns.Select(c => new GridReopenColumn(GetColumnIndex(c), c.DisplayIndex, c.Visibility, c.Width)).ToArray();
        view = new(query, columns, _gridItems!.Limits, _gridSource.OverlayGeneration, _gridSource.OverlayBytes,
            JsonSerializer.Serialize(new
            {
                Query = query,
                Columns = columns.Select(c => new { c.Ordinal, c.DisplayIndex, c.Visibility, c.Width.Value, c.Width.UnitType })
            }));
        return true;
    }

    internal void RestoreGridReopenView(GridReopenView view)
    {
        if (_gridStarted || _gridClosing) throw new InvalidOperationException("Restore the grid view before preparing publication.");
        _filters.Clear(); _anyMatchFilterColumnIndexes.Clear(); _gridSorts.Clear();
        foreach (var filter in view.Query.Filters ?? []) _filters.Add(filter.ColumnOrdinal, filter.Text);
        foreach (int ordinal in view.Query.AnyMatchColumnOrdinals ?? []) _anyMatchFilterColumnIndexes.Add(ordinal);
        _matchAnyColumnFilter = _anyMatchFilterColumnIndexes.Count != 0;
        _gridSorts.AddRange(view.Query.Sorts ?? []);
        foreach (var column in view.Columns.OrderBy(c => c.DisplayIndex))
        {
            var target = SpreadsheetGrid.Columns[column.Ordinal];
            target.DisplayIndex = column.DisplayIndex; target.Visibility = column.Visibility; target.Width = column.Width;
            var sort = _gridSorts.FirstOrDefault(s => s.ColumnOrdinal == column.Ordinal);
            target.SortDirection = sort == null ? null : sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        }
        RefreshFilterTextBoxes();
    }

    internal async Task PrepareGridPublicationAsync(CancellationToken cancellationToken)
    {
        if (_gridSource == null || _gridClosing) throw new ObjectDisposedException(nameof(FloatingSpreadsheetWindow));
        // Complete the bounded disk staging/index, not a resident row graph.
        _gridDisplayCount = await _gridSource.DisplayRowCount.WaitAsync(cancellationToken);
        _gridSourceCountStarted = true; _gridStarted = true;
        RequestGridQuery(force: true);
        await _gridQueryWork.WaitAsync(cancellationToken);
        if (_gridReadError != null) throw _gridReadError;
        var query = _gridQuery ?? throw new InvalidOperationException("The replacement grid has no prepared query.");
        await query.Completion.WaitAsync(cancellationToken);
        var page = await query.ReadPageAsync(new(0, _gridItems!.Limits.PageRows, _gridItems.Limits.PageBytes), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        PollGridCount();
        _gridItems.Publish(page, _gridItems.Generation);
        if (_gridItems.Error != null) throw _gridItems.Error;
        if (!page.IsRangeComplete) throw new InvalidDataException("The replacement's first bounded page is incomplete.");
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    internal void AdoptPreparedGridState(OpenDocumentState original)
    {
        if (_gridSource == null || _gridClosing || !_gridStarted || _gridReadError != null || !string.Equals(State.FilePath, original.FilePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Only a prepared same-document grid can adopt its original layout identity.");
        State = original;
    }

    private GridQuery CaptureGridQuery() => new(
        _filters.OrderBy(pair => pair.Key).Select(pair => new GridFilter(pair.Key, pair.Value)).ToArray(),
        _matchAnyColumnFilter ? _anyMatchFilterColumnIndexes.Order().ToArray() : [],
        _gridSorts.ToArray(), CultureInfo.CurrentCulture.Name);

    private void RequestGridQuery(bool force = false)
    {
        if (_gridSource == null || !_gridStarted || _gridClosing) return;
        if (!CommitGridEditor()) return;
        var specification = CaptureGridQuery();
        string signature = JsonSerializer.Serialize(new { Query = specification, _gridSource.OverlayGeneration });
        if (!force && string.Equals(signature, _gridSignature, StringComparison.Ordinal)) return;
        _gridSignature = signature;
        _gridEditRefresh = false;
        var previousCancellation = _gridQueryCancellation;
        CancelGridWork(previousCancellation);
        CancelGridWork(_gridPageCancellation);
        var cancellation = new GridCancellation(_gridLifetime!.Token);
        _gridQueryCancellation = cancellation;
        long generation = ++_gridRequestGeneration;
        Task preceding = _gridQueryWork;
        if (_gridOutputCancellation == null) _gridOutputStatus = null;
        _gridQueryWork = ReplaceGridQueryAsync(preceding, specification, generation, previousCancellation, cancellation);
        TrackGridWork(_gridQueryWork);
    }

    private async Task ReplaceGridQueryAsync(Task preceding, GridQuery specification, long generation,
        GridCancellation? previousCancellation, GridCancellation cancellation)
    {
        IGridQuerySession? created = null;
        try
        {
            try { await preceding; } catch { /* The preceding boundary already reported its failure. */ }
            if (_gridQuery != null)
            {
                var previous = _gridQuery;
                _gridQuery = null;
                await previous.DisposeAsync();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_gridClosing || generation != _gridRequestGeneration) return;
            _gridItems!.Bind(null);
            _gridReadError = null;
            RetryGridButton.Visibility = Visibility.Collapsed;
            LoadingText.Text = specification.Sorts?.Count > 0 ? "Sorting table..." : "Loading table...";
            LoadingOverlay.Visibility = Visibility.Visible;
            created = await _gridSource!.CreateQueryAsync(specification, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_gridClosing || generation != _gridRequestGeneration) return;
            _gridQuery = created;
            _gridActiveRequestGeneration = generation;
            created = null;
            _gridItems.Bind(_gridQuery);
            PollGridCount();
            QueueGridViewport();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_gridClosing && generation == _gridRequestGeneration) ShowGridReadError(error);
        }
        finally
        {
            if (created != null) await created.DisposeAsync();
            if (previousCancellation != null) await previousCancellation.RetireAsync();
            // A live query keeps this CTS until it is replaced/closed: it owns
            // the provider's complete query lifetime, not merely query creation.
            if (!ReferenceEquals(_gridQueryCancellation, cancellation)) await cancellation.RetireAsync();
        }
    }

    private void ResumeGridViewport()
    {
        if (_gridSource == null || _gridClosing) return;
        _gridStarted = true;
        if (!_gridSourceCountStarted)
        {
            _gridSourceCountStarted = true;
            TrackGridWork(ObserveSourceCountAsync());
        }
        _gridCountTimer!.Start();
        RequestGridQuery();
        QueueGridViewport();
    }

    private async Task ObserveSourceCountAsync()
    {
        try
        {
            long count = await _gridSource!.DisplayRowCount.WaitAsync(_gridLifetime!.Token);
            if (!_gridClosing) { _gridDisplayCount = count; PollGridCount(); }
        }
        catch (OperationCanceledException) when (_gridClosing) { }
        catch (Exception error) { if (!_gridClosing) ShowGridReadError(error); }
    }

    private void SuspendGridViewport()
    {
        _gridCountTimer?.Stop();
        _gridViewportTimer?.Stop();
        CancelGridWork(_gridPageCancellation);
        // Keep the source/query/overlay and pending output through reparenting.
    }

    private void GridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is GridViewportRow row) _gridItems!.Pin(row, true);
        QueueGridViewport();
    }
    private void GridUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is GridViewportRow row) _gridItems!.Pin(row, false);
    }
    private void GridSizeChanged(object sender, SizeChangedEventArgs e) => QueueGridViewport();
    private void GridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _gridScroll ??= FindVisualChildren<ScrollViewer>(SpreadsheetGrid).FirstOrDefault();
        if (ReferenceEquals(e.OriginalSource, _gridScroll) && (e.VerticalChange != 0 || e.ViewportHeightChange != 0))
        { CancelGridWork(_gridPageCancellation); QueueGridViewport(); }
    }
    private void QueueGridViewport()
    {
        if (_gridClosing || !_gridStarted || !IsVisible || _isDetachedFromVisualTree || _gridQuery == null) return;
        _gridViewportTimer!.Stop(); _gridViewportTimer.Start();
    }
    private void GridViewportTick(object? sender, EventArgs e)
    {
        _gridViewportTimer?.Stop();
        if (_gridClosing || _isDetachedFromVisualTree || _gridQuery == null || _gridItems!.UnaddressableCount != null) return;
        if (!_gridPageWork.IsCompleted)
        {
            CancelGridWork(_gridPageCancellation);
            _gridViewportTimer!.Start();
            return;
        }
        CancelGridWork(_gridPageCancellation);
        var cancellation = new GridCancellation(_gridLifetime!.Token);
        _gridPageCancellation = cancellation;
        _gridPageWork = ReadGridViewportAsync(_gridQuery, _gridItems.Generation, cancellation);
        TrackGridWork(_gridPageWork);
    }

    private async Task ReadGridViewportAsync(IGridQuerySession query, long generation, GridCancellation cancellation)
    {
        try
        {
            _gridScroll ??= FindVisualChildren<ScrollViewer>(SpreadsheetGrid).FirstOrDefault();
            long start = Math.Max(0, (long)Math.Floor(_gridScroll?.VerticalOffset ?? 0));
            int viewport = checked((int)Math.Ceiling(Math.Max(0, SpreadsheetGrid.ActualHeight) / SpreadsheetGrid.RowHeight)) + 4;
            if (viewport > _gridItems!.Limits.MaxItems / 2)
                throw new GridLimitException("The visible viewport exceeds its bounded item budget. Reduce the window height or increase the explicit budget.");
            long end = checked(start + Math.Max(1, viewport));
            do
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int rows = (int)Math.Min(_gridItems.Limits.PageRows, Math.Max(1, end - start));
                var request = new GridPageRequest(start, rows, _gridItems.Limits.PageBytes);
                GridPage page = await query.ReadPageAsync(request, cancellation.Token);
                if (_gridClosing || !ReferenceEquals(query, _gridQuery) || generation != _gridItems.Generation) return;
                PollGridCount();
                _gridItems.Publish(page, generation);
                if (_gridItems.Error != null) throw _gridItems.Error;
                if (page.Rows.Count != 0 || query.Count.IsComplete)
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                if (page.Rows.Count == 0 || !page.HasMore || !page.IsRangeComplete) break;
                start += page.Rows.Count;
            } while (start < end);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_gridClosing && ReferenceEquals(query, _gridQuery) && generation == _gridItems!.Generation)
            { _gridItems.Fail(error, generation); ShowGridReadError(error); }
        }
        finally
        {
            if (ReferenceEquals(_gridPageCancellation, cancellation)) _gridPageCancellation = null;
            await cancellation.RetireAsync();
        }
    }

    private void GridCountTick(object? sender, EventArgs e)
    {
        if (_gridClosing) return;
        PollGridCount();
        if (_gridEditRefresh && _gridView?.IsEditingItem != true) RequestGridQuery();
    }

    private void PollGridCount()
    {
        if (_gridClosing || _gridQuery == null || _gridActiveRequestGeneration != _gridRequestGeneration) return;
        if (_gridQuery.Completion.IsFaulted)
        { ShowGridReadError(_gridQuery.Completion.Exception!.GetBaseException()); return; }
        if (_gridQuery.Completion.IsCanceled)
        { ShowGridReadError(new OperationCanceledException("Table query was canceled. Retry the view.")); return; }
        if (_gridView?.IsEditingItem != true)
        {
            double offset = _gridScroll?.VerticalOffset ?? 0;
            var selected = SpreadsheetGrid.CurrentCell;
            int position = selected.Item is GridViewportRow row ? row.Position : -1;
            if (_gridItems!.UpdateCount(_gridQuery.Count))
            {
                if (position >= 0 && position < _gridItems.Count && selected.Column != null)
                {
                    var cell = new DataGridCellInfo(_gridItems[position]!, selected.Column);
                    SpreadsheetGrid.CurrentCell = cell;
                    SpreadsheetGrid.SelectedCells.Clear(); SpreadsheetGrid.SelectedCells.Add(cell);
                }
                _gridScroll?.ScrollToVerticalOffset(Math.Min(offset, Math.Max(0, _gridItems.Count - 1)));
                QueueGridViewport();
            }
        }
        if (_gridItems!.Error != null) { ShowGridReadError(_gridItems.Error); return; }
        if (_gridReadError != null || _gridOutputStatus != null) return;
        var count = _gridQuery.Count;
        string total = _gridDisplayCount?.ToString("N0") ?? "pending";
        StatusText.Text = $"Rows: {count.AvailableRows:N0}{(count.IsComplete ? "" : "+ (count pending)")}/{total}    Columns: {_columnCount}";
        StatusText.ToolTip = StatusText.Text;
        if (count.IsComplete && count.AvailableRows == 0) LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void GridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!CommitGridEditor()) return;
        int column = GetColumnIndex(e.Column);
        if (column < 0) return;
        bool descending = e.Column.SortDirection == ListSortDirection.Ascending;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            _gridSorts.Clear();
            foreach (var existing in SpreadsheetGrid.Columns) existing.SortDirection = null;
        }
        _gridSorts.RemoveAll(sort => sort.ColumnOrdinal == column);
        _gridSorts.Add(new(column, descending));
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        RequestGridQuery();
    }

    private void UpdateGridHeaderHeight()
    {
        SpreadsheetGrid.ColumnHeaderHeight = Math.Max(52, SpreadsheetGrid.FontSize * 1.5 + 32);
        int chromeRows = (int)Math.Ceiling((SpreadsheetGrid.ColumnHeaderHeight + SystemParameters.HorizontalScrollBarHeight) / SpreadsheetGrid.RowHeight);
        int rows = Math.Max(1, _gridItems!.Limits.MaxItems / 2 - chromeRows - 4);
        SpreadsheetGrid.MaxHeight = rows * SpreadsheetGrid.RowHeight + SpreadsheetGrid.ColumnHeaderHeight + SystemParameters.HorizontalScrollBarHeight;
        // Error/loading state must not block the filters needed to narrow an
        // unaddressable result or correct an invalid query specification.
        LoadingOverlay.Margin = new(0, SpreadsheetGrid.ColumnHeaderHeight, 0, 0);
    }

    private void GridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_gridClosing || _gridReadError != null || e.Row.Item is not GridViewportRow { IsLoaded: true })
        { e.Cancel = true; StatusText.Text = "Wait for a readable row before editing."; }
    }
    private void GridCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not GridViewportRow row || e.EditingElement is not TextBox editor) return;
        try
        {
            row[GetColumnIndex(e.Column)] = editor.Text;
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        catch (Exception error)
        {
            e.Cancel = true;
            StatusText.Text = "Edit not committed: " + error.Message;
            editor.ToolTip = error.Message;
        }
    }
    private void GridCellCommitted(object? sender, EventArgs e) => _gridEditRefresh = true;
    private void GridRowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_gridClosing && _gridEditRefresh && _gridView?.IsEditingItem != true) RequestGridQuery();
        }));
    }
    private bool CommitGridEditor()
    {
        if (_gridClosing) return false;
        if (SpreadsheetGrid.CommitEdit(DataGridEditingUnit.Cell, true) && SpreadsheetGrid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        StatusText.Text = "Commit or cancel the active cell edit before changing the view or exporting.";
        return false;
    }

    private void GridCanSelectAll(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = true; e.Handled = true; }
    private void GridSelectAll(object sender, ExecutedRoutedEventArgs e)
    { e.Handled = true; StatusText.Text = "Select All is unavailable in this paged view."; }

    private void ShowGridReadError(Exception error)
    {
        if (_gridClosing) return;
        bool changed = !ReferenceEquals(_gridReadError, error) && _gridReadError?.Message != error.Message;
        _gridReadError = error;
        StatusText.Text = "Table read failed: " + error.Message;
        StatusText.ToolTip = StatusText.Text;
        LoadingText.Text = StatusText.Text;
        LoadingOverlay.Visibility = Visibility.Visible;
        RetryGridButton.Visibility = Visibility.Visible;
        if (changed && _gridSource != null)
            GridReadFailed?.Invoke(this, new(error, _gridSource.Descriptor.SourceId, _gridQuery?.Generation ?? 0,
                _gridSource.IsInvalidated || _gridSource.DisplayRowCount.IsFaulted));
    }

    private void GridRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_gridSource == null || _gridClosing) return;
        if (_gridSource.IsInvalidated || _gridSource.DisplayRowCount.IsFaulted)
        {
            StatusText.Text = "The pinned source must be reopened; session edits remain until logical close.";
            GridReopenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _gridReadError = null;
        _gridItems!.ClearError();
        if (_gridQuery?.Completion.IsFaulted == true || _gridQuery?.Completion.IsCanceled == true) RequestGridQuery(force: true);
        else { RetryGridButton.Visibility = Visibility.Collapsed; QueueGridViewport(); }
    }

    private void StartGridOutput(bool export)
    {
        if (_gridSource == null || _gridClosing || _gridOutputCancellation != null || !CommitGridEditor()) return;
        _filterDebounceTimer.Stop();
        GridQuery specification = CaptureGridQuery();
        int[] columns = GetVisibleColumnDescriptors().Select(column => column.ColumnIndex).ToArray();
        if (columns.Length == 0) { StatusText.Text = "No visible columns to output."; return; }
        string? destination = null;
        if (export)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export Table Data", DefaultExt = ".csv", FileName = CreateCsvExportFileName(),
                Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*", OverwritePrompt = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            destination = dialog.FileName;
        }
        var cancellation = new GridCancellation(_gridLifetime!.Token);
        _gridOutputCancellation = cancellation;
        CopyGridButton.IsEnabled = ExportGridButton.IsEnabled = false;
        CancelGridOperationButton.Visibility = Visibility.Visible;
        TrackGridWork(OutputGridAsync(specification, columns, destination, cancellation));
    }

    private async Task OutputGridAsync(GridQuery specification, int[] columns, string? destination, GridCancellation cancellation)
    {
        try
        {
            _gridOutputStatus = destination == null ? "Preparing Copy..." : "Exporting table...";
            StatusText.Text = _gridOutputStatus;
            await using var operation = await _gridSource!.CreateQueryAsync(specification, cancellation.Token);
            if (destination == null)
            {
                var result = await GridOutput.CopyAsync(operation, columns, cancellationToken: cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_gridClosing) return;
                TextClipboardService.CopyText(result.Text);
                _gridOutputStatus = $"Copied {result.RowCount:N0} matching row(s) and {result.ColumnCount} column(s).";
                StatusText.Text = _gridOutputStatus;
            }
            else
            {
                var progress = new Progress<long>(rows =>
                {
                    if (!_gridClosing && ReferenceEquals(_gridOutputCancellation, cancellation))
                    { _gridOutputStatus = $"Exporting {rows:N0} matching row(s)..."; StatusText.Text = _gridOutputStatus; }
                });
                var result = await GridOutput.ExportAsync(operation, destination, columns, replaceExisting: true,
                    progress: progress, cancellationToken: cancellation.Token);
                if (!_gridClosing) { _gridOutputStatus = $"Exported {result.RowCount:N0} matching row(s) to {result.Destination}."; StatusText.Text = _gridOutputStatus; }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_gridClosing) { _gridOutputStatus = "Output canceled; no partial destination was published."; StatusText.Text = _gridOutputStatus; } }
        catch (Exception error)
        {
            if (!_gridClosing)
            {
                _gridOutputStatus = "Output failed: " + error.Message;
                StatusText.Text = _gridOutputStatus; StatusText.ToolTip = StatusText.Text;
                if (_gridSource?.IsInvalidated == true) ShowGridReadError(error);
            }
        }
        finally
        {
            if (ReferenceEquals(_gridOutputCancellation, cancellation)) _gridOutputCancellation = null;
            await cancellation.RetireAsync();
            if (!_gridClosing) { CopyGridButton.IsEnabled = ExportGridButton.IsEnabled = true; CancelGridOperationButton.Visibility = Visibility.Collapsed; }
        }
    }
    private void GridCancelOperation_Click(object sender, RoutedEventArgs e) => CancelGridWork(_gridOutputCancellation);

    private void CancelGridWork(GridCancellation? cancellation)
    {
        if (cancellation != null) TrackGridWork(cancellation.Cancel());
    }

    private void TrackGridWork(Task task)
    {
        lock (_gridTasks) if (!_gridTasks.Add(task)) return;
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_gridTasks)
            {
                _gridTasks.Remove(completed);
                if (completed.Exception != null && _gridBackgroundErrors.Count < 16) _gridBackgroundErrors.Add(completed.Exception.GetBaseException());
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task DisposeGridAsync()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(DisposeGridAsync).Task.Unwrap();
        return _gridCloseTask ??= CloseGridAsync();
    }
    private async Task CloseGridAsync()
    {
        _gridClosing = true;
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Tick -= FilterDebounceTimer_Tick;
        PreviewMouseWheel -= FloatingSpreadsheetWindow_PreviewMouseWheel;
        IsVisibleChanged -= FloatingSpreadsheetWindow_IsVisibleChanged;
        Loaded -= FloatingSpreadsheetWindow_Loaded;
        Unloaded -= FloatingSpreadsheetWindow_Unloaded;
        _gridCountTimer?.Stop(); _gridViewportTimer?.Stop();
        GridCancellation?[] cancellations = [_gridQueryCancellation, _gridPageCancellation, _gridOutputCancellation, _gridLifetime];
        foreach (var cancellation in cancellations) CancelGridWork(cancellation);
        SpreadsheetGrid.CancelEdit(DataGridEditingUnit.Cell); SpreadsheetGrid.CancelEdit(DataGridEditingUnit.Row);
        SpreadsheetGrid.LoadingRow -= GridLoadingRow;
        SpreadsheetGrid.UnloadingRow -= GridUnloadingRow;
        SpreadsheetGrid.Sorting -= GridSorting;
        SpreadsheetGrid.BeginningEdit -= GridBeginningEdit;
        SpreadsheetGrid.CellEditEnding -= GridCellEditEnding;
        SpreadsheetGrid.RowEditEnding -= GridRowEditEnding;
        SpreadsheetGrid.SizeChanged -= GridSizeChanged;
        SpreadsheetGrid.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(GridScrollChanged));
        if (_gridItems != null) _gridItems.CellCommitted -= GridCellCommitted;
        SpreadsheetGrid.ItemsSource = null;
        _gridView?.DetachFromSourceCollection(); _gridItems?.Close();
        Task[] pending;
        lock (_gridTasks) pending = _gridTasks.ToArray();
        List<Exception> failures = [];
        try { await Task.WhenAll(pending); } catch (Exception error) { failures.Add(error); }
        lock (_gridTasks) failures.AddRange(_gridBackgroundErrors);
        if (_gridQuery != null) try { await _gridQuery.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        if (_gridSource != null) try { await _gridSource.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        foreach (var cancellation in cancellations)
            if (cancellation != null) try { await cancellation.RetireAsync(); } catch (Exception error) { failures.Add(error); }
        _gridQueryCancellation = null; _gridPageCancellation = null; _gridOutputCancellation = null; _gridLifetime = null;
        _gridSource = null; _gridQuery = null; _gridItems = null; _gridView = null;
        _gridCountTimer = null; _gridViewportTimer = null; _gridScroll = null;
        SpreadsheetGrid.Columns.Clear(); _headers.Clear(); _filters.Clear(); _rows.Clear();
        GridReadFailed = null; GridReopenRequested = null;
        if (failures.Count != 0) throw new AggregateException("Grid logical-close cleanup failed.", failures);
    }
}

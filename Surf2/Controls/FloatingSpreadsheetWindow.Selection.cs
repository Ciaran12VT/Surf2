using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Surf2.Controls.RelationalGrid;
using Surf2.Services.RelationalGrid;

namespace Surf2.Controls;

public static class GridSelectionVisual
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(GridSelectionVisual), new PropertyMetadata(false));
    public static bool GetIsSelected(DependencyObject value) => (bool)value.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject value, bool selected) => value.SetValue(IsSelectedProperty, selected);
}

public partial class FloatingSpreadsheetWindow
{
    private readonly GridCellSelection _gridSelection = new();
    private bool _gridSelectionDragging;
    private DispatcherTimer? _gridSelectionDragTimer;
    private CommandBinding? _gridCopySelectionBinding;

    private void InitializeGridSelection()
    {
        var style = new Style(typeof(DataGridCell), SpreadsheetGrid.CellStyle);
        var selected = new Trigger { Property = GridSelectionVisual.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
        style.Triggers.Add(selected); SpreadsheetGrid.CellStyle = style;
        SpreadsheetGrid.PreviewMouseLeftButtonDown += GridSelectionMouseDown;
        SpreadsheetGrid.PreviewMouseLeftButtonUp += GridSelectionMouseUp;
        SpreadsheetGrid.PreviewMouseMove += GridSelectionMouseMove;
        SpreadsheetGrid.LostMouseCapture += GridSelectionLostCapture;
        SpreadsheetGrid.PreviewKeyDown += GridSelectionKeyDown;
        SpreadsheetGrid.LayoutUpdated += GridSelectionLayoutUpdated;
        SpreadsheetGrid.ColumnReordered += GridSelectionColumnReordered;
        _gridCopySelectionBinding = new(ApplicationCommands.Copy, GridCopySelection, GridCanCopySelection);
        SpreadsheetGrid.CommandBindings.Add(_gridCopySelectionBinding);
        _gridSelectionDragTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Input,
            (_, _) => UpdateGridDragSelection(), Dispatcher);
        _gridSelectionDragTimer.Stop();
    }

    private void DisposeGridSelection()
    {
        StopGridSelectionDrag();
        _gridSelectionDragTimer = null;
        SpreadsheetGrid.PreviewMouseLeftButtonDown -= GridSelectionMouseDown;
        SpreadsheetGrid.PreviewMouseLeftButtonUp -= GridSelectionMouseUp;
        SpreadsheetGrid.PreviewMouseMove -= GridSelectionMouseMove;
        SpreadsheetGrid.LostMouseCapture -= GridSelectionLostCapture;
        SpreadsheetGrid.PreviewKeyDown -= GridSelectionKeyDown;
        SpreadsheetGrid.LayoutUpdated -= GridSelectionLayoutUpdated;
        SpreadsheetGrid.ColumnReordered -= GridSelectionColumnReordered;
        if (_gridCopySelectionBinding != null) SpreadsheetGrid.CommandBindings.Remove(_gridCopySelectionBinding);
        _gridCopySelectionBinding = null; _gridSelection.Clear();
    }

    private void GridSelectionLayoutUpdated(object? sender, EventArgs e) => PaintGridSelection();
    private void GridSelectionColumnReordered(object? sender, DataGridColumnEventArgs e)
    { _gridSelection.Clear(); PaintGridSelection(); }
    private void PaintGridSelection()
    {
        if (_gridClosing) return;
        var range = _gridSelection.Range;
        // Only realized cells receive visual state. No row is fetched for selection.
        foreach (var cell in FindVisualChildren<DataGridCell>(SpreadsheetGrid))
        {
            bool selected = cell.DataContext is GridViewportRow row && cell.Column is { } column && range?.Contains(row.Position, column.DisplayIndex) == true;
            if (GridSelectionVisual.GetIsSelected(cell) != selected) GridSelectionVisual.SetIsSelected(cell, selected);
        }
    }

    private static DataGridCell? GridCellAncestor(DependencyObject? target)
    {
        while (target != null)
        {
            if (target is DataGridCell cell) return cell;
            target = target is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(target)
                : LogicalTreeHelper.GetParent(target);
        }
        return null;
    }

    private void GridSelectionMouseDown(object sender, MouseButtonEventArgs e)
    {
        var cell = GridCellAncestor(e.OriginalSource as DependencyObject);
        if (_gridClosing || cell == null || cell.IsEditing || cell.DataContext is not GridViewportRow { Position: >= 0 } row) return;
        if (!CommitGridEditor()) { e.Handled = true; return; }
        _gridSelection.Select(row.Position, cell.Column.DisplayIndex, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        PaintGridSelection();
        // Let the native grid start its editor on a double click; selection itself stays logical.
        if (e.ClickCount > 1) return;
        SetGridCurrentCell(row.Position, cell.Column, scroll: false);
        cell.Focus();
        _gridSelectionDragging = true;
        Mouse.Capture(SpreadsheetGrid, CaptureMode.SubTree);
        _gridSelectionDragTimer?.Start();
        e.Handled = true;
    }

    private void GridSelectionMouseMove(object sender, MouseEventArgs e)
    {
        if (!_gridSelectionDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) StopGridSelectionDrag();
        else { UpdateGridDragSelection(); e.Handled = true; }
    }
    private void GridSelectionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gridSelectionDragging) return;
        StopGridSelectionDrag(); e.Handled = true;
    }
    private void GridSelectionLostCapture(object sender, MouseEventArgs e) => StopGridSelectionDrag();
    private void StopGridSelectionDrag()
    {
        _gridSelectionDragging = false; _gridSelectionDragTimer?.Stop();
        if (ReferenceEquals(Mouse.Captured, SpreadsheetGrid)) Mouse.Capture(null);
    }
    private void UpdateGridDragSelection()
    {
        if (!_gridSelectionDragging || _gridClosing) return;
        Point point = Mouse.GetPosition(SpreadsheetGrid);
        if (point.Y < SpreadsheetGrid.ColumnHeaderHeight + 4) _gridScroll?.LineUp();
        else if (point.Y > SpreadsheetGrid.ActualHeight - SystemParameters.HorizontalScrollBarHeight - 4) _gridScroll?.LineDown();
        if (point.X < 4) _gridScroll?.LineLeft(); else if (point.X > SpreadsheetGrid.ActualWidth - 20) _gridScroll?.LineRight();
        point.X = Math.Clamp(point.X, 1, Math.Max(1, SpreadsheetGrid.ActualWidth - 20));
        point.Y = Math.Clamp(point.Y, SpreadsheetGrid.ColumnHeaderHeight + 2,
            Math.Max(SpreadsheetGrid.ColumnHeaderHeight + 2, SpreadsheetGrid.ActualHeight - SystemParameters.HorizontalScrollBarHeight - 3));
        var cell = GridCellAncestor(SpreadsheetGrid.InputHitTest(point) as DependencyObject);
        if (cell?.DataContext is not GridViewportRow { Position: >= 0 } row) return;
        if (row.Position == _gridSelection.FocusRow && cell.Column.DisplayIndex == _gridSelection.FocusColumn) return;
        _gridSelection.Select(row.Position, cell.Column.DisplayIndex, extend: true);
        PaintGridSelection();
    }

    private void SelectAllGridCells()
    {
        if (_gridClosing || !CommitGridEditor()) return;
        var columns = SpreadsheetGrid.Columns.Where(column => column.Visibility == Visibility.Visible).OrderBy(column => column.DisplayIndex).ToArray();
        if (columns.Length == 0) return;
        _gridSelection.SelectAll(columns[0].DisplayIndex, columns[^1].DisplayIndex);
        PaintGridSelection();
        StatusText.Text = "All matching cells selected. Ctrl+C copies the selection.";
    }

    private void GridSelectionKeyDown(object sender, KeyEventArgs e)
    {
        // Text editing and filter text boxes retain their native text selection/copy.
        if (Keyboard.FocusedElement is TextBox || _gridClosing) return;
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool extend = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (control && e.Key == Key.A) { SelectAllGridCells(); e.Handled = true; return; }
        if (control && e.Key == Key.C) { StartGridSelectionCopy(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { _gridSelection.Clear(); PaintGridSelection(); return; }
        if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Tab)) return;
        if (_gridItems == null || _gridItems.Count == 0 || !CommitGridEditor()) return;
        var columns = SpreadsheetGrid.Columns.Where(column => column.Visibility == Visibility.Visible).OrderBy(column => column.DisplayIndex).ToArray();
        if (columns.Length == 0) return;
        var current = SpreadsheetGrid.CurrentCell;
        long row = _gridSelection.Range != null ? _gridSelection.FocusRow : current.Item is GridViewportRow item ? item.Position : 0;
        int display = _gridSelection.Range != null ? _gridSelection.FocusColumn : current.Column?.DisplayIndex ?? columns[0].DisplayIndex;
        int column = Array.FindIndex(columns, candidate => candidate.DisplayIndex == display);
        if (column < 0) column = 0;
        if (_gridSelection.Range == null) _gridSelection.Select(Math.Max(0, row), columns[column].DisplayIndex);
        int page = Math.Max(1, (int)(SpreadsheetGrid.ActualHeight / SpreadsheetGrid.RowHeight) - 2);
        switch (e.Key)
        {
            case Key.Up: row = control ? 0 : row - 1; break;
            case Key.Down: row = control ? _gridItems.Count - 1 : row + 1; break;
            case Key.Left: column = control ? 0 : column - 1; break;
            case Key.Right: column = control ? columns.Length - 1 : column + 1; break;
            case Key.Home: column = 0; if (control) row = 0; break;
            case Key.End: column = columns.Length - 1; if (control) row = _gridItems.Count - 1; break;
            case Key.PageUp: row -= page; break;
            case Key.PageDown: row += page; break;
            case Key.Tab:
                column += extend ? -1 : 1; extend = false;
                if (column < 0) { column = columns.Length - 1; row--; }
                else if (column >= columns.Length) { column = 0; row++; }
                break;
        }
        row = Math.Clamp(row, 0, _gridItems.Count - 1);
        column = Math.Clamp(column, 0, columns.Length - 1);
        _gridSelection.Select(row, columns[column].DisplayIndex, extend);
        SetGridCurrentCell((int)row, columns[column], scroll: true);
        PaintGridSelection(); e.Handled = true;
    }

    private void SetGridCurrentCell(int row, DataGridColumn column, bool scroll)
    {
        if (_gridItems == null || row < 0 || row >= _gridItems.Count) return;
        var cell = new DataGridCellInfo(_gridItems[row]!, column);
        SpreadsheetGrid.CurrentCell = cell;
        SpreadsheetGrid.SelectedCells.Clear(); SpreadsheetGrid.SelectedCells.Add(cell);
        if (scroll) SpreadsheetGrid.ScrollIntoView(cell.Item, column);
        QueueGridViewport();
    }

    private void GridCanCopySelection(object sender, CanExecuteRoutedEventArgs e)
    { e.CanExecute = !_gridClosing && _gridSelection.Range != null && _gridOutputCancellation == null; e.Handled = true; }
    private void GridCopySelection(object sender, ExecutedRoutedEventArgs e)
    { e.Handled = true; StartGridSelectionCopy(); }
    private void StartGridSelectionCopy()
    {
        if (_filterDebounceTimer.IsEnabled)
        {
            _filterDebounceTimer.Stop(); RequestGridQuery();
            StatusText.Text = "Filters changed. Select cells in the refreshed view before copying.";
            return;
        }
        if (_gridSelection.Range == null && SpreadsheetGrid.CurrentCell.Item is GridViewportRow { Position: >= 0 } current &&
            SpreadsheetGrid.CurrentCell.Column is { } currentColumn)
            _gridSelection.Select(current.Position, currentColumn.DisplayIndex);
        if (_gridSource == null || _gridClosing || _gridOutputCancellation != null || _gridSelection.Range is not { } range || !CommitGridEditor()) return;
        _filterDebounceTimer.Stop();
        int[] columns = SpreadsheetGrid.Columns.Where(column => column.Visibility == Visibility.Visible &&
            column.DisplayIndex >= range.FirstColumn && column.DisplayIndex <= range.LastColumn)
            .OrderBy(column => column.DisplayIndex).Select(GetColumnIndex).ToArray();
        if (columns.Length == 0) return;
        var cancellation = new GridCancellation(_gridLifetime!.Token);
        _gridOutputCancellation = cancellation;
        CopyGridButton.IsEnabled = ExportGridButton.IsEnabled = false;
        CancelGridOperationButton.Visibility = Visibility.Visible;
        TrackGridWork(OutputGridAsync(CaptureGridQuery(), columns, null, cancellation, range));
    }
}

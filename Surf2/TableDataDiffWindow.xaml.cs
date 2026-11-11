using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Surf2.Models;
using Surf2.Services.RelationalComparison;

namespace Surf2;

public partial class TableDataDiffWindow : Window
{
    private readonly PairedSelection _selection = new();
    private bool _updatingPage;
    private bool _synchronizingScroll;

    private TableDataDiffWindow(ComparisonResource left, ComparisonResource right)
    {
        InitializeComponent();
        Title = $"Compare {left.DisplayName} and {right.DisplayName}";
        HeaderText.Text = $"Table data: {left.DisplayName}  <->  {right.DisplayName}";
    }

    internal TableDataDiffWindow(ComparisonResource left, ComparisonResource right, ComparisonTableLayout layout)
        : this(left, right)
    {
        LeftGridHeading.Text = left.DisplayName;
        RightGridHeading.Text = right.DisplayName;
        AutomationProperties.SetName(RowsDataGrid, "Left table data: " + left.DisplayName);
        AutomationProperties.SetName(RightRowsDataGrid, "Right table data: " + right.DisplayName);
        AddColumns(RowsDataGrid, layout, isLeft: true);
        AddColumns(RightRowsDataGrid, layout, isLeft: false);
    }

    public TableDataDiffWindow(TableDataDiffResult result) : this(result.Left, result.Right)
    {
        PairedRowsPanel.Visibility = Visibility.Collapsed;
        LegacyRowsDataGrid.Visibility = Visibility.Visible;
        LegacyRowsDataGrid.Columns[0].Header = KeyHeading(result.KeyColumns);
        LegacyRowsDataGrid.ItemsSource = result.Rows;

        int added = result.Rows.Count(row => row.Status == TableDataDiffStatus.Added);
        int removed = result.Rows.Count(row => row.Status == TableDataDiffStatus.Removed);
        int altered = result.Rows.Count(row => row.Status == TableDataDiffStatus.Altered);
        int identical = result.Rows.Count(row => row.Status == TableDataDiffStatus.Identical);
        StatusTextBlock.Text = $"Key: {string.Join(", ", result.KeyColumns)}. {identical} identical, {altered} altered, {added} added, {removed} removed.";
    }

    internal void SetComparisonPage(IReadOnlyList<ComparisonResultRow> rows)
    {
        _updatingPage = true;
        try
        {
            _selection.Set(null, null);
            RowsDataGrid.ItemsSource = rows;
            RightRowsDataGrid.ItemsSource = rows;
            GetScrollViewer(RowsDataGrid)?.ScrollToTop();
            GetScrollViewer(RightRowsDataGrid)?.ScrollToTop();
        }
        finally
        {
            _updatingPage = false;
        }
    }

    private void AddColumns(DataGrid grid, ComparisonTableLayout layout, bool isLeft)
    {
        AddColumn(grid, KeyHeading(layout.KeyColumns), nameof(ComparisonResultRow.Key), 160);
        AddColumn(grid, "Status", nameof(ComparisonResultRow.Status), 90, showRowStatus: true);
        var keys = layout.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < layout.Columns.Count; index++)
        {
            var column = layout.Columns[index];
            if (keys.Contains(column.Name)) continue;

            var columnColor = !column.ExistsOnLeft ? ComparisonColors.Added :
                !column.ExistsOnRight ? ComparisonColors.Removed : null;
            var absent = isLeft ? !column.ExistsOnLeft : !column.ExistsOnRight;
            AddColumn(grid, column.Name, $"{nameof(ComparisonResultRow.TableCells)}[{index}].{(isLeft ? nameof(ComparisonTableCell.Left) : nameof(ComparisonTableCell.Right))}",
                150, index, columnColor, absent);
        }
    }

    private void AddColumn(DataGrid grid, string header, string valuePath, double width, int? dataIndex = null,
        Brush? columnColor = null, bool absent = false, bool showRowStatus = false)
    {
        var text = new Style(typeof(TextBlock));
        text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
        text.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        text.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self }));

        var heading = new Style(typeof(DataGridColumnHeader));
        heading.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, absent ? header + " (not present in this version)" : header));
        if (columnColor != null)
        {
            heading.Setters.Add(new Setter(Control.BackgroundProperty, columnColor));
            heading.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        }

        var background = new Binding { Converter = new CellBackground(dataIndex, columnColor, showRowStatus) };
        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.BackgroundProperty, background));
        cell.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        cell.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));

        // The selection outline is independent of the difference fill, so the paired cell keeps its status color.
        var nativeSelection = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        nativeSelection.Setters.Add(new Setter(Control.BackgroundProperty, background));
        nativeSelection.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        cell.Triggers.Add(nativeSelection);
        var selected = new MultiBinding { Converter = new CellHighlight(grid.Columns.Count) };
        selected.Bindings.Add(new Binding());
        selected.Bindings.Add(new Binding(nameof(PairedSelection.Row)) { Source = _selection });
        selected.Bindings.Add(new Binding(nameof(PairedSelection.Column)) { Source = _selection });
        var highlight = new DataTrigger { Binding = selected, Value = true };
        highlight.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.RoyalBlue));
        cell.Triggers.Add(highlight);

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            Binding = new Binding(valuePath) { FallbackValue = "", TargetNullValue = "" },
            HeaderStyle = heading,
            ElementStyle = text,
            CellStyle = cell
        });
    }

    private static string KeyHeading(IReadOnlyList<string> columns) => columns.Count == 0
        ? "Key"
        : $"Key ({string.Join(", ", columns)})";

    private void RowsGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        if (_updatingPage || sender is not DataGrid grid || e.AddedCells.Count == 0) return;
        var selected = e.AddedCells[0];
        if (selected.Item is not ComparisonResultRow row) return;
        int column = grid.Columns.IndexOf(selected.Column);
        if (column < 0) return;

        _selection.Set(row, column == 0 ? null : column);
        OtherGrid(grid).SelectedCells.Clear();
    }

    private void RowsGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_synchronizingScroll || sender is not DataGrid grid ||
            e.OriginalSource is not ScrollViewer source || !ReferenceEquals(source, GetScrollViewer(grid))) return;
        var other = GetScrollViewer(OtherGrid(grid));
        if (other == null) return;

        _synchronizingScroll = true;
        try
        {
            if (e.HorizontalChange != 0 && Math.Abs(other.HorizontalOffset - source.HorizontalOffset) > 0.01)
                other.ScrollToHorizontalOffset(source.HorizontalOffset);
            if (e.VerticalChange != 0 && Math.Abs(other.VerticalOffset - source.VerticalOffset) > 0.01)
                other.ScrollToVerticalOffset(source.VerticalOffset);
        }
        finally
        {
            _synchronizingScroll = false;
        }
    }

    private DataGrid OtherGrid(DataGrid grid) => ReferenceEquals(grid, RowsDataGrid) ? RightRowsDataGrid : RowsDataGrid;

    private static ScrollViewer? GetScrollViewer(DataGrid grid) =>
        grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer;

    private sealed class PairedSelection : INotifyPropertyChanged
    {
        public ComparisonResultRow? Row { get; private set; }
        public int? Column { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Set(ComparisonResultRow? row, int? column)
        {
            if (ReferenceEquals(row, Row) && column == Column) return;
            Row = row;
            Column = column;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private sealed class CellHighlight(int columnIndex) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values[0] is ComparisonResultRow row && ReferenceEquals(row, values[1]) &&
            (values[2] is not int selectedColumn || selectedColumn == columnIndex);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class CellBackground(int? dataIndex, Brush? columnColor, bool showRowStatus) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ComparisonResultRow row) return ComparisonColors.Unchanged;
            if (row.Status == nameof(TableDataDiffStatus.Removed)) return ComparisonColors.Removed;
            if (row.Status == nameof(TableDataDiffStatus.Added)) return ComparisonColors.Added;
            if (columnColor != null) return columnColor;
            if (showRowStatus && row.Status == nameof(TableDataDiffStatus.Altered)) return ComparisonColors.Altered;
            if (dataIndex is not int index || row.TableCells == null || index >= row.TableCells.Count)
                return ComparisonColors.Unchanged;

            return row.TableCells[index].Status switch
            {
                ComparisonCellStatus.Removed => ComparisonColors.Removed,
                ComparisonCellStatus.Altered => ComparisonColors.Altered,
                ComparisonCellStatus.Added => ComparisonColors.Added,
                _ => ComparisonColors.Unchanged
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

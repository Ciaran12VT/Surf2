using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Surf2.Models;

namespace Surf2.Services.RelationalComparison;

internal static class PairedTableDataUiChecks
{
    public static void Run(Action<bool, string> check)
    {
        var left = new ComparisonResource("Before", "Table data", "before", ComparisonResourceKind.TableData,
            "TableData", false, false, true, "before");
        var right = left with { DisplayName = "After", Path = "after", IdentityKey = "after" };
        var layout = new ComparisonTableLayout(
            [new("Id", true, true), new("Changed", true, true), new("Old column", true, false),
                new("New column", false, true), new("Same", true, true), new("Last", true, true)], ["Id"]);
        var rows = Enumerable.Range(0, 80).Select(Row).ToArray();
        var window = new TableDataDiffWindow(left, right, layout);
        var leftGrid = window.RowsDataGrid;
        var rightGrid = window.RightRowsDataGrid;
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var surface = new Grid { Width = 1080, Height = 350, Resources = window.Resources };
        surface.Children.Add(content);
        try
        {
            window.SetComparisonPage(rows);
            Arrange();
            check(ReferenceEquals(leftGrid.ItemsSource, rows) && ReferenceEquals(rightGrid.ItemsSource, rows) &&
                leftGrid.Columns.Select(column => column.Header).SequenceEqual(rightGrid.Columns.Select(column => column.Header)) &&
                leftGrid.Columns.Select(column => column.Header as string).SequenceEqual(
                    new[] { "Key (Id)", "Status", "Changed", "Old column", "New column", "Same", "Last" }),
                "Paired table grids share page rows and aligned columns with a labeled key");

            Show(rows[0], 2);
            var before = Cell(leftGrid, rows[0], 2);
            var after = Cell(rightGrid, rows[0], 2);
            check(before.Content is TextBlock { Text: "old" } && after.Content is TextBlock { Text: "new" } &&
                ColorOf(before.Background) == ColorOf(ComparisonColors.Altered) && ColorOf(after.Background) == ColorOf(ComparisonColors.Altered),
                "Altered cells display actual before and after values in matching orange fills");
            leftGrid.SelectedCells.Add(new DataGridCellInfo(rows[0], leftGrid.Columns[2]));
            Arrange();
            check(IsHighlighted(before) && IsHighlighted(after) && !IsHighlighted(Cell(leftGrid, rows[0], 0)) &&
                ColorOf(before.Background) == ColorOf(ComparisonColors.Altered) && ColorOf(after.Background) == ColorOf(ComparisonColors.Altered),
                "Selecting a data cell highlights only its paired cells and retains their difference fills");
            rightGrid.SelectedCells.Add(new DataGridCellInfo(rows[0], rightGrid.Columns[0]));
            Arrange();
            check(IsHighlighted(Cell(leftGrid, rows[0], 0)) && IsHighlighted(Cell(rightGrid, rows[0], 0)) &&
                IsHighlighted(before) && IsHighlighted(after) && !IsHighlighted(Cell(leftGrid, rows[1], 0)),
                "Selecting a key highlights the full corresponding row in both grids");

            Show(rows[0], 3);
            check(Cell(leftGrid, rows[0], 3).Content is TextBlock { Text: "previous only" } &&
                Cell(rightGrid, rows[0], 3).Content is TextBlock { Text: "" } &&
                PairColor(rows[0], 3, ComparisonColors.Removed),
                "Removed columns show their old values and aligned red placeholders");
            Show(rows[0], 4);
            check(Cell(leftGrid, rows[0], 4).Content is TextBlock { Text: "" } &&
                Cell(rightGrid, rows[0], 4).Content is TextBlock { Text: "current only" } &&
                PairColor(rows[0], 4, ComparisonColors.Added),
                "Added columns show their new values and aligned green placeholders");
            Show(rows[0], 5);
            check(PairColor(rows[0], 5, ComparisonColors.Unchanged) && PairColor(rows[1], 5, ComparisonColors.Removed) &&
                PairColor(rows[2], 5, ComparisonColors.Added) && PairColor(rows[1], 0, ComparisonColors.Removed) &&
                PairColor(rows[2], 0, ComparisonColors.Added),
                "Unchanged cells are white while added and removed rows retain their fills across the grid");

            var leftScroll = ScrollViewer(leftGrid);
            var rightScroll = ScrollViewer(rightGrid);
            leftScroll.ScrollToHorizontalOffset(85);
            leftScroll.ScrollToVerticalOffset(200);
            Arrange();
            check(OffsetsMatch(leftScroll, rightScroll, 85, 200), "Both scroll axes follow the left table grid");
            rightScroll.ScrollToHorizontalOffset(150);
            rightScroll.ScrollToVerticalOffset(450);
            Arrange();
            check(OffsetsMatch(leftScroll, rightScroll, 150, 450), "Both scroll axes also follow the right table grid");
        }
        finally
        {
            surface.Children.Remove(content);
            window.Content = content;
            window.Close();
        }

        void Arrange()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                surface.Measure(new Size(surface.Width, surface.Height));
                surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
                surface.UpdateLayout();
                surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        void Show(ComparisonResultRow row, int column)
        {
            leftGrid.ScrollIntoView(row, leftGrid.Columns[column]);
            rightGrid.ScrollIntoView(row, rightGrid.Columns[column]);
            Arrange();
        }

        bool PairColor(ComparisonResultRow row, int column, Brush color) =>
            ColorOf(Cell(leftGrid, row, column).Background) == ColorOf(color) &&
            ColorOf(Cell(rightGrid, row, column).Background) == ColorOf(color);
    }

    private static ComparisonResultRow Row(int index)
    {
        string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(key, index switch { 0 => "Altered", 1 => "Removed", 2 => "Added", _ => "Identical" }, "", "", "",
            TableCells: [new(key, key, ComparisonCellStatus.Identical), new("old", "new", ComparisonCellStatus.Altered),
                new("previous only", "", ComparisonCellStatus.Removed), new("", "current only", ComparisonCellStatus.Added),
                new("unchanged", "unchanged", ComparisonCellStatus.Identical), new("last", "last", ComparisonCellStatus.Identical)]);
    }

    private static DataGridCell Cell(DataGrid grid, ComparisonResultRow row, int column) =>
        Descendants(grid).OfType<DataGridCell>().Single(cell =>
            ReferenceEquals(cell.DataContext, row) && ReferenceEquals(cell.Column, grid.Columns[column]));

    private static ScrollViewer ScrollViewer(DataGrid grid) =>
        Descendants(grid).OfType<ScrollViewer>().First();

    private static bool IsHighlighted(DataGridCell cell) => ColorOf(cell.BorderBrush) == Colors.RoyalBlue;

    private static Color ColorOf(Brush? brush) => (brush as SolidColorBrush)?.Color ?? Colors.Transparent;

    private static bool OffsetsMatch(ScrollViewer left, ScrollViewer right, double x, double y) =>
        Math.Abs(left.HorizontalOffset - x) < 0.1 && Math.Abs(right.HorizontalOffset - x) < 0.1 &&
        Math.Abs(left.VerticalOffset - y) < 0.1 && Math.Abs(right.VerticalOffset - y) < 0.1;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

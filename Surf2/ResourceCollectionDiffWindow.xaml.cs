using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using Surf2.Models;

namespace Surf2;

public partial class ResourceCollectionDiffWindow : Window
{
    private readonly IReadOnlyList<ResourceCollectionDiffRow> _allRows;
    private readonly ObservableCollection<ResourceCollectionDiffRow> _visibleRows = [];
    private readonly Action<ResourceCollectionDiffRow> _openRow;
    private bool _ignoreWhitespace;

    public ResourceCollectionDiffWindow(ResourceCollectionDiffResult result, Action<ResourceCollectionDiffRow> openRow)
    {
        InitializeComponent();
        _openRow = openRow;
        _allRows = result.Rows.ToList();
        Title = $"Compare {result.Left.DisplayName} and {result.Right.DisplayName}";
        LeftHeaderText.Text = $"{result.Left.TypeDisplay}: {result.Left.DisplayName}";
        RightHeaderText.Text = $"{result.Right.TypeDisplay}: {result.Right.DisplayName}";
        RowsListView.ItemsSource = _visibleRows;
        RefreshVisibleRows();
        StatusTextBlock.Text = $"{_allRows.Count} item(s). Double-click a changed file, database object, table, or table data row to inspect details.";
    }

    private void RowsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowsListView.SelectedItem is ResourceCollectionDiffRow row && row.CanOpenDiff)
        {
            _openRow(row);
        }
    }

    private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void ExcludeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RowsListView.SelectedItem is not ResourceCollectionDiffRow row)
        {
            return;
        }

        row.IsExcluded = true;
        if (!row.IsFolder)
        {
            return;
        }

        string prefix = row.RelativePath + "/";
        foreach (ResourceCollectionDiffRow candidate in _allRows)
        {
            if (candidate.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate.IsExcluded = true;
            }
        }
    }

    private void IgnoreWhitespaceCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _ignoreWhitespace = IgnoreWhitespaceCheckBox.IsChecked == true;
        RecalculateStatuses();
    }

    private void ToggleRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResourceCollectionDiffRow row || !row.IsFolder)
        {
            return;
        }

        row.IsExpanded = !row.IsExpanded;
        RefreshVisibleRows();
    }

    private void RefreshVisibleRows()
    {
        _visibleRows.Clear();
        foreach (ResourceCollectionDiffRow row in _allRows)
        {
            if (IsRowVisible(row))
            {
                _visibleRows.Add(row);
            }
        }
    }

    private bool IsRowVisible(ResourceCollectionDiffRow row)
    {
        foreach (ResourceCollectionDiffRow candidate in _allRows)
        {
            if (ReferenceEquals(candidate, row))
            {
                return true;
            }

            if (!candidate.IsFolder ||
                candidate.IsExpanded ||
                row.Depth <= candidate.Depth)
            {
                continue;
            }

            string prefix = candidate.RelativePath + "/";
            if (row.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void RecalculateStatuses()
    {
        foreach (ResourceCollectionDiffRow row in _allRows)
        {
            if (row.IsFolder)
            {
                row.Status = GetDirectStatus(row);
                continue;
            }

            row.Status = GetLeafStatus(row);
        }

        foreach (ResourceCollectionDiffRow row in _allRows.Where(row => row.IsFolder).OrderByDescending(row => row.Depth))
        {
            ResourceComparisonStatus directStatus = GetDirectStatus(row);
            if (directStatus != ResourceComparisonStatus.Identical)
            {
                row.Status = directStatus;
                continue;
            }

            string prefix = row.RelativePath + "/";
            row.Status = _allRows.Any(candidate =>
                    candidate.Depth > row.Depth &&
                    candidate.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Status != ResourceComparisonStatus.Identical)
                ? ResourceComparisonStatus.Different
                : ResourceComparisonStatus.Identical;
        }
    }

    private ResourceComparisonStatus GetLeafStatus(ResourceCollectionDiffRow row)
    {
        if (row.LeftDocument == null)
        {
            return ResourceComparisonStatus.MissingLeft;
        }

        if (row.RightDocument == null)
        {
            return ResourceComparisonStatus.MissingRight;
        }

        string leftContent = _ignoreWhitespace
            ? RemoveWhitespace(row.LeftDocument.Content)
            : row.LeftDocument.Content;
        string rightContent = _ignoreWhitespace
            ? RemoveWhitespace(row.RightDocument.Content)
            : row.RightDocument.Content;

        return string.Equals(leftContent, rightContent, StringComparison.Ordinal)
            ? ResourceComparisonStatus.Identical
            : ResourceComparisonStatus.Different;
    }

    private static ResourceComparisonStatus GetDirectStatus(ResourceCollectionDiffRow row)
    {
        if (row.LeftDocument == null)
        {
            return ResourceComparisonStatus.MissingLeft;
        }

        if (row.RightDocument == null)
        {
            return ResourceComparisonStatus.MissingRight;
        }

        return row.LeftDocument.IsCollection == row.RightDocument.IsCollection
            ? ResourceComparisonStatus.Identical
            : ResourceComparisonStatus.Different;
    }

    private static string RemoveWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }
}

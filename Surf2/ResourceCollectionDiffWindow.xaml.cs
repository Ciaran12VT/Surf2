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
    private readonly ComparisonResource _leftCollection;
    private readonly ComparisonResource _rightCollection;
    private readonly IReadOnlyList<ResourceComparisonDocument> _leftDocuments;
    private readonly IReadOnlyList<ResourceComparisonDocument> _rightDocuments;
    private bool _ignoreWhitespace;
    private bool _ignoreCase;
    private bool _loadingDiffOptions;
    private string _searchText = string.Empty;

    public ResourceCollectionDiffWindow(
        ResourceCollectionDiffResult result,
        Action<ResourceCollectionDiffRow> openRow,
        bool ignoreWhitespaceByDefault,
        bool ignoreCaseByDefault)
    {
        InitializeComponent();
        _openRow = openRow;
        _allRows = result.Rows.ToList();
        _leftCollection = result.Left;
        _rightCollection = result.Right;
        _leftDocuments = _allRows
            .Select(row => row.LeftDocument)
            .OfType<ResourceComparisonDocument>()
            .Where(document => !document.IsCollection)
            .GroupBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _rightDocuments = _allRows
            .Select(row => row.RightDocument)
            .OfType<ResourceComparisonDocument>()
            .Where(document => !document.IsCollection)
            .GroupBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Title = $"Compare {result.Left.DisplayName} and {result.Right.DisplayName}";
        LeftHeaderText.Text = $"{result.Left.TypeDisplay}: {result.Left.DisplayName}";
        RightHeaderText.Text = $"{result.Right.TypeDisplay}: {result.Right.DisplayName}";
        RowsListView.ItemsSource = _visibleRows;
        _ignoreWhitespace = ignoreWhitespaceByDefault;
        _ignoreCase = ignoreCaseByDefault;
        _loadingDiffOptions = true;
        IgnoreWhitespaceCheckBox.IsChecked = _ignoreWhitespace;
        IgnoreCaseCheckBox.IsChecked = _ignoreCase;
        _loadingDiffOptions = false;
        RecalculateStatuses();
        RefreshVisibleRows();
        UpdateStatusText();
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

    private void CompareWithMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_leftDocuments.Count == 0 && _rightDocuments.Count == 0)
        {
            StatusTextBlock.Text = "There are no comparable resources in either collection.";
            return;
        }

        ResourceCollectionDiffRow? row = RowsListView.SelectedItem as ResourceCollectionDiffRow;
        ResourceComparisonDocument? defaultLeft = row?.LeftDocument?.IsCollection == false
            ? row.LeftDocument
            : _leftDocuments.FirstOrDefault();
        ResourceComparisonDocument? defaultRight = row?.RightDocument?.IsCollection == false
            ? row.RightDocument
            : FindCorrespondingRightDocument(defaultLeft) ?? _rightDocuments.FirstOrDefault();

        var dialog = new ResourceCompareWithWindow(
            _leftCollection.DisplayName,
            _rightCollection.DisplayName,
            _leftDocuments,
            _rightDocuments,
            defaultLeft,
            defaultRight)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true ||
            dialog.SelectedDocument1 == null ||
            dialog.SelectedDocument2 == null)
        {
            return;
        }

        OpenSelectedDocumentPair(dialog.SelectedDocument1, dialog.SelectedDocument2);
    }

    private ResourceComparisonDocument? FindCorrespondingRightDocument(ResourceComparisonDocument? leftDocument)
    {
        if (leftDocument == null)
        {
            return null;
        }

        return _rightDocuments.FirstOrDefault(document =>
            string.Equals(document.RelativePath, leftDocument.RelativePath, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenSelectedDocumentPair(
        ResourceComparisonDocument document1,
        ResourceComparisonDocument document2)
    {
        var row = new ResourceCollectionDiffRow(
            0,
            $"{document1.RelativePath} <-> {document2.RelativePath}",
            document1.DisplayName,
            document2.DisplayName,
            ResourceComparisonStatus.Different,
            document1,
            document2);
        _openRow(row);
    }

    private void DiffOptionCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingDiffOptions)
        {
            return;
        }

        _ignoreWhitespace = IgnoreWhitespaceCheckBox.IsChecked == true;
        _ignoreCase = IgnoreCaseCheckBox.IsChecked == true;
        RecalculateStatuses();
        RefreshVisibleRows();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchTextBox.Text.Trim();
        RefreshVisibleRows();
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

        UpdateStatusText();
    }

    private bool IsRowVisible(ResourceCollectionDiffRow row)
    {
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            return IsSearchMatch(row) || HasMatchingDescendant(row);
        }

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

    private bool IsSearchMatch(ResourceCollectionDiffRow row)
    {
        return row.LeftName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
               row.RightName.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasMatchingDescendant(ResourceCollectionDiffRow row)
    {
        if (!row.IsFolder)
        {
            return false;
        }

        string prefix = row.RelativePath + "/";
        return _allRows.Any(candidate =>
            candidate.Depth > row.Depth &&
            candidate.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            IsSearchMatch(candidate));
    }

    private void UpdateStatusText()
    {
        string filterText = string.IsNullOrWhiteSpace(_searchText)
            ? string.Empty
            : $" Showing {_visibleRows.Count} of {_allRows.Count} item(s).";
        StatusTextBlock.Text = $"{_allRows.Count} item(s).{filterText} Double-click a changed file, database object, table, or table data row to inspect details.";
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

        return string.Equals(
                leftContent,
                rightContent,
                _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
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

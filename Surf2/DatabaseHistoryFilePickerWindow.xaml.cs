using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Surf2.Models;

namespace Surf2;

public partial class DatabaseHistoryFilePickerWindow : Window
{
    private readonly IReadOnlyList<DatabaseHistoryFilePickerItem> _allItems;
    private readonly ObservableCollection<DatabaseHistoryFilePickerItem> _filteredItems = [];

    public DatabaseHistoryFilePickerWindow(IEnumerable<DatabaseHistoryFilePickerItem> items)
    {
        InitializeComponent();
        _allItems = items.ToList();
        FilesListView.ItemsSource = _filteredItems;
        ApplyFilter();
        FilterTextBox.Focus();
    }

    public DatabaseHistoryFilePickerItem? SelectedItem { get; private set; }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PickButton.IsEnabled = FilesListView.SelectedItem is DatabaseHistoryFilePickerItem;
    }

    private void FilesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        PickSelected();
    }

    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        PickSelected();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PickSelected()
    {
        if (FilesListView.SelectedItem is not DatabaseHistoryFilePickerItem item)
        {
            return;
        }

        SelectedItem = item;
        DialogResult = true;
        Close();
    }

    private void ApplyFilter()
    {
        string filter = FilterTextBox.Text.Trim();
        IEnumerable<DatabaseHistoryFilePickerItem> matches = _allItems;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                item.DatabaseName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.VersionName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        _filteredItems.Clear();
        foreach (DatabaseHistoryFilePickerItem item in matches.Take(500))
        {
            _filteredItems.Add(item);
        }

        StatusTextBlock.Text = $"{_filteredItems.Count} file(s).";
        PickButton.IsEnabled = FilesListView.SelectedItem is DatabaseHistoryFilePickerItem;
    }
}

public sealed record DatabaseHistoryFilePickerItem(
    string DatabaseName,
    string SnapshotId,
    DatabaseSnapshotVersion Version,
    DatabaseVersionedResourceKind Kind,
    string ResourceKey,
    string RelativePath,
    string Content,
    DatabaseSnapshotResourcePayload? Payload)
{
    public string VersionName => Version.VersionName;
}

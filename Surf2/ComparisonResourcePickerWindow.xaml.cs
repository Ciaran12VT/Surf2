using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Surf2.Models;

namespace Surf2;

public partial class ComparisonResourcePickerWindow : Window
{
    private readonly List<ComparisonPickerItem> _allItems;
    private readonly ObservableCollection<ComparisonPickerItem> _filteredItems = [];

    public ComparisonResourcePickerWindow(IEnumerable<ComparisonResource> resources)
    {
        InitializeComponent();
        _allItems = resources
            .Select(resource => new ComparisonPickerItem(
                resource.DisplayName,
                resource.TypeDisplay,
                resource.Path,
                resource))
            .ToList();
        ResourcesListView.ItemsSource = _filteredItems;
        ApplyFilter();
        FilterTextBox.Focus();
    }

    public ComparisonResource? SelectedResource { get; private set; }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = FilterTextBox.Text.Trim();
        IEnumerable<ComparisonPickerItem> matches = _allItems;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        _filteredItems.Clear();
        foreach (ComparisonPickerItem item in matches.Take(500))
        {
            _filteredItems.Add(item);
        }

        int totalMatches = string.IsNullOrWhiteSpace(filter)
            ? _allItems.Count
            : _allItems.Count(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));

        StatusTextBlock.Text = totalMatches > _filteredItems.Count
            ? $"Showing first {_filteredItems.Count} of {totalMatches} resources."
            : $"{totalMatches} resource(s).";
        UpdatePickButton();
    }

    private void ResourcesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePickButton();
    }

    private void ResourcesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryPickSelectedResource();
    }

    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        TryPickSelectedResource();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdatePickButton()
    {
        PickButton.IsEnabled = ResourcesListView.SelectedItem is ComparisonPickerItem;
    }

    private void TryPickSelectedResource()
    {
        if (ResourcesListView.SelectedItem is not ComparisonPickerItem item)
        {
            return;
        }

        SelectedResource = item.Resource;
        DialogResult = true;
        Close();
    }
}

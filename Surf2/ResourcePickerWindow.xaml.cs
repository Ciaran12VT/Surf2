using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Surf2.Models;

namespace Surf2;

public partial class ResourcePickerWindow : Window
{
    private readonly List<ResourcePickerItem> _allItems;
    private readonly ObservableCollection<ResourcePickerItem> _filteredItems = [];

    public ResourcePickerWindow(IEnumerable<LinkableResource> resources)
    {
        InitializeComponent();
        _allItems = resources
            .Select(resource => new ResourcePickerItem(
                resource.Name,
                resource.Type,
                resource.Path,
                resource.Kind))
            .ToList();
        ResourcesListView.ItemsSource = _filteredItems;
        ApplyFilter();
        FilterTextBox.Focus();
    }

    public string SelectedResourcePath { get; private set; } = string.Empty;

    public string SelectedResourceName { get; private set; } = string.Empty;

    public LinkableResourceKind? SelectedResourceKind { get; private set; }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = FilterTextBox.Text.Trim();
        IEnumerable<ResourcePickerItem> matches = _allItems;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        _filteredItems.Clear();
        foreach (ResourcePickerItem item in matches.Take(500))
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
        PickButton.IsEnabled = ResourcesListView.SelectedItem is ResourcePickerItem;
    }

    private void TryPickSelectedResource()
    {
        if (ResourcesListView.SelectedItem is not ResourcePickerItem item)
        {
            return;
        }

        SelectedResourcePath = item.Path;
        SelectedResourceName = item.Name;
        SelectedResourceKind = item.Kind;
        DialogResult = true;
        Close();
    }

    private sealed record ResourcePickerItem(
        string Name,
        string Type,
        string Path,
        LinkableResourceKind Kind);
}

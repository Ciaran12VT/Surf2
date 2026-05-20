using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

public partial class ResourcePickerWindow : Window
{
    private readonly List<ResourcePickerItem> _allItems;
    private readonly ObservableCollection<ResourcePickerItem> _filteredItems = [];

    public ResourcePickerWindow(IEnumerable<FileSystemNode> rootNodes)
    {
        InitializeComponent();
        _allItems = FlattenNodes(rootNodes).ToList();
        ResourcesListView.ItemsSource = _filteredItems;
        ApplyFilter();
        FilterTextBox.Focus();
    }

    public string SelectedResourcePath { get; private set; } = string.Empty;

    public string SelectedResourceName { get; private set; } = string.Empty;

    private static IEnumerable<ResourcePickerItem> FlattenNodes(IEnumerable<FileSystemNode> nodes)
    {
        foreach (FileSystemNode node in nodes)
        {
            if (IsLoadingPlaceholder(node))
            {
                continue;
            }

            yield return new ResourcePickerItem(
                node.Name,
                GetNodeType(node),
                node.FullPath,
                IsSelectable(node));

            foreach (ResourcePickerItem child in FlattenNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    private static bool IsLoadingPlaceholder(FileSystemNode node)
    {
        return !node.IsDirectory &&
            string.Equals(node.Name, "Loading...", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSelectable(FileSystemNode node)
    {
        return node.Exists && !node.IsDirectory;
    }

    private static string GetNodeType(FileSystemNode node)
    {
        if (DiagramDocumentService.IsDiagramDocumentPath(node.FullPath))
        {
            return "Diagram";
        }

        if (DatabaseDocumentService.IsDatabaseDocumentPath(node.FullPath))
        {
            return "Database Object";
        }

        if (node.IsDirectory)
        {
            return "Folder";
        }

        string extension = Path.GetExtension(node.FullPath);
        return string.IsNullOrWhiteSpace(extension)
            ? "File"
            : extension.TrimStart('.').ToUpperInvariant();
    }

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
        PickButton.IsEnabled = ResourcesListView.SelectedItem is ResourcePickerItem { IsSelectable: true };
    }

    private void TryPickSelectedResource()
    {
        if (ResourcesListView.SelectedItem is not ResourcePickerItem { IsSelectable: true } item)
        {
            return;
        }

        SelectedResourcePath = item.Path;
        SelectedResourceName = item.Name;
        DialogResult = true;
        Close();
    }

    private sealed record ResourcePickerItem(
        string Name,
        string Type,
        string Path,
        bool IsSelectable);
}

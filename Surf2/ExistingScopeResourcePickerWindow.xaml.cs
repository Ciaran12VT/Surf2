using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Surf2.Models;

namespace Surf2;

public partial class ExistingScopeResourcePickerWindow : Window
{
    private readonly List<ExistingScopeResourceCandidate> _allItems;
    private readonly ObservableCollection<ExistingScopeResourceCandidate> _filteredItems = [];

    public ExistingScopeResourcePickerWindow(IEnumerable<ExistingScopeResourceCandidate> resources)
    {
        InitializeComponent();
        _allItems = resources.ToList();
        ResourcesListView.ItemsSource = _filteredItems;
        ApplyFilter();
        FilterTextBox.Focus();
    }

    public IReadOnlyList<ExistingScopeResourceCandidate> SelectedResources { get; private set; } = [];

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ResourcesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAddButton();
    }

    private void ResourcesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryAddSelectedResources();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        TryAddSelectedResources();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyFilter()
    {
        string filter = FilterTextBox.Text.Trim();
        IEnumerable<ExistingScopeResourceCandidate> matches = _allItems;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Details.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        List<ExistingScopeResourceCandidate> filteredItems = matches.Take(500).ToList();
        HashSet<ExistingScopeResourceCandidate> selectedItems = ResourcesListView.SelectedItems
            .OfType<ExistingScopeResourceCandidate>()
            .ToHashSet();

        _filteredItems.Clear();
        foreach (ExistingScopeResourceCandidate item in filteredItems)
        {
            _filteredItems.Add(item);
        }

        foreach (ExistingScopeResourceCandidate item in selectedItems)
        {
            if (_filteredItems.Contains(item))
            {
                ResourcesListView.SelectedItems.Add(item);
            }
        }

        int totalMatches = string.IsNullOrWhiteSpace(filter)
            ? _allItems.Count
            : _allItems.Count(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Details.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));

        StatusTextBlock.Text = totalMatches > _filteredItems.Count
            ? $"Showing first {_filteredItems.Count} of {totalMatches} resources."
            : $"{totalMatches} resource(s).";
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        int selectedCount = ResourcesListView.SelectedItems.Count;
        AddButton.IsEnabled = selectedCount > 0;
        if (selectedCount > 0)
        {
            StatusTextBlock.Text = $"{selectedCount} selected.";
        }
    }

    private void TryAddSelectedResources()
    {
        SelectedResources = ResourcesListView.SelectedItems
            .OfType<ExistingScopeResourceCandidate>()
            .ToList();
        if (SelectedResources.Count == 0)
        {
            return;
        }

        DialogResult = true;
        Close();
    }
}

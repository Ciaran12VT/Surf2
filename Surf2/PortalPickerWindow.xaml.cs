using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Surf2;

public sealed record PortalPickerItem(
    string PortalObjectId,
    string Name,
    double Left,
    double Top)
{
    public string Position => $"{Left:0}, {Top:0}";
}

public partial class PortalPickerWindow : Window
{
    private readonly List<PortalPickerItem> _allItems;
    private readonly ObservableCollection<PortalPickerItem> _filteredItems = [];

    public PortalPickerWindow(IEnumerable<PortalPickerItem> portals, string diagramName)
    {
        InitializeComponent();
        Title = $"Select Portal - {diagramName}";
        _allItems = portals
            .OrderBy(portal => portal.Name)
            .ThenBy(portal => portal.Left)
            .ThenBy(portal => portal.Top)
            .ToList();
        PortalsListView.ItemsSource = _filteredItems;
        ApplyFilter();
        FilterTextBox.Focus();
    }

    public string SelectedPortalObjectId { get; private set; } = string.Empty;

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void PortalsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectButton();
    }

    private void PortalsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TrySelectPortal();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        TrySelectPortal();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyFilter()
    {
        string filter = FilterTextBox.Text.Trim();
        IEnumerable<PortalPickerItem> matches = _allItems;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Position.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        _filteredItems.Clear();
        foreach (PortalPickerItem item in matches)
        {
            _filteredItems.Add(item);
        }

        StatusTextBlock.Text = $"{_filteredItems.Count} unpaired portal(s).";
        UpdateSelectButton();
    }

    private void UpdateSelectButton()
    {
        SelectButton.IsEnabled = PortalsListView.SelectedItem is PortalPickerItem;
    }

    private void TrySelectPortal()
    {
        if (PortalsListView.SelectedItem is not PortalPickerItem item)
        {
            return;
        }

        SelectedPortalObjectId = item.PortalObjectId;
        DialogResult = true;
        Close();
    }
}

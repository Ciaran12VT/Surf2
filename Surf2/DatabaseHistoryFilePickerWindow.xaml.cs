using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Surf2.Models;

namespace Surf2;

public partial class DatabaseHistoryFilePickerWindow : Window
{
    private readonly IReadOnlyList<DatabaseHistoryDatabasePickerItem> _databases;
    private readonly string? _defaultSnapshotId;
    private readonly DatabaseVersionedResourceKind? _defaultKind;
    private readonly string? _defaultResourceKey;
    private readonly ObservableCollection<DatabaseHistoryFilePickerItem> _filteredItems = [];
    private bool _isInitializing = true;

    public DatabaseHistoryFilePickerWindow(
        IEnumerable<DatabaseHistoryDatabasePickerItem> databases,
        string? defaultSnapshotId,
        DatabaseVersionedResourceKind? defaultKind,
        string? defaultResourceKey)
    {
        InitializeComponent();
        _databases = databases.ToList();
        _defaultSnapshotId = defaultSnapshotId;
        _defaultKind = defaultKind;
        _defaultResourceKey = defaultResourceKey;

        FilesListView.ItemsSource = _filteredItems;
        DatabaseComboBox.ItemsSource = _databases;
        DatabaseComboBox.SelectedItem = _databases.FirstOrDefault(database =>
            string.Equals(database.SnapshotId, defaultSnapshotId, StringComparison.OrdinalIgnoreCase)) ??
            _databases.FirstOrDefault();

        _isInitializing = false;
        UpdateVersions(selectDefaultFile: true);
        FilterTextBox.Focus();
    }

    public DatabaseHistoryFilePickerItem? SelectedItem { get; private set; }

    private void DatabaseComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        UpdateVersions(selectDefaultFile: true);
    }

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ApplyFilter(selectDefaultFile: true);
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(selectDefaultFile: false);
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

    private void UpdateVersions(bool selectDefaultFile)
    {
        if (DatabaseComboBox.SelectedItem is not DatabaseHistoryDatabasePickerItem database)
        {
            VersionComboBox.ItemsSource = null;
            _filteredItems.Clear();
            StatusTextBlock.Text = "No database selected.";
            PickButton.IsEnabled = false;
            return;
        }

        VersionComboBox.ItemsSource = database.Versions;
        VersionComboBox.SelectedItem = database.Versions.FirstOrDefault();
        ApplyFilter(selectDefaultFile);
    }

    private void ApplyFilter(bool selectDefaultFile)
    {
        DatabaseHistoryVersionPickerItem? version = VersionComboBox.SelectedItem as DatabaseHistoryVersionPickerItem;
        IEnumerable<DatabaseHistoryFilePickerItem> matches = version?.Files ?? [];
        string filter = FilterTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                DatabaseHistoryDisplay.ContainsText(item.ResourceTypeDisplay, filter) ||
                DatabaseHistoryDisplay.ContainsText(item.FileNameDisplay, filter) ||
                DatabaseHistoryDisplay.ContainsText(item.DisplayName, filter) ||
                DatabaseHistoryDisplay.ContainsText(item.RelativePath, filter));
        }

        _filteredItems.Clear();
        foreach (DatabaseHistoryFilePickerItem item in matches.Take(500))
        {
            _filteredItems.Add(item);
        }

        StatusTextBlock.Text = version == null
            ? "No version selected."
            : $"{_filteredItems.Count} file(s).";
        PickButton.IsEnabled = FilesListView.SelectedItem is DatabaseHistoryFilePickerItem;

        if (selectDefaultFile)
        {
            SelectDefaultFile();
        }
    }

    private void SelectDefaultFile()
    {
        if (DatabaseComboBox.SelectedItem is not DatabaseHistoryDatabasePickerItem database ||
            !string.Equals(database.SnapshotId, _defaultSnapshotId, StringComparison.OrdinalIgnoreCase) ||
            _defaultKind == null ||
            string.IsNullOrWhiteSpace(_defaultResourceKey))
        {
            FilesListView.SelectedItem = null;
            return;
        }

        DatabaseHistoryFilePickerItem? item = _filteredItems.FirstOrDefault(candidate =>
            candidate.Kind == _defaultKind &&
            string.Equals(candidate.ResourceKey, _defaultResourceKey, StringComparison.OrdinalIgnoreCase));
        FilesListView.SelectedItem = item;
        if (item != null)
        {
            Dispatcher.BeginInvoke(
                new Action(() => FilesListView.ScrollIntoView(item)),
                DispatcherPriority.ContextIdle);
        }
    }
}

public sealed record DatabaseHistoryDatabasePickerItem(
    string DisplayName,
    string SnapshotId,
    IReadOnlyList<DatabaseHistoryVersionPickerItem> Versions);

public sealed record DatabaseHistoryVersionPickerItem(
    string DisplayName,
    string SnapshotId,
    DatabaseSnapshotVersion Version,
    IReadOnlyList<DatabaseHistoryFilePickerItem> Files);

public sealed record DatabaseHistoryFilePickerItem(
    string DatabaseName,
    string SnapshotId,
    string VersionDisplayName,
    DatabaseSnapshotVersion Version,
    DatabaseVersionedResourceKind Kind,
    string ResourceKey,
    string DisplayName,
    string RelativePath,
    string Content,
    DatabaseSnapshotResourcePayload? Payload)
{
    public string ResourceTypeDisplay => DatabaseHistoryDisplay.GetResourceTypeDisplay(Kind);

    public string FileNameDisplay => DatabaseHistoryDisplay.GetFileNameDisplay(DisplayName, RelativePath);

    public string ComparisonLabel => $"{DatabaseName} {VersionDisplayName}: {FileNameDisplay}";
}

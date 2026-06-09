using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Surf2.Models;
using Surf2.Services;

namespace Surf2;

public partial class DatabaseSnapshotHistoryWindow : Window
{
    private readonly DatabaseSnapshotLibrary _library;
    private readonly DatabaseSnapshotHistory _history;
    private readonly DatabaseSnapshotHistoryService _historyService;
    private readonly List<DatabaseHistoryVersionRow> _versionRows = [];
    private readonly ObservableCollection<DatabaseHistoryRow> _visibleRows = [];

    public DatabaseSnapshotHistoryWindow(
        DatabaseMetadataSnapshot snapshot,
        DatabaseSnapshotLibrary library,
        DatabaseSnapshotHistory history,
        DatabaseSnapshotHistoryService historyService)
    {
        InitializeComponent();
        _library = library;
        _history = history;
        _historyService = historyService;

        Title = $"History - {snapshot.DisplayName}";
        HeaderTextBlock.Text = $"Database History: {snapshot.DisplayName}";
        RowsListView.ItemsSource = _visibleRows;
        ReloadRows();
    }

    public event EventHandler<DatabaseHistoryVersionEventArgs>? VersionCompareRequested;

    public event EventHandler<DatabaseHistoryVersionEventArgs>? VersionRestoreRequested;

    public event EventHandler<DatabaseHistoryVersionEventArgs>? VersionRestoreToRequested;

    public event EventHandler<DatabaseHistoryFileEventArgs>? FileViewRequested;

    public event EventHandler<DatabaseHistoryFileEventArgs>? FileCompareRequested;

    public event EventHandler<DatabaseHistoryFileEventArgs>? FileRestoreRequested;

    public event EventHandler<DatabaseHistoryFileEventArgs>? FileRestoreToRequested;

    public void ReloadRows()
    {
        _versionRows.Clear();
        foreach (DatabaseSnapshotVersion version in _history.Versions.OrderByDescending(version => version.VersionNumber))
        {
            DatabaseHistoryVersionSummary summary = _historyService.CreateVersionSummary(_library, _history, version);
            var versionRow = new DatabaseHistoryVersionRow(summary);
            versionRow.Children.AddRange(summary.Changes
                .OrderBy(change => DatabaseHistoryDisplay.GetResourceTypeSortOrder(change.Kind))
                .ThenBy(change => DatabaseHistoryDisplay.GetFileNameDisplay(change.DisplayName, change.RelativePath), StringComparer.OrdinalIgnoreCase)
                .Select(change => new DatabaseHistoryFileRow(change)));
            _versionRows.Add(versionRow);
        }

        RefreshVisibleRows();
    }

    private void RefreshVisibleRows()
    {
        string filter = HistoryFilterTextBox.Text.Trim();
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        int visibleFileCount = 0;
        int visibleVersionCount = 0;

        _visibleRows.Clear();
        foreach (DatabaseHistoryVersionRow versionRow in _versionRows)
        {
            List<DatabaseHistoryFileRow> matchingChildren = hasFilter
                ? versionRow.Children.Where(child => child.Matches(filter)).ToList()
                : versionRow.Children;
            bool versionMatches = hasFilter && versionRow.Matches(filter);
            if (hasFilter && !versionMatches && matchingChildren.Count == 0)
            {
                continue;
            }

            _visibleRows.Add(versionRow);
            visibleVersionCount++;
            if (!versionRow.IsExpanded && !hasFilter)
            {
                continue;
            }

            foreach (DatabaseHistoryFileRow child in matchingChildren)
            {
                _visibleRows.Add(child);
                visibleFileCount++;
            }
        }

        StatusTextBlock.Text = hasFilter
            ? $"{visibleFileCount} matching file(s) in {visibleVersionCount} version(s)."
            : $"{_history.Versions.Count} version(s).";
    }

    private void HistoryFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshVisibleRows();
    }

    private void RowsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowsListView.SelectedItem is DatabaseHistoryVersionRow row)
        {
            row.IsExpanded = !row.IsExpanded;
            RefreshVisibleRows();
        }
    }

    private void ToggleRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DatabaseHistoryVersionRow row)
        {
            return;
        }

        row.IsExpanded = !row.IsExpanded;
        RefreshVisibleRows();
    }

    private void RowsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ListViewItem? item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item == null)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();
    }

    private void RowsListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ContextMenu? contextMenu = RowsListView.SelectedItem switch
        {
            DatabaseHistoryVersionRow versionRow => CreateVersionContextMenu(versionRow),
            DatabaseHistoryFileRow fileRow => CreateFileContextMenu(fileRow),
            _ => null
        };

        RowsListView.ContextMenu = contextMenu;
        if (contextMenu == null)
        {
            e.Handled = true;
        }
    }

    private ContextMenu CreateVersionContextMenu(DatabaseHistoryVersionRow row)
    {
        var contextMenu = new ContextMenu();
        var compareItem = new MenuItem { Header = "Compare With..." };
        compareItem.Click += (_, _) => VersionCompareRequested?.Invoke(this, new DatabaseHistoryVersionEventArgs(row.Summary.Version));
        contextMenu.Items.Add(compareItem);

        var restoreItem = new MenuItem { Header = "Restore" };
        restoreItem.Click += (_, _) => VersionRestoreRequested?.Invoke(this, new DatabaseHistoryVersionEventArgs(row.Summary.Version));
        contextMenu.Items.Add(restoreItem);

        var restoreToItem = new MenuItem { Header = "Restore To..." };
        restoreToItem.Click += (_, _) => VersionRestoreToRequested?.Invoke(this, new DatabaseHistoryVersionEventArgs(row.Summary.Version));
        contextMenu.Items.Add(restoreToItem);
        return contextMenu;
    }

    private ContextMenu CreateFileContextMenu(DatabaseHistoryFileRow row)
    {
        var contextMenu = new ContextMenu();
        var viewItem = new MenuItem { Header = "View" };
        viewItem.Click += (_, _) => FileViewRequested?.Invoke(this, new DatabaseHistoryFileEventArgs(row.Change));
        contextMenu.Items.Add(viewItem);

        var compareItem = new MenuItem { Header = "Compare With..." };
        compareItem.Click += (_, _) => FileCompareRequested?.Invoke(this, new DatabaseHistoryFileEventArgs(row.Change));
        contextMenu.Items.Add(compareItem);

        var restoreItem = new MenuItem { Header = "Restore" };
        restoreItem.Click += (_, _) => FileRestoreRequested?.Invoke(this, new DatabaseHistoryFileEventArgs(row.Change));
        contextMenu.Items.Add(restoreItem);

        var restoreToItem = new MenuItem { Header = "Restore To..." };
        restoreToItem.Click += (_, _) => FileRestoreToRequested?.Invoke(this, new DatabaseHistoryFileEventArgs(row.Change));
        contextMenu.Items.Add(restoreToItem);
        return contextMenu;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public abstract class DatabaseHistoryRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public abstract bool IsVersion { get; }

        public abstract string VersionColumn { get; }

        public abstract string ResourceTypeColumn { get; }

        public abstract string NameColumn { get; }

        public abstract string AddedColumn { get; }

        public abstract string RemovedColumn { get; }

        public virtual Thickness IndentMargin => new(0);

        public virtual Visibility ToggleVisibility => Visibility.Hidden;

        public virtual string ToggleGlyph => string.Empty;

        public virtual string ToggleToolTip => string.Empty;

        public virtual bool Matches(string filter)
        {
            return DatabaseHistoryDisplay.ContainsText(VersionColumn, filter) ||
                   DatabaseHistoryDisplay.ContainsText(ResourceTypeColumn, filter) ||
                   DatabaseHistoryDisplay.ContainsText(NameColumn, filter) ||
                   DatabaseHistoryDisplay.ContainsText(AddedColumn, filter) ||
                   DatabaseHistoryDisplay.ContainsText(RemovedColumn, filter);
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DatabaseHistoryVersionRow(DatabaseHistoryVersionSummary summary) : DatabaseHistoryRow
    {
        private bool _isExpanded;

        public DatabaseHistoryVersionSummary Summary { get; } = summary;

        public List<DatabaseHistoryFileRow> Children { get; } = [];

        public override bool IsVersion => true;

        public override string VersionColumn => $"Version {Summary.Version.VersionName}";

        public override string ResourceTypeColumn => string.Empty;

        public override string NameColumn => string.Empty;

        public override string AddedColumn => Summary.Version.CreatedAtUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

        public override string RemovedColumn => $"{Summary.Changes.Count} file(s)";

        public override Visibility ToggleVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;

        public override string ToggleGlyph => IsExpanded ? "▲" : "▼";

        public override string ToggleToolTip => IsExpanded ? "Collapse" : "Expand";

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToggleGlyph));
                OnPropertyChanged(nameof(ToggleToolTip));
            }
        }
    }

    public sealed class DatabaseHistoryFileRow(DatabaseHistoryFileChangeSummary change) : DatabaseHistoryRow
    {
        public DatabaseHistoryFileChangeSummary Change { get; } = change;

        public override bool IsVersion => false;

        public override string VersionColumn => string.Empty;

        public override string ResourceTypeColumn => DatabaseHistoryDisplay.GetResourceTypeDisplay(Change.Kind);

        public override string NameColumn => DatabaseHistoryDisplay.GetFileNameDisplay(Change.DisplayName, Change.RelativePath);

        public override string AddedColumn => $"+{Change.AddedLines}";

        public override string RemovedColumn => $"-{Change.RemovedLines}";

        public override Thickness IndentMargin => new(28, 0, 0, 0);

        public override bool Matches(string filter)
        {
            return base.Matches(filter) ||
                   DatabaseHistoryDisplay.ContainsText(Change.DisplayName, filter) ||
                   DatabaseHistoryDisplay.ContainsText(Change.RelativePath, filter);
        }
    }
}

public sealed class DatabaseHistoryVersionEventArgs(DatabaseSnapshotVersion version) : EventArgs
{
    public DatabaseSnapshotVersion Version { get; } = version;
}

public sealed class DatabaseHistoryFileEventArgs(DatabaseHistoryFileChangeSummary change) : EventArgs
{
    public DatabaseHistoryFileChangeSummary Change { get; } = change;
}

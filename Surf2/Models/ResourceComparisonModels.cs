using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Surf2.Models;

public enum ComparisonResourceKind
{
    File,
    Folder,
    VirtualFolder,
    DatabaseSnapshot,
    DatabaseObjectFolder,
    StoredProcedure,
    View,
    Function,
    Trigger,
    TableMetadata,
    TableData
}

public enum ResourceComparisonStatus
{
    Identical,
    Different,
    MissingLeft,
    MissingRight,
    Excluded
}

public sealed record ComparisonResource(
    string DisplayName,
    string TypeDisplay,
    string Path,
    ComparisonResourceKind Kind,
    string ComparisonTypeKey,
    bool IsCollection,
    bool IsText,
    bool IsTableData,
    string IdentityKey,
    FileSystemNode? ExplorerNode = null,
    string SnapshotId = "",
    string DatabaseFolderName = "",
    SqlDatabaseObjectKind? DatabaseObjectKind = null,
    string DatabaseObjectName = "",
    string TableSchemaName = "",
    string TableName = "",
    string SyntaxPath = "")
{
    public bool IsTableMetadata => Kind == ComparisonResourceKind.TableMetadata;
}

public sealed record ComparisonPickerItem(
    string Name,
    string Type,
    string Path,
    ComparisonResource Resource);

public sealed record ResourceComparisonDocument(
    string RelativePath,
    string DisplayName,
    ComparisonResourceKind Kind,
    bool IsCollection,
    string Content,
    string SyntaxPath,
    ComparisonResource Resource);

public sealed class ResourceCollectionDiffRow : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isExcluded;
    private ResourceComparisonStatus _status;

    public ResourceCollectionDiffRow(
        int depth,
        string relativePath,
        string leftName,
        string rightName,
        ResourceComparisonStatus status,
        ResourceComparisonDocument? leftDocument,
        ResourceComparisonDocument? rightDocument)
    {
        Depth = depth;
        RelativePath = relativePath;
        LeftName = leftName;
        RightName = rightName;
        _status = status;
        LeftDocument = leftDocument;
        RightDocument = rightDocument;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Depth { get; }

    public string RelativePath { get; }

    public string LeftName { get; }

    public string RightName { get; }

    public ResourceComparisonDocument? LeftDocument { get; }

    public ResourceComparisonDocument? RightDocument { get; }

    public bool CanOpenDiff =>
        !IsExcluded &&
        (LeftDocument?.IsCollection != true) &&
        (RightDocument?.IsCollection != true) &&
        (LeftDocument != null || RightDocument != null);

    public bool IsFolder => LeftDocument?.IsCollection == true || RightDocument?.IsCollection == true;

    public int Indent => Depth * 18;

    public Thickness IndentMargin => new(Indent, 0, 0, 0);

    public Visibility LeftToggleVisibility => IsFolder && !string.IsNullOrWhiteSpace(LeftName)
        ? Visibility.Visible
        : Visibility.Hidden;

    public Visibility RightToggleVisibility => IsFolder && !string.IsNullOrWhiteSpace(RightName)
        ? Visibility.Visible
        : Visibility.Hidden;

    public string ToggleGlyph => IsExpanded ? "▲" : "▼";

    public string ToggleToolTip => IsExpanded ? "Collapse" : "Expand";

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

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded == value)
            {
                return;
            }

            _isExcluded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(LeftBackground));
            OnPropertyChanged(nameof(RightBackground));
            OnPropertyChanged(nameof(StatusForeground));
            OnPropertyChanged(nameof(CanOpenDiff));
        }
    }

    public ResourceComparisonStatus Status
    {
        get => IsExcluded ? ResourceComparisonStatus.Excluded : _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(LeftBackground));
            OnPropertyChanged(nameof(RightBackground));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public string DisplayStatus => Status switch
    {
        ResourceComparisonStatus.Identical => "Identical",
        ResourceComparisonStatus.Different => "Different",
        ResourceComparisonStatus.MissingLeft => "Missing left",
        ResourceComparisonStatus.MissingRight => "Missing right",
        ResourceComparisonStatus.Excluded => "Excluded",
        _ => string.Empty
    };

    public Brush LeftBackground => GetBackground(isLeft: true);

    public Brush RightBackground => GetBackground(isLeft: false);

    public Brush StatusForeground => Status switch
    {
        ResourceComparisonStatus.Identical => Brushes.ForestGreen,
        ResourceComparisonStatus.Different => Brushes.DarkOrange,
        ResourceComparisonStatus.MissingLeft or ResourceComparisonStatus.MissingRight => Brushes.Firebrick,
        ResourceComparisonStatus.Excluded => Brushes.Gray,
        _ => Brushes.DimGray
    };

    private Brush GetBackground(bool isLeft)
    {
        if (Status == ResourceComparisonStatus.Excluded)
        {
            return CreateBrush(0xF1, 0xF5, 0xF9);
        }

        if (Status == ResourceComparisonStatus.MissingLeft)
        {
            return isLeft ? Brushes.White : CreateBrush(0xFE, 0xE2, 0xE2);
        }

        if (Status == ResourceComparisonStatus.MissingRight)
        {
            return isLeft ? CreateBrush(0xFE, 0xE2, 0xE2) : Brushes.White;
        }

        return Status switch
        {
            ResourceComparisonStatus.Identical => CreateBrush(0xDC, 0xFC, 0xE7),
            ResourceComparisonStatus.Different => CreateBrush(0xFF, 0xED, 0xCC),
            _ => Brushes.White
        };
    }

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record ResourceCollectionDiffResult(
    ComparisonResource Left,
    ComparisonResource Right,
    ObservableCollection<ResourceCollectionDiffRow> Rows);

public enum TableDataDiffStatus
{
    Identical,
    Added,
    Removed,
    Altered
}

public sealed record TableDataDiffRow(
    string Key,
    TableDataDiffStatus Status,
    string ChangedColumns,
    string LeftPreview,
    string RightPreview);

public sealed record TableDataDiffResult(
    ComparisonResource Left,
    ComparisonResource Right,
    IReadOnlyList<string> KeyColumns,
    ObservableCollection<TableDataDiffRow> Rows);

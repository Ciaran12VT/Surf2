using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace Surf2.Models;

public enum FileSystemNodeIconKind
{
    File,
    Folder,
    Database,
    Diagrams,
    VirtualFolder,
    Missing
}

public sealed class FileSystemNode
{
    public const string RootParentKey = "__root__";
    private const string VirtualFolderNodeKeyPrefix = "surf2://virtual-folder/";

    private static readonly Geometry FileIconGeometry = CreateFrozenGeometry("M5,2.5 L11,2.5 L14,5.5 L14,15.5 L5,15.5 Z M11,2.5 L11,5.5 L14,5.5");
    private static readonly Geometry FolderIconGeometry = CreateFrozenGeometry("M2.5,5.5 L7,5.5 L8.5,7.2 L15.5,7.2 Q16.5,7.2 16.5,8.2 L16.5,14.5 Q16.5,15.5 15.5,15.5 L2.5,15.5 Q1.5,15.5 1.5,14.5 L1.5,6.5 Q1.5,5.5 2.5,5.5 Z");
    private static readonly Geometry DatabaseIconGeometry = CreateFrozenGeometry("M4,5 C4,3.7 14,3.7 14,5 C14,6.3 4,6.3 4,5 M4,5 L4,13 C4,14.3 14,14.3 14,13 L14,5 M4,9 C4,10.3 14,10.3 14,9");
    private static readonly Geometry DiagramsIconGeometry = CreateFrozenGeometry("M5,3.5 L13,3.5 L13,7.5 L5,7.5 Z M9,7.5 L9,10 M5,13.5 L2,13.5 L2,16.5 L5,16.5 Z M16,13.5 L13,13.5 L13,16.5 L16,16.5 Z M9,10 L3.5,13.5 M9,10 L14.5,13.5");
    private static readonly Geometry MissingIconGeometry = CreateFrozenGeometry("M9,2.5 L16,15.5 L2,15.5 Z M9,6.5 L9,10.5 M9,13.2 L9.1,13.2");

    private static readonly Brush FileIconBrush = CreateFrozenBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush FolderIconBrush = CreateFrozenBrush(Color.FromRgb(0xB4, 0x79, 0x00));
    private static readonly Brush DatabaseIconBrush = CreateFrozenBrush(Color.FromRgb(0x0F, 0x76, 0x6E));
    private static readonly Brush DiagramsIconBrush = CreateFrozenBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly Brush VirtualFolderIconBrush = CreateFrozenBrush(Color.FromRgb(0xC2, 0x41, 0x0C));
    private static readonly Brush UnloadedBrush = CreateFrozenBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush MissingIconBrush = Brushes.Firebrick;

    public FileSystemNode(
        string path,
        bool isDirectory,
        bool exists = true,
        string? displayName = null,
        FileSystemNodeIconKind? iconKind = null,
        string? parentKey = null,
        string? nodeKey = null,
        bool isVirtualFolder = false,
        string? virtualFolderId = null,
        string? scopeResourceId = null,
        bool isScopeResourceRoot = false,
        bool isScopeResourceLoaded = true,
        string? toolTip = null)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        Exists = exists;
        IsVirtualFolder = isVirtualFolder;
        VirtualFolderId = virtualFolderId ?? string.Empty;
        ScopeResourceId = scopeResourceId ?? string.Empty;
        IsScopeResourceRoot = isScopeResourceRoot;
        IsScopeResourceLoaded = isScopeResourceLoaded;
        ToolTip = toolTip;
        NodeKey = string.IsNullOrWhiteSpace(nodeKey) ? path : nodeKey;
        ParentKey = string.IsNullOrWhiteSpace(parentKey) ? RootParentKey : parentKey;
        NaturalParentKey = ParentKey;
        IconKind = !exists ? FileSystemNodeIconKind.Missing : iconKind ?? (isDirectory ? FileSystemNodeIconKind.Folder : FileSystemNodeIconKind.File);
        Name = displayName ?? Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path;
        }
    }

    public string Name { get; }

    public string FullPath { get; }

    public string NodeKey { get; }

    public string ParentKey { get; set; }

    public string NaturalParentKey { get; set; }

    public bool IsDirectory { get; }

    public bool Exists { get; }

    public bool IsVirtualFolder { get; }

    public string VirtualFolderId { get; }

    public string ScopeResourceId { get; }

    public bool IsScopeResourceRoot { get; }

    public bool IsScopeResourceLoaded { get; }

    public string? ToolTip { get; }

    public bool IsLoaded { get; set; }

    public bool IsExpanded { get; set; }

    public bool IsVirtualDocument { get; set; }

    public bool HasUnresolvedQueries { get; set; }

    public string ContentSearchPattern { get; set; } = string.Empty;

    public bool ContentSearchUseRegex { get; set; }

    public Dictionary<int, string> SpreadsheetSearchFilters { get; } = [];

    public FileSystemNodeIconKind IconKind { get; }

    public ObservableCollection<FileSystemNode> Children { get; } = [];

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(FullPath).ToLowerInvariant();

    public Geometry IconGeometry => IconKind switch
    {
        FileSystemNodeIconKind.Folder => FolderIconGeometry,
        FileSystemNodeIconKind.Database => DatabaseIconGeometry,
        FileSystemNodeIconKind.Diagrams => DiagramsIconGeometry,
        FileSystemNodeIconKind.VirtualFolder => FolderIconGeometry,
        FileSystemNodeIconKind.Missing => MissingIconGeometry,
        _ => FileIconGeometry
    };

    public Brush IconBrush => !IsScopeResourceLoaded ? UnloadedBrush : IconKind switch
    {
        FileSystemNodeIconKind.Folder => FolderIconBrush,
        FileSystemNodeIconKind.Database => DatabaseIconBrush,
        FileSystemNodeIconKind.Diagrams => DiagramsIconBrush,
        FileSystemNodeIconKind.VirtualFolder => VirtualFolderIconBrush,
        FileSystemNodeIconKind.Missing => MissingIconBrush,
        _ => FileIconBrush
    };

    public Brush TextBrush => !IsScopeResourceLoaded
        ? UnloadedBrush
        : !Exists || HasUnresolvedQueries
            ? Brushes.Firebrick
            : IsDirectory
                ? Brushes.Black
                : Brushes.DimGray;

    public void AddLoadingPlaceholder()
    {
        if (!Exists || !IsDirectory || Children.Count > 0)
        {
            return;
        }

        Children.Add(new FileSystemNode(
            Path.Combine(FullPath, "Loading..."),
            false,
            parentKey: NodeKey));
    }

    public static string CreateVirtualFolderNodeKey(string virtualFolderId)
    {
        return $"{VirtualFolderNodeKeyPrefix}{virtualFolderId}";
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

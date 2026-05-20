using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;

namespace Surf2.Models;

public sealed class FileSystemNode
{
    public FileSystemNode(string path, bool isDirectory, bool exists = true, string? displayName = null)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        Exists = exists;
        Name = displayName ?? Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path;
        }
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool Exists { get; }

    public bool IsLoaded { get; set; }

    public bool IsExpanded { get; set; }

    public bool IsVirtualDocument { get; set; }

    public bool HasUnresolvedQueries { get; set; }

    public ObservableCollection<FileSystemNode> Children { get; } = [];

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(FullPath).ToLowerInvariant();

    public string Glyph => !Exists ? "!" : IsDirectory ? ">" : "-";

    public Brush AccentBrush => !Exists ? Brushes.Firebrick : IsDirectory ? Brushes.DarkGoldenrod : Brushes.SlateGray;

    public Brush TextBrush => !Exists || HasUnresolvedQueries ? Brushes.Firebrick : IsDirectory ? Brushes.Black : Brushes.DimGray;

    public void AddLoadingPlaceholder()
    {
        if (!Exists || !IsDirectory || Children.Count > 0)
        {
            return;
        }

        Children.Add(new FileSystemNode(Path.Combine(FullPath, "Loading..."), false));
    }
}

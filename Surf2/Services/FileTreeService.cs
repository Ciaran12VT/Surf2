using System.Collections.ObjectModel;
using System.IO;
using Surf2.Models;

namespace Surf2.Services;

public sealed class FileTreeService
{
    public ObservableCollection<FileSystemNode> CreateRoots(IEnumerable<ScopedResource> resources)
    {
        var roots = new ObservableCollection<FileSystemNode>();

        foreach (ScopedResource resource in resources)
        {
            FileSystemNode node = CreateRoot(resource);
            roots.Add(node);
        }

        return roots;
    }

    public ObservableCollection<FileSystemNode> CreateRoot(string folderPath)
    {
        var root = new FileSystemNode(folderPath, isDirectory: true);
        root.AddLoadingPlaceholder();
        return [root];
    }

    public void LoadChildren(FileSystemNode node)
    {
        if (!node.Exists || !node.IsDirectory || node.IsLoaded)
        {
            return;
        }

        node.Children.Clear();

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(node.FullPath).OrderBy(Path.GetFileName))
            {
                var child = new FileSystemNode(directory, isDirectory: true);
                child.AddLoadingPlaceholder();
                node.Children.Add(child);
            }

            foreach (string file in Directory.EnumerateFiles(node.FullPath).OrderBy(Path.GetFileName))
            {
                node.Children.Add(new FileSystemNode(file, isDirectory: false));
            }

            node.IsLoaded = true;
        }
        catch (UnauthorizedAccessException)
        {
            node.IsLoaded = true;
        }
        catch (DirectoryNotFoundException)
        {
            node.IsLoaded = true;
        }
        catch (IOException)
        {
            node.IsLoaded = true;
        }
    }

    private static FileSystemNode CreateRoot(ScopedResource resource)
    {
        bool isDirectory = resource.Kind == ResourceKind.Folder;
        bool exists = resource.Exists;
        string suffix = exists ? string.Empty : " (missing)";

        var root = new FileSystemNode(
            resource.Path,
            isDirectory,
            exists,
            $"{resource.DisplayName}{suffix}");

        if (isDirectory)
        {
            root.AddLoadingPlaceholder();
        }

        return root;
    }
}

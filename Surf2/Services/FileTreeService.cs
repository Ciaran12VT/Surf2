using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Surf2.Models;

namespace Surf2.Services;

public sealed class FileTreeService
{
    public ObservableCollection<FileSystemNode> CreateRoots(
        IEnumerable<ScopedResource> resources,
        DatabaseSnapshotLibrary? databaseSnapshots = null,
        DiagramLibrary? diagramLibrary = null,
        IEnumerable<VirtualFolder>? virtualFolders = null)
    {
        var roots = new ObservableCollection<FileSystemNode>();
        List<ScopedResource> resourceList = resources.ToList();

        foreach (ScopedResource resource in resourceList.Where(resource => resource.Kind != ResourceKind.Diagram))
        {
            FileSystemNode node = CreateRoot(resource, databaseSnapshots);
            roots.Add(node);
        }

        FileSystemNode? diagramRoot = CreateDiagramRoot(resourceList, diagramLibrary);
        if (diagramRoot != null)
        {
            roots.Add(diagramRoot);
        }

        ApplyVirtualFolders(roots, FileSystemNode.RootParentKey, virtualFolders);
        return roots;
    }

    public ObservableCollection<FileSystemNode> CreateRoot(string folderPath)
    {
        var root = new FileSystemNode(
            folderPath,
            isDirectory: true,
            iconKind: FileSystemNodeIconKind.Folder,
            parentKey: FileSystemNode.RootParentKey);
        root.AddLoadingPlaceholder();
        return [root];
    }

    public void LoadChildren(FileSystemNode node, IEnumerable<VirtualFolder>? virtualFolders = null)
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
                var child = new FileSystemNode(directory, isDirectory: true, parentKey: node.NodeKey);
                child.AddLoadingPlaceholder();
                node.Children.Add(child);
            }

            foreach (string file in Directory.EnumerateFiles(node.FullPath).OrderBy(Path.GetFileName))
            {
                node.Children.Add(new FileSystemNode(file, isDirectory: false, parentKey: node.NodeKey));
            }

            ApplyVirtualFolders(node.Children, node.NodeKey, virtualFolders);
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

    public ObjectExplorerSearchResult CreateFilteredRoots(
        IEnumerable<ScopedResource> resources,
        DatabaseSnapshotLibrary? databaseSnapshots,
        DiagramLibrary? diagramLibrary,
        string query,
        ObjectExplorerSearchTarget searchTarget,
        bool useRegex,
        IEnumerable<VirtualFolder>? virtualFolders = null)
    {
        var matcher = SearchMatcher.Create(query, useRegex);
        var roots = new ObservableCollection<FileSystemNode>();
        List<ScopedResource> resourceList = resources.ToList();
        int matchCount = 0;
        int searchedCount = 0;

        foreach (ScopedResource resource in resourceList.Where(resource => resource.Kind != ResourceKind.Diagram))
        {
            FileSystemNode? node = resource.Kind == ResourceKind.DatabaseSnapshot
                ? SearchDatabaseResource(resource, databaseSnapshots, matcher, searchTarget, ref matchCount, ref searchedCount)
                : SearchFileSystemResource(resource, matcher, searchTarget, ref matchCount, ref searchedCount);

            if (node != null)
            {
                roots.Add(node);
            }
        }

        FileSystemNode? diagramRoot = SearchDiagramResources(resourceList, diagramLibrary, matcher, searchTarget, ref matchCount, ref searchedCount);
        if (diagramRoot != null)
        {
            roots.Add(diagramRoot);
        }

        ApplyVirtualFolders(roots, FileSystemNode.RootParentKey, virtualFolders);
        return new ObjectExplorerSearchResult(roots, matchCount, searchedCount);
    }

    private static FileSystemNode CreateRoot(ScopedResource resource, DatabaseSnapshotLibrary? databaseSnapshots)
    {
        if (resource.Kind == ResourceKind.DatabaseSnapshot)
        {
            return CreateDatabaseRoot(resource, databaseSnapshots);
        }

        bool isDirectory = resource.Kind == ResourceKind.Folder;
        bool exists = resource.Exists;
        string suffix = exists ? string.Empty : " (missing)";

        var root = new FileSystemNode(
            resource.Path,
            isDirectory,
            exists,
            $"{resource.DisplayName}{suffix}",
            resource.Kind switch
            {
                ResourceKind.Project => FileSystemNodeIconKind.Project,
                ResourceKind.Folder => FileSystemNodeIconKind.Folder,
                _ => null
            },
            parentKey: FileSystemNode.RootParentKey);

        if (isDirectory)
        {
            root.AddLoadingPlaceholder();
        }

        return root;
    }

    private static FileSystemNode? SearchFileSystemResource(
        ScopedResource resource,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount)
    {
        if (!resource.Exists)
        {
            return null;
        }

        if (resource.Kind is ResourceKind.File or ResourceKind.Project)
        {
            return SearchFile(
                resource.Path,
                resource.DisplayName,
                matcher,
                searchTarget,
                ref matchCount,
                ref searchedCount,
                resource.Kind == ResourceKind.Project ? FileSystemNodeIconKind.Project : null);
        }

        if (resource.Kind == ResourceKind.Folder && Directory.Exists(resource.Path))
        {
            return SearchDirectory(
                resource.Path,
                resource.DisplayName,
                matcher,
                searchTarget,
                ref matchCount,
                ref searchedCount,
                FileSystemNodeIconKind.Folder,
                FileSystemNode.RootParentKey);
        }

        return null;
    }

    private static FileSystemNode? SearchDirectory(
        string directoryPath,
        string displayName,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount,
        FileSystemNodeIconKind? iconKind = null,
        string parentKey = FileSystemNode.RootParentKey)
    {
        bool isMatch = searchTarget == ObjectExplorerSearchTarget.Name && matcher.IsMatch(displayName);
        searchedCount++;

        var node = new FileSystemNode(
            directoryPath,
            isDirectory: true,
            displayName: displayName,
            iconKind: iconKind,
            parentKey: parentKey)
        {
            IsLoaded = true,
            IsExpanded = true
        };

        if (isMatch)
        {
            matchCount++;
        }

        foreach (string childDirectory in EnumerateDirectoriesSafely(directoryPath).OrderBy(Path.GetFileName))
        {
            string childName = Path.GetFileName(childDirectory);
            FileSystemNode? childNode = SearchDirectory(
                childDirectory,
                childName,
                matcher,
                searchTarget,
                ref matchCount,
                ref searchedCount,
                FileSystemNodeIconKind.Folder,
                node.NodeKey);
            if (childNode != null)
            {
                node.Children.Add(childNode);
            }
        }

        foreach (string filePath in EnumerateFilesSafely(directoryPath).OrderBy(Path.GetFileName))
        {
            FileSystemNode? fileNode = SearchFile(
                filePath,
                Path.GetFileName(filePath),
                matcher,
                searchTarget,
                ref matchCount,
                ref searchedCount,
                parentKey: node.NodeKey);
            if (fileNode != null)
            {
                node.Children.Add(fileNode);
            }
        }

        return isMatch || node.Children.Count > 0 ? node : null;
    }

    private static FileSystemNode? SearchFile(
        string filePath,
        string displayName,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount,
        FileSystemNodeIconKind? iconKind = null,
        string parentKey = FileSystemNode.RootParentKey)
    {
        bool isMatch;
        searchedCount++;

        if (searchTarget == ObjectExplorerSearchTarget.Name)
        {
            isMatch = matcher.IsMatch(displayName);
        }
        else
        {
            if (!TryReadText(filePath, out string content))
            {
                return null;
            }

            isMatch = matcher.IsMatch(content);
        }

        if (!isMatch)
        {
            return null;
        }

        matchCount++;
        return new FileSystemNode(
            filePath,
            isDirectory: false,
            displayName: displayName,
            iconKind: iconKind,
            parentKey: parentKey);
    }

    private static FileSystemNode? SearchDatabaseResource(
        ScopedResource resource,
        DatabaseSnapshotLibrary? databaseSnapshots,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount)
    {
        DatabaseMetadataSnapshot? snapshot = databaseSnapshots?.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, resource.Path, StringComparison.OrdinalIgnoreCase));
        if (snapshot == null)
        {
            return null;
        }

        string displayName = !string.IsNullOrWhiteSpace(resource.DisplayNameOverride)
            ? resource.DisplayNameOverride
            : snapshot.DisplayName;
        bool rootMatch = searchTarget == ObjectExplorerSearchTarget.Name && matcher.IsMatch(displayName);
        searchedCount++;

        var root = new FileSystemNode(
            resource.Path,
            isDirectory: true,
            displayName: displayName,
            iconKind: FileSystemNodeIconKind.Database,
            parentKey: FileSystemNode.RootParentKey)
        {
            IsLoaded = true,
            IsExpanded = true
        };

        if (rootMatch)
        {
            matchCount++;
        }

        AddFilteredObjectFolder(root, snapshot, "Stored Procedures", SqlDatabaseObjectKind.StoredProcedure, matcher, searchTarget, ref matchCount, ref searchedCount);
        AddFilteredObjectFolder(root, snapshot, "Views", SqlDatabaseObjectKind.View, matcher, searchTarget, ref matchCount, ref searchedCount);
        AddFilteredObjectFolder(root, snapshot, "Functions", SqlDatabaseObjectKind.Function, matcher, searchTarget, ref matchCount, ref searchedCount);
        AddFilteredObjectFolder(root, snapshot, "Triggers", SqlDatabaseObjectKind.Trigger, matcher, searchTarget, ref matchCount, ref searchedCount);
        AddFilteredTablesFolder(root, snapshot, matcher, searchTarget, ref matchCount, ref searchedCount);

        return rootMatch || root.Children.Count > 0 ? root : null;
    }

    private static void AddFilteredObjectFolder(
        FileSystemNode root,
        DatabaseMetadataSnapshot snapshot,
        string folderName,
        SqlDatabaseObjectKind kind,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount)
    {
        var folder = new FileSystemNode(
            $"{snapshot.SnapshotId}/{folderName}",
            isDirectory: true,
            displayName: folderName,
            parentKey: root.NodeKey)
        {
            IsLoaded = true,
            IsExpanded = true
        };

        foreach (SqlDatabaseObject databaseObject in snapshot.Objects
                     .Where(item => item.Kind == kind)
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.ObjectName))
        {
            string displayName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
            string content = databaseObject.Definition;
            searchedCount++;

            bool isMatch = searchTarget == ObjectExplorerSearchTarget.Name
                ? matcher.IsMatch(displayName)
                : matcher.IsMatch(content);

            if (!isMatch)
            {
                continue;
            }

            matchCount++;
            folder.Children.Add(new FileSystemNode(
                DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject),
                isDirectory: false,
                displayName: displayName,
                parentKey: folder.NodeKey)
            {
                IsVirtualDocument = true
            });
        }

        if (folder.Children.Count > 0)
        {
            root.Children.Add(folder);
        }
    }

    private static void AddFilteredTablesFolder(
        FileSystemNode root,
        DatabaseMetadataSnapshot snapshot,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount)
    {
        var folder = new FileSystemNode(
            $"{snapshot.SnapshotId}/Tables",
            isDirectory: true,
            displayName: "Tables",
            parentKey: root.NodeKey)
        {
            IsLoaded = true,
            IsExpanded = true
        };

        var documentService = new DatabaseDocumentService();
        foreach (SqlTable table in snapshot.Tables
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.TableName))
        {
            string displayName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
            string label = displayName;
            if (table.HasFullData)
            {
                label += $" ({table.FullDataRowCount} rows)";
            }

            searchedCount++;
            bool isMatch = searchTarget == ObjectExplorerSearchTarget.Name
                ? matcher.IsMatch(displayName)
                : matcher.IsMatch(documentService.CreateTableDocument(snapshot, table));

            if (!isMatch)
            {
                continue;
            }

            matchCount++;
            folder.Children.Add(new FileSystemNode(
                DatabaseDocumentService.CreateTableDocumentPath(snapshot, table),
                isDirectory: false,
                displayName: label,
                parentKey: folder.NodeKey)
            {
                IsVirtualDocument = true
            });
        }

        if (folder.Children.Count > 0)
        {
            root.Children.Add(folder);
        }
    }

    private static FileSystemNode CreateDatabaseRoot(ScopedResource resource, DatabaseSnapshotLibrary? databaseSnapshots)
    {
        DatabaseMetadataSnapshot? snapshot = databaseSnapshots?.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, resource.Path, StringComparison.OrdinalIgnoreCase));
        string displayName = !string.IsNullOrWhiteSpace(resource.DisplayNameOverride)
            ? resource.DisplayNameOverride
            : snapshot?.DisplayName ?? resource.DisplayName;
        var root = new FileSystemNode(
            resource.Path,
            isDirectory: true,
            exists: snapshot != null,
            displayName: displayName,
            iconKind: FileSystemNodeIconKind.Database,
            parentKey: FileSystemNode.RootParentKey);

        if (snapshot == null)
        {
            return root;
        }

        root.IsLoaded = true;
        AddObjectFolder(root, snapshot, "Stored Procedures", SqlDatabaseObjectKind.StoredProcedure);
        AddObjectFolder(root, snapshot, "Views", SqlDatabaseObjectKind.View);
        AddObjectFolder(root, snapshot, "Functions", SqlDatabaseObjectKind.Function);
        AddObjectFolder(root, snapshot, "Triggers", SqlDatabaseObjectKind.Trigger);
        AddTablesFolder(root, snapshot);
        return root;
    }

    private static void AddObjectFolder(
        FileSystemNode root,
        DatabaseMetadataSnapshot snapshot,
        string folderName,
        SqlDatabaseObjectKind kind)
    {
        var folder = new FileSystemNode(
            $"{snapshot.SnapshotId}/{folderName}",
            isDirectory: true,
            displayName: folderName,
            parentKey: root.NodeKey)
        {
            IsLoaded = true
        };

        foreach (SqlDatabaseObject databaseObject in snapshot.Objects
                     .Where(item => item.Kind == kind)
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.ObjectName))
        {
            folder.Children.Add(new FileSystemNode(
                DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject),
                isDirectory: false,
                displayName: SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName),
                parentKey: folder.NodeKey)
            {
                IsVirtualDocument = true
            });
        }

        root.Children.Add(folder);
    }

    private static void AddTablesFolder(FileSystemNode root, DatabaseMetadataSnapshot snapshot)
    {
        var folder = new FileSystemNode(
            $"{snapshot.SnapshotId}/Tables",
            isDirectory: true,
            displayName: "Tables",
            parentKey: root.NodeKey)
        {
            IsLoaded = true
        };

        foreach (SqlTable table in snapshot.Tables
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.TableName))
        {
            string label = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
            if (table.HasFullData)
            {
                label += $" ({table.FullDataRowCount} rows)";
            }

            folder.Children.Add(new FileSystemNode(
                DatabaseDocumentService.CreateTableDocumentPath(snapshot, table),
                isDirectory: false,
                displayName: label,
                parentKey: folder.NodeKey)
            {
                IsVirtualDocument = true
            });
        }

        root.Children.Add(folder);
    }

    private static FileSystemNode? CreateDiagramRoot(
        IEnumerable<ScopedResource> resources,
        DiagramLibrary? diagramLibrary)
    {
        List<ScopedResource> diagramResources = resources
            .Where(resource => resource.Kind == ResourceKind.Diagram)
            .OrderBy(resource => GetDiagramDisplayName(resource, diagramLibrary?.Find(resource.Path)))
            .ToList();

        var root = new FileSystemNode(
            DiagramDocumentService.DiagramRootPath,
            isDirectory: true,
            displayName: "Diagrams",
            iconKind: FileSystemNodeIconKind.Diagrams,
            parentKey: FileSystemNode.RootParentKey)
        {
            IsLoaded = true
        };

        foreach (ScopedResource resource in diagramResources)
        {
            root.Children.Add(CreateDiagramNode(resource, diagramLibrary?.Find(resource.Path), root.NodeKey));
        }

        return root;
    }

    private static FileSystemNode? SearchDiagramResources(
        IEnumerable<ScopedResource> resources,
        DiagramLibrary? diagramLibrary,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount)
    {
        List<ScopedResource> diagramResources = resources
            .Where(resource => resource.Kind == ResourceKind.Diagram)
            .ToList();

        if (diagramResources.Count == 0)
        {
            return null;
        }

        searchedCount++;
        bool rootMatch = searchTarget == ObjectExplorerSearchTarget.Name && matcher.IsMatch("Diagrams");
        if (rootMatch)
        {
            matchCount++;
        }

        var root = new FileSystemNode(
            DiagramDocumentService.DiagramRootPath,
            isDirectory: true,
            displayName: "Diagrams",
            iconKind: FileSystemNodeIconKind.Diagrams,
            parentKey: FileSystemNode.RootParentKey)
        {
            IsLoaded = true,
            IsExpanded = true
        };

        foreach (ScopedResource resource in diagramResources
                     .OrderBy(resource => GetDiagramDisplayName(resource, diagramLibrary?.Find(resource.Path))))
        {
            DiagramDocument? diagram = diagramLibrary?.Find(resource.Path);
            string displayName = GetDiagramDisplayName(resource, diagram);
            string content = CreateDiagramSearchContent(diagram);
            searchedCount++;

            bool isMatch = searchTarget == ObjectExplorerSearchTarget.Name
                ? matcher.IsMatch(displayName)
                : matcher.IsMatch(content);

            if (!isMatch)
            {
                continue;
            }

            matchCount++;
            root.Children.Add(CreateDiagramNode(resource, diagram, root.NodeKey));
        }

        return rootMatch || root.Children.Count > 0 ? root : null;
    }

    private static FileSystemNode CreateDiagramNode(ScopedResource resource, DiagramDocument? diagram, string parentKey)
    {
        string displayName = GetDiagramDisplayName(resource, diagram);
        string suffix = diagram == null ? " (missing)" : string.Empty;
        return new FileSystemNode(
            DiagramDocumentService.CreateDiagramDocumentPath(resource.Path),
            isDirectory: false,
            exists: diagram != null,
            displayName: $"{displayName}{suffix}",
            parentKey: parentKey)
        {
            IsVirtualDocument = true,
            HasUnresolvedQueries = DiagramQueryState.HasUnresolvedMetadataQueries(diagram)
        };
    }

    private static string GetDiagramDisplayName(ScopedResource resource, DiagramDocument? diagram)
    {
        if (!string.IsNullOrWhiteSpace(resource.DisplayNameOverride))
        {
            return resource.DisplayNameOverride;
        }

        if (!string.IsNullOrWhiteSpace(diagram?.Name))
        {
            return diagram.Name;
        }

        return resource.DisplayName;
    }

    private static string CreateDiagramSearchContent(DiagramDocument? diagram)
    {
        if (diagram == null)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            diagram.Objects.Select(diagramObject =>
                $"{diagramObject.ObjectType} {diagramObject.LabelText} {diagramObject.ImageName}"));
    }

    private static IEnumerable<string> EnumerateDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directory)
    {
        try
        {
            return Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool TryReadText(string filePath, out string content)
    {
        content = string.Empty;
        try
        {
            content = File.ReadAllText(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void ApplyVirtualFolders(
        ObservableCollection<FileSystemNode> nodes,
        string parentKey,
        IEnumerable<VirtualFolder>? virtualFolders)
    {
        if (virtualFolders == null)
        {
            return;
        }

        List<VirtualFolder> allVirtualFolders = virtualFolders.ToList();
        foreach (FileSystemNode node in nodes.ToList())
        {
            if (node.Children.Count > 0)
            {
                ApplyVirtualFolders(node.Children, node.NodeKey, allVirtualFolders);
            }
        }

        List<VirtualFolder> foldersAtLevel = allVirtualFolders
            .Where(folder => string.Equals(NormalizeParentNodeKey(folder.ParentNodeKey), parentKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(folder => folder.Name)
            .ToList();

        foreach (VirtualFolder virtualFolder in foldersAtLevel)
        {
            var virtualFolderNode = new FileSystemNode(
                virtualFolder.VirtualFolderId,
                isDirectory: true,
                displayName: virtualFolder.Name,
                iconKind: FileSystemNodeIconKind.VirtualFolder,
                parentKey: parentKey,
                nodeKey: FileSystemNode.CreateVirtualFolderNodeKey(virtualFolder.VirtualFolderId),
                isVirtualFolder: true,
                virtualFolderId: virtualFolder.VirtualFolderId)
            {
                IsLoaded = true
            };

            foreach (string childNodeKey in virtualFolder.ChildNodeKeys.ToList())
            {
                FileSystemNode? child = nodes.FirstOrDefault(node =>
                    !node.IsVirtualFolder &&
                    string.Equals(node.NodeKey, childNodeKey, StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    continue;
                }

                nodes.Remove(child);
                child.ParentKey = virtualFolderNode.NodeKey;
                child.NaturalParentKey = parentKey;
                virtualFolderNode.Children.Add(child);
            }

            nodes.Add(virtualFolderNode);
        }
    }

    private static string NormalizeParentNodeKey(string? parentNodeKey)
    {
        return string.IsNullOrWhiteSpace(parentNodeKey)
            ? FileSystemNode.RootParentKey
            : parentNodeKey;
    }

    private sealed class SearchMatcher
    {
        private readonly string _query;
        private readonly Regex? _regex;

        private SearchMatcher(string query, Regex? regex)
        {
            _query = query;
            _regex = regex;
        }

        public static SearchMatcher Create(string query, bool useRegex)
        {
            if (useRegex)
            {
                return new SearchMatcher(
                    query,
                    new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }

            return new SearchMatcher(query, null);
        }

        public bool IsMatch(string text)
        {
            if (_regex != null)
            {
                return _regex.IsMatch(text ?? string.Empty);
            }

            return (text ?? string.Empty).Contains(_query, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public enum ObjectExplorerSearchTarget
{
    Name,
    Content
}

public sealed record ObjectExplorerSearchResult(
    ObservableCollection<FileSystemNode> Roots,
    int MatchCount,
    int SearchedCount);

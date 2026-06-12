using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Surf2.Models;

namespace Surf2.Services;

public sealed class FileTreeService
{
    public ObservableCollection<FileSystemNode> CreateRoots(
        IEnumerable<ScopedResource> resources,
        DatabaseSnapshotLibrary? databaseSnapshots = null,
        DiagramLibrary? diagramLibrary = null,
        IEnumerable<VirtualFolder>? virtualFolders = null,
        IReadOnlySet<string>? unloadedResourceIds = null)
    {
        var roots = new ObservableCollection<FileSystemNode>();
        List<ScopedResource> resourceList = resources.ToList();

        foreach (ScopedResource resource in resourceList.Where(resource => resource.Kind != ResourceKind.Diagram))
        {
            FileSystemNode node = CreateRoot(resource, databaseSnapshots, IsResourceLoaded(resource, unloadedResourceIds));
            roots.Add(node);
        }

        FileSystemNode? diagramRoot = CreateDiagramRoot(resourceList, diagramLibrary, unloadedResourceIds);
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
        IEnumerable<VirtualFolder>? virtualFolders = null,
        IReadOnlySet<string>? unloadedResourceIds = null)
    {
        var matcher = SearchMatcher.Create(query, useRegex);
        var roots = new ObservableCollection<FileSystemNode>();
        List<ScopedResource> resourceList = resources.ToList();
        int matchCount = 0;
        int searchedCount = 0;

        foreach (ScopedResource resource in resourceList
                     .Where(resource => resource.Kind != ResourceKind.Diagram && IsResourceLoaded(resource, unloadedResourceIds)))
        {
            FileSystemNode? node = resource.Kind == ResourceKind.DatabaseSnapshot
                ? SearchDatabaseResource(resource, databaseSnapshots, matcher, searchTarget, ref matchCount, ref searchedCount)
                : SearchFileSystemResource(resource, matcher, searchTarget, ref matchCount, ref searchedCount);

            if (node != null)
            {
                roots.Add(node);
            }
        }

        FileSystemNode? diagramRoot = SearchDiagramResources(resourceList, diagramLibrary, matcher, searchTarget, ref matchCount, ref searchedCount, unloadedResourceIds);
        if (diagramRoot != null)
        {
            roots.Add(diagramRoot);
        }

        ApplyVirtualFolders(roots, FileSystemNode.RootParentKey, virtualFolders);
        return new ObjectExplorerSearchResult(roots, matchCount, searchedCount);
    }

    private static FileSystemNode CreateRoot(
        ScopedResource resource,
        DatabaseSnapshotLibrary? databaseSnapshots,
        bool isScopeResourceLoaded)
    {
        if (resource.Kind == ResourceKind.DatabaseSnapshot)
        {
            return CreateDatabaseRoot(resource, databaseSnapshots, isScopeResourceLoaded);
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
                ResourceKind.Folder => FileSystemNodeIconKind.Folder,
                _ => null
            },
            parentKey: FileSystemNode.RootParentKey,
            scopeResourceId: resource.ResourceId,
            resourceKind: resource.Kind,
            isScopeResourceRoot: true,
            isScopeResourceLoaded: isScopeResourceLoaded,
            toolTip: CreateFolderResourceToolTip(resource.Path, isDirectory && exists));

        if (!isScopeResourceLoaded)
        {
            root.IsLoaded = true;
            return root;
        }

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

        if (resource.Kind == ResourceKind.File)
        {
            return SearchFile(
                resource.Path,
                resource.DisplayName,
                matcher,
                searchTarget,
                ref matchCount,
                ref searchedCount,
                null,
                FileSystemNode.RootParentKey,
                resource.ResourceId,
                isScopeResourceRoot: true);
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
                FileSystemNode.RootParentKey,
                resource.ResourceId,
                isScopeResourceRoot: true);
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
        string parentKey = FileSystemNode.RootParentKey,
        string? scopeResourceId = null,
        bool isScopeResourceRoot = false)
    {
        bool isMatch = searchTarget == ObjectExplorerSearchTarget.Name && matcher.IsMatch(displayName);
        searchedCount++;

        var node = new FileSystemNode(
            directoryPath,
            isDirectory: true,
            displayName: displayName,
            iconKind: iconKind,
            parentKey: parentKey,
            scopeResourceId: scopeResourceId,
            resourceKind: ResourceKind.Folder,
            isScopeResourceRoot: isScopeResourceRoot,
            toolTip: isScopeResourceRoot ? CreateFolderResourceToolTip(directoryPath, exists: true) : null)
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
        string parentKey = FileSystemNode.RootParentKey,
        string? scopeResourceId = null,
        bool isScopeResourceRoot = false)
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
        var node = new FileSystemNode(
            filePath,
            isDirectory: false,
            displayName: displayName,
            iconKind: iconKind,
            parentKey: parentKey,
            scopeResourceId: scopeResourceId,
            resourceKind: ResourceKind.File,
            isScopeResourceRoot: isScopeResourceRoot);
        ApplyContentSearchMetadata(node, matcher, searchTarget);
        return node;
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
            parentKey: FileSystemNode.RootParentKey,
            scopeResourceId: resource.ResourceId,
            resourceKind: ResourceKind.DatabaseSnapshot,
            isScopeResourceRoot: true)
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
            parentKey: root.NodeKey,
            resourceKind: ResourceKind.DatabaseSnapshot)
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

            bool contentMatch = searchTarget == ObjectExplorerSearchTarget.Content && matcher.IsMatch(content);
            bool isMatch = searchTarget == ObjectExplorerSearchTarget.Name
                ? matcher.IsMatch(displayName)
                : contentMatch;

            if (!isMatch)
            {
                continue;
            }

            matchCount++;
            var node = new FileSystemNode(
                DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject),
                isDirectory: false,
                displayName: displayName,
                parentKey: folder.NodeKey,
                resourceKind: ResourceKind.DatabaseSnapshot)
            {
                IsVirtualDocument = true
            };
            if (contentMatch)
            {
                ApplyContentSearchMetadata(node, matcher, searchTarget);
            }

            folder.Children.Add(node);
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
            parentKey: root.NodeKey,
            resourceKind: ResourceKind.DatabaseSnapshot)
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
            IReadOnlyList<int> fullDataMatchColumnIndexes = [];
            bool tableDocumentMatch = false;
            bool isMatch;
            if (searchTarget == ObjectExplorerSearchTarget.Name)
            {
                isMatch = matcher.IsMatch(displayName);
            }
            else
            {
                tableDocumentMatch = matcher.IsMatch(documentService.CreateTableDocument(snapshot, table));
                fullDataMatchColumnIndexes = FindFullDataMatchColumnIndexes(snapshot, table, matcher);
                isMatch = tableDocumentMatch || fullDataMatchColumnIndexes.Count > 0;
            }

            if (!isMatch)
            {
                continue;
            }

            matchCount++;
            var node = new FileSystemNode(
                DatabaseDocumentService.CreateTableDocumentPath(snapshot, table),
                isDirectory: false,
                displayName: label,
                parentKey: folder.NodeKey,
                resourceKind: ResourceKind.DatabaseSnapshot)
            {
                IsVirtualDocument = true
            };

            foreach (int columnIndex in fullDataMatchColumnIndexes)
            {
                node.SpreadsheetSearchFilters[columnIndex] = matcher.Query;
            }

            if (searchTarget == ObjectExplorerSearchTarget.Content &&
                tableDocumentMatch)
            {
                ApplyContentSearchMetadata(node, matcher, searchTarget);
            }

            folder.Children.Add(node);
        }

        if (folder.Children.Count > 0)
        {
            root.Children.Add(folder);
        }
    }

    private static IReadOnlyList<int> FindFullDataMatchColumnIndexes(
        DatabaseMetadataSnapshot snapshot,
        SqlTable table,
        SearchMatcher matcher)
    {
        SqlTableDataSet? dataSet = snapshot.GetTableDataSet(table.SchemaName, table.TableName);
        if (dataSet == null || dataSet.Rows.Count == 0)
        {
            return [];
        }

        List<string> headers = GetTableDataHeaders(snapshot, table, dataSet);
        if (headers.Count == 0)
        {
            return [];
        }

        var matchingColumnIndexes = new SortedSet<int>();
        foreach (JsonElement row in dataSet.Rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            for (int columnIndex = 0; columnIndex < headers.Count; columnIndex++)
            {
                if (matchingColumnIndexes.Contains(columnIndex))
                {
                    continue;
                }

                if (TryGetPropertyIgnoreCase(row, headers[columnIndex], out JsonElement value) &&
                    matcher.IsMatch(FormatJsonSearchValue(value)))
                {
                    matchingColumnIndexes.Add(columnIndex);
                }
            }
        }

        return matchingColumnIndexes.ToList();
    }

    private static List<string> GetTableDataHeaders(
        DatabaseMetadataSnapshot snapshot,
        SqlTable table,
        SqlTableDataSet dataSet)
    {
        List<string> headers = snapshot.Columns
            .Where(column =>
                string.Equals(column.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(column.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(column => column.Ordinal)
            .Select(column => column.ColumnName)
            .ToList();

        if (headers.Count == 0 && dataSet.Rows.FirstOrDefault().ValueKind == JsonValueKind.Object)
        {
            headers = dataSet.Rows[0]
                .EnumerateObject()
                .Select(property => property.Name)
                .ToList();
        }

        return headers;
    }

    private static void ApplyContentSearchMetadata(
        FileSystemNode node,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget)
    {
        if (searchTarget != ObjectExplorerSearchTarget.Content)
        {
            return;
        }

        node.ContentSearchPattern = matcher.Query;
        node.ContentSearchUseRegex = matcher.UseRegex;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement row, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string FormatJsonSearchValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.ToString()
        };
    }

    private static string? CreateFolderResourceToolTip(string folderPath, bool exists)
    {
        if (!exists || string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        return TryGetGitBranch(folderPath, out string branchName, out string repositoryPath)
            ? string.Equals(NormalizePathForDisplay(folderPath), NormalizePathForDisplay(repositoryPath), StringComparison.OrdinalIgnoreCase)
                ? $"Git branch: {branchName}"
                : $"Git branch: {branchName}{Environment.NewLine}Repository: {repositoryPath}"
            : null;
    }

    private static bool TryGetGitBranch(string folderPath, out string branchName, out string repositoryPath)
    {
        branchName = string.Empty;
        repositoryPath = string.Empty;

        string? currentPath = NormalizePathForDisplay(folderPath);
        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            string dotGitPath = Path.Combine(currentPath, ".git");
            string? gitDirectory = ResolveGitDirectory(dotGitPath, currentPath);
            if (!string.IsNullOrWhiteSpace(gitDirectory))
            {
                repositoryPath = currentPath;
                return TryReadGitHead(gitDirectory, out branchName);
            }

            DirectoryInfo? parent = Directory.GetParent(currentPath);
            currentPath = parent?.FullName;
        }

        return false;
    }

    private static string? ResolveGitDirectory(string dotGitPath, string repositoryPath)
    {
        if (Directory.Exists(dotGitPath))
        {
            return dotGitPath;
        }

        if (!File.Exists(dotGitPath))
        {
            return null;
        }

        try
        {
            string gitFileText = File.ReadLines(dotGitPath).FirstOrDefault() ?? string.Empty;
            const string prefix = "gitdir:";
            if (!gitFileText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string gitDirectory = gitFileText[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(gitDirectory))
            {
                return null;
            }

            return Path.IsPathRooted(gitDirectory)
                ? Path.GetFullPath(gitDirectory)
                : Path.GetFullPath(Path.Combine(repositoryPath, gitDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryReadGitHead(string gitDirectory, out string branchName)
    {
        branchName = string.Empty;

        try
        {
            string headPath = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(headPath))
            {
                return false;
            }

            string head = File.ReadLines(headPath).FirstOrDefault()?.Trim() ?? string.Empty;
            const string refPrefix = "ref: refs/heads/";
            if (head.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
            {
                branchName = head[refPrefix.Length..].Trim();
                return !string.IsNullOrWhiteSpace(branchName);
            }

            if (head.Length >= 7 && head.All(Uri.IsHexDigit))
            {
                branchName = $"detached HEAD {head[..7]}";
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return false;
    }

    private static string NormalizePathForDisplay(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static FileSystemNode CreateDatabaseRoot(
        ScopedResource resource,
        DatabaseSnapshotLibrary? databaseSnapshots,
        bool isScopeResourceLoaded)
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
            parentKey: FileSystemNode.RootParentKey,
            scopeResourceId: resource.ResourceId,
            resourceKind: ResourceKind.DatabaseSnapshot,
            isScopeResourceRoot: true,
            isScopeResourceLoaded: isScopeResourceLoaded);

        if (snapshot == null || !isScopeResourceLoaded)
        {
            root.IsLoaded = true;
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
            parentKey: root.NodeKey,
            resourceKind: ResourceKind.DatabaseSnapshot)
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
                parentKey: folder.NodeKey,
                resourceKind: ResourceKind.DatabaseSnapshot)
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
            parentKey: root.NodeKey,
            resourceKind: ResourceKind.DatabaseSnapshot)
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
                parentKey: folder.NodeKey,
                resourceKind: ResourceKind.DatabaseSnapshot)
            {
                IsVirtualDocument = true
            });
        }

        root.Children.Add(folder);
    }

    private static FileSystemNode? CreateDiagramRoot(
        IEnumerable<ScopedResource> resources,
        DiagramLibrary? diagramLibrary,
        IReadOnlySet<string>? unloadedResourceIds)
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
            parentKey: FileSystemNode.RootParentKey,
            resourceKind: ResourceKind.Diagram)
        {
            IsLoaded = true
        };

        foreach (ScopedResource resource in diagramResources)
        {
            root.Children.Add(CreateDiagramNode(
                resource,
                diagramLibrary?.Find(resource.Path),
                root.NodeKey,
                IsResourceLoaded(resource, unloadedResourceIds)));
        }

        return root;
    }

    private static FileSystemNode? SearchDiagramResources(
        IEnumerable<ScopedResource> resources,
        DiagramLibrary? diagramLibrary,
        SearchMatcher matcher,
        ObjectExplorerSearchTarget searchTarget,
        ref int matchCount,
        ref int searchedCount,
        IReadOnlySet<string>? unloadedResourceIds)
    {
        List<ScopedResource> diagramResources = resources
            .Where(resource => resource.Kind == ResourceKind.Diagram && IsResourceLoaded(resource, unloadedResourceIds))
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
            parentKey: FileSystemNode.RootParentKey,
            resourceKind: ResourceKind.Diagram)
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
            root.Children.Add(CreateDiagramNode(resource, diagram, root.NodeKey, isScopeResourceLoaded: true));
        }

        return rootMatch || root.Children.Count > 0 ? root : null;
    }

    private static FileSystemNode CreateDiagramNode(
        ScopedResource resource,
        DiagramDocument? diagram,
        string parentKey,
        bool isScopeResourceLoaded)
    {
        string displayName = GetDiagramDisplayName(resource, diagram);
        string suffix = diagram == null ? " (missing)" : string.Empty;
        return new FileSystemNode(
            DiagramDocumentService.CreateDiagramDocumentPath(resource.Path),
            isDirectory: false,
            exists: diagram != null,
            displayName: $"{displayName}{suffix}",
            parentKey: parentKey,
            scopeResourceId: resource.ResourceId,
            resourceKind: ResourceKind.Diagram,
            isScopeResourceRoot: true,
            isScopeResourceLoaded: isScopeResourceLoaded)
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

    private static bool IsResourceLoaded(ScopedResource resource, IReadOnlySet<string>? unloadedResourceIds)
    {
        return unloadedResourceIds == null ||
               string.IsNullOrWhiteSpace(resource.ResourceId) ||
               !unloadedResourceIds.Contains(resource.ResourceId);
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

        public string Query => _query;

        public bool UseRegex => _regex != null;

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

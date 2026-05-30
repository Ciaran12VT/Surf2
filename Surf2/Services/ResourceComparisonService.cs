using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services;

public sealed class ResourceComparisonService
{
    private static readonly string[] DatabaseFolderNames =
    [
        "Stored Procedures",
        "Views",
        "Functions",
        "Triggers",
        "Tables"
    ];

    private readonly DatabaseDocumentService _databaseDocumentService = new();

    public ComparisonResource? CreateResourceFromNode(
        FileSystemNode node,
        DatabaseSnapshotLibrary databaseSnapshots,
        bool tableData = false)
    {
        if (!node.IsScopeResourceLoaded)
        {
            return null;
        }

        if (string.Equals(node.FullPath, DiagramDocumentService.DiagramRootPath, StringComparison.OrdinalIgnoreCase) ||
            DiagramDocumentService.IsDiagramDocumentPath(node.FullPath))
        {
            return null;
        }

        if (node.IsVirtualFolder)
        {
            return new ComparisonResource(
                node.Name,
                "Virtual Folder",
                node.VirtualFolderId,
                ComparisonResourceKind.VirtualFolder,
                "VirtualFolder",
                IsCollection: true,
                IsText: false,
                IsTableData: false,
                IdentityKey: $"virtual-folder:{node.VirtualFolderId}",
                ExplorerNode: node);
        }

        if (TryCreateDatabaseResourceFromNode(node, databaseSnapshots, tableData, out ComparisonResource? databaseResource))
        {
            return databaseResource;
        }

        if (node.IsDirectory)
        {
            return new ComparisonResource(
                node.Name,
                "Folder",
                node.FullPath,
                ComparisonResourceKind.Folder,
                "Folder",
                IsCollection: true,
                IsText: false,
                IsTableData: false,
                IdentityKey: $"folder:{NormalizePathKey(node.FullPath)}",
                ExplorerNode: node);
        }

        return new ComparisonResource(
            node.Name,
            "File",
            node.FullPath,
            ComparisonResourceKind.File,
            "File",
            IsCollection: false,
            IsText: true,
            IsTableData: false,
            IdentityKey: $"file:{NormalizePathKey(node.FullPath)}",
            ExplorerNode: node,
            SyntaxPath: node.FullPath);
    }

    public bool CanCompareTableData(FileSystemNode node, DatabaseSnapshotLibrary databaseSnapshots)
    {
        return CreateResourceFromNode(node, databaseSnapshots, tableData: true) is { IsTableData: true };
    }

    public IReadOnlyList<ComparisonResource> CreateCandidates(
        ComparisonResource source,
        Scope scope,
        DatabaseSnapshotLibrary databaseSnapshots,
        IEnumerable<FileSystemNode> rootNodes,
        IReadOnlySet<string> unloadedResourceIds)
    {
        return CreateResources(scope, databaseSnapshots, rootNodes, unloadedResourceIds)
            .Where(resource =>
                string.Equals(resource.ComparisonTypeKey, source.ComparisonTypeKey, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(resource.IdentityKey, source.IdentityKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(resource => resource.TypeDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ResourceCollectionDiffResult BuildCollectionDiff(
        ComparisonResource left,
        ComparisonResource right,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        Dictionary<string, ResourceComparisonDocument> leftDocuments = EnumerateCollectionDocuments(left, databaseSnapshots)
            .GroupBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ResourceComparisonDocument> rightDocuments = EnumerateCollectionDocuments(right, databaseSnapshots)
            .GroupBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        List<string> paths = leftDocuments.Keys
            .Concat(rightDocuments.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new ObservableCollection<ResourceCollectionDiffRow>();
        foreach (string path in paths)
        {
            leftDocuments.TryGetValue(path, out ResourceComparisonDocument? leftDocument);
            rightDocuments.TryGetValue(path, out ResourceComparisonDocument? rightDocument);

            ResourceComparisonStatus status = GetDocumentStatus(leftDocument, rightDocument);
            string leftName = leftDocument?.DisplayName ?? string.Empty;
            string rightName = rightDocument?.DisplayName ?? string.Empty;
            int depth = string.IsNullOrWhiteSpace(path)
                ? 0
                : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1;

            rows.Add(new ResourceCollectionDiffRow(depth, path, leftName, rightName, status, leftDocument, rightDocument));
        }

        foreach (ResourceCollectionDiffRow row in rows.Where(row => row.IsFolder).OrderByDescending(row => row.Depth))
        {
            if (row.Status != ResourceComparisonStatus.Identical)
            {
                continue;
            }

            string prefix = row.RelativePath + "/";
            if (rows.Any(candidate =>
                    candidate.Depth > row.Depth &&
                    candidate.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Status != ResourceComparisonStatus.Identical))
            {
                row.Status = ResourceComparisonStatus.Different;
            }
        }

        return new ResourceCollectionDiffResult(left, right, rows);
    }

    public bool TryGetTextContent(
        ComparisonResource resource,
        DatabaseSnapshotLibrary databaseSnapshots,
        out string content,
        out string syntaxPath,
        out string errorMessage)
    {
        content = string.Empty;
        syntaxPath = string.IsNullOrWhiteSpace(resource.SyntaxPath) ? resource.Path : resource.SyntaxPath;
        errorMessage = string.Empty;

        if (resource.IsTableData)
        {
            errorMessage = "Table data uses the table data comparison screen.";
            return false;
        }

        if (DatabaseDocumentService.IsDatabaseDocumentPath(resource.Path))
        {
            if (_databaseDocumentService.TryGetDocument(
                    resource.Path,
                    databaseSnapshots,
                    out content,
                    out syntaxPath,
                    out _))
            {
                return true;
            }

            errorMessage = "Could not load database metadata document.";
            return false;
        }

        try
        {
            content = File.ReadAllText(resource.Path);
            syntaxPath = resource.Path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errorMessage = $"Could not read {resource.DisplayName}: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<string> GetPreferredTableDataKeyColumns(
        ComparisonResource left,
        ComparisonResource right,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        if (!TryGetTable(left, databaseSnapshots, out DatabaseMetadataSnapshot? leftSnapshot, out SqlTable? leftTable) ||
            !TryGetTable(right, databaseSnapshots, out DatabaseMetadataSnapshot? rightSnapshot, out SqlTable? rightTable))
        {
            return [];
        }

        List<string> leftKeys = leftSnapshot.PrimaryKeys
            .Where(key =>
                string.Equals(key.SchemaName, leftTable.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(key.TableName, leftTable.TableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key.KeyOrdinal)
            .Select(key => key.ColumnName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (leftKeys.Count == 0)
        {
            return [];
        }

        HashSet<string> rightHeaders = GetTableDataHeaders(right, databaseSnapshots).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftKeys.All(rightHeaders.Contains) ? leftKeys : [];
    }

    public IReadOnlyList<string> GetCommonTableDataColumns(
        ComparisonResource left,
        ComparisonResource right,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        HashSet<string> rightHeaders = GetTableDataHeaders(right, databaseSnapshots).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetTableDataHeaders(left, databaseSnapshots)
            .Where(rightHeaders.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(column => column, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TableDataDiffResult BuildTableDataDiff(
        ComparisonResource left,
        ComparisonResource right,
        IReadOnlyList<string> keyColumns,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        if (!TryGetTableData(left, databaseSnapshots, out _, out _, out SqlTableDataSet? leftDataSet) ||
            !TryGetTableData(right, databaseSnapshots, out _, out _, out SqlTableDataSet? rightDataSet))
        {
            return new TableDataDiffResult(left, right, keyColumns, []);
        }

        List<string> headers = GetTableDataHeaders(left, databaseSnapshots)
            .Concat(GetTableDataHeaders(right, databaseSnapshots))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(header => header, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<string, JsonElement> leftRows = CreateRowIndex(leftDataSet, keyColumns);
        Dictionary<string, JsonElement> rightRows = CreateRowIndex(rightDataSet, keyColumns);
        List<string> allKeys = leftRows.Keys
            .Concat(rightRows.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new ObservableCollection<TableDataDiffRow>();
        foreach (string key in allKeys)
        {
            bool hasLeft = leftRows.TryGetValue(key, out JsonElement leftRow);
            bool hasRight = rightRows.TryGetValue(key, out JsonElement rightRow);
            if (!hasLeft)
            {
                rows.Add(new TableDataDiffRow(
                    key,
                    TableDataDiffStatus.Added,
                    string.Empty,
                    string.Empty,
                    CreateRowPreview(rightRow, headers)));
                continue;
            }

            if (!hasRight)
            {
                rows.Add(new TableDataDiffRow(
                    key,
                    TableDataDiffStatus.Removed,
                    string.Empty,
                    CreateRowPreview(leftRow, headers),
                    string.Empty));
                continue;
            }

            List<string> changedColumns = headers
                .Where(header => !string.Equals(GetJsonValue(leftRow, header), GetJsonValue(rightRow, header), StringComparison.Ordinal))
                .ToList();

            rows.Add(new TableDataDiffRow(
                key,
                changedColumns.Count == 0 ? TableDataDiffStatus.Identical : TableDataDiffStatus.Altered,
                string.Join(", ", changedColumns),
                CreateRowPreview(leftRow, headers),
                CreateRowPreview(rightRow, headers)));
        }

        return new TableDataDiffResult(left, right, keyColumns, rows);
    }

    private bool TryCreateDatabaseResourceFromNode(
        FileSystemNode node,
        DatabaseSnapshotLibrary databaseSnapshots,
        bool tableData,
        out ComparisonResource? resource)
    {
        resource = null;

        DatabaseMetadataSnapshot? snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, node.FullPath, StringComparison.OrdinalIgnoreCase));
        if (snapshot != null && node.IsScopeResourceRoot)
        {
            resource = new ComparisonResource(
                node.Name,
                "Database Snapshot",
                snapshot.SnapshotId,
                ComparisonResourceKind.DatabaseSnapshot,
                "DatabaseSnapshot",
                IsCollection: true,
                IsText: false,
                IsTableData: false,
                IdentityKey: $"database:{snapshot.SnapshotId}",
                ExplorerNode: node,
                SnapshotId: snapshot.SnapshotId);
            return true;
        }

        if (node.IsDirectory && TryGetDatabaseFolder(node.FullPath, databaseSnapshots, out snapshot, out string folderName))
        {
            resource = new ComparisonResource(
                folderName,
                folderName,
                node.FullPath,
                ComparisonResourceKind.DatabaseObjectFolder,
                $"DatabaseFolder:{folderName}",
                IsCollection: true,
                IsText: false,
                IsTableData: false,
                IdentityKey: $"database-folder:{snapshot.SnapshotId}:{folderName}",
                ExplorerNode: node,
                SnapshotId: snapshot.SnapshotId,
                DatabaseFolderName: folderName);
            return true;
        }

        if (!DatabaseDocumentService.TryParseDocumentPath(node.FullPath, out DatabaseDocumentReference reference))
        {
            return false;
        }

        if (tableData)
        {
            if (!string.Equals(reference.DocumentType, "table", StringComparison.OrdinalIgnoreCase) ||
                !TryGetTableFromReference(reference, databaseSnapshots, out snapshot, out SqlTable? table) ||
                !table.HasFullData ||
                snapshot.GetTableDataSet(table.SchemaName, table.TableName) == null)
            {
                return false;
            }

            string tableDataPath = DatabaseDocumentService.CreateTableDataDocumentPath(snapshot, table);
            resource = CreateTableDataResource(snapshot, table, tableDataPath, node);
            return true;
        }

        if (string.Equals(reference.DocumentType, "table", StringComparison.OrdinalIgnoreCase) &&
            TryGetTableFromReference(reference, databaseSnapshots, out snapshot, out SqlTable? tableMetadata))
        {
            resource = CreateTableMetadataResource(snapshot, tableMetadata, node.FullPath, node);
            return true;
        }

        if (string.Equals(reference.DocumentType, "object", StringComparison.OrdinalIgnoreCase) &&
            reference.ObjectKind.HasValue)
        {
            resource = CreateDatabaseObjectResource(reference, node);
            return true;
        }

        return false;
    }

    private IEnumerable<ComparisonResource> CreateResources(
        Scope scope,
        DatabaseSnapshotLibrary databaseSnapshots,
        IEnumerable<FileSystemNode> rootNodes,
        IReadOnlySet<string> unloadedResourceIds)
    {
        foreach (ScopedResource scopedResource in scope.Resources)
        {
            if (!IsResourceLoaded(scopedResource, unloadedResourceIds))
            {
                continue;
            }

            if (scopedResource.Kind == ResourceKind.File)
            {
                if (File.Exists(scopedResource.Path))
                {
                    yield return CreateFileResource(scopedResource.Path, scopedResource.DisplayName);
                }

                continue;
            }

            if (scopedResource.Kind == ResourceKind.Folder)
            {
                if (!Directory.Exists(scopedResource.Path))
                {
                    continue;
                }

                yield return CreateFolderResource(scopedResource.Path, scopedResource.DisplayName);
                foreach (string directory in EnumerateDirectoriesSafely(scopedResource.Path))
                {
                    yield return CreateFolderResource(directory, GetFileSystemDisplayName(directory));
                }

                foreach (string file in EnumerateFilesSafely(scopedResource.Path))
                {
                    yield return CreateFileResource(file, GetFileSystemDisplayName(file));
                }

                continue;
            }

            if (scopedResource.Kind == ResourceKind.DatabaseSnapshot)
            {
                DatabaseMetadataSnapshot? snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
                    string.Equals(candidate.SnapshotId, scopedResource.Path, StringComparison.OrdinalIgnoreCase));
                if (snapshot == null)
                {
                    continue;
                }

                yield return new ComparisonResource(
                    string.IsNullOrWhiteSpace(scopedResource.DisplayNameOverride) ? snapshot.DisplayName : scopedResource.DisplayNameOverride,
                    "Database Snapshot",
                    snapshot.SnapshotId,
                    ComparisonResourceKind.DatabaseSnapshot,
                    "DatabaseSnapshot",
                    IsCollection: true,
                    IsText: false,
                    IsTableData: false,
                    IdentityKey: $"database:{snapshot.SnapshotId}",
                    SnapshotId: snapshot.SnapshotId);

                foreach (string folderName in DatabaseFolderNames)
                {
                    yield return new ComparisonResource(
                        folderName,
                        folderName,
                        $"{snapshot.SnapshotId}/{folderName}",
                        ComparisonResourceKind.DatabaseObjectFolder,
                        $"DatabaseFolder:{folderName}",
                        IsCollection: true,
                        IsText: false,
                        IsTableData: false,
                        IdentityKey: $"database-folder:{snapshot.SnapshotId}:{folderName}",
                        SnapshotId: snapshot.SnapshotId,
                        DatabaseFolderName: folderName);
                }

                foreach (SqlDatabaseObject databaseObject in snapshot.Objects)
                {
                    string documentPath = DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject);
                    yield return CreateDatabaseObjectResource(
                        new DatabaseDocumentReference(snapshot.SnapshotId, "object", databaseObject.Kind, SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName)),
                        documentPath,
                        SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName));
                }

                foreach (SqlTable table in snapshot.Tables)
                {
                    string tablePath = DatabaseDocumentService.CreateTableDocumentPath(snapshot, table);
                    yield return CreateTableMetadataResource(snapshot, table, tablePath);

                    if (table.HasFullData && snapshot.GetTableDataSet(table.SchemaName, table.TableName) != null)
                    {
                        yield return CreateTableDataResource(snapshot, table, DatabaseDocumentService.CreateTableDataDocumentPath(snapshot, table));
                    }
                }
            }
        }

        foreach (FileSystemNode virtualFolderNode in TraverseNodes(rootNodes).Where(node => node.IsVirtualFolder))
        {
            yield return new ComparisonResource(
                virtualFolderNode.Name,
                "Virtual Folder",
                virtualFolderNode.VirtualFolderId,
                ComparisonResourceKind.VirtualFolder,
                "VirtualFolder",
                IsCollection: true,
                IsText: false,
                IsTableData: false,
                IdentityKey: $"virtual-folder:{virtualFolderNode.VirtualFolderId}",
                ExplorerNode: virtualFolderNode);
        }
    }

    private IEnumerable<ResourceComparisonDocument> EnumerateCollectionDocuments(
        ComparisonResource resource,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        if (resource.Kind == ComparisonResourceKind.Folder)
        {
            foreach (ResourceComparisonDocument document in EnumerateFileSystemFolderDocuments(resource.Path, string.Empty))
            {
                yield return document;
            }

            yield break;
        }

        if (resource.Kind == ComparisonResourceKind.VirtualFolder && resource.ExplorerNode != null)
        {
            foreach (FileSystemNode child in resource.ExplorerNode.Children)
            {
                foreach (ResourceComparisonDocument document in EnumerateNodeDocuments(child, SanitizeRelativeSegment(child.Name), databaseSnapshots))
                {
                    yield return document;
                }
            }

            yield break;
        }

        if (resource.Kind == ComparisonResourceKind.DatabaseSnapshot &&
            TryGetSnapshot(resource.SnapshotId, out DatabaseMetadataSnapshot? snapshot))
        {
            foreach (ResourceComparisonDocument document in EnumerateDatabaseSnapshotDocuments(snapshot!, includeFolderPrefix: true, folderName: null, databaseSnapshots))
            {
                yield return document;
            }

            yield break;
        }

        if (resource.Kind == ComparisonResourceKind.DatabaseObjectFolder &&
            TryGetSnapshot(resource.SnapshotId, out snapshot))
        {
            foreach (ResourceComparisonDocument document in EnumerateDatabaseSnapshotDocuments(snapshot!, includeFolderPrefix: false, resource.DatabaseFolderName, databaseSnapshots))
            {
                yield return document;
            }
        }

        bool TryGetSnapshot(string snapshotId, out DatabaseMetadataSnapshot? snapshot)
        {
            snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
            return snapshot != null;
        }
    }

    private IEnumerable<ResourceComparisonDocument> EnumerateNodeDocuments(
        FileSystemNode node,
        string relativePath,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        if (CreateResourceFromNode(node, databaseSnapshots) is { } nodeResource)
        {
            if (nodeResource.IsCollection)
            {
                yield return new ResourceComparisonDocument(
                    relativePath,
                    node.Name,
                    nodeResource.Kind,
                    IsCollection: true,
                    Content: string.Empty,
                    SyntaxPath: string.Empty,
                    nodeResource);

                foreach (ResourceComparisonDocument document in EnumerateCollectionDocuments(nodeResource, databaseSnapshots))
                {
                    yield return document with
                    {
                        RelativePath = CombineRelativePath(relativePath, document.RelativePath)
                    };
                }

                yield break;
            }

            if (TryGetDocumentContentForResource(nodeResource, databaseSnapshots, out string content, out string syntaxPath))
            {
                yield return new ResourceComparisonDocument(
                    relativePath,
                    node.Name,
                    nodeResource.Kind,
                    IsCollection: false,
                    content,
                    syntaxPath,
                    nodeResource);
            }
        }
    }

    private IEnumerable<ResourceComparisonDocument> EnumerateFileSystemFolderDocuments(string folderPath, string prefix)
    {
        foreach (string directory in EnumerateImmediateDirectoriesSafely(folderPath).OrderBy(Path.GetFileName))
        {
            string relativePath = CombineRelativePath(prefix, GetFileSystemDisplayName(directory));
            ComparisonResource folderResource = CreateFolderResource(directory, GetFileSystemDisplayName(directory));
            yield return new ResourceComparisonDocument(
                relativePath,
                GetFileSystemDisplayName(directory),
                ComparisonResourceKind.Folder,
                IsCollection: true,
                Content: string.Empty,
                SyntaxPath: string.Empty,
                folderResource);

            foreach (ResourceComparisonDocument document in EnumerateFileSystemFolderDocuments(directory, relativePath))
            {
                yield return document;
            }
        }

        foreach (string file in EnumerateImmediateFilesSafely(folderPath).OrderBy(Path.GetFileName))
        {
            ComparisonResource fileResource = CreateFileResource(file, GetFileSystemDisplayName(file));
            string content = TryReadText(file, out string fileContent)
                ? fileContent
                : $"Could not read {file}.";
            yield return new ResourceComparisonDocument(
                CombineRelativePath(prefix, GetFileSystemDisplayName(file)),
                GetFileSystemDisplayName(file),
                ComparisonResourceKind.File,
                IsCollection: false,
                content,
                file,
                fileResource);
        }
    }

    private IEnumerable<ResourceComparisonDocument> EnumerateDatabaseSnapshotDocuments(
        DatabaseMetadataSnapshot snapshot,
        bool includeFolderPrefix,
        string? folderName,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        if (folderName == null || string.Equals(folderName, "Stored Procedures", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ResourceComparisonDocument document in EnumerateDatabaseObjectDocuments(snapshot, "Stored Procedures", SqlDatabaseObjectKind.StoredProcedure, includeFolderPrefix))
            {
                yield return document;
            }
        }

        if (folderName == null || string.Equals(folderName, "Views", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ResourceComparisonDocument document in EnumerateDatabaseObjectDocuments(snapshot, "Views", SqlDatabaseObjectKind.View, includeFolderPrefix))
            {
                yield return document;
            }
        }

        if (folderName == null || string.Equals(folderName, "Functions", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ResourceComparisonDocument document in EnumerateDatabaseObjectDocuments(snapshot, "Functions", SqlDatabaseObjectKind.Function, includeFolderPrefix))
            {
                yield return document;
            }
        }

        if (folderName == null || string.Equals(folderName, "Triggers", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ResourceComparisonDocument document in EnumerateDatabaseObjectDocuments(snapshot, "Triggers", SqlDatabaseObjectKind.Trigger, includeFolderPrefix))
            {
                yield return document;
            }
        }

        if (folderName == null || string.Equals(folderName, "Tables", StringComparison.OrdinalIgnoreCase))
        {
            string tableFolder = includeFolderPrefix ? "Tables" : string.Empty;
            if (includeFolderPrefix)
            {
                yield return CreateDatabaseFolderDocument("Tables");
            }

            foreach (SqlTable table in snapshot.Tables.OrderBy(table => table.SchemaName).ThenBy(table => table.TableName))
            {
                string tablePath = DatabaseDocumentService.CreateTableDocumentPath(snapshot, table);
                ComparisonResource tableResource = CreateTableMetadataResource(snapshot, table, tablePath);
                _databaseDocumentService.TryGetDocument(tablePath, databaseSnapshots, out string content, out string syntaxPath, out _);
                yield return new ResourceComparisonDocument(
                    CombineRelativePath(tableFolder, $"{SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName)}.sql"),
                    SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName),
                    ComparisonResourceKind.TableMetadata,
                    IsCollection: false,
                    content,
                    syntaxPath,
                    tableResource);
            }

            List<SqlTable> fullDataTables = snapshot.Tables
                .Where(table => table.HasFullData && snapshot.GetTableDataSet(table.SchemaName, table.TableName) != null)
                .OrderBy(table => table.SchemaName)
                .ThenBy(table => table.TableName)
                .ToList();

            if (folderName == null && fullDataTables.Count > 0)
            {
                string dataFolder = "Full Table Data";
                yield return CreateDatabaseFolderDocument(dataFolder);
                foreach (SqlTable table in fullDataTables)
                {
                    string tableDataPath = DatabaseDocumentService.CreateTableDataDocumentPath(snapshot, table);
                    ComparisonResource tableDataResource = CreateTableDataResource(snapshot, table, tableDataPath);
                    _databaseDocumentService.TryGetSpreadsheetDocument(tableDataPath, databaseSnapshots, out string content, out _);
                    yield return new ResourceComparisonDocument(
                        CombineRelativePath(dataFolder, $"{SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName)}.csv"),
                        $"{SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName)} data",
                        ComparisonResourceKind.TableData,
                        IsCollection: false,
                        content,
                        tableDataPath,
                        tableDataResource);
                }
            }
        }

        ResourceComparisonDocument CreateDatabaseFolderDocument(string name)
        {
            return new ResourceComparisonDocument(
                name,
                name,
                ComparisonResourceKind.DatabaseObjectFolder,
                IsCollection: true,
                Content: string.Empty,
                SyntaxPath: string.Empty,
                new ComparisonResource(
                    name,
                    name,
                    $"{snapshot.SnapshotId}/{name}",
                    ComparisonResourceKind.DatabaseObjectFolder,
                    $"DatabaseFolder:{name}",
                    IsCollection: true,
                    IsText: false,
                    IsTableData: false,
                    IdentityKey: $"database-folder:{snapshot.SnapshotId}:{name}",
                    SnapshotId: snapshot.SnapshotId,
                    DatabaseFolderName: name));
        }
    }

    private IEnumerable<ResourceComparisonDocument> EnumerateDatabaseObjectDocuments(
        DatabaseMetadataSnapshot snapshot,
        string folderName,
        SqlDatabaseObjectKind kind,
        bool includeFolderPrefix)
    {
        if (includeFolderPrefix)
        {
            yield return new ResourceComparisonDocument(
                folderName,
                folderName,
                ComparisonResourceKind.DatabaseObjectFolder,
                IsCollection: true,
                Content: string.Empty,
                SyntaxPath: string.Empty,
                new ComparisonResource(
                    folderName,
                    folderName,
                    $"{snapshot.SnapshotId}/{folderName}",
                    ComparisonResourceKind.DatabaseObjectFolder,
                    $"DatabaseFolder:{folderName}",
                    IsCollection: true,
                    IsText: false,
                    IsTableData: false,
                    IdentityKey: $"database-folder:{snapshot.SnapshotId}:{folderName}",
                    SnapshotId: snapshot.SnapshotId,
                    DatabaseFolderName: folderName));
        }

        string prefix = includeFolderPrefix ? folderName : string.Empty;
        foreach (SqlDatabaseObject databaseObject in snapshot.Objects
                     .Where(item => item.Kind == kind)
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.ObjectName))
        {
            string fullName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
            string documentPath = DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject);
            ComparisonResource objectResource = CreateDatabaseObjectResource(
                new DatabaseDocumentReference(snapshot.SnapshotId, "object", databaseObject.Kind, fullName),
                documentPath,
                fullName);
            yield return new ResourceComparisonDocument(
                CombineRelativePath(prefix, $"{fullName}.sql"),
                fullName,
                objectResource.Kind,
                IsCollection: false,
                databaseObject.Definition,
                "document.sql",
                objectResource);
        }
    }

    private bool TryGetDocumentContentForResource(
        ComparisonResource resource,
        DatabaseSnapshotLibrary databaseSnapshots,
        out string content,
        out string syntaxPath)
    {
        content = string.Empty;
        syntaxPath = resource.SyntaxPath;
        if (resource.IsTableData)
        {
            return _databaseDocumentService.TryGetSpreadsheetDocument(
                resource.Path,
                databaseSnapshots,
                out content,
                out _);
        }

        return TryGetTextContent(resource, databaseSnapshots, out content, out syntaxPath, out _);
    }

    private ResourceComparisonStatus GetDocumentStatus(
        ResourceComparisonDocument? left,
        ResourceComparisonDocument? right)
    {
        if (left == null)
        {
            return ResourceComparisonStatus.MissingLeft;
        }

        if (right == null)
        {
            return ResourceComparisonStatus.MissingRight;
        }

        if (left.IsCollection || right.IsCollection)
        {
            return left.IsCollection == right.IsCollection
                ? ResourceComparisonStatus.Identical
                : ResourceComparisonStatus.Different;
        }

        return string.Equals(left.Content, right.Content, StringComparison.Ordinal)
            ? ResourceComparisonStatus.Identical
            : ResourceComparisonStatus.Different;
    }

    private static ComparisonResource CreateFileResource(string path, string displayName)
    {
        return new ComparisonResource(
            displayName,
            "File",
            path,
            ComparisonResourceKind.File,
            "File",
            IsCollection: false,
            IsText: true,
            IsTableData: false,
            IdentityKey: $"file:{NormalizePathKey(path)}",
            SyntaxPath: path);
    }

    private static ComparisonResource CreateFolderResource(string path, string displayName)
    {
        return new ComparisonResource(
            displayName,
            "Folder",
            path,
            ComparisonResourceKind.Folder,
            "Folder",
            IsCollection: true,
            IsText: false,
            IsTableData: false,
            IdentityKey: $"folder:{NormalizePathKey(path)}");
    }

    private static ComparisonResource CreateDatabaseObjectResource(DatabaseDocumentReference reference, FileSystemNode node)
    {
        return CreateDatabaseObjectResource(reference, node.FullPath, node.Name, node);
    }

    private static ComparisonResource CreateDatabaseObjectResource(
        DatabaseDocumentReference reference,
        string path,
        string displayName,
        FileSystemNode? node = null)
    {
        ComparisonResourceKind kind = reference.ObjectKind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => ComparisonResourceKind.StoredProcedure,
            SqlDatabaseObjectKind.View => ComparisonResourceKind.View,
            SqlDatabaseObjectKind.Function => ComparisonResourceKind.Function,
            SqlDatabaseObjectKind.Trigger => ComparisonResourceKind.Trigger,
            _ => ComparisonResourceKind.File
        };

        string typeDisplay = reference.ObjectKind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => "Stored Procedure",
            SqlDatabaseObjectKind.View => "View",
            SqlDatabaseObjectKind.Function => "Function",
            SqlDatabaseObjectKind.Trigger => "Trigger",
            _ => "Database Object"
        };

        return new ComparisonResource(
            displayName,
            typeDisplay,
            path,
            kind,
            $"DatabaseObject:{reference.ObjectKind}",
            IsCollection: false,
            IsText: true,
            IsTableData: false,
            IdentityKey: $"database-object:{reference.SnapshotId}:{reference.ObjectKind}:{reference.FullName}",
            ExplorerNode: node,
            SnapshotId: reference.SnapshotId,
            DatabaseObjectKind: reference.ObjectKind,
            DatabaseObjectName: reference.FullName,
            SyntaxPath: "document.sql");
    }

    private static ComparisonResource CreateTableMetadataResource(
        DatabaseMetadataSnapshot snapshot,
        SqlTable table,
        string path,
        FileSystemNode? node = null)
    {
        string fullName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
        return new ComparisonResource(
            fullName,
            "Table Metadata",
            path,
            ComparisonResourceKind.TableMetadata,
            "DatabaseObject:TableMetadata",
            IsCollection: false,
            IsText: true,
            IsTableData: false,
            IdentityKey: $"table-metadata:{snapshot.SnapshotId}:{fullName}",
            ExplorerNode: node,
            SnapshotId: snapshot.SnapshotId,
            TableSchemaName: table.SchemaName,
            TableName: table.TableName,
            SyntaxPath: "document.sql");
    }

    private static ComparisonResource CreateTableDataResource(
        DatabaseMetadataSnapshot snapshot,
        SqlTable table,
        string path,
        FileSystemNode? node = null)
    {
        string fullName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
        return new ComparisonResource(
            $"{fullName} data",
            "Table Data",
            path,
            ComparisonResourceKind.TableData,
            "DatabaseObject:TableData",
            IsCollection: false,
            IsText: false,
            IsTableData: true,
            IdentityKey: $"table-data:{snapshot.SnapshotId}:{fullName}",
            ExplorerNode: node,
            SnapshotId: snapshot.SnapshotId,
            TableSchemaName: table.SchemaName,
            TableName: table.TableName,
            SyntaxPath: path);
    }

    private static bool IsResourceLoaded(ScopedResource resource, IReadOnlySet<string> unloadedResourceIds)
    {
        return string.IsNullOrWhiteSpace(resource.ResourceId) ||
               !unloadedResourceIds.Contains(resource.ResourceId);
    }

    private static bool TryGetDatabaseFolder(
        string path,
        DatabaseSnapshotLibrary databaseSnapshots,
        out DatabaseMetadataSnapshot snapshot,
        out string folderName)
    {
        snapshot = null!;
        folderName = string.Empty;
        foreach (DatabaseMetadataSnapshot candidate in databaseSnapshots.Snapshots)
        {
            foreach (string name in DatabaseFolderNames)
            {
                if (string.Equals(path, $"{candidate.SnapshotId}/{name}", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot = candidate;
                    folderName = name;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetTableFromReference(
        DatabaseDocumentReference reference,
        DatabaseSnapshotLibrary databaseSnapshots,
        out DatabaseMetadataSnapshot snapshot,
        out SqlTable table)
    {
        snapshot = null!;
        table = null!;
        snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, reference.SnapshotId, StringComparison.OrdinalIgnoreCase))!;
        if (snapshot == null)
        {
            return false;
        }

        table = snapshot.Tables.FirstOrDefault(candidate =>
            string.Equals(SqlName.FormatPlainMultipartName(candidate.SchemaName, candidate.TableName), reference.FullName, StringComparison.OrdinalIgnoreCase))!;
        return table != null;
    }

    private static bool TryGetTable(
        ComparisonResource resource,
        DatabaseSnapshotLibrary databaseSnapshots,
        out DatabaseMetadataSnapshot snapshot,
        out SqlTable table)
    {
        snapshot = null!;
        table = null!;
        snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, resource.SnapshotId, StringComparison.OrdinalIgnoreCase))!;
        if (snapshot == null)
        {
            return false;
        }

        table = snapshot.Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.SchemaName, resource.TableSchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, resource.TableName, StringComparison.OrdinalIgnoreCase))!;
        return table != null;
    }

    private static bool TryGetTableData(
        ComparisonResource resource,
        DatabaseSnapshotLibrary databaseSnapshots,
        out DatabaseMetadataSnapshot snapshot,
        out SqlTable table,
        out SqlTableDataSet dataSet)
    {
        dataSet = null!;
        if (!TryGetTable(resource, databaseSnapshots, out snapshot, out table))
        {
            return false;
        }

        dataSet = snapshot.GetTableDataSet(table.SchemaName, table.TableName)!;
        return dataSet != null;
    }

    private static IReadOnlyList<string> GetTableDataHeaders(
        ComparisonResource resource,
        DatabaseSnapshotLibrary databaseSnapshots)
    {
        if (!TryGetTableData(resource, databaseSnapshots, out DatabaseMetadataSnapshot snapshot, out SqlTable table, out SqlTableDataSet dataSet))
        {
            return [];
        }

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

    private static Dictionary<string, JsonElement> CreateRowIndex(
        SqlTableDataSet dataSet,
        IReadOnlyList<string> keyColumns)
    {
        var rows = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        int duplicate = 2;
        foreach (JsonElement row in dataSet.Rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string key = string.Join(" | ", keyColumns.Select(column => GetJsonValue(row, column)));
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "(blank key)";
            }

            string uniqueKey = key;
            while (rows.ContainsKey(uniqueKey))
            {
                uniqueKey = $"{key} #{duplicate++}";
            }

            rows[uniqueKey] = row;
        }

        return rows;
    }

    private static string CreateRowPreview(JsonElement row, IReadOnlyList<string> headers)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        string preview = string.Join("; ", headers.Select(header => $"{header}={GetJsonValue(row, header)}"));
        return preview.Length <= 800 ? preview : preview[..800] + "...";
    }

    private static string GetJsonValue(JsonElement row, string propertyName)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (JsonProperty property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    _ => property.Value.ToString()
                };
            }
        }

        return string.Empty;
    }

    private static IEnumerable<FileSystemNode> TraverseNodes(IEnumerable<FileSystemNode> nodes)
    {
        foreach (FileSystemNode node in nodes)
        {
            yield return node;
            foreach (FileSystemNode child in TraverseNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafely(string folderPath)
    {
        List<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(folderPath).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            yield break;
        }

        foreach (string directory in directories)
        {
            yield return directory;
            foreach (string child in EnumerateDirectoriesSafely(directory))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<string> EnumerateImmediateDirectoriesSafely(string folderPath)
    {
        try
        {
            return Directory.EnumerateDirectories(folderPath).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateImmediateFilesSafely(string folderPath)
    {
        try
        {
            return Directory.EnumerateFiles(folderPath).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string folderPath)
    {
        List<string> files;
        List<string> directories;
        try
        {
            files = Directory.EnumerateFiles(folderPath).ToList();
            directories = Directory.EnumerateDirectories(folderPath).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            yield return file;
        }

        foreach (string directory in directories)
        {
            foreach (string file in EnumerateFilesSafely(directory))
            {
                yield return file;
            }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string GetFileSystemDisplayName(string path)
    {
        string name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string CombineRelativePath(string prefix, string segment)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return segment.Replace('\\', '/');
        }

        if (string.IsNullOrWhiteSpace(segment))
        {
            return prefix.Replace('\\', '/');
        }

        return $"{prefix.TrimEnd('/', '\\')}/{segment.TrimStart('/', '\\')}".Replace('\\', '/');
    }

    private static string SanitizeRelativeSegment(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(unnamed)"
            : value.Replace('/', '_').Replace('\\', '_');
    }
}

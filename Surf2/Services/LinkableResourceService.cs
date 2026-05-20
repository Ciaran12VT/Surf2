using System.IO;
using Surf2.Models;

namespace Surf2.Services;

public sealed class LinkableResourceService
{
    public IReadOnlyList<LinkableResource> CreateLinkableResources(
        Scope scope,
        DatabaseSnapshotLibrary databaseSnapshots,
        DiagramLibrary diagramLibrary)
    {
        var resources = new List<LinkableResource>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ScopedResource scopedResource in scope.Resources)
        {
            switch (scopedResource.Kind)
            {
                case ResourceKind.Folder:
                    AddFolderResource(scopedResource, resources, addedPaths);
                    break;

                case ResourceKind.File:
                case ResourceKind.Project:
                    AddFileOrProjectResource(scopedResource, resources, addedPaths);
                    break;

                case ResourceKind.DatabaseSnapshot:
                    AddDatabaseResource(scopedResource, databaseSnapshots, resources, addedPaths);
                    break;

                case ResourceKind.Diagram:
                    AddDiagramResource(scopedResource, diagramLibrary, resources, addedPaths);
                    break;
            }
        }

        return resources
            .OrderBy(resource => resource.Type)
            .ThenBy(resource => resource.Name)
            .ThenBy(resource => resource.Path)
            .ToList();
    }

    private static void AddFolderResource(
        ScopedResource scopedResource,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        if (!Directory.Exists(scopedResource.Path))
        {
            return;
        }

        AddFolder(scopedResource.Path, scopedResource.DisplayName, resources, addedPaths);
        if (!scopedResource.IncludeChildren)
        {
            return;
        }

        AddFolderContents(scopedResource.Path, resources, addedPaths);
    }

    private static void AddFolderContents(
        string folderPath,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        foreach (string directoryPath in EnumerateDirectoriesSafely(folderPath).OrderBy(Path.GetFileName))
        {
            AddFolder(directoryPath, GetFileSystemDisplayName(directoryPath), resources, addedPaths);
            AddFolderContents(directoryPath, resources, addedPaths);
        }

        foreach (string filePath in EnumerateFilesSafely(folderPath).OrderBy(Path.GetFileName))
        {
            AddFile(filePath, GetFileSystemDisplayName(filePath), "File", resources, addedPaths);
        }
    }

    private static void AddFileOrProjectResource(
        ScopedResource scopedResource,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        if (Directory.Exists(scopedResource.Path))
        {
            AddFolder(scopedResource.Path, scopedResource.DisplayName, resources, addedPaths);
            if (scopedResource.IncludeChildren)
            {
                AddFolderContents(scopedResource.Path, resources, addedPaths);
            }

            return;
        }

        if (!File.Exists(scopedResource.Path))
        {
            return;
        }

        string type = scopedResource.Kind == ResourceKind.Project ? "Project" : "File";
        AddFile(scopedResource.Path, scopedResource.DisplayName, type, resources, addedPaths);
    }

    private static void AddDatabaseResource(
        ScopedResource scopedResource,
        DatabaseSnapshotLibrary databaseSnapshots,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        DatabaseMetadataSnapshot? snapshot = databaseSnapshots.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, scopedResource.Path, StringComparison.OrdinalIgnoreCase));
        if (snapshot == null)
        {
            return;
        }

        string displayName = !string.IsNullOrWhiteSpace(scopedResource.DisplayNameOverride)
            ? scopedResource.DisplayNameOverride
            : snapshot.DisplayName;
        AddResource(resources, addedPaths, new LinkableResource(
            displayName,
            "Database",
            scopedResource.Path,
            LinkableResourceKind.Database));

        foreach (SqlDatabaseObject databaseObject in snapshot.Objects
                     .OrderBy(item => item.Kind)
                     .ThenBy(item => item.SchemaName)
                     .ThenBy(item => item.ObjectName))
        {
            string name = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
            AddResource(resources, addedPaths, new LinkableResource(
                name,
                GetSqlObjectTypeDisplay(databaseObject.Kind),
                DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject),
                GetLinkableKind(databaseObject.Kind)));
        }

        foreach (SqlTable table in snapshot.Tables
                     .OrderBy(item => item.SchemaName)
                     .ThenBy(item => item.TableName))
        {
            string name = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
            AddResource(resources, addedPaths, new LinkableResource(
                name,
                "Table",
                DatabaseDocumentService.CreateTableDocumentPath(snapshot, table),
                LinkableResourceKind.Table));
        }
    }

    private static void AddDiagramResource(
        ScopedResource scopedResource,
        DiagramLibrary diagramLibrary,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        DiagramDocument? diagram = diagramLibrary.Find(scopedResource.Path);
        if (diagram == null)
        {
            return;
        }

        string displayName = !string.IsNullOrWhiteSpace(scopedResource.DisplayNameOverride)
            ? scopedResource.DisplayNameOverride
            : diagram.Name;
        AddResource(resources, addedPaths, new LinkableResource(
            displayName,
            "Diagram",
            DiagramDocumentService.CreateDiagramDocumentPath(scopedResource.Path),
            LinkableResourceKind.Diagram));
    }

    private static void AddFolder(
        string folderPath,
        string displayName,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        AddResource(resources, addedPaths, new LinkableResource(
            displayName,
            "Folder",
            folderPath,
            LinkableResourceKind.Folder));
    }

    private static void AddFile(
        string filePath,
        string displayName,
        string fallbackType,
        List<LinkableResource> resources,
        HashSet<string> addedPaths)
    {
        string extension = Path.GetExtension(filePath);
        string type = string.IsNullOrWhiteSpace(extension)
            ? fallbackType
            : extension.TrimStart('.').ToUpperInvariant();
        AddResource(resources, addedPaths, new LinkableResource(
            displayName,
            type,
            filePath,
            LinkableResourceKind.File));
    }

    private static void AddResource(
        List<LinkableResource> resources,
        HashSet<string> addedPaths,
        LinkableResource resource)
    {
        string key = $"{resource.Kind}|{resource.Path}";
        if (addedPaths.Add(key))
        {
            resources.Add(resource);
        }
    }

    private static string GetSqlObjectTypeDisplay(SqlDatabaseObjectKind kind)
    {
        return kind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => "Stored Procedure",
            SqlDatabaseObjectKind.View => "View",
            SqlDatabaseObjectKind.Function => "Function",
            SqlDatabaseObjectKind.Trigger => "Trigger",
            _ => "Database Object"
        };
    }

    private static LinkableResourceKind GetLinkableKind(SqlDatabaseObjectKind kind)
    {
        return kind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => LinkableResourceKind.StoredProcedure,
            SqlDatabaseObjectKind.View => LinkableResourceKind.View,
            SqlDatabaseObjectKind.Function => LinkableResourceKind.Function,
            SqlDatabaseObjectKind.Trigger => LinkableResourceKind.Trigger,
            _ => LinkableResourceKind.File
        };
    }

    private static string GetFileSystemDisplayName(string path)
    {
        string name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafely(string folderPath)
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

    private static IEnumerable<string> EnumerateFilesSafely(string folderPath)
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
}

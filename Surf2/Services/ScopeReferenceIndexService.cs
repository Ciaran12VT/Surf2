using System.IO;
using Surf2.Models;

namespace Surf2.Services;

public sealed class ScopeReferenceIndexService
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules"
    };

    private readonly List<IReferenceDefinitionParser> _parsers =
    [
        new CSharpReferenceDefinitionParser(),
        new VisualBasicReferenceDefinitionParser(),
        new SqlServerReferenceDefinitionParser(),
        new JavaScriptReferenceDefinitionParser()
    ];

    private readonly DatabaseDocumentService _databaseDocumentService = new();

    public ScopeReferenceIndex Build(
        Scope? scope,
        DatabaseSnapshotLibrary? databaseSnapshots = null,
        CodeWindowSettings? codeWindowSettings = null,
        IReadOnlySet<string>? unloadedResourceIds = null)
    {
        var index = new ScopeReferenceIndex();
        if (scope == null)
        {
            return index;
        }

        var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ScopedResource resource in scope.Resources)
        {
            if (!IsResourceLoaded(resource, unloadedResourceIds))
            {
                continue;
            }

            if (resource.Kind == ResourceKind.DatabaseSnapshot)
            {
                AddDatabaseSnapshotReferences(index, resource, databaseSnapshots);
                continue;
            }

            foreach (string filePath in EnumerateResourceFiles(resource))
            {
                if (!visitedFiles.Add(filePath))
                {
                    continue;
                }

                index.AddFile();
                AddFileReference(index, filePath);

                IReferenceDefinitionParser? parser = GetParser(filePath, codeWindowSettings);
                if (parser == null)
                {
                    continue;
                }

                try
                {
                    string content = File.ReadAllText(filePath);
                    foreach (ReferenceEntity entity in parser.Parse(filePath, content))
                    {
                        index.Add(entity);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Keep the rest of the scope usable if one file cannot be read or parsed.
                }
            }
        }

        return index;
    }

    private IReferenceDefinitionParser? GetParser(string filePath, CodeWindowSettings? codeWindowSettings)
    {
        string language = codeWindowSettings?.GetLanguageForFile(filePath) ??
            CodeWindowSettings.GetDefaultLanguageForExtension(filePath);

        return CodeWindowSettings.NormalizeLanguage(language) switch
        {
            CodeWindowSettings.CSharpLanguage => _parsers.OfType<CSharpReferenceDefinitionParser>().FirstOrDefault(),
            CodeWindowSettings.VisualBasicLanguage => _parsers.OfType<VisualBasicReferenceDefinitionParser>().FirstOrDefault(),
            CodeWindowSettings.SqlServerLanguage => _parsers.OfType<SqlServerReferenceDefinitionParser>().FirstOrDefault(),
            CodeWindowSettings.JavaScriptLanguage => _parsers.OfType<JavaScriptReferenceDefinitionParser>().FirstOrDefault(),
            _ => null
        };
    }

    private void AddDatabaseSnapshotReferences(
        ScopeReferenceIndex index,
        ScopedResource resource,
        DatabaseSnapshotLibrary? databaseSnapshots)
    {
        DatabaseMetadataSnapshot? snapshot = databaseSnapshots?.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, resource.Path, StringComparison.OrdinalIgnoreCase));
        if (snapshot == null)
        {
            return;
        }

        foreach (SqlDatabaseObject databaseObject in snapshot.Objects)
        {
            if (databaseObject.Kind == SqlDatabaseObjectKind.Unknown)
            {
                continue;
            }

            string documentPath = DatabaseDocumentService.CreateObjectDocumentPath(snapshot, databaseObject);
            index.Add(new ReferenceEntity
            {
                Name = databaseObject.ObjectName,
                QualifiedName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName),
                Kind = MapReferenceKind(databaseObject.Kind),
                FilePath = documentPath,
                LineNumber = 1,
                ColumnNumber = 1,
                EndLineNumber = Math.Max(1, CountLines(databaseObject.Definition)),
                EndColumnNumber = 1,
                Language = "SQL Server",
                ContainerName = snapshot.DisplayName
            });
        }

        foreach (SqlTable table in snapshot.Tables)
        {
            string documentPath = DatabaseDocumentService.CreateTableDocumentPath(snapshot, table);
            index.Add(new ReferenceEntity
            {
                Name = table.TableName,
                QualifiedName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName),
                Kind = ReferenceEntityKind.Table,
                FilePath = documentPath,
                LineNumber = 1,
                ColumnNumber = 1,
                EndLineNumber = 1,
                EndColumnNumber = 1,
                Language = "SQL Server",
                ContainerName = snapshot.DisplayName
            });

            foreach (SqlColumn column in snapshot.Columns
                         .Where(candidate =>
                             string.Equals(candidate.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(candidate.TableName, table.TableName, StringComparison.OrdinalIgnoreCase)))
            {
                int lineNumber = _databaseDocumentService.GetColumnLineNumber(snapshot, table, column);
                index.Add(new ReferenceEntity
                {
                    Name = column.ColumnName,
                    QualifiedName = $"{SqlName.FormatPlainMultipartName(column.SchemaName, column.TableName)}.{column.ColumnName}",
                    Kind = ReferenceEntityKind.Field,
                    FilePath = documentPath,
                    LineNumber = lineNumber,
                    ColumnNumber = 5,
                    EndLineNumber = lineNumber,
                    EndColumnNumber = 5 + column.ColumnName.Length,
                    Language = "SQL Server",
                    ContainerName = SqlName.FormatPlainMultipartName(column.SchemaName, column.TableName)
                });
            }
        }
    }

    private static void AddFileReference(ScopeReferenceIndex index, string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string name = string.IsNullOrWhiteSpace(fileNameWithoutExtension) ? fileName : fileNameWithoutExtension;

        index.Add(new ReferenceEntity
        {
            Name = name,
            QualifiedName = fileName,
            Kind = ReferenceEntityKind.File,
            FilePath = filePath,
            LineNumber = 1,
            ColumnNumber = 1,
            EndLineNumber = 1,
            EndColumnNumber = 1,
            Language = "File"
        });
    }

    private static IEnumerable<string> EnumerateResourceFiles(ScopedResource resource)
    {
        if (resource.Kind == ResourceKind.Diagram)
        {
            yield break;
        }

        if (!resource.Exists)
        {
            yield break;
        }

        if (resource.Kind == ResourceKind.File)
        {
            yield return resource.Path;
            yield break;
        }

        if (Directory.Exists(resource.Path))
        {
            foreach (string filePath in EnumerateFilesSafely(resource.Path))
            {
                yield return filePath;
            }
        }
    }

    private static ReferenceEntityKind MapReferenceKind(SqlDatabaseObjectKind kind)
    {
        return kind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => ReferenceEntityKind.StoredProcedure,
            SqlDatabaseObjectKind.View => ReferenceEntityKind.View,
            SqlDatabaseObjectKind.Function => ReferenceEntityKind.Function,
            SqlDatabaseObjectKind.Trigger => ReferenceEntityKind.Trigger,
            _ => ReferenceEntityKind.File
        };
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        return text.Count(character => character == '\n') + 1;
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            yield return file;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(directory)
                .Where(path => !ExcludedDirectoryNames.Contains(Path.GetFileName(path)))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (string childDirectory in directories)
        {
            foreach (string file in EnumerateFilesSafely(childDirectory))
            {
                yield return file;
            }
        }
    }

    private static bool IsResourceLoaded(ScopedResource resource, IReadOnlySet<string>? unloadedResourceIds)
    {
        return unloadedResourceIds == null ||
               string.IsNullOrWhiteSpace(resource.ResourceId) ||
               !unloadedResourceIds.Contains(resource.ResourceId);
    }
}

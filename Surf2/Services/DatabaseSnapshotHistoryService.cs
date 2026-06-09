using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Surf2.Models;

namespace Surf2.Services;

public sealed class DatabaseSnapshotHistoryService(DatabaseDocumentService documentService)
{
    private sealed record VersionedResource(
        DatabaseVersionedResourceKind Kind,
        string ResourceKey,
        string DisplayName,
        string RelativePath,
        string Content,
        DatabaseSnapshotResourcePayload Payload);

    public bool Normalize(DatabaseSnapshotLibrary library)
    {
        library.Histories ??= [];
        bool changed = false;

        foreach (DatabaseMetadataSnapshot snapshot in library.Snapshots)
        {
            changed |= EnsureInitialVersion(library, snapshot);
        }

        foreach (DatabaseSnapshotHistory history in library.Histories)
        {
            if (history.Versions.Count == 0)
            {
                history.NextVersionNumber = Math.Max(1, history.NextVersionNumber);
                continue;
            }

            int maxVersion = history.Versions.Max(version => version.VersionNumber);
            if (history.NextVersionNumber <= maxVersion)
            {
                history.NextVersionNumber = maxVersion + 1;
                changed = true;
            }
        }

        return changed;
    }

    public DatabaseSnapshotHistory GetOrCreateHistory(DatabaseSnapshotLibrary library, DatabaseMetadataSnapshot snapshot)
    {
        EnsureInitialVersion(library, snapshot);
        return library.Histories.First(history =>
            string.Equals(history.SnapshotId, snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase));
    }

    public bool EnsureInitialVersion(DatabaseSnapshotLibrary library, DatabaseMetadataSnapshot snapshot)
    {
        library.Histories ??= [];
        DatabaseSnapshotHistory? history = library.Histories.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, snapshot.SnapshotId, StringComparison.OrdinalIgnoreCase));
        if (history != null)
        {
            return false;
        }

        history = new DatabaseSnapshotHistory
        {
            SnapshotId = snapshot.SnapshotId,
            NextVersionNumber = 2,
            Versions =
            [
                new DatabaseSnapshotVersion
                {
                    VersionName = "V1",
                    VersionNumber = 1,
                    CreatedAtUtc = snapshot.ImportedAtUtc == default ? DateTimeOffset.UtcNow : snapshot.ImportedAtUtc,
                    IsInitial = true
                }
            ]
        };
        library.Histories.Add(history);
        return true;
    }

    public DatabaseSnapshotVersion? RecordSnapshotReplacement(
        DatabaseSnapshotLibrary library,
        DatabaseMetadataSnapshot previousSnapshot,
        DatabaseMetadataSnapshot newSnapshot)
    {
        DatabaseSnapshotHistory history = GetOrCreateHistory(library, previousSnapshot);
        List<DatabaseSnapshotResourceChange> changes = BuildRollbackChanges(previousSnapshot, newSnapshot);
        if (changes.Count == 0)
        {
            return null;
        }

        int versionNumber = Math.Max(1, history.NextVersionNumber);
        var version = new DatabaseSnapshotVersion
        {
            VersionName = $"V{versionNumber}",
            VersionNumber = versionNumber,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Changes = changes
        };

        history.Versions.Add(version);
        history.NextVersionNumber = versionNumber + 1;
        return version;
    }

    public DatabaseMetadataSnapshot ReconstructSnapshot(
        DatabaseSnapshotLibrary library,
        string snapshotId,
        string versionId)
    {
        DatabaseMetadataSnapshot current = library.Snapshots.First(candidate =>
            string.Equals(candidate.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
        DatabaseSnapshotHistory history = library.Histories.First(candidate =>
            string.Equals(candidate.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
        DatabaseSnapshotVersion targetVersion = history.Versions.First(version =>
            string.Equals(version.VersionId, versionId, StringComparison.OrdinalIgnoreCase));

        DatabaseMetadataSnapshot reconstructed = current.Clone();
        foreach (DatabaseSnapshotVersion version in history.Versions
                     .Where(version => version.VersionNumber > targetVersion.VersionNumber)
                     .OrderByDescending(version => version.VersionNumber))
        {
            ApplyRollbackVersion(reconstructed, version);
        }

        reconstructed.ImportedAtUtc = targetVersion.CreatedAtUtc;
        return reconstructed;
    }

    public DatabaseHistoryVersionSummary CreateVersionSummary(
        DatabaseSnapshotLibrary library,
        DatabaseSnapshotHistory history,
        DatabaseSnapshotVersion version)
    {
        IReadOnlyList<DatabaseHistoryFileChangeSummary> changes = CreateFileChangeSummaries(library, history, version);
        return new DatabaseHistoryVersionSummary(version, changes);
    }

    public IReadOnlyList<DatabaseHistoryFileChangeSummary> CreateFileChangeSummaries(
        DatabaseSnapshotLibrary library,
        DatabaseSnapshotHistory history,
        DatabaseSnapshotVersion version)
    {
        DatabaseMetadataSnapshot versionSnapshot = ReconstructSnapshot(library, history.SnapshotId, version.VersionId);
        Dictionary<string, VersionedResource> versionResources = EnumerateResources(versionSnapshot)
            .ToDictionary(resource => resource.ResourceKey, StringComparer.OrdinalIgnoreCase);

        if (version.IsInitial)
        {
            return versionResources.Values
                .OrderBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(resource => new DatabaseHistoryFileChangeSummary(
                    version,
                    resource.Kind,
                    resource.ResourceKey,
                    resource.DisplayName,
                    resource.RelativePath,
                    DatabaseSnapshotResourceChangeKind.Added,
                    CountLines(resource.Content),
                    0))
                .ToList();
        }

        DatabaseSnapshotVersion? previousVersion = history.Versions
            .Where(candidate => candidate.VersionNumber < version.VersionNumber)
            .OrderByDescending(candidate => candidate.VersionNumber)
            .FirstOrDefault();
        Dictionary<string, VersionedResource> previousResources = previousVersion == null
            ? []
            : EnumerateResources(ReconstructSnapshot(library, history.SnapshotId, previousVersion.VersionId))
                .ToDictionary(resource => resource.ResourceKey, StringComparer.OrdinalIgnoreCase);

        return version.Changes
            .OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(change =>
            {
                previousResources.TryGetValue(change.ResourceKey, out VersionedResource? oldResource);
                versionResources.TryGetValue(change.ResourceKey, out VersionedResource? newResource);
                (int added, int removed) = CountLineChanges(oldResource?.Content ?? string.Empty, newResource?.Content ?? string.Empty);
                return new DatabaseHistoryFileChangeSummary(
                    version,
                    change.Kind,
                    change.ResourceKey,
                    change.DisplayName,
                    change.RelativePath,
                    change.ChangeKind,
                    added,
                    removed);
            })
            .ToList();
    }

    public bool TryGetResourceContent(
        DatabaseMetadataSnapshot snapshot,
        DatabaseVersionedResourceKind kind,
        string resourceKey,
        out string content,
        out DatabaseSnapshotResourcePayload? payload)
    {
        VersionedResource? resource = EnumerateResources(snapshot)
            .FirstOrDefault(candidate =>
                candidate.Kind == kind &&
                string.Equals(candidate.ResourceKey, resourceKey, StringComparison.OrdinalIgnoreCase));
        if (resource == null)
        {
            content = string.Empty;
            payload = null;
            return false;
        }

        content = resource.Content;
        payload = resource.Payload;
        return true;
    }

    public IReadOnlyList<DatabaseSnapshotResourceFile> CreateResourceFiles(DatabaseMetadataSnapshot snapshot)
    {
        return EnumerateResources(snapshot)
            .OrderBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(resource => new DatabaseSnapshotResourceFile(
                resource.Kind,
                resource.ResourceKey,
                resource.DisplayName,
                resource.RelativePath,
                resource.Content,
                ClonePayload(resource.Payload)))
            .ToList();
    }

    public DatabaseMetadataSnapshot RestoreResource(
        DatabaseMetadataSnapshot currentSnapshot,
        DatabaseMetadataSnapshot sourceSnapshot,
        DatabaseVersionedResourceKind kind,
        string resourceKey)
    {
        DatabaseMetadataSnapshot restored = currentSnapshot.Clone();
        if (TryGetResourceContent(sourceSnapshot, kind, resourceKey, out _, out DatabaseSnapshotResourcePayload? payload) &&
            payload != null)
        {
            ApplyPayload(restored, payload);
        }
        else
        {
            RemoveResource(restored, kind, resourceKey);
        }

        return restored;
    }

    public DatabaseMetadataSnapshot RestoreResourceAsCopy(
        DatabaseMetadataSnapshot targetSnapshot,
        DatabaseSnapshotResourcePayload payload,
        string schemaName,
        string objectNameOrTableName,
        string? editedText = null)
    {
        DatabaseMetadataSnapshot restored = targetSnapshot.Clone();
        DatabaseSnapshotResourcePayload copy = ClonePayload(payload);
        RenamePayload(copy, schemaName, objectNameOrTableName, editedText);
        ApplyPayload(restored, copy);
        return restored;
    }

    private List<DatabaseSnapshotResourceChange> BuildRollbackChanges(
        DatabaseMetadataSnapshot previousSnapshot,
        DatabaseMetadataSnapshot newSnapshot)
    {
        Dictionary<string, VersionedResource> previousResources = EnumerateResources(previousSnapshot)
            .ToDictionary(resource => resource.ResourceKey, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, VersionedResource> newResources = EnumerateResources(newSnapshot)
            .ToDictionary(resource => resource.ResourceKey, StringComparer.OrdinalIgnoreCase);

        return previousResources.Keys
            .Concat(newResources.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => previousResources.TryGetValue(key, out VersionedResource? previous)
                ? previous.RelativePath
                : newResources[key].RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(key => CreateRollbackChange(previousResources, newResources, key))
            .OfType<DatabaseSnapshotResourceChange>()
            .ToList();
    }

    private static DatabaseSnapshotResourceChange? CreateRollbackChange(
        IReadOnlyDictionary<string, VersionedResource> previousResources,
        IReadOnlyDictionary<string, VersionedResource> newResources,
        string key)
    {
        bool hasPrevious = previousResources.TryGetValue(key, out VersionedResource? previous);
        bool hasNew = newResources.TryGetValue(key, out VersionedResource? current);
        if (hasPrevious && hasNew && string.Equals(previous!.Content, current!.Content, StringComparison.Ordinal))
        {
            return null;
        }

        VersionedResource reference = previous ?? current!;
        return new DatabaseSnapshotResourceChange
        {
            Kind = reference.Kind,
            ResourceKey = reference.ResourceKey,
            DisplayName = reference.DisplayName,
            RelativePath = reference.RelativePath,
            ChangeKind = hasPrevious
                ? hasNew
                    ? DatabaseSnapshotResourceChangeKind.Modified
                    : DatabaseSnapshotResourceChangeKind.Deleted
                : DatabaseSnapshotResourceChangeKind.Added,
            PreviousPayload = hasPrevious ? ClonePayload(previous!.Payload) : null
        };
    }

    private IEnumerable<VersionedResource> EnumerateResources(DatabaseMetadataSnapshot snapshot)
    {
        foreach (SqlDatabaseObject databaseObject in snapshot.Objects)
        {
            DatabaseVersionedResourceKind? kind = ToVersionedKind(databaseObject.Kind);
            if (kind == null)
            {
                continue;
            }

            string fullName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
            string folderName = GetFolderName(kind.Value);
            yield return new VersionedResource(
                kind.Value,
                CreateResourceKey(kind.Value, databaseObject.SchemaName, databaseObject.ObjectName),
                fullName,
                $"{folderName}/{fullName}.txt",
                databaseObject.Definition,
                new DatabaseSnapshotResourcePayload
                {
                    Kind = kind.Value,
                    DatabaseObject = CloneDatabaseObject(databaseObject)
                });
        }

        foreach (SqlTable table in snapshot.Tables)
        {
            string fullName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
            List<SqlColumn> columns = snapshot.Columns
                .Where(column =>
                    string.Equals(column.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(column.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
                .Select(CloneColumn)
                .ToList();
            List<SqlPrimaryKeyColumn> primaryKeys = snapshot.PrimaryKeys
                .Where(primaryKey =>
                    string.Equals(primaryKey.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(primaryKey.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
                .Select(ClonePrimaryKey)
                .ToList();

            yield return new VersionedResource(
                DatabaseVersionedResourceKind.TableMetadata,
                CreateResourceKey(DatabaseVersionedResourceKind.TableMetadata, table.SchemaName, table.TableName),
                fullName,
                $"Tables/{fullName}.txt",
                documentService.CreateTableDocument(snapshot, table),
                new DatabaseSnapshotResourcePayload
                {
                    Kind = DatabaseVersionedResourceKind.TableMetadata,
                    Table = CloneTable(table),
                    Columns = columns,
                    PrimaryKeys = primaryKeys
                });
        }

        foreach (SqlTableDataSet dataSet in snapshot.TableDataSets)
        {
            SqlTable table = snapshot.Tables.FirstOrDefault(candidate =>
                string.Equals(candidate.SchemaName, dataSet.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.TableName, dataSet.TableName, StringComparison.OrdinalIgnoreCase)) ?? new SqlTable
                {
                    SchemaName = dataSet.SchemaName,
                    TableName = dataSet.TableName,
                    HasFullData = true,
                    FullDataRowCount = Math.Max(dataSet.RowCount, dataSet.Rows.Count),
                    FullDataImportedAtUtc = dataSet.ImportedAtUtc
                };
            string fullName = SqlName.FormatPlainMultipartName(dataSet.SchemaName, dataSet.TableName);

            yield return new VersionedResource(
                DatabaseVersionedResourceKind.TableData,
                CreateResourceKey(DatabaseVersionedResourceKind.TableData, dataSet.SchemaName, dataSet.TableName),
                fullName,
                $"Table Data/{fullName}.csv",
                documentService.CreateTableDataDocument(snapshot, table, dataSet),
                new DatabaseSnapshotResourcePayload
                {
                    Kind = DatabaseVersionedResourceKind.TableData,
                    Table = CloneTable(table),
                    TableDataSet = CloneTableDataSet(dataSet)
                });
        }
    }

    private static void ApplyRollbackVersion(DatabaseMetadataSnapshot snapshot, DatabaseSnapshotVersion version)
    {
        foreach (DatabaseSnapshotResourceChange change in version.Changes)
        {
            if (change.ChangeKind == DatabaseSnapshotResourceChangeKind.Added)
            {
                RemoveResource(snapshot, change.Kind, change.ResourceKey);
                continue;
            }

            if (change.PreviousPayload != null)
            {
                ApplyPayload(snapshot, change.PreviousPayload);
            }
        }
    }

    private static void ApplyPayload(DatabaseMetadataSnapshot snapshot, DatabaseSnapshotResourcePayload payload)
    {
        switch (payload.Kind)
        {
            case DatabaseVersionedResourceKind.StoredProcedure:
            case DatabaseVersionedResourceKind.View:
            case DatabaseVersionedResourceKind.Function:
            case DatabaseVersionedResourceKind.Trigger:
                if (payload.DatabaseObject != null)
                {
                    SqlDatabaseObject databaseObject = CloneDatabaseObject(payload.DatabaseObject);
                    snapshot.Objects.RemoveAll(candidate =>
                        candidate.Kind == databaseObject.Kind &&
                        string.Equals(candidate.SchemaName, databaseObject.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.ObjectName, databaseObject.ObjectName, StringComparison.OrdinalIgnoreCase));
                    snapshot.Objects.Add(databaseObject);
                }
                break;

            case DatabaseVersionedResourceKind.TableMetadata:
                if (payload.Table != null)
                {
                    SqlTable table = CloneTable(payload.Table);
                    RemoveTableMetadata(snapshot, table.SchemaName, table.TableName);
                    snapshot.Tables.Add(table);
                    snapshot.Columns.AddRange(payload.Columns.Select(CloneColumn));
                    snapshot.PrimaryKeys.AddRange(payload.PrimaryKeys.Select(ClonePrimaryKey));
                }
                break;

            case DatabaseVersionedResourceKind.TableData:
                if (payload.TableDataSet != null)
                {
                    SqlTableDataSet dataSet = CloneTableDataSet(payload.TableDataSet);
                    snapshot.TableDataSets.RemoveAll(candidate =>
                        string.Equals(candidate.SchemaName, dataSet.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.TableName, dataSet.TableName, StringComparison.OrdinalIgnoreCase));
                    snapshot.TableDataSets.Add(dataSet);
                    SqlTable? table = snapshot.Tables.FirstOrDefault(candidate =>
                        string.Equals(candidate.SchemaName, dataSet.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.TableName, dataSet.TableName, StringComparison.OrdinalIgnoreCase));
                    if (table == null && payload.Table != null)
                    {
                        table = CloneTable(payload.Table);
                        snapshot.Tables.Add(table);
                    }

                    if (table != null)
                    {
                        table.HasFullData = true;
                        table.FullDataRowCount = Math.Max(dataSet.RowCount, dataSet.Rows.Count);
                        table.FullDataImportedAtUtc = dataSet.ImportedAtUtc;
                    }

                    string fullName = SqlName.FormatPlainMultipartName(dataSet.SchemaName, dataSet.TableName);
                    snapshot.FullDataTableNames ??= [];
                    if (!snapshot.FullDataTableNames.Contains(fullName, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshot.FullDataTableNames.Add(fullName);
                    }
                }
                break;
        }
    }

    private static void RemoveResource(DatabaseMetadataSnapshot snapshot, DatabaseVersionedResourceKind kind, string resourceKey)
    {
        if (!TryParseResourceKey(resourceKey, out _, out string schemaName, out string name))
        {
            return;
        }

        switch (kind)
        {
            case DatabaseVersionedResourceKind.StoredProcedure:
            case DatabaseVersionedResourceKind.View:
            case DatabaseVersionedResourceKind.Function:
            case DatabaseVersionedResourceKind.Trigger:
                SqlDatabaseObjectKind objectKind = ToDatabaseObjectKind(kind);
                snapshot.Objects.RemoveAll(candidate =>
                    candidate.Kind == objectKind &&
                    string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.ObjectName, name, StringComparison.OrdinalIgnoreCase));
                break;

            case DatabaseVersionedResourceKind.TableMetadata:
                RemoveTableMetadata(snapshot, schemaName, name);
                break;

            case DatabaseVersionedResourceKind.TableData:
                snapshot.TableDataSets.RemoveAll(candidate =>
                    string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.TableName, name, StringComparison.OrdinalIgnoreCase));
                SqlTable? table = snapshot.Tables.FirstOrDefault(candidate =>
                    string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.TableName, name, StringComparison.OrdinalIgnoreCase));
                if (table != null)
                {
                    table.HasFullData = false;
                    table.FullDataRowCount = 0;
                    table.FullDataImportedAtUtc = null;
                }

                string fullName = SqlName.FormatPlainMultipartName(schemaName, name);
                snapshot.FullDataTableNames?.RemoveAll(candidate =>
                    string.Equals(candidate, fullName, StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    private static void RemoveTableMetadata(DatabaseMetadataSnapshot snapshot, string schemaName, string tableName)
    {
        snapshot.Tables.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        snapshot.Columns.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        snapshot.PrimaryKeys.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        snapshot.TableDataSets.RemoveAll(candidate =>
            string.Equals(candidate.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.TableName, tableName, StringComparison.OrdinalIgnoreCase));

        string fullName = SqlName.FormatPlainMultipartName(schemaName, tableName);
        snapshot.FullDataTableNames?.RemoveAll(candidate =>
            string.Equals(candidate, fullName, StringComparison.OrdinalIgnoreCase));
    }

    private static void RenamePayload(
        DatabaseSnapshotResourcePayload payload,
        string schemaName,
        string objectNameOrTableName,
        string? editedText)
    {
        switch (payload.Kind)
        {
            case DatabaseVersionedResourceKind.StoredProcedure:
            case DatabaseVersionedResourceKind.View:
            case DatabaseVersionedResourceKind.Function:
            case DatabaseVersionedResourceKind.Trigger:
                if (payload.DatabaseObject != null)
                {
                    payload.DatabaseObject.SchemaName = schemaName;
                    payload.DatabaseObject.ObjectName = objectNameOrTableName;
                    if (editedText != null)
                    {
                        payload.DatabaseObject.Definition = editedText;
                    }
                }
                break;

            case DatabaseVersionedResourceKind.TableMetadata:
                if (payload.Table != null)
                {
                    RenameTablePayload(payload, schemaName, objectNameOrTableName);
                }
                break;

            case DatabaseVersionedResourceKind.TableData:
                if (payload.Table != null)
                {
                    payload.Table.SchemaName = schemaName;
                    payload.Table.TableName = objectNameOrTableName;
                }

                if (payload.TableDataSet != null)
                {
                    payload.TableDataSet.SchemaName = schemaName;
                    payload.TableDataSet.TableName = objectNameOrTableName;
                }
                break;
        }
    }

    private static void RenameTablePayload(DatabaseSnapshotResourcePayload payload, string schemaName, string tableName)
    {
        payload.Table!.SchemaName = schemaName;
        payload.Table.TableName = tableName;
        foreach (SqlColumn column in payload.Columns)
        {
            column.SchemaName = schemaName;
            column.TableName = tableName;
        }

        foreach (SqlPrimaryKeyColumn primaryKey in payload.PrimaryKeys)
        {
            primaryKey.SchemaName = schemaName;
            primaryKey.TableName = tableName;
        }
    }

    private static DatabaseVersionedResourceKind? ToVersionedKind(SqlDatabaseObjectKind kind)
    {
        return kind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => DatabaseVersionedResourceKind.StoredProcedure,
            SqlDatabaseObjectKind.View => DatabaseVersionedResourceKind.View,
            SqlDatabaseObjectKind.Function => DatabaseVersionedResourceKind.Function,
            SqlDatabaseObjectKind.Trigger => DatabaseVersionedResourceKind.Trigger,
            _ => null
        };
    }

    private static SqlDatabaseObjectKind ToDatabaseObjectKind(DatabaseVersionedResourceKind kind)
    {
        return kind switch
        {
            DatabaseVersionedResourceKind.StoredProcedure => SqlDatabaseObjectKind.StoredProcedure,
            DatabaseVersionedResourceKind.View => SqlDatabaseObjectKind.View,
            DatabaseVersionedResourceKind.Function => SqlDatabaseObjectKind.Function,
            DatabaseVersionedResourceKind.Trigger => SqlDatabaseObjectKind.Trigger,
            _ => SqlDatabaseObjectKind.Unknown
        };
    }

    private static string GetFolderName(DatabaseVersionedResourceKind kind)
    {
        return kind switch
        {
            DatabaseVersionedResourceKind.StoredProcedure => "Stored Procedures",
            DatabaseVersionedResourceKind.View => "Views",
            DatabaseVersionedResourceKind.Function => "Functions",
            DatabaseVersionedResourceKind.Trigger => "Triggers",
            DatabaseVersionedResourceKind.TableMetadata => "Tables",
            DatabaseVersionedResourceKind.TableData => "Table Data",
            _ => "Objects"
        };
    }

    private static string CreateResourceKey(DatabaseVersionedResourceKind kind, string schemaName, string name)
    {
        return $"{kind}|{SqlName.FormatPlainMultipartName(schemaName, name)}";
    }

    private static bool TryParseResourceKey(
        string resourceKey,
        out DatabaseVersionedResourceKind kind,
        out string schemaName,
        out string name)
    {
        kind = default;
        schemaName = string.Empty;
        name = string.Empty;

        string[] parts = resourceKey.Split('|', 2);
        if (parts.Length != 2 || !Enum.TryParse(parts[0], out kind))
        {
            return false;
        }

        string[] nameParts = parts[1].Split('.', 2);
        if (nameParts.Length != 2)
        {
            return false;
        }

        schemaName = nameParts[0];
        name = nameParts[1];
        return true;
    }

    private static (int Added, int Removed) CountLineChanges(string oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent))
        {
            return (CountLines(newContent), 0);
        }

        if (string.IsNullOrEmpty(newContent))
        {
            return (0, CountLines(oldContent));
        }

        DiffPaneModel diff = InlineDiffBuilder.Diff(oldContent, newContent);
        return (
            diff.Lines.Count(line => line.Type == ChangeType.Inserted),
            diff.Lines.Count(line => line.Type == ChangeType.Deleted));
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n').Length;
    }

    private static DatabaseSnapshotResourcePayload ClonePayload(DatabaseSnapshotResourcePayload payload)
    {
        return new DatabaseSnapshotResourcePayload
        {
            Kind = payload.Kind,
            DatabaseObject = payload.DatabaseObject == null ? null : CloneDatabaseObject(payload.DatabaseObject),
            Table = payload.Table == null ? null : CloneTable(payload.Table),
            Columns = payload.Columns.Select(CloneColumn).ToList(),
            PrimaryKeys = payload.PrimaryKeys.Select(ClonePrimaryKey).ToList(),
            TableDataSet = payload.TableDataSet == null ? null : CloneTableDataSet(payload.TableDataSet)
        };
    }

    private static SqlDatabaseObject CloneDatabaseObject(SqlDatabaseObject databaseObject)
    {
        return new SqlDatabaseObject
        {
            SchemaName = databaseObject.SchemaName,
            ObjectName = databaseObject.ObjectName,
            Kind = databaseObject.Kind,
            TypeDescription = databaseObject.TypeDescription,
            Definition = databaseObject.Definition,
            ParentSchemaName = databaseObject.ParentSchemaName,
            ParentObjectName = databaseObject.ParentObjectName
        };
    }

    private static SqlTable CloneTable(SqlTable table)
    {
        return new SqlTable
        {
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            HasFullData = table.HasFullData,
            FullDataRowCount = table.FullDataRowCount,
            FullDataImportedAtUtc = table.FullDataImportedAtUtc
        };
    }

    private static SqlColumn CloneColumn(SqlColumn column)
    {
        return new SqlColumn
        {
            SchemaName = column.SchemaName,
            TableName = column.TableName,
            ColumnName = column.ColumnName,
            DataType = column.DataType,
            MaxLength = column.MaxLength,
            NumericPrecision = column.NumericPrecision,
            NumericScale = column.NumericScale,
            IsNullable = column.IsNullable,
            IsIdentity = column.IsIdentity,
            Ordinal = column.Ordinal
        };
    }

    private static SqlPrimaryKeyColumn ClonePrimaryKey(SqlPrimaryKeyColumn primaryKey)
    {
        return new SqlPrimaryKeyColumn
        {
            SchemaName = primaryKey.SchemaName,
            TableName = primaryKey.TableName,
            ConstraintName = primaryKey.ConstraintName,
            ColumnName = primaryKey.ColumnName,
            KeyOrdinal = primaryKey.KeyOrdinal
        };
    }

    private static SqlTableDataSet CloneTableDataSet(SqlTableDataSet dataSet)
    {
        return new SqlTableDataSet
        {
            SchemaName = dataSet.SchemaName,
            TableName = dataSet.TableName,
            RowCount = dataSet.RowCount,
            ImportedAtUtc = dataSet.ImportedAtUtc,
            Rows = dataSet.Rows.Select(row => row.Clone()).ToList()
        };
    }
}

public sealed record DatabaseHistoryVersionSummary(
    DatabaseSnapshotVersion Version,
    IReadOnlyList<DatabaseHistoryFileChangeSummary> Changes);

public sealed record DatabaseHistoryFileChangeSummary(
    DatabaseSnapshotVersion Version,
    DatabaseVersionedResourceKind Kind,
    string ResourceKey,
    string DisplayName,
    string RelativePath,
    DatabaseSnapshotResourceChangeKind ChangeKind,
    int AddedLines,
    int RemovedLines);

public sealed record DatabaseSnapshotResourceFile(
    DatabaseVersionedResourceKind Kind,
    string ResourceKey,
    string DisplayName,
    string RelativePath,
    string Content,
    DatabaseSnapshotResourcePayload Payload);

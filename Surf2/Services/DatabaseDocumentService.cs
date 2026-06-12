using System.Text;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services;

public sealed class DatabaseDocumentService
{
    private const string Prefix = "db://";

    public static string CreateSnapshotDocumentPath(DatabaseMetadataSnapshot snapshot)
    {
        return $"{Prefix}{CreateSnapshotDocumentSegment(snapshot)}";
    }

    public static string CreateObjectDocumentPath(DatabaseMetadataSnapshot snapshot, SqlDatabaseObject databaseObject)
    {
        string fullName = SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName);
        return CreateObjectDocumentPath(snapshot, databaseObject.Kind, fullName);
    }

    public static string CreateTableDocumentPath(DatabaseMetadataSnapshot snapshot, SqlTable table)
    {
        string fullName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
        return CreateTableDocumentPath(snapshot, fullName);
    }

    public static string CreateTableDataDocumentPath(DatabaseMetadataSnapshot snapshot, SqlTable table)
    {
        string fullName = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
        return CreateTableDataDocumentPath(snapshot, fullName);
    }

    public static bool IsDatabaseDocumentPath(string path)
    {
        return path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseDocumentPath(string documentPath, out DatabaseDocumentReference reference)
    {
        reference = DatabaseDocumentReference.Empty;
        if (!TryParsePath(documentPath, out string snapshotKey, out string documentType, out string kind, out string fullName))
        {
            return false;
        }

        SqlDatabaseObjectKind? objectKind = null;
        if (!string.IsNullOrWhiteSpace(kind) &&
            Enum.TryParse(kind, ignoreCase: true, out SqlDatabaseObjectKind parsedKind))
        {
            objectKind = parsedKind;
        }

        reference = new DatabaseDocumentReference(snapshotKey, documentType, objectKind, fullName);
        return true;
    }

    public static bool TryGetSnapshotKey(string documentPath, out string snapshotKey)
    {
        snapshotKey = string.Empty;
        if (!documentPath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = documentPath[Prefix.Length..];
        int slashIndex = rest.IndexOf('/', StringComparison.Ordinal);
        snapshotKey = slashIndex >= 0 ? rest[..slashIndex] : rest;
        return !string.IsNullOrWhiteSpace(snapshotKey);
    }

    public static bool TryResolveSnapshot(
        DatabaseSnapshotLibrary snapshotLibrary,
        string snapshotKey,
        out DatabaseMetadataSnapshot snapshot)
    {
        snapshot = null!;
        if (string.IsNullOrWhiteSpace(snapshotKey))
        {
            return false;
        }

        snapshot = snapshotLibrary.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, snapshotKey, StringComparison.OrdinalIgnoreCase))!;
        if (snapshot != null)
        {
            return true;
        }

        snapshot = snapshotLibrary.Snapshots.FirstOrDefault(candidate =>
            string.Equals(CreateSnapshotDocumentSegment(candidate), snapshotKey, StringComparison.OrdinalIgnoreCase))!;
        if (snapshot != null)
        {
            return true;
        }

        string decodedSnapshotKey = UnescapeSegment(snapshotKey);
        snapshot = snapshotLibrary.Snapshots.FirstOrDefault(candidate =>
            string.Equals(GetSnapshotDocumentName(candidate), decodedSnapshotKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.DisplayName, decodedSnapshotKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.DatabaseName, decodedSnapshotKey, StringComparison.OrdinalIgnoreCase))!;
        return snapshot != null;
    }

    public static bool TryResolveDocumentSnapshot(
        string documentPath,
        DatabaseSnapshotLibrary snapshotLibrary,
        out DatabaseMetadataSnapshot snapshot)
    {
        snapshot = null!;
        return TryGetSnapshotKey(documentPath, out string snapshotKey) &&
               TryResolveSnapshot(snapshotLibrary, snapshotKey, out snapshot);
    }

    public static bool TryCreateCanonicalDocumentPath(
        string documentPath,
        DatabaseSnapshotLibrary snapshotLibrary,
        out string canonicalDocumentPath)
    {
        canonicalDocumentPath = documentPath;
        if (!TryParseDocumentPath(documentPath, out DatabaseDocumentReference reference) ||
            !TryResolveSnapshot(snapshotLibrary, reference.SnapshotId, out DatabaseMetadataSnapshot snapshot))
        {
            return false;
        }

        if (string.Equals(reference.DocumentType, "object", StringComparison.OrdinalIgnoreCase) &&
            reference.ObjectKind.HasValue)
        {
            canonicalDocumentPath = CreateObjectDocumentPath(snapshot, reference.ObjectKind.Value, reference.FullName);
            return true;
        }

        if (string.Equals(reference.DocumentType, "table", StringComparison.OrdinalIgnoreCase))
        {
            canonicalDocumentPath = CreateTableDocumentPath(snapshot, reference.FullName);
            return true;
        }

        if (string.Equals(reference.DocumentType, "table-data", StringComparison.OrdinalIgnoreCase))
        {
            canonicalDocumentPath = CreateTableDataDocumentPath(snapshot, reference.FullName);
            return true;
        }

        return false;
    }

    public bool TryGetTableDataDocumentPathForTableDocument(
        string tableDocumentPath,
        DatabaseSnapshotLibrary snapshotLibrary,
        out string tableDataDocumentPath)
    {
        tableDataDocumentPath = string.Empty;

        if (!TryParsePath(tableDocumentPath, out string snapshotKey, out string documentType, out _, out string fullName) ||
            !string.Equals(documentType, "table", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryResolveSnapshot(snapshotLibrary, snapshotKey, out DatabaseMetadataSnapshot snapshot))
        {
            return false;
        }

        SqlTable? table = snapshot.Tables.FirstOrDefault(candidate =>
            string.Equals(SqlName.FormatPlainMultipartName(candidate.SchemaName, candidate.TableName), fullName, StringComparison.OrdinalIgnoreCase));
        if (table == null || !table.HasFullData || snapshot.GetTableDataSet(table.SchemaName, table.TableName) == null)
        {
            return false;
        }

        tableDataDocumentPath = CreateTableDataDocumentPath(snapshot, table);
        return true;
    }

    public bool TryGetDocument(
        string documentPath,
        DatabaseSnapshotLibrary snapshotLibrary,
        out string content,
        out string syntaxPath,
        out string displayName)
    {
        content = string.Empty;
        syntaxPath = "document.sql";
        displayName = string.Empty;

        if (!TryParsePath(documentPath, out string snapshotKey, out string documentType, out string kind, out string fullName))
        {
            return false;
        }

        if (!TryResolveSnapshot(snapshotLibrary, snapshotKey, out DatabaseMetadataSnapshot snapshot))
        {
            return false;
        }

        if (string.Equals(documentType, "object", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse(kind, ignoreCase: true, out SqlDatabaseObjectKind objectKind))
        {
            SqlDatabaseObject? databaseObject = snapshot.Objects.FirstOrDefault(candidate =>
                candidate.Kind == objectKind &&
                string.Equals(SqlName.FormatPlainMultipartName(candidate.SchemaName, candidate.ObjectName), fullName, StringComparison.OrdinalIgnoreCase));
            if (databaseObject == null)
            {
                return false;
            }

            displayName = $"{SqlName.FormatPlainMultipartName(databaseObject.SchemaName, databaseObject.ObjectName)}.sql";
            content = string.IsNullOrWhiteSpace(databaseObject.Definition)
                ? $"-- No definition imported for {databaseObject.FullName}."
                : databaseObject.Definition;
            return true;
        }

        if (string.Equals(documentType, "table", StringComparison.OrdinalIgnoreCase))
        {
            SqlTable? table = snapshot.Tables.FirstOrDefault(candidate =>
                string.Equals(SqlName.FormatPlainMultipartName(candidate.SchemaName, candidate.TableName), fullName, StringComparison.OrdinalIgnoreCase));
            if (table == null)
            {
                return false;
            }

            displayName = $"{SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName)}.sql";
            content = CreateTableDocument(snapshot, table);
            return true;
        }

        return false;
    }

    public bool TryGetSpreadsheetDocument(
        string documentPath,
        DatabaseSnapshotLibrary snapshotLibrary,
        out string content,
        out string displayName)
    {
        content = string.Empty;
        displayName = string.Empty;

        if (!TryParsePath(documentPath, out string snapshotKey, out string documentType, out _, out string fullName) ||
            !string.Equals(documentType, "table-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryResolveSnapshot(snapshotLibrary, snapshotKey, out DatabaseMetadataSnapshot snapshot))
        {
            return false;
        }

        SqlTable? table = snapshot.Tables.FirstOrDefault(candidate =>
            string.Equals(SqlName.FormatPlainMultipartName(candidate.SchemaName, candidate.TableName), fullName, StringComparison.OrdinalIgnoreCase));
        if (table == null)
        {
            return false;
        }

        SqlTableDataSet? dataSet = snapshot.GetTableDataSet(table.SchemaName, table.TableName);
        if (dataSet == null)
        {
            return false;
        }

        displayName = $"{SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName)} data.csv";
        content = CreateTableDataDocument(snapshot, table, dataSet);
        return true;
    }

    public string CreateTableDocument(DatabaseMetadataSnapshot snapshot, SqlTable table)
    {
        List<SqlColumn> columns = snapshot.Columns
            .Where(column =>
                string.Equals(column.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(column.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(column => column.Ordinal)
            .ToList();

        HashSet<string> primaryKeyColumns = snapshot.PrimaryKeys
            .Where(primaryKey =>
                string.Equals(primaryKey.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(primaryKey.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
            .Select(primaryKey => primaryKey.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.AppendLine($"-- Table: {table.FullName}");
        if (table.HasFullData)
        {
            builder.AppendLine($"-- Full data imported: {table.FullDataRowCount} row(s)");
        }

        builder.AppendLine("SELECT");

        for (int index = 0; index < columns.Count; index++)
        {
            SqlColumn column = columns[index];
            string comma = index == columns.Count - 1 ? string.Empty : ",";
            string primaryKey = primaryKeyColumns.Contains(column.ColumnName) ? " PK" : string.Empty;
            string identity = column.IsIdentity ? " IDENTITY" : string.Empty;
            string nullable = column.IsNullable ? " NULL" : " NOT NULL";
            builder.AppendLine($"    {SqlName.Bracket(column.ColumnName)}{comma} -- {FormatDataType(column)}{nullable}{primaryKey}{identity}");
        }

        if (columns.Count == 0)
        {
            builder.AppendLine("    *");
        }

        builder.AppendLine($"FROM {table.FullName};");
        return builder.ToString();
    }

    public string CreateTableDataDocument(DatabaseMetadataSnapshot snapshot, SqlTable table, SqlTableDataSet dataSet)
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

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(EscapeCsvValue)));

        foreach (JsonElement row in dataSet.Rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            IEnumerable<string> values = headers.Select(header =>
                TryGetPropertyIgnoreCase(row, header, out JsonElement value)
                    ? FormatJsonValue(value)
                    : string.Empty);
            builder.AppendLine(string.Join(",", values.Select(EscapeCsvValue)));
        }

        return builder.ToString();
    }

    public int GetColumnLineNumber(DatabaseMetadataSnapshot snapshot, SqlTable table, SqlColumn targetColumn)
    {
        int headerLines = table.HasFullData ? 3 : 2;
        int columnIndex = snapshot.Columns
            .Where(column =>
                string.Equals(column.SchemaName, table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(column.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(column => column.Ordinal)
            .TakeWhile(column => !string.Equals(column.ColumnName, targetColumn.ColumnName, StringComparison.OrdinalIgnoreCase))
            .Count();

        return headerLines + columnIndex + 1;
    }

    private static bool TryParsePath(
        string documentPath,
        out string snapshotKey,
        out string documentType,
        out string kind,
        out string fullName)
    {
        snapshotKey = string.Empty;
        documentType = string.Empty;
        kind = string.Empty;
        fullName = string.Empty;

        if (!documentPath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = documentPath[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        snapshotKey = parts[0];
        documentType = parts[1];

        if (string.Equals(documentType, "object", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 4)
            {
                return false;
            }

            kind = parts[2];
            fullName = UnescapeName(parts[3]);
            return true;
        }

        if (string.Equals(documentType, "table", StringComparison.OrdinalIgnoreCase))
        {
            fullName = UnescapeName(parts[2]);
            return true;
        }

        if (string.Equals(documentType, "table-data", StringComparison.OrdinalIgnoreCase))
        {
            fullName = UnescapeName(parts[2]);
            return true;
        }

        return false;
    }

    private static string CreateSnapshotDocumentSegment(DatabaseMetadataSnapshot snapshot)
    {
        return EscapeSnapshotDocumentSegment(GetSnapshotDocumentName(snapshot));
    }

    private static string CreateObjectDocumentPath(
        DatabaseMetadataSnapshot snapshot,
        SqlDatabaseObjectKind objectKind,
        string fullName)
    {
        return $"{Prefix}{CreateSnapshotDocumentSegment(snapshot)}/object/{objectKind}/{Uri.EscapeDataString(fullName)}.sql";
    }

    private static string CreateTableDocumentPath(DatabaseMetadataSnapshot snapshot, string fullName)
    {
        return $"{Prefix}{CreateSnapshotDocumentSegment(snapshot)}/table/{Uri.EscapeDataString(fullName)}.sql";
    }

    private static string CreateTableDataDocumentPath(DatabaseMetadataSnapshot snapshot, string fullName)
    {
        return $"{Prefix}{CreateSnapshotDocumentSegment(snapshot)}/table-data/{Uri.EscapeDataString(fullName)}.csv";
    }

    private static string GetSnapshotDocumentName(DatabaseMetadataSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.DisplayName) &&
            !string.Equals(snapshot.DisplayName, "Database Snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.DatabaseName))
        {
            return snapshot.DatabaseName.Trim();
        }

        return string.IsNullOrWhiteSpace(snapshot.DisplayName)
            ? snapshot.SnapshotId
            : snapshot.DisplayName.Trim();
    }

    private static string EscapeSnapshotDocumentSegment(string value)
    {
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("/", "%2F", StringComparison.Ordinal)
            .Replace("\\", "%5C", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal);
    }

    private static string UnescapeSegment(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string UnescapeName(string value)
    {
        if (value.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        if (value.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return Uri.UnescapeDataString(value);
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

    private static string FormatJsonValue(JsonElement value)
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

    private static string EscapeCsvValue(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal) ||
            value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('\r') ||
            value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }

    private static string FormatDataType(SqlColumn column)
    {
        string dataType = column.DataType;
        return dataType.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{dataType}({FormatLength(column.MaxLength)})",
            "nvarchar" or "nchar" => $"{dataType}({FormatLength(column.MaxLength / 2)})",
            "decimal" or "numeric" => $"{dataType}({column.NumericPrecision},{column.NumericScale})",
            _ => dataType
        };
    }

    private static string FormatLength(int length)
    {
        return length < 0 ? "max" : length.ToString();
    }
}

public sealed record DatabaseDocumentReference(
    string SnapshotId,
    string DocumentType,
    SqlDatabaseObjectKind? ObjectKind,
    string FullName)
{
    public static DatabaseDocumentReference Empty { get; } = new(string.Empty, string.Empty, null, string.Empty);
}

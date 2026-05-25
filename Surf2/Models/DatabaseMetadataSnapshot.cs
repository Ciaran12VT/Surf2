using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surf2.Models;

public sealed class DatabaseMetadataSnapshot
{
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "Database Snapshot";

    public string DatabaseName { get; set; } = string.Empty;

    public DateTimeOffset ImportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<SqlDatabaseObject> Objects { get; set; } = [];

    public List<SqlTable> Tables { get; set; } = [];

    public List<SqlColumn> Columns { get; set; } = [];

    public List<SqlPrimaryKeyColumn> PrimaryKeys { get; set; } = [];

    public List<SqlTableDataSet> TableDataSets { get; set; } = [];

    public List<string> FullDataTableNames { get; set; } = [];

    [JsonIgnore]
    public DatabaseImportCounts Counts => DatabaseImportCounts.FromSnapshot(this);

    public SqlTableDataSet? GetTableDataSet(string schemaName, string tableName)
    {
        return TableDataSets.FirstOrDefault(dataSet =>
            string.Equals(dataSet.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(dataSet.TableName, tableName, StringComparison.OrdinalIgnoreCase));
    }

    public DatabaseMetadataSnapshot Clone()
    {
        return new DatabaseMetadataSnapshot
        {
            SnapshotId = SnapshotId,
            DisplayName = DisplayName,
            DatabaseName = DatabaseName,
            ImportedAtUtc = ImportedAtUtc,
            Objects = Objects.Select(item => new SqlDatabaseObject
            {
                SchemaName = item.SchemaName,
                ObjectName = item.ObjectName,
                Kind = item.Kind,
                TypeDescription = item.TypeDescription,
                Definition = item.Definition,
                ParentSchemaName = item.ParentSchemaName,
                ParentObjectName = item.ParentObjectName
            }).ToList(),
            Tables = Tables.Select(table => new SqlTable
            {
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                HasFullData = table.HasFullData,
                FullDataRowCount = table.FullDataRowCount,
                FullDataImportedAtUtc = table.FullDataImportedAtUtc
            }).ToList(),
            Columns = Columns.Select(column => new SqlColumn
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
            }).ToList(),
            PrimaryKeys = PrimaryKeys.Select(primaryKey => new SqlPrimaryKeyColumn
            {
                SchemaName = primaryKey.SchemaName,
                TableName = primaryKey.TableName,
                ConstraintName = primaryKey.ConstraintName,
                ColumnName = primaryKey.ColumnName,
                KeyOrdinal = primaryKey.KeyOrdinal
            }).ToList(),
            TableDataSets = TableDataSets.Select(dataSet => new SqlTableDataSet
            {
                SchemaName = dataSet.SchemaName,
                TableName = dataSet.TableName,
                RowCount = dataSet.RowCount,
                ImportedAtUtc = dataSet.ImportedAtUtc,
                Rows = dataSet.Rows.Select(row => row.Clone()).ToList()
            }).ToList(),
            FullDataTableNames = (FullDataTableNames ?? []).ToList()
        };
    }
}

public enum DatabaseEntityEditKind
{
    StoredProcedures,
    Views,
    Functions,
    Triggers,
    Tables,
    Fields,
    PrimaryKeys,
    FullDataTables
}

public sealed class SqlDatabaseObject
{
    public string SchemaName { get; set; } = "dbo";

    public string ObjectName { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SqlDatabaseObjectKind Kind { get; set; }

    public string TypeDescription { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public string ParentSchemaName { get; set; } = string.Empty;

    public string ParentObjectName { get; set; } = string.Empty;

    [JsonIgnore]
    public string FullName => SqlName.FormatMultipartName(SchemaName, ObjectName);
}

public enum SqlDatabaseObjectKind
{
    Unknown,
    StoredProcedure,
    View,
    Function,
    Trigger
}

public sealed class SqlTable
{
    public string SchemaName { get; set; } = "dbo";

    public string TableName { get; set; } = string.Empty;

    public bool HasFullData { get; set; }

    public long FullDataRowCount { get; set; }

    public DateTimeOffset? FullDataImportedAtUtc { get; set; }

    [JsonIgnore]
    public string FullName => SqlName.FormatMultipartName(SchemaName, TableName);
}

public sealed class SqlColumn
{
    public string SchemaName { get; set; } = "dbo";

    public string TableName { get; set; } = string.Empty;

    public string ColumnName { get; set; } = string.Empty;

    public string DataType { get; set; } = string.Empty;

    public int MaxLength { get; set; }

    public byte NumericPrecision { get; set; }

    public int NumericScale { get; set; }

    public bool IsNullable { get; set; }

    public bool IsIdentity { get; set; }

    public int Ordinal { get; set; }

    [JsonIgnore]
    public string TableFullName => SqlName.FormatMultipartName(SchemaName, TableName);

    [JsonIgnore]
    public string FullName => $"{TableFullName}.{SqlName.Bracket(ColumnName)}";
}

public sealed class SqlPrimaryKeyColumn
{
    public string SchemaName { get; set; } = "dbo";

    public string TableName { get; set; } = string.Empty;

    public string ConstraintName { get; set; } = string.Empty;

    public string ColumnName { get; set; } = string.Empty;

    public int KeyOrdinal { get; set; }
}

public sealed class SqlTableDataSet
{
    public string SchemaName { get; set; } = "dbo";

    public string TableName { get; set; } = string.Empty;

    public long RowCount { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<JsonElement> Rows { get; set; } = [];

    [JsonIgnore]
    public string FullName => SqlName.FormatMultipartName(SchemaName, TableName);
}

public sealed class DatabaseImportCounts
{
    public int StoredProcedures { get; set; }

    public int Views { get; set; }

    public int Functions { get; set; }

    public int Triggers { get; set; }

    public int Tables { get; set; }

    public int Fields { get; set; }

    public int PrimaryKeys { get; set; }

    public int FullDataTables { get; set; }

    public long DataRows { get; set; }

    public static DatabaseImportCounts FromSnapshot(DatabaseMetadataSnapshot snapshot)
    {
        return new DatabaseImportCounts
        {
            StoredProcedures = snapshot.Objects.Count(item => item.Kind == SqlDatabaseObjectKind.StoredProcedure),
            Views = snapshot.Objects.Count(item => item.Kind == SqlDatabaseObjectKind.View),
            Functions = snapshot.Objects.Count(item => item.Kind == SqlDatabaseObjectKind.Function),
            Triggers = snapshot.Objects.Count(item => item.Kind == SqlDatabaseObjectKind.Trigger),
            Tables = snapshot.Tables.Count,
            Fields = snapshot.Columns.Count,
            PrimaryKeys = snapshot.PrimaryKeys.Select(key => $"{key.SchemaName}.{key.TableName}.{key.ConstraintName}").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            FullDataTables = snapshot.TableDataSets
                .Select(dataSet => $"{dataSet.SchemaName}.{dataSet.TableName}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            DataRows = snapshot.TableDataSets
                .GroupBy(dataSet => $"{dataSet.SchemaName}.{dataSet.TableName}", StringComparer.OrdinalIgnoreCase)
                .Sum(group => group.Max(dataSet => Math.Max(dataSet.RowCount, dataSet.Rows.Count)))
        };
    }
}

public static class SqlName
{
    public static string Bracket(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "[]";
        }

        return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static string FormatMultipartName(string schemaName, string objectName)
    {
        return $"{Bracket(string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName)}.{Bracket(objectName)}";
    }

    public static string FormatPlainMultipartName(string schemaName, string objectName)
    {
        string schema = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName;
        return $"{schema}.{objectName}";
    }
}

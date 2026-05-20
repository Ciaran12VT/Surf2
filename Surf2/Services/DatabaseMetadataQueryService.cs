using System.Text;
using Surf2.Models;

namespace Surf2.Services;

public sealed class DatabaseMetadataQueryService
{
    private const int ChunkSize = 30000;

    public string CreateImportQuery(IEnumerable<string> fullDataTableNames)
    {
        List<SqlTableName> fullDataTables = fullDataTableNames
            .Select(SqlTableName.TryParse)
            .Where(table => table != null)
            .Cast<SqlTableName>()
            .DistinctBy(table => $"{table.SchemaName}.{table.TableName}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table.SchemaName)
            .ThenBy(table => table.TableName)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine(CreateMetadataQueryHeader());

        foreach (SqlTableName table in fullDataTables)
        {
            builder.AppendLine();
            builder.AppendLine(CreateFullTableDataBlock(table));
        }

        builder.AppendLine();
        builder.AppendLine(CreateChunkedOutputFooter());
        return builder.ToString();
    }

    private static string CreateMetadataQueryHeader()
    {
        return $$"""
SET NOCOUNT ON;

DECLARE @Surf2ChunkSize int = {{ChunkSize}};

IF OBJECT_ID('tempdb..#Surf2Payloads') IS NOT NULL
BEGIN
    DROP TABLE #Surf2Payloads;
END;

CREATE TABLE #Surf2Payloads
(
    EntityType nvarchar(50) NOT NULL,
    EntityKey nvarchar(512) NOT NULL,
    PayloadJson nvarchar(max) NOT NULL
);

INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
SELECT
    N'Database',
    DB_NAME(),
    (
        SELECT DB_NAME() AS [DatabaseName]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
SELECT
    N'SqlObject',
    CONCAT(SCHEMA_NAME(o.schema_id), N'.', o.name),
    (
        SELECT
            SCHEMA_NAME(o.schema_id) AS [SchemaName],
            o.name AS [ObjectName],
            o.type_desc AS [TypeDescription],
            ISNULL(m.definition, N'') AS [Definition],
            ISNULL(SCHEMA_NAME(parent.schema_id), N'') AS [ParentSchemaName],
            ISNULL(parent.name, N'') AS [ParentObjectName]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    )
FROM sys.objects o
INNER JOIN sys.sql_modules m
    ON o.object_id = m.object_id
LEFT JOIN sys.objects parent
    ON o.parent_object_id = parent.object_id
WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR')
  AND OBJECTPROPERTY(o.object_id, 'IsMSShipped') = 0
  AND SCHEMA_NAME(o.schema_id) <> N'sys';

INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
SELECT
    N'Table',
    CONCAT(SCHEMA_NAME(t.schema_id), N'.', t.name),
    (
        SELECT
            SCHEMA_NAME(t.schema_id) AS [SchemaName],
            t.name AS [TableName]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    )
FROM sys.tables t
WHERE t.is_ms_shipped = 0
  AND SCHEMA_NAME(t.schema_id) <> N'sys';

INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
SELECT
    N'Column',
    CONCAT(SCHEMA_NAME(t.schema_id), N'.', t.name, N'.', c.name),
    (
        SELECT
            SCHEMA_NAME(t.schema_id) AS [SchemaName],
            t.name AS [TableName],
            c.name AS [ColumnName],
            ty.name AS [DataType],
            c.max_length AS [MaxLength],
            c.precision AS [NumericPrecision],
            c.scale AS [NumericScale],
            c.is_nullable AS [IsNullable],
            c.is_identity AS [IsIdentity],
            c.column_id AS [Ordinal]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    )
FROM sys.tables t
INNER JOIN sys.columns c
    ON t.object_id = c.object_id
INNER JOIN sys.types ty
    ON c.user_type_id = ty.user_type_id
WHERE t.is_ms_shipped = 0
  AND SCHEMA_NAME(t.schema_id) <> N'sys';

INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
SELECT
    N'PrimaryKey',
    CONCAT(SCHEMA_NAME(t.schema_id), N'.', t.name, N'.', kc.name, N'.', c.name),
    (
        SELECT
            SCHEMA_NAME(t.schema_id) AS [SchemaName],
            t.name AS [TableName],
            kc.name AS [ConstraintName],
            c.name AS [ColumnName],
            ic.key_ordinal AS [KeyOrdinal]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    )
FROM sys.key_constraints kc
INNER JOIN sys.tables t
    ON kc.parent_object_id = t.object_id
INNER JOIN sys.index_columns ic
    ON kc.parent_object_id = ic.object_id
   AND kc.unique_index_id = ic.index_id
INNER JOIN sys.columns c
    ON ic.object_id = c.object_id
   AND ic.column_id = c.column_id
WHERE kc.type = 'PK'
  AND t.is_ms_shipped = 0
  AND SCHEMA_NAME(t.schema_id) <> N'sys';
""";
    }

    private static string CreateFullTableDataBlock(SqlTableName table)
    {
        string bracketedTableName = SqlName.FormatMultipartName(table.SchemaName, table.TableName);
        string tableKey = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
        string tableObjectIdLiteral = ToSqlStringLiteral(bracketedTableName);
        string tableKeyLiteral = ToSqlStringLiteral(tableKey);
        string schemaLiteral = ToSqlStringLiteral(table.SchemaName);
        string tableLiteral = ToSqlStringLiteral(table.TableName);

        return $$"""
IF OBJECT_ID(N{{tableObjectIdLiteral}}, N'U') IS NOT NULL
BEGIN
    DECLARE @Surf2RowCount_{{table.VariableSuffix}} bigint;
    SELECT @Surf2RowCount_{{table.VariableSuffix}} = COUNT_BIG(*) FROM {{bracketedTableName}};

    INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
    SELECT
        N'TableDataSet',
        N{{tableKeyLiteral}},
        (
            SELECT
                N{{schemaLiteral}} AS [SchemaName],
                N{{tableLiteral}} AS [TableName],
                @Surf2RowCount_{{table.VariableSuffix}} AS [RowCount]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

    INSERT INTO #Surf2Payloads (EntityType, EntityKey, PayloadJson)
    SELECT
        N'TableDataRow',
        CONCAT(N{{tableKeyLiteral}}, N'#', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))),
        (
            SELECT rowData.*
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        )
    FROM {{bracketedTableName}} AS rowData;
END;
""";
    }

    private static string CreateChunkedOutputFooter()
    {
        return """
DECLARE @Surf2MaxChunkCount int;

SELECT @Surf2MaxChunkCount = ISNULL(MAX(
    CASE
        WHEN LEN(PayloadJson) = 0 THEN 1
        ELSE CONVERT(int, CEILING(CONVERT(decimal(19, 4), LEN(PayloadJson)) / @Surf2ChunkSize))
    END), 0)
FROM #Surf2Payloads;

;WITH Digit AS
(
    SELECT Value
    FROM (VALUES (0), (0), (0), (0), (0), (0), (0), (0), (0), (0)) AS DigitSource(Value)
),
NumberSource AS
(
    SELECT TOP (@Surf2MaxChunkCount)
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS ChunkIndex
    FROM Digit a
    CROSS JOIN Digit b
    CROSS JOIN Digit c
    CROSS JOIN Digit d
    CROSS JOIN Digit e
),
Payloads AS
(
    SELECT
        EntityType,
        EntityKey,
        PayloadJson,
        CASE
            WHEN LEN(PayloadJson) = 0 THEN 1
            ELSE CONVERT(int, CEILING(CONVERT(decimal(19, 4), LEN(PayloadJson)) / @Surf2ChunkSize))
        END AS ChunkCount
    FROM #Surf2Payloads
)
SELECT
    p.EntityType,
    p.EntityKey,
    n.ChunkIndex,
    p.ChunkCount,
    SUBSTRING(p.PayloadJson, (n.ChunkIndex * @Surf2ChunkSize) + 1, @Surf2ChunkSize) AS PayloadJsonChunk
FROM Payloads p
INNER JOIN NumberSource n
    ON n.ChunkIndex < p.ChunkCount
ORDER BY
    p.EntityType,
    p.EntityKey,
    n.ChunkIndex;

DROP TABLE #Surf2Payloads;
""";
    }

    private static string ToSqlStringLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private sealed class SqlTableName
    {
        private SqlTableName(string schemaName, string tableName)
        {
            SchemaName = schemaName;
            TableName = tableName;
            VariableSuffix = new string($"{schemaName}_{tableName}"
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray());

            if (string.IsNullOrWhiteSpace(VariableSuffix))
            {
                VariableSuffix = "Table";
            }
        }

        public string SchemaName { get; }

        public string TableName { get; }

        public string VariableSuffix { get; }

        public static SqlTableName? TryParse(string? value)
        {
            string cleaned = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return null;
            }

            cleaned = cleaned
                .Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal);

            string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length switch
            {
                1 => new SqlTableName("dbo", parts[0]),
                >= 2 => new SqlTableName(parts[^2], parts[^1]),
                _ => null
            };
        }
    }
}

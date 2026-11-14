using System.Text;
using Surf2.Models;

namespace Surf2.Services;

public sealed partial class DatabaseMetadataQueryService
{
    // SQL Server 2012/2014 have no FOR JSON. Stage scalar fields and encode JSON
    // without installing functions or other objects in the source database.
    internal string CreateLegacyImportQuery(IEnumerable<string> fullDataTableNames)
    {
        var tables = fullDataTableNames.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(ParseFullTableDataSelection)
            .DistinctBy(table => SqlName.FormatMultipartName(table.SchemaName, table.TableName), StringComparer.OrdinalIgnoreCase)
            .OrderBy(table => table.SchemaName).ThenBy(table => table.TableName).ToList();
        var sql = new StringBuilder(LegacyPreamble());
        AppendLegacyOrigin(sql);
        AppendLegacyMetadata(sql, null, "", "", "", "", "");
        foreach (var table in tables) AppendLegacyTableData(sql, table);
        return sql.AppendLine(LegacyEncodeFieldsSql).AppendLine(CreateChunkedOutputFooter()).AppendLine("DROP TABLE #Surf2LegacyFields;").ToString();
    }

    internal string CreateLegacyEntityQuery(DatabaseEntityEditKind kind, string schemaName, string entityName,
        string tableName = "", string columnName = "", string constraintName = "", string? fullDataTableSelection = null)
    {
        string schema = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName.Trim();
        var sql = new StringBuilder(LegacyPreamble());
        AppendLegacyOrigin(sql);
        AppendLegacyMetadata(sql, kind, schema, entityName.Trim(), tableName.Trim(), columnName.Trim(), constraintName.Trim());
        if (kind == DatabaseEntityEditKind.FullDataTables)
            AppendLegacyTableData(sql, string.IsNullOrWhiteSpace(fullDataTableSelection)
                ? FullTableDataSelection.FromParts(schema, entityName.Trim()) : ParseFullTableDataSelection(fullDataTableSelection));
        return sql.AppendLine(LegacyEncodeFieldsSql).AppendLine(CreateChunkedOutputFooter()).AppendLine("DROP TABLE #Surf2LegacyFields;").ToString();
    }

    internal static string CreateLegacyOriginQuery()
    {
        var sql = new StringBuilder(LegacyPreamble());
        AppendLegacyOrigin(sql);
        return sql.AppendLine(LegacyEncodeFieldsSql).AppendLine("""
            SELECT PayloadJson AS Result FROM #Surf2Payloads WHERE EntityType=N'DatabaseOrigin';
            DROP TABLE #Surf2Payloads;
            DROP TABLE #Surf2LegacyFields;
            """).ToString();
    }

    private static string LegacyPreamble() => $$"""
        SET NOCOUNT ON;
        SET QUOTED_IDENTIFIER ON;
        DECLARE @Surf2ChunkSize int={{ChunkSize}};
        IF OBJECT_ID('tempdb..#Surf2Payloads') IS NOT NULL DROP TABLE #Surf2Payloads;
        IF OBJECT_ID('tempdb..#Surf2LegacyFields') IS NOT NULL DROP TABLE #Surf2LegacyFields;
        CREATE TABLE #Surf2Payloads(EntityType nvarchar(50) COLLATE database_default NOT NULL,EntityKey nvarchar(512) COLLATE database_default NOT NULL,PayloadJson nvarchar(max) NOT NULL);
        CREATE TABLE #Surf2LegacyFields(FieldId bigint IDENTITY NOT NULL PRIMARY KEY,EntityType nvarchar(50) COLLATE database_default NOT NULL,
            EntityKey nvarchar(512) COLLATE database_default NOT NULL,PropertyOrdinal int NOT NULL,PropertyName nvarchar(128) COLLATE database_default NOT NULL,
            ScalarKind nchar(1) NOT NULL,Value nvarchar(max) NULL,NameJson nvarchar(max) NULL,ValueJson nvarchar(max) NULL,
            EntityHash binary(32) NULL);
        DECLARE @Surf2LegacyColumns nvarchar(max),@Surf2LegacyDataSql nvarchar(max),@Surf2LegacyRowId sysname,
            @Surf2LegacyColumnCount int,@Surf2LegacyRowCount bigint;
        """;

    private static void AppendLegacyOrigin(StringBuilder sql)
    {
        sql.AppendLine(DatabaseOriginQueryService.ReadVariablesSql);
        AppendLegacyFields(sql, "DatabaseOrigin", "DB_NAME()", "", "",
            ("Format", "N'Surf2.DatabaseOrigin'", 'S'), ("Version", "1", 'N'),
            ("Engine", "CASE WHEN @edition=5 THEN N'AzureSqlDatabase' WHEN @edition=8 THEN N'AzureSqlManagedInstance' WHEN @edition IN (2,3,4) THEN N'SqlServer' ELSE N'Unknown' END", 'S'),
            ("Server", "CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))", 'S'), ("Database", "DB_NAME()", 'S'),
            ("IdentifierKind", "@kind", 'S'), ("Identifier", "COALESCE(@identifier,N'')", 'S'),
            ("Availability", "@availability", 'S'), ("ObservedAtUtc", "CONVERT(nvarchar(40),SYSUTCDATETIME(),127)", 'S'));
    }

    private static void AppendLegacyMetadata(StringBuilder sql, DatabaseEntityEditKind? kind,
        string schema, string name, string table, string column, string constraint)
    {
        AppendLegacyFields(sql, "Database", "DB_NAME()", "", "", ("DatabaseName", "DB_NAME()", 'S'));
        string TableWhere(string selected) => "t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)<>N'sys'" +
            (kind == null ? "" : $" AND SCHEMA_NAME(t.schema_id)=N{ToSqlStringLiteral(schema)} AND t.name=N{ToSqlStringLiteral(selected)}");
        string tableKey = "CONCAT(SCHEMA_NAME(t.schema_id),N'.',t.name)";
        if (kind == null || kind is DatabaseEntityEditKind.StoredProcedures or DatabaseEntityEditKind.Views or DatabaseEntityEditKind.Functions or DatabaseEntityEditKind.Triggers)
        {
            string types = kind switch { DatabaseEntityEditKind.StoredProcedures => "'P'", DatabaseEntityEditKind.Views => "'V'",
                DatabaseEntityEditKind.Functions => "'FN','IF','TF'", DatabaseEntityEditKind.Triggers => "'TR'", _ => "'P','V','FN','IF','TF','TR'" };
            AppendLegacyFields(sql, "SqlObject", "CONCAT(SCHEMA_NAME(o.schema_id),N'.',o.name)",
                "sys.objects o INNER JOIN sys.sql_modules m ON o.object_id=m.object_id LEFT JOIN sys.objects parent ON o.parent_object_id=parent.object_id",
                $"o.type IN ({types}) AND OBJECTPROPERTY(o.object_id,'IsMSShipped')=0 AND SCHEMA_NAME(o.schema_id)<>N'sys'" +
                (kind == null ? "" : $" AND SCHEMA_NAME(o.schema_id)=N{ToSqlStringLiteral(schema)} AND o.name=N{ToSqlStringLiteral(name)}"),
                ("SchemaName", "SCHEMA_NAME(o.schema_id)", 'S'), ("ObjectName", "o.name", 'S'), ("TypeDescription", "o.type_desc", 'S'),
                ("Definition", "ISNULL(m.definition,N'')", 'S'), ("ParentSchemaName", "ISNULL(SCHEMA_NAME(parent.schema_id),N'')", 'S'), ("ParentObjectName", "ISNULL(parent.name,N'')", 'S'));
        }
        if (kind == null || kind is DatabaseEntityEditKind.Tables or DatabaseEntityEditKind.FullDataTables)
            AppendLegacyFields(sql, "Table", tableKey, "sys.tables t", TableWhere(name),
                ("SchemaName", "SCHEMA_NAME(t.schema_id)", 'S'), ("TableName", "t.name", 'S'));
        if (kind == null || kind is DatabaseEntityEditKind.Tables or DatabaseEntityEditKind.FullDataTables or DatabaseEntityEditKind.Fields)
            AppendLegacyFields(sql, "Column", "CONCAT(SCHEMA_NAME(t.schema_id),N'.',t.name,N'.',c.name)",
                "sys.tables t INNER JOIN sys.columns c ON t.object_id=c.object_id INNER JOIN sys.types ty ON c.user_type_id=ty.user_type_id",
                TableWhere(kind == DatabaseEntityEditKind.Fields ? table : name) +
                (kind == DatabaseEntityEditKind.Fields ? $" AND c.name=N{ToSqlStringLiteral(column)}" : ""),
                ("SchemaName", "SCHEMA_NAME(t.schema_id)", 'S'), ("TableName", "t.name", 'S'), ("ColumnName", "c.name", 'S'),
                ("DataType", "ty.name", 'S'), ("MaxLength", "c.max_length", 'N'), ("NumericPrecision", "c.precision", 'N'),
                ("NumericScale", "c.scale", 'N'), ("IsNullable", "c.is_nullable", 'B'), ("IsIdentity", "c.is_identity", 'B'), ("Ordinal", "c.column_id", 'N'));
        if (kind == null || kind is DatabaseEntityEditKind.Tables or DatabaseEntityEditKind.FullDataTables or DatabaseEntityEditKind.PrimaryKeys)
            AppendLegacyFields(sql, "PrimaryKey", "CONCAT(SCHEMA_NAME(t.schema_id),N'.',t.name,N'.',kc.name,N'.',c.name)",
                "sys.key_constraints kc INNER JOIN sys.tables t ON kc.parent_object_id=t.object_id INNER JOIN sys.index_columns ic ON kc.parent_object_id=ic.object_id AND kc.unique_index_id=ic.index_id INNER JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id",
                "kc.type='PK' AND " + TableWhere(kind == DatabaseEntityEditKind.PrimaryKeys ? table : name) +
                (kind == DatabaseEntityEditKind.PrimaryKeys ? $" AND kc.name=N{ToSqlStringLiteral(constraint)}" : ""),
                ("SchemaName", "SCHEMA_NAME(t.schema_id)", 'S'), ("TableName", "t.name", 'S'), ("ConstraintName", "kc.name", 'S'),
                ("ColumnName", "c.name", 'S'), ("KeyOrdinal", "ic.key_ordinal", 'N'));
    }

    private static void AppendLegacyFields(StringBuilder sql, string entityType, string entityKey, string from, string where,
        params (string Name, string Expression, char Kind)[] fields)
    {
        sql.AppendLine("INSERT #Surf2LegacyFields(EntityType,EntityKey,PropertyOrdinal,PropertyName,ScalarKind,Value)");
        sql.Append($"SELECT N{ToSqlStringLiteral(entityType)},{entityKey},f.* FROM ");
        sql.Append(string.IsNullOrEmpty(from) ? "(SELECT 1 AS unused) source" : from).AppendLine(" CROSS APPLY(VALUES");
        sql.AppendLine(string.Join(",\n", fields.Select((field, ordinal) =>
            $"({ordinal},N{ToSqlStringLiteral(field.Name)},N'{field.Kind}'," +
            (field.Kind == 'B' ? $"CASE WHEN {field.Expression} IS NULL THEN NULL WHEN {field.Expression}=1 THEN N'true' ELSE N'false' END" : $"CONVERT(nvarchar(max),{field.Expression})") + ")")));
        sql.Append(") f(PropertyOrdinal,PropertyName,ScalarKind,Value)");
        if (!string.IsNullOrEmpty(where)) sql.Append(" WHERE ").Append(where);
        sql.AppendLine(";");
    }

    private static void AppendLegacyTableData(StringBuilder sql, FullTableDataSelection table)
    {
        string quoted = SqlName.FormatMultipartName(table.SchemaName, table.TableName);
        string key = SqlName.FormatPlainMultipartName(table.SchemaName, table.TableName);
        string selection = quoted + (table.TopCount is int count ? " TOP " + count : table.WhereClause != null ? " WHERE " + table.WhereClause : "");
        AppendLegacyFields(sql, "FullDataSelection", "N" + ToSqlStringLiteral(key), "", "", ("Selection", "N" + ToSqlStringLiteral(selection), 'S'));
        sql.AppendLine($"IF OBJECT_ID(N{ToSqlStringLiteral(quoted)},N'U') IS NOT NULL BEGIN");
        sql.AppendLine($"IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N{ToSqlStringLiteral(quoted)}) AND system_type_id=240) BEGIN RAISERROR(N'Legacy snapshot table data cannot serialize CLR types. Convert them in the source or omit this table.',16,1); RETURN; END;");
        sql.AppendLine("SET @Surf2LegacyRowId=N'__Surf2RowId_'+REPLACE(CONVERT(nvarchar(36),NEWID()),N'-',N'');");
        sql.AppendLine($"WHILE EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N{ToSqlStringLiteral(quoted)}) AND name=@Surf2LegacyRowId) SET @Surf2LegacyRowId=N'__Surf2RowId_'+REPLACE(CONVERT(nvarchar(36),NEWID()),N'-',N'');");
        sql.AppendLine($$"""
            SELECT @Surf2LegacyColumnCount=COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N{{ToSqlStringLiteral(quoted)}});
            SELECT @Surf2LegacyColumns=STUFF((SELECT N',('+CONVERT(nvarchar(12),c.column_id)+N',N'''+REPLACE(c.name,N'''',N'''''')+N''','+
              CASE WHEN c.system_type_id=98 THEN REPLACE(N{{ToSqlStringLiteral(LegacyVariantKindExpression)}},N'__COLUMN__',N'rowData.'+QUOTENAME(c.name))
                ELSE N'N'''+CASE WHEN c.system_type_id IN (48,52,56,59,60,62,106,108,122,127) THEN N'N' WHEN c.system_type_id=104 THEN N'B' ELSE N'S' END+N'''' END+N','+
              N'CASE WHEN rowData.'+QUOTENAME(c.name)+N' IS NULL THEN NULL ELSE '+
              CASE WHEN c.system_type_id=104 THEN N'CASE WHEN rowData.'+QUOTENAME(c.name)+N'=1 THEN N''true'' ELSE N''false'' END'
                WHEN c.system_type_id=98 THEN REPLACE(N{{ToSqlStringLiteral(LegacyVariantValueExpression)}},N'__COLUMN__',N'rowData.'+QUOTENAME(c.name))
                WHEN c.system_type_id IN (34,165,173,189) THEN N'(SELECT rowData.'+QUOTENAME(c.name)+N' AS [text()] FOR XML PATH(''''),BINARY BASE64,TYPE).value(''.'',''nvarchar(max)'')'
                WHEN c.system_type_id IN (59,62) THEN N'(SELECT rowData.'+QUOTENAME(c.name)+N' AS [text()] FOR XML PATH(''''),TYPE).value(''.'',''nvarchar(max)'')'
                WHEN c.system_type_id=43 THEN N'CONVERT(nvarchar(max),rowData.'+QUOTENAME(c.name)+N',126)'
                WHEN c.system_type_id IN (40,41,42,58,61) THEN N'CONVERT(nvarchar(max),rowData.'+QUOTENAME(c.name)+N',127)'
                WHEN c.system_type_id IN (60,122) THEN N'CONVERT(nvarchar(max),rowData.'+QUOTENAME(c.name)+N',2)'
                ELSE N'CONVERT(nvarchar(max),rowData.'+QUOTENAME(c.name)+N')' END+N' END)'
              FROM sys.columns c WHERE c.object_id=OBJECT_ID(N{{ToSqlStringLiteral(quoted)}}) ORDER BY c.column_id
              FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,1,N'');
            """);
        string select = "SELECT" + (table.TopCount is int top ? " TOP (" + top + ")" : "") +
            " ROW_NUMBER() OVER(ORDER BY(SELECT NULL)) AS " ;
        string rest = ",* FROM " + quoted + (table.WhereClause != null ? " WHERE " + table.WhereClause : "");
        string prefix = "INSERT #Surf2LegacyFields(EntityType,EntityKey,PropertyOrdinal,PropertyName,ScalarKind,Value) SELECT N'TableDataRow',CONCAT(N" +
            ToSqlStringLiteral(key) + ",N'#',rowData.";
        sql.AppendLine($"SET @Surf2LegacyDataSql=N{ToSqlStringLiteral(prefix)}+QUOTENAME(@Surf2LegacyRowId)+N'),f.* FROM ('+N{ToSqlStringLiteral(select)}+QUOTENAME(@Surf2LegacyRowId)+N{ToSqlStringLiteral(rest)}+N') rowData CROSS APPLY(VALUES '+@Surf2LegacyColumns+N') f(PropertyOrdinal,PropertyName,ScalarKind,Value); SET @rowCount=ROWCOUNT_BIG()/@columnCount;';");
        sql.AppendLine("EXEC sys.sp_executesql @Surf2LegacyDataSql,N'@rowCount bigint OUTPUT,@columnCount int',@rowCount=@Surf2LegacyRowCount OUTPUT,@columnCount=@Surf2LegacyColumnCount;");
        AppendLegacyFields(sql, "TableDataSet", "N" + ToSqlStringLiteral(key), "", "",
            ("SchemaName", "N" + ToSqlStringLiteral(table.SchemaName), 'S'), ("TableName", "N" + ToSqlStringLiteral(table.TableName), 'S'), ("RowCount", "@Surf2LegacyRowCount", 'N'));
        sql.AppendLine("END;");
    }

    private const string LegacyEncodeFieldsSql = """
        UPDATE #Surf2LegacyFields SET EntityHash=CONVERT(binary(32),HASHBYTES('SHA2_256',EntityType+N':'+EntityKey));
        CREATE INDEX IX_Surf2LegacyFields_Entity ON #Surf2LegacyFields(EntityHash,PropertyOrdinal);
        DECLARE @Surf2FieldId bigint,@Surf2FieldName nvarchar(128),@Surf2FieldValue nvarchar(max),@Surf2FieldKind nchar(1),
            @Surf2Input nvarchar(max),@Surf2Escaped nvarchar(max),@Surf2Position int,@Surf2Start int,@Surf2Length int,@Surf2Code int,@Surf2Part int;
        DECLARE Surf2FieldsCursor CURSOR LOCAL FAST_FORWARD FOR SELECT FieldId,PropertyName,ScalarKind,Value FROM #Surf2LegacyFields WHERE Value IS NOT NULL;
        OPEN Surf2FieldsCursor;
        FETCH NEXT FROM Surf2FieldsCursor INTO @Surf2FieldId,@Surf2FieldName,@Surf2FieldKind,@Surf2FieldValue;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SET @Surf2Part=0;
            WHILE @Surf2Part<2
            BEGIN
                IF @Surf2Part=1 AND @Surf2FieldKind<>N'S'
                    UPDATE #Surf2LegacyFields SET ValueJson=@Surf2FieldValue WHERE FieldId=@Surf2FieldId;
                ELSE
                BEGIN
                    SET @Surf2Input=CASE WHEN @Surf2Part=0 THEN @Surf2FieldName ELSE @Surf2FieldValue END;
                    SET @Surf2Input=REPLACE(REPLACE(@Surf2Input,N'\',N'\\'),N'"',N'\"');
                    SET @Surf2Length=DATALENGTH(@Surf2Input)/2;
                    SET @Surf2Escaped=CONVERT(nvarchar(max),N'"'); SET @Surf2Position=1; SET @Surf2Start=1;
                    WHILE @Surf2Position<=@Surf2Length
                    BEGIN
                        SET @Surf2Code=UNICODE(SUBSTRING(@Surf2Input COLLATE Latin1_General_100_BIN2,@Surf2Position,1));
                        IF @Surf2Code<32 OR @Surf2Code BETWEEN 55296 AND 57343 OR @Surf2Code>=65534
                        BEGIN
                            SET @Surf2Escaped=@Surf2Escaped+SUBSTRING(@Surf2Input COLLATE Latin1_General_100_BIN2,@Surf2Start,@Surf2Position-@Surf2Start)+N'\u'+CONVERT(nvarchar(4),CONVERT(binary(2),@Surf2Code),2);
                            SET @Surf2Start=@Surf2Position+1;
                        END;
                        SET @Surf2Position=@Surf2Position+1;
                    END;
                    SET @Surf2Escaped=@Surf2Escaped+SUBSTRING(@Surf2Input COLLATE Latin1_General_100_BIN2,@Surf2Start,@Surf2Length-@Surf2Start+1)+N'"';
                    IF @Surf2Part=0 UPDATE #Surf2LegacyFields SET NameJson=@Surf2Escaped WHERE FieldId=@Surf2FieldId;
                    ELSE UPDATE #Surf2LegacyFields SET ValueJson=@Surf2Escaped WHERE FieldId=@Surf2FieldId;
                END;
                SET @Surf2Part=@Surf2Part+1;
            END;
            FETCH NEXT FROM Surf2FieldsCursor INTO @Surf2FieldId,@Surf2FieldName,@Surf2FieldKind,@Surf2FieldValue;
        END;
        CLOSE Surf2FieldsCursor; DEALLOCATE Surf2FieldsCursor;
        INSERT #Surf2Payloads(EntityType,EntityKey,PayloadJson)
        SELECT p.EntityType,p.EntityKey,N'{'+ISNULL(STUFF((SELECT N','+f.NameJson+N':'+f.ValueJson
            FROM #Surf2LegacyFields f WHERE f.EntityHash=p.EntityHash AND f.EntityType=p.EntityType AND f.EntityKey=p.EntityKey AND f.Value IS NOT NULL
            ORDER BY f.PropertyOrdinal FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,1,N''),N'')+N'}'
        FROM (SELECT DISTINCT EntityType,EntityKey,EntityHash FROM #Surf2LegacyFields) p;
        """;

    private const string LegacyVariantKindExpression = """
        CASE CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(__COLUMN__,'BaseType'))
            WHEN N'bit' THEN N'B' WHEN N'tinyint' THEN N'N' WHEN N'smallint' THEN N'N' WHEN N'int' THEN N'N' WHEN N'bigint' THEN N'N'
            WHEN N'decimal' THEN N'N' WHEN N'numeric' THEN N'N' WHEN N'float' THEN N'N' WHEN N'real' THEN N'N'
            WHEN N'money' THEN N'N' WHEN N'smallmoney' THEN N'N' ELSE N'S' END
        """;

    private const string LegacyVariantValueExpression = """
        CASE CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(__COLUMN__,'BaseType'))
            WHEN N'bit' THEN CASE CONVERT(bit,__COLUMN__) WHEN 1 THEN N'true' ELSE N'false' END
            WHEN N'float' THEN (SELECT CONVERT(float,__COLUMN__) AS [text()] FOR XML PATH(''),TYPE).value('.','nvarchar(max)')
            WHEN N'real' THEN (SELECT CONVERT(real,__COLUMN__) AS [text()] FOR XML PATH(''),TYPE).value('.','nvarchar(max)')
            WHEN N'binary' THEN (SELECT CONVERT(varbinary(max),__COLUMN__) AS [text()] FOR XML PATH(''),BINARY BASE64,TYPE).value('.','nvarchar(max)')
            WHEN N'varbinary' THEN (SELECT CONVERT(varbinary(max),__COLUMN__) AS [text()] FOR XML PATH(''),BINARY BASE64,TYPE).value('.','nvarchar(max)')
            WHEN N'datetime' THEN CONVERT(nvarchar(max),CONVERT(datetime,__COLUMN__),127)
            WHEN N'smalldatetime' THEN CONVERT(nvarchar(max),CONVERT(smalldatetime,__COLUMN__),127)
            WHEN N'date' THEN CONVERT(nvarchar(max),CONVERT(date,__COLUMN__),127)
            WHEN N'time' THEN CONVERT(nvarchar(max),CONVERT(time,__COLUMN__),127)
            WHEN N'datetime2' THEN CONVERT(nvarchar(max),CONVERT(datetime2,__COLUMN__),127)
            WHEN N'datetimeoffset' THEN CONVERT(nvarchar(max),CONVERT(datetimeoffset,__COLUMN__),126)
            WHEN N'money' THEN CONVERT(nvarchar(max),CONVERT(money,__COLUMN__),2)
            WHEN N'smallmoney' THEN CONVERT(nvarchar(max),CONVERT(smallmoney,__COLUMN__),2)
            ELSE CONVERT(nvarchar(max),__COLUMN__) END
        """;
}

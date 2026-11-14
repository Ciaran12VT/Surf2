using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Surf2.Models;
using Surf2.Services;

public static partial class StorageRegressionSuite
{
    public static async Task RunManualSnapshotQueryChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        const string procedureName = "Procedure '測試";
        const string value = "O'Brien \"測試\"\\folder\r\nsecond line\tend\0\b\f\U0001F680\uFFFE";
        string procedure = $"CREATE PROCEDURE dbo.{SqlName.Bracket(procedureName)} AS\nBEGIN\n" +
            "SELECT N'O''Brien \"測試\"' AS [Text];\n/*" + new string('x', 65000) + "測試*/\nEND";
        await fixture.DestinationSqlAsync("""
            CREATE TABLE dbo.ManualData(Id int NOT NULL CONSTRAINT PK_ManualData PRIMARY KEY,
                [Note "quoted"] nvarchar(max) NULL, Amount decimal(38,8) NULL, Bytes varbinary(max) NULL,
                Flag bit NULL, Occurred datetimeoffset(7) NULL, Optional nvarchar(20) NULL, Ratio float NULL);
            INSERT dbo.ManualData(Id,[Note "quoted"]) VALUES(1,N'first');
            INSERT dbo.ManualData VALUES(2,@Value,123456789012345678901234567890.12345678,0x00FF7F,1,
                '2026-01-02T03:04:05.1234567+01:00',NULL,1.25);
            CREATE TABLE dbo.[A-B](Id int); INSERT dbo.[A-B] VALUES(101);
            CREATE TABLE dbo.[A B](Id int); INSERT dbo.[A B] VALUES(202);
            """, new SqlParameter("@Value", System.Data.SqlDbType.NVarChar, -1) { Value = value });
        await fixture.DestinationSqlAsync(procedure);
        await fixture.DestinationSqlAsync("CREATE VIEW dbo.ManualView AS SELECT Id FROM dbo.ManualData;");
        await fixture.DestinationSqlAsync("CREATE FUNCTION dbo.ManualFunction(@Id int) RETURNS int AS BEGIN RETURN @Id; END;");
        await fixture.DestinationSqlAsync("CREATE TRIGGER dbo.ManualTrigger ON dbo.ManualData AFTER INSERT AS BEGIN SET NOCOUNT ON; END;");

        await using var connection = new SqlConnection(fixture.DestinationConnectionString);
        await connection.OpenAsync();
        int originalCompatibility;
        using (var information = connection.CreateCommand())
        {
            information.CommandText = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')),compatibility_level FROM sys.databases WHERE database_id=DB_ID();";
            await using var reader = await information.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Fixture database information missing.");
            originalCompatibility = reader.GetByte(1);
            Console.WriteLine($"Manual SQL execution engine: {reader.GetString(0)}. Compatibility-level checks do not emulate older engine binaries.");
        }
        var generator = new DatabaseMetadataQueryService();
        foreach (int level in new[] { 100, 110, 120, 130, originalCompatibility }.Distinct())
        {
            // Only the database created and owned by this fixture is altered.
            using (var compatibility = connection.CreateCommand())
            {
                compatibility.CommandText = $"ALTER DATABASE {SqlName.Bracket(connection.Database)} SET COMPATIBILITY_LEVEL = {level};";
                await compatibility.ExecuteNonQueryAsync();
            }
            foreach (bool legacy in new[] { false, true })
            {
                string context = $"{(legacy ? "SQL2012 fallback" : "automatic")} manual SQL at compatibility {level}";
                string[] requestedTables = ["[dbo].[ManualData] WHERE Id = 2", "[dbo].[AbsentManualData]", "[dbo].[A-B]", "[dbo].[A B]"];
                string importQuery = legacy ? generator.CreateLegacyImportQuery(requestedTables) : generator.CreateImportQuery(requestedTables);
                VerifyManualQuerySql2012Grammar(importQuery, check, context);
                var parsed = await ExecuteManualSnapshotQueryAsync(connection, importQuery, check, context);
                var snapshot = parsed.Snapshot ?? throw new InvalidOperationException("Manual import did not produce a snapshot.");
                check(snapshot.Origin is { Acquisition: "PastedMetadataQuery", Engine: "SqlServer", ObservedAtUtc: not null } &&
                    snapshot.Origin.Database == connection.Database && snapshot.DatabaseName == connection.Database,
                    context + ": full generated script captures database-origin metadata without INSERT/EXEC JSON errors");
                var directOrigin = await DatabaseOriginQueryService.ReadAsync(connection);
                check(snapshot.Origin!.AgreesWith(directOrigin), context + ": manual and standalone origin queries identify the same database");
                if (legacy)
                {
                    string originQuery = DatabaseMetadataQueryService.CreateLegacyOriginQuery();
                    VerifyManualQuerySql2012Grammar(originQuery, check, context + " standalone origin");
                    using var originCommand = connection.CreateCommand();
                    originCommand.CommandText = originQuery;
                    var legacyOrigin = DatabaseOriginQueryService.Parse((string?)await originCommand.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("Legacy origin query returned no JSON."));
                    check(snapshot.Origin!.AgreesWith(legacyOrigin), context + ": standalone SQL2012 origin query agrees with the full snapshot");
                }
                check(snapshot.Objects.Single(item => item.ObjectName == procedureName).Definition == procedure &&
                    snapshot.Objects.Select(item => item.Kind).Order().SequenceEqual(new[]
                    {
                        SqlDatabaseObjectKind.StoredProcedure, SqlDatabaseObjectKind.View,
                        SqlDatabaseObjectKind.Function, SqlDatabaseObjectKind.Trigger
                    }.Order()), context + ": all object kinds and long chunked Unicode/quoted definitions round-trip");
                check(snapshot.Tables.Count == 3 && snapshot.Columns.Count == 10 && snapshot.PrimaryKeys.Count == 1,
                    context + ": table, column and primary-key metadata round-trip");
                var data = snapshot.TableDataSets.Single(item => item.TableName == "ManualData");
                check(data.TableName == "ManualData" && data.RowCount == 1 && data.Rows.Count == 1 &&
                    data.Rows[0].GetProperty("Id").GetInt32() == 2 && data.Rows[0].GetProperty("Note \"quoted\"").GetString() == value,
                    context + ": filtered full-table capture preserves Unicode, quotes and escaped control characters");
                var row = data.Rows[0];
                CheckCell(row.GetProperty("Amount").GetRawText() == "123456789012345678901234567890.12345678", "decimal precision", "Amount");
                CheckCell(row.GetProperty("Bytes").GetString() == "AP9/", "binary base64", "Bytes");
                CheckCell(row.GetProperty("Flag").GetBoolean(), "boolean type", "Flag");
                CheckCell(row.GetProperty("Occurred").GetString() == "2026-01-02T03:04:05.1234567+01:00", "datetimeoffset value", "Occurred");
                CheckCell(row.GetProperty("Ratio").GetDouble() == 1.25, "float value", "Ratio");
                check(!row.TryGetProperty("Optional", out _), context + ": null full-row values are omitted consistently");
                void CheckCell(bool condition, string description, string property) => check(condition,
                    context + ": full rows retain " + description + (condition ? "" : ": " + row.GetProperty(property).GetRawText()));
                check(snapshot.TableDataSets.Single(item => item.TableName == "A-B").Rows[0].GetProperty("Id").GetInt32() == 101 &&
                    snapshot.TableDataSets.Single(item => item.TableName == "A B").Rows[0].GetProperty("Id").GetInt32() == 202,
                    context + ": full-data table names with colliding normalized suffixes remain independent");
                check(snapshot.FullDataTableNames.Contains("[dbo].[AbsentManualData]") &&
                    snapshot.FullDataTableNames.Contains("[dbo].[ManualData] WHERE Id = 2") &&
                    snapshot.TableDataSets.All(item => item.TableName != "AbsentManualData"),
                    context + ": absent selected table is skipped while its capture recipe remains saved");

                foreach (var kind in Enum.GetValues<DatabaseEntityEditKind>())
                {
                    string name = kind switch
                    {
                        DatabaseEntityEditKind.StoredProcedures => procedureName,
                        DatabaseEntityEditKind.Views => "ManualView",
                        DatabaseEntityEditKind.Functions => "ManualFunction",
                        DatabaseEntityEditKind.Triggers => "ManualTrigger",
                        _ => "ManualData"
                    };
                    string entityQuery = legacy
                        ? generator.CreateLegacyEntityQuery(kind, "dbo", name, tableName: "ManualData", columnName: "Note \"quoted\"",
                            constraintName: "PK_ManualData", fullDataTableSelection: "[dbo].[ManualData] TOP 1")
                        : generator.CreateEntityQuery(kind, "dbo", name, tableName: "ManualData", columnName: "Note \"quoted\"",
                            constraintName: "PK_ManualData", fullDataTableSelection: "[dbo].[ManualData] TOP 1");
                    VerifyManualQuerySql2012Grammar(entityQuery, check, context + " " + kind);
                    var entity = (await ExecuteManualSnapshotQueryAsync(connection, entityQuery, check, context + " " + kind)).Snapshot
                        ?? throw new InvalidOperationException("Manual entity import did not produce a snapshot.");
                    bool expected = kind switch
                    {
                        DatabaseEntityEditKind.StoredProcedures => entity.Objects.Count == 1 && entity.Objects[0].Definition == procedure,
                        DatabaseEntityEditKind.Views or DatabaseEntityEditKind.Functions or DatabaseEntityEditKind.Triggers =>
                            entity.Objects.Count == 1 && entity.Objects[0].ObjectName == name,
                        DatabaseEntityEditKind.Tables => entity.Tables.Count == 1 && entity.Columns.Count == 8 && entity.PrimaryKeys.Count == 1,
                        DatabaseEntityEditKind.Fields => entity.Columns.Count == 1 && entity.Columns[0].ColumnName == "Note \"quoted\"",
                        DatabaseEntityEditKind.PrimaryKeys => entity.PrimaryKeys.Count == 1 && entity.PrimaryKeys[0].ConstraintName == "PK_ManualData",
                        DatabaseEntityEditKind.FullDataTables => entity.Tables.Count == 1 && entity.TableDataSets.Count == 1 && entity.TableDataSets[0].Rows.Count == 1,
                        _ => false
                    };
                    check(expected && entity.Origin?.Database == connection.Database, context + ": " + kind + " entity refresh produces importable metadata");
                }
            }
        }
        using (var restricted = connection.CreateCommand())
        {
            restricted.CommandText = """
                CREATE USER ManualSnapshotReader WITHOUT LOGIN;
                GRANT VIEW DEFINITION TO ManualSnapshotReader;
                GRANT SELECT TO ManualSnapshotReader;
                DENY VIEW DATABASE STATE TO ManualSnapshotReader;
                EXECUTE AS USER=N'ManualSnapshotReader';
                """;
            await restricted.ExecuteNonQueryAsync();
        }
        try
        {
            foreach (bool legacy in new[] { false, true })
            {
                string query = legacy ? generator.CreateLegacyImportQuery([]) : generator.CreateImportQuery([]);
                var snapshot = (await ExecuteManualSnapshotQueryAsync(connection, query, check,
                    legacy ? "SQL2012 fallback restricted reader" : "automatic restricted reader")).Snapshot!;
                check(snapshot.Tables.Count == 3 && snapshot.Objects.Count == 4 && snapshot.Origin?.Database == connection.Database,
                    "Optional origin identification does not block manual schema capture by a reader without database-state permission");
            }
        }
        finally
        {
            using var revert = connection.CreateCommand(); revert.CommandText = "REVERT;";
            await revert.ExecuteNonQueryAsync();
        }
        using (var unchanged = connection.CreateCommand())
        {
            unchanged.CommandText = "SELECT COUNT_BIG(*) FROM dbo.ManualData;";
            check(Convert.ToInt64(await unchanged.ExecuteScalarAsync()) == 2, "Generated manual snapshot scripts leave source table rows unchanged");
        }
        await fixture.DestinationSqlAsync("""
            CREATE TABLE dbo.ManualVariantData(Id int NOT NULL PRIMARY KEY, Value sql_variant NULL);
            INSERT dbo.ManualVariantData VALUES
                (1,CONVERT(sql_variant,42)),
                (2,CONVERT(sql_variant,CONVERT(nvarchar(2000),@VariantText))),
                (3,CONVERT(sql_variant,CONVERT(bit,1))),
                (4,CONVERT(sql_variant,CONVERT(varbinary(3),0x00FF7F))),
                (5,CONVERT(sql_variant,CONVERT(decimal(38,8),123456789012345678901234567890.12345678))),
                (6,CONVERT(sql_variant,CONVERT(money,123456.7890))),
                (7,CONVERT(sql_variant,CONVERT(datetime2(7),'2026-01-02T03:04:05.1234567'))),
                (8,CONVERT(sql_variant,NULL));
            """, new SqlParameter("@VariantText", System.Data.SqlDbType.NVarChar, 2000) { Value = value });
        foreach (bool legacy in new[] { false, true })
        {
            string context = legacy ? "SQL2012 fallback sql_variant" : "automatic sql_variant";
            string query = legacy ? generator.CreateLegacyImportQuery(["[dbo].[ManualVariantData]"])
                : generator.CreateImportQuery(["[dbo].[ManualVariantData]"]);
            var snapshot = (await ExecuteManualSnapshotQueryAsync(connection, query, check, context)).Snapshot!;
            var rows = snapshot.TableDataSets.Single(item => item.TableName == "ManualVariantData").Rows;
            check(rows.Count == 8 && rows.Select(row => row.GetProperty("Id").GetInt32()).Distinct().Count() == 8,
                context + ": every variant row is captured exactly once");
            foreach (var row in rows)
            {
                int id = row.GetProperty("Id").GetInt32();
                bool present = row.TryGetProperty("Value", out JsonElement cell);
                bool correct = id switch
                {
                    1 => present && cell.ValueKind == JsonValueKind.Number && cell.GetInt32() == 42,
                    2 => present && cell.ValueKind == JsonValueKind.String && cell.GetString() == value,
                    3 => present && cell.ValueKind == JsonValueKind.True,
                    4 => present && cell.ValueKind == JsonValueKind.String && cell.GetString() == "AP9/",
                    5 => present && cell.ValueKind == JsonValueKind.Number && cell.GetRawText() == "123456789012345678901234567890.12345678",
                    6 => present && cell.ValueKind == JsonValueKind.Number && cell.GetDecimal() == 123456.7890m,
                    7 => present && cell.ValueKind == JsonValueKind.String && cell.GetString() == "2026-01-02T03:04:05.1234567",
                    8 => !present,
                    _ => false
                };
                check(correct, context + $": row {id} retains its underlying type and value" +
                    (correct ? "" : ": " + row.GetRawText()));
            }
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static void VerifyManualQuerySql2012Grammar(string sql, Action<bool, string> check, string context)
    {
        new TSql110Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        check(errors.Count == 0, context + ": generated batch parses with SQL Server 2012 grammar" +
            (errors.Count == 0 ? "" : ": " + string.Join("; ", errors.Select(error => $"line {error.Line}: {error.Message}"))));
    }

    private static async Task<DatabaseImportResult> ExecuteManualSnapshotQueryAsync(SqlConnection connection, string query,
        Action<bool, string> check, string context)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query; command.CommandTimeout = 120;
        await using var reader = await command.ExecuteReaderAsync();
        check(reader.FieldCount == 5 && reader.GetName(0) == "EntityType" && reader.GetName(4) == "PayloadJsonChunk",
            context + ": first result is the five-column clipboard payload");
        var clipboard = new StringBuilder("EntityType\tEntityKey\tChunkIndex\tChunkCount\tPayloadJsonChunk\n");
        while (await reader.ReadAsync())
        {
            clipboard.Append(reader.GetString(0)).Append('\t').Append(reader.GetString(1)).Append('\t')
                .Append(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)).Append('\t')
                .Append(Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)).Append('\t')
                .Append(reader.GetString(4)).Append('\n');
        }
        check(!await reader.NextResultAsync(), context + ": no identity or diagnostic result set leaks into clipboard output");
        var parsed = new DatabaseMetadataImportService().ParseClipboardResult(clipboard.ToString(), "Manual SQL fixture");
        check(parsed.Warnings.Count == 0, context + ": clipboard output parses without warnings" +
            (parsed.Warnings.Count == 0 ? "" : ": " + string.Join("; ", parsed.Warnings)));
        return parsed;
    }
}

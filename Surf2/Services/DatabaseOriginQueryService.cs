using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Services;

public static class DatabaseOriginQueryService
{
    public static string Query => DatabaseMetadataQueryService.CreateCompatibleQuery(NativeQuery,
        DatabaseMetadataQueryService.CreateLegacyOriginQuery());

    internal const string ReadVariablesSql = """
        SET NOCOUNT ON;
        DECLARE @edition int=CONVERT(int,SERVERPROPERTY('EngineEdition'));
        DECLARE @kind nvarchar(64)=N'', @identifier nvarchar(2048)=N'', @availability nvarchar(64)=N'Unavailable';
        BEGIN TRY
          IF @edition=5
          BEGIN
            SET @kind=N'physical_database_name';
            EXEC sys.sp_executesql N'SELECT @value=physical_database_name FROM sys.databases WHERE database_id=DB_ID();',N'@value nvarchar(2048) OUTPUT',@value=@identifier OUTPUT;
          END
          ELSE IF @edition IN (2,3,4,8)
          BEGIN
            SET @kind=N'database_guid';
            EXEC sys.sp_executesql N'SELECT @value=CONVERT(nvarchar(36),database_guid) FROM sys.database_recovery_status WHERE database_id=DB_ID();',N'@value nvarchar(2048) OUTPUT',@value=@identifier OUTPUT;
          END;
          IF NULLIF(@identifier,N'') IS NOT NULL SET @availability=N'Available';
        END TRY
        BEGIN CATCH
          SET @availability=N'Unavailable';
        END CATCH;
        """;

    internal const string SelectFieldsSql = """
        SELECT N'Surf2.DatabaseOrigin' AS Format,1 AS Version,
          CASE WHEN @edition=5 THEN N'AzureSqlDatabase' WHEN @edition=8 THEN N'AzureSqlManagedInstance'
               WHEN @edition IN (2,3,4) THEN N'SqlServer' ELSE N'Unknown' END AS Engine,
          CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) AS Server,DB_NAME() AS [Database],
          @kind AS IdentifierKind,COALESCE(@identifier,N'') AS Identifier,@availability AS Availability,
          SYSUTCDATETIME() AS ObservedAtUtc
        """;

    // A scalar JSON value is an ordinary result column, including when the caller uses INSERT ... EXEC.
    internal const string NativeQuery = ReadVariablesSql + "\nSELECT (" + SelectFieldsSql +
        "\nFOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS Result;";

    public static DatabaseOrigin Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 32768) throw new FormatException("Paste the single JSON result cell from Copy origin SQL query.");
        using var doc = JsonDocument.Parse(input.Trim(), new JsonDocumentOptions { MaxDepth = 8 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Format", out var format) || format.GetString() != "Surf2.DatabaseOrigin")
            throw new FormatException("This is not a Surf2 database-origin query result.");
        string Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        if (!root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int number) || number != 1)
            throw new FormatException("Unsupported database-origin result version.");
        DateTimeOffset? observed = DateTimeOffset.TryParse(Get("ObservedAtUtc"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
        return new DatabaseOrigin { Engine = Get("Engine"), Server = Get("Server"), Database = Get("Database"),
            IdentifierKind = Get("IdentifierKind"), Identifier = Get("Identifier"), Availability = Get("Availability"),
            ObservedAtUtc = observed, Acquisition = "PastedIdentityQuery" }.Validate();
    }

    public static async Task<DatabaseOrigin> ReadAsync(SqlConnection connection, SqlTransaction? transaction = null,
        Guid? sourceId = null, CancellationToken ct = default)
    {
        using var command = new SqlCommand(Query, connection, transaction) { CommandTimeout = 30 };
        using var cancel = ct.Register(() => { try { command.Cancel(); } catch (Exception) { } });
        string value = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Source identity query returned no result.");
        return (Parse(value) with { Acquisition = "DirectCapture", RegisteredSourceId = sourceId, Endpoint = connection.DataSource }).Validate();
    }
}

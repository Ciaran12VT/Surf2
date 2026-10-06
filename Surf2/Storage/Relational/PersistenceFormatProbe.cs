using System.Data;
using System.IO;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational;

public enum PersistenceFormat
{
    Empty,
    Legacy,
    Relational,
    Incomplete,
    Unsupported,
    Unavailable
}

public sealed record PersistenceFormatResult(
    PersistenceFormat Format,
    string DatabaseName,
    string? ServerIdentity = null,
    int? DatabaseNumber = null,
    Guid? DatabaseIdentity = null,
    int? SchemaVersion = null,
    string? Reason = null);

public sealed class PersistenceFormatProbe
{
    public const string FormatIdentifier = "Surf2.Relational";
    public const int SupportedSchemaVersion = 1;

    public async Task<PersistenceFormatResult> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var options = SqlServerConnectionOptions.FromConnectionString(connectionString);
        await using var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), CONVERT(int, DB_ID()), DB_NAME(),
       OBJECT_ID(N'surf.StorageFormatInfo', N'U'),
       OBJECT_ID(N'app.Surf2Documents', N'U'),
       (SELECT COUNT_BIG(*) FROM sys.objects WHERE is_ms_shipped = 0)
       + (SELECT COUNT_BIG(*) FROM sys.types WHERE is_user_defined = 1)
       + (SELECT COUNT_BIG(*) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383),
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION');
""";
            string server;
            int databaseNumber;
            string databaseName;
            bool hasMarker;
            bool hasLegacy;
            long tableCount;
            bool canProveEmpty;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidDataException("Database identity could not be read.");
                }

                server = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                databaseNumber = reader.GetInt32(1);
                databaseName = reader.GetString(2);
                hasMarker = !reader.IsDBNull(3);
                hasLegacy = !reader.IsDBNull(4);
                tableCount = reader.GetInt64(5);
                canProveEmpty = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
            }

            if (hasMarker)
            {
                command.CommandText = """
SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.is_nullable
FROM sys.columns AS c WHERE c.object_id=OBJECT_ID(N'surf.StorageFormatInfo',N'U');
""";
                var markerColumns = new Dictionary<string, (string Type, short Length, bool Nullable)>(StringComparer.Ordinal);
                await using (var columns = await command.ExecuteReaderAsync(cancellationToken))
                    while (await columns.ReadAsync(cancellationToken))
                        markerColumns.Add(columns.GetString(0), (columns.GetString(1), columns.GetInt16(2), columns.GetBoolean(3)));
                if (!MarkerColumn("Singleton", "tinyint", 1) ||
                    !MarkerColumn("FormatIdentifier", "nvarchar", 128) ||
                    !MarkerColumn("SchemaVersion", "int", 4) ||
                    !MarkerColumn("MinimumReaderVersion", "int", 4) ||
                    !MarkerColumn("MinimumWriterVersion", "int", 4) ||
                    !MarkerColumn("State", "varchar", 24) ||
                    !MarkerColumn("DatabaseIdentity", "uniqueidentifier", 16))
                    return Result(PersistenceFormat.Unsupported, "The storage format marker has an unexpected structure.");

                bool MarkerColumn(string name, string type, short length) =>
                    markerColumns.TryGetValue(name, out var column) && column == (type, length, false);
                command.CommandText = """
SELECT FormatIdentifier, SchemaVersion, MinimumReaderVersion, MinimumWriterVersion,
       State, DatabaseIdentity, Singleton
FROM surf.StorageFormatInfo;
""";
                string identifier;
                int version;
                int minimumReader;
                int minimumWriter;
                string state;
                Guid identity;
                byte singleton;
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        return Result(PersistenceFormat.Incomplete, "The format marker is empty.");
                    identifier = reader.GetString(0);
                    version = reader.GetInt32(1);
                    minimumReader = reader.GetInt32(2);
                    minimumWriter = reader.GetInt32(3);
                    state = reader.GetString(4);
                    identity = reader.GetGuid(5);
                    singleton = reader.GetByte(6);
                    if (await reader.ReadAsync(cancellationToken))
                        return Result(PersistenceFormat.Unsupported, "The format marker is not unique.");
                }

                if (identifier != FormatIdentifier || version != SupportedSchemaVersion ||
                    minimumReader > SupportedSchemaVersion || minimumWriter > SupportedSchemaVersion ||
                    minimumReader < 1 || minimumWriter < 1 || identity == Guid.Empty || singleton != 1 || state is not ("Ready" or "Migrating"))
                {
                    return new(PersistenceFormat.Unsupported, databaseName, server, databaseNumber,
                        identity, version, "This database requires a different Surf storage version.");
                }

                command.CommandText = """
SELECT COUNT_BIG(*) FROM surf.SchemaMigration
WHERE SchemaVersion=@Version AND ScriptChecksum=@Checksum;
""";
                command.Parameters.Add(RelationalSession.Parameter("@Version", SqlDbType.Int, SupportedSchemaVersion));
                command.Parameters.Add(RelationalSession.Parameter("@Checksum", SqlDbType.Binary,
                    RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts()), 32));
                if (!Equals(await command.ExecuteScalarAsync(cancellationToken), 1L))
                    return Result(PersistenceFormat.Unsupported, "The installed schema checksum is not supported by this application.");

                return new(state == "Ready" ? PersistenceFormat.Relational : PersistenceFormat.Incomplete,
                    databaseName, server, databaseNumber, identity, version,
                    state == "Ready" ? null : "Conversion or initialization has not completed.");
            }

            if (hasLegacy)
            {
                command.CommandText = """
SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.is_nullable
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'app.Surf2Documents', N'U');
""";
                var columns = new Dictionary<string, (string Type, short Length, bool Nullable)>(StringComparer.Ordinal);
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        columns.Add(reader.GetString(0), (reader.GetString(1), reader.GetInt16(2), reader.GetBoolean(3)));
                    }
                }

                if (columns.Count != 3 ||
                    !columns.TryGetValue("DocumentKey", out var key) || key.Type != "nvarchar" || key.Length != 256 || key.Nullable ||
                    !columns.TryGetValue("PayloadJson", out var payload) || payload.Type != "nvarchar" || payload.Length != -1 || payload.Nullable ||
                    !columns.TryGetValue("UpdatedAtUtc", out var timestamp) || timestamp.Type != "datetime2" || timestamp.Nullable)
                {
                    return Result(PersistenceFormat.Unsupported, "The legacy document table has an unexpected structure.");
                }

                return Result(PersistenceFormat.Legacy);
            }

            if (tableCount == 0 && !canProveEmpty)
                return Result(PersistenceFormat.Unavailable, "Database metadata permissions cannot prove that the destination is empty.");
            return tableCount == 0
                ? Result(PersistenceFormat.Empty)
                : Result(PersistenceFormat.Unsupported, "The database contains an unrecognized schema.");

            PersistenceFormatResult Result(PersistenceFormat format, string? reason = null) =>
                new(format, databaseName, server, databaseNumber, Reason: reason);
        }
        catch (SqlException) when (!cancellationToken.IsCancellationRequested)
        {
            // An inaccessible database is not proof that it is absent or empty.
            return new(PersistenceFormat.Unavailable, options.DatabaseName,
                Reason: "The database or its format metadata could not be read. Check connection and read permissions.");
        }
        catch (Exception ex) when (ex is InvalidCastException or InvalidDataException or SqlNullValueException)
        {
            return new(PersistenceFormat.Unsupported, options.DatabaseName,
                Reason: "The storage format metadata is malformed.");
        }
    }

    public static bool SameDatabase(PersistenceFormatResult source, PersistenceFormatResult destination) =>
        source.DatabaseNumber.HasValue && destination.DatabaseNumber.HasValue &&
        !string.IsNullOrWhiteSpace(source.ServerIdentity) &&
        string.Equals(source.ServerIdentity, destination.ServerIdentity, StringComparison.OrdinalIgnoreCase) &&
        source.DatabaseNumber == destination.DatabaseNumber;
}

using Microsoft.Data.SqlClient;
using Surf2.Storage;
using Surf2.Storage.Relational;

namespace Surf2.Services;

public static class RelationalDatabaseBootstrap
{
    // Connection failure alone is never evidence that a database is absent.
    public static async Task<bool> CanProveDatabaseAbsentAsync(SqlServerConnectionOptions options, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        if (!string.IsNullOrEmpty(builder.AttachDBFilename) || builder.UserInstance) return false;
        string name = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin'), (SELECT COUNT_BIG(*) FROM sys.databases WHERE name=@Name);";
            command.Parameters.Add(RelationalSession.Parameter("@Name", System.Data.SqlDbType.NVarChar, name, 128));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) && !reader.IsDBNull(0) && reader.GetInt32(0) == 1 && reader.GetInt64(1) == 0;
        }
        catch (SqlException) when (!ct.IsCancellationRequested) { return false; }
    }
}

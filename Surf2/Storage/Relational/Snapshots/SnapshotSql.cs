using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Snapshots;

internal static class SnapshotSql
{
    internal static SqlParameter Text(string name, string? value) => RelationalSession.Parameter(name, SqlDbType.NVarChar, value, -1);
    internal static SqlParameter Key(string name, long? value) => RelationalSession.Parameter(name, SqlDbType.BigInt, value);
    internal static SqlParameter Int(string name, int? value) => RelationalSession.Parameter(name, SqlDbType.Int, value);
    internal static SqlParameter Bit(string name, bool value) => RelationalSession.Parameter(name, SqlDbType.Bit, value);
    internal static SqlParameter Hash(string name, byte[] value) => RelationalSession.Parameter(name, SqlDbType.Binary, value, 32);
    internal static SqlParameter Token(string name, byte[]? value) => RelationalSession.Parameter(name, SqlDbType.Binary, value, 8);
    internal static SqlParameter Date(string name, DateTimeOffset? value)
    {
        var parameter = RelationalSession.Parameter(name, SqlDbType.DateTimeOffset, value);
        parameter.Scale = 7;
        return parameter;
    }

    internal static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 60;
        command.Parameters.AddRange(parameters);
        return command;
    }

    internal static async Task<object?> ScalarAsync(SqlConnection connection, SqlTransaction? transaction,
        string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<long> InsertAsync(SqlConnection connection, SqlTransaction transaction,
        string sql, CancellationToken ct, params SqlParameter[] parameters) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction, sql, ct, parameters).ConfigureAwait(false));

    internal static long? NullableKey(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    internal static DateTimeOffset? NullableDate(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}

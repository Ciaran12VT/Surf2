using System.Buffers.Binary;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;

namespace Surf2.Services.RelationalSnapshots;

internal sealed record SqlCaptureDatabaseIdentity(string ServerName, int DatabaseId)
{
    public static async Task<SqlCaptureDatabaseIdentity> ReadAsync(SqlConnection connection, int timeout, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandTimeout = timeout;
        command.CommandText = "SELECT CONVERT(nvarchar(256),SERVERPROPERTY('ServerName')),CONVERT(int,DB_ID());";
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
            throw new InvalidOperationException("Unable to resolve the SQL server/database identity for capture.");
        string server = reader.GetString(0); int database = reader.GetInt32(1);
        if (string.IsNullOrWhiteSpace(server) || database < 1) throw new InvalidOperationException("Invalid SQL capture database identity.");
        return new(server, database);
    }
    public bool SameDatabase(SqlCaptureDatabaseIdentity other) => DatabaseId == other.DatabaseId &&
        StringComparer.OrdinalIgnoreCase.Equals(ServerName, other.ServerName);
}

internal sealed class SqlSnapshotSource : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly RelationalSnapshotOperationLimits _limits;
    private SqlSnapshotSource(SqlConnection connection, SqlTransaction transaction, RelationalSnapshotOperationLimits limits)
    { _connection = connection; _transaction = transaction; _limits = limits; }
    public IsolationLevel Isolation => _transaction.IsolationLevel;
    public static async Task<SqlSnapshotSource> OpenAsync(string connectionString, RelationalSnapshotOperationLimits limits,
        SqlCaptureDatabaseIdentity destination, CancellationToken ct, bool allowSerializableFallback = false, bool metadataOnly = false)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog) || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new ArgumentException("Select an explicit existing source database; attached-file sources are unsupported.");
        var connection = new SqlConnection(builder.ConnectionString);
        SqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var identity = await SqlCaptureDatabaseIdentity.ReadAsync(connection, limits.SourceCommandTimeoutSeconds, ct).ConfigureAwait(false);
            if (identity.SameDatabase(destination)) throw new ArgumentException("The source and destination resolve to the same physical SQL database.");
            await using var guard = connection.CreateCommand(); guard.CommandTimeout = limits.SourceCommandTimeoutSeconds;
            guard.CommandText = """
                SELECT CASE WHEN OBJECT_ID(N'surf.StorageFormatInfo',N'U') IS NULL THEN 0 ELSE 1 END,
                    snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID();
                """;
            using var cancel = RelationalSession.CancelCommand(guard, ct);
            bool snapshotEnabled;
            await using (var reader = await guard.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("Unable to inspect source snapshot isolation.");
                if (reader.GetInt32(0) != 0) throw new ArgumentException("A Surf2 relational persistence database cannot be used as a live SQL capture source.");
                snapshotEnabled = reader.GetByte(1) == 1;
            }
            if (!metadataOnly && !snapshotEnabled && !allowSerializableFallback) throw new SourceSnapshotUnavailableException();
            var isolation = metadataOnly ? IsolationLevel.ReadCommitted : snapshotEnabled ? IsolationLevel.Snapshot : IsolationLevel.Serializable;
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolation, ct).ConfigureAwait(false);
            return new(connection, transaction, limits);
        }
        catch
        {
            if (transaction != null) await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false); throw;
        }
    }
    private SqlCommand Command(string sql, params SqlParameter[] parameters)
    {
        var command = _connection.CreateCommand(); command.Transaction = _transaction;
        command.CommandText = sql; command.CommandTimeout = _limits.SourceCommandTimeoutSeconds;
        command.Parameters.AddRange(parameters); return command;
    }
    private static SqlParameter Key(string name, int value) => RelationalSession.Parameter(name, SqlDbType.Int, value);
    public async Task<string> DatabaseNameAsync(CancellationToken ct)
    {
        await using var command = Command("SELECT DB_NAME();");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        return (string)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
    public async Task<RelationalSourceTablePage> TablesAsync(int size, int after, CancellationToken ct)
    {
        if (size is < 1 or > 256 || after < 0) throw new ArgumentOutOfRangeException(nameof(size));
        await using var command = Command("""
            SELECT TOP(@Take) t.object_id,SCHEMA_NAME(t.schema_id),t.name FROM sys.tables t
            WHERE t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)<>N'sys' AND t.object_id>@After ORDER BY t.object_id;
            """, Key("@Take", size + 1), Key("@After", after));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var rows = new List<RelationalSourceTable>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count == size) return new(rows.AsReadOnly(), rows[^1].ObjectId);
            rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return new(rows.AsReadOnly(), null);
    }
    public async Task<(int Id, SqlDatabaseObject Value)?> NextObjectAsync(int after, CancellationToken ct)
    {
        await using var command = Command("""
            SELECT TOP(1) o.object_id,SCHEMA_NAME(o.schema_id),o.name,o.type,o.type_desc,
                ISNULL(SCHEMA_NAME(p.schema_id),N''),ISNULL(p.name,N''),DATALENGTH(m.definition),CONVERT(varbinary(max),m.definition)
            FROM sys.objects o LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            LEFT JOIN sys.objects p ON p.object_id=o.parent_object_id
            WHERE o.type IN ('P','V','FN','IF','TF','TR') AND o.is_ms_shipped=0
                AND SCHEMA_NAME(o.schema_id)<>N'sys' AND o.object_id>@After ORDER BY o.object_id;
            """, Key("@After", after));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        int id = reader.GetInt32(0);
        var value = new SqlDatabaseObject { SchemaName = reader.GetString(1), ObjectName = reader.GetString(2) };
        value.Kind = reader.GetString(3).Trim() switch
        { "P" => SqlDatabaseObjectKind.StoredProcedure, "V" => SqlDatabaseObjectKind.View, "TR" => SqlDatabaseObjectKind.Trigger,
            _ => SqlDatabaseObjectKind.Function };
        value.TypeDescription = reader.GetString(4); value.ParentSchemaName = reader.GetString(5); value.ParentObjectName = reader.GetString(6);
        if (reader.IsDBNull(7))
            throw new InvalidOperationException("A selected SQL definition is encrypted or not visible. Grant VIEW DEFINITION before capture.");
        long bytes = reader.GetInt64(7);
        if (reader.IsDBNull(8))
            throw new InvalidOperationException("A selected SQL definition is encrypted or not visible. Grant VIEW DEFINITION before capture.");
        value.Definition = await TextAsync(reader, 8, bytes, _limits.MaximumDefinitionBytes, ct).ConfigureAwait(false);
        return (id, value);
    }
    public async Task<(List<SqlColumn> Columns, List<SqlPrimaryKeyColumn> Keys)> MetadataAsync(RelationalSourceTable table, CancellationToken ct)
    {
        await using var command = Command("""
            SELECT c.name,ty.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.column_id
            FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            JOIN sys.tables t ON t.object_id=c.object_id
            WHERE t.object_id=@Id AND SCHEMA_NAME(t.schema_id)=@Schema AND t.name=@Name ORDER BY c.column_id;
            SELECT kc.name,c.name,ic.key_ordinal FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id=kc.parent_object_id AND ic.index_id=kc.unique_index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE kc.parent_object_id=@Id AND kc.type='PK' ORDER BY ic.key_ordinal;
            """, Key("@Id", table.ObjectId), RelationalSession.Parameter("@Schema", SqlDbType.NVarChar, table.SchemaName, 128),
            RelationalSession.Parameter("@Name", SqlDbType.NVarChar, table.TableName, 128));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var columns = new List<SqlColumn>(); var keys = new List<SqlPrimaryKeyColumn>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (columns.Count == _limits.MaximumTableColumns) throw new InvalidOperationException("Selected table exceeds its column budget.");
            columns.Add(new() { SchemaName = table.SchemaName, TableName = table.TableName, ColumnName = reader.GetString(0),
                DataType = reader.GetString(1), MaxLength = reader.GetInt16(2), NumericPrecision = reader.GetByte(3), NumericScale = reader.GetByte(4),
                IsNullable = reader.GetBoolean(5), IsIdentity = reader.GetBoolean(6), Ordinal = reader.GetInt32(7) });
        }
        if (columns.Count == 0) throw new InvalidOperationException("Selected source table disappeared or its metadata is not visible.");
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (keys.Count == _limits.MaximumTableColumns) throw new InvalidOperationException("Selected table exceeds its key budget.");
            keys.Add(new() { SchemaName = table.SchemaName, TableName = table.TableName, ConstraintName = reader.GetString(0),
                ColumnName = reader.GetString(1), KeyOrdinal = reader.GetByte(2) });
        }
        return (columns, keys);
    }
    public async Task<long> RowCountAsync(RelationalSourceTable table, CancellationToken ct)
    {
        await using var command = Command("SELECT COUNT_BIG(*) FROM " + SqlName.FormatMultipartName(table.SchemaName, table.TableName) + ";");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
    public async IAsyncEnumerable<JsonElement> RowsAsync(RelationalSourceTable table, [EnumeratorCancellation] CancellationToken ct)
    {
        // One JSON object per reader row, never FOR JSON over a whole table or an in-memory DataTable.
        await using var command = Command("SELECT DATALENGTH(j.Payload),CONVERT(varbinary(max),j.Payload) FROM " +
            SqlName.FormatMultipartName(table.SchemaName, table.TableName) +
            " AS rowData CROSS APPLY(SELECT (SELECT rowData.* FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) AS Payload) j;");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string json = await TextAsync(reader, 1, reader.GetInt64(0), 2L * _limits.Capture.MaxRowUtf8Bytes, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            yield return document.RootElement.Clone();
        }
    }
    private static async Task<string> TextAsync(SqlDataReader reader, int ordinal, long bytes, long maximum, CancellationToken ct)
    {
        if (bytes < 0 || bytes > maximum || bytes % 2 != 0) throw new InvalidDataException("Selected source text exceeds its bounded read budget.");
        await using var stream = reader.GetStream(ordinal);
        var chars = new char[checked((int)(bytes / 2))]; var buffer = new byte[8192];
        long position = 0; int carry = -1, read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (read > bytes - position) throw new InvalidDataException("Selected source text changed during its read.");
            int start = 0;
            if (carry >= 0)
            {
                chars[checked((int)((position - 1) / 2))] = (char)(carry | buffer[0] << 8);
                carry = -1; start = 1;
            }
            for (int i = start; i + 1 < read; i += 2)
                chars[checked((int)((position + i) / 2))] = (char)BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i, 2));
            if (((read - start) & 1) != 0) carry = buffer[read - 1];
            position += read;
        }
        if (position != bytes || carry >= 0) throw new InvalidDataException("Selected source text length was not preserved.");
        return new string(chars);
    }
    public Task CompleteAsync(CancellationToken ct) => _transaction.CommitAsync(ct);
    public async ValueTask DisposeAsync()
    { await _transaction.DisposeAsync().ConfigureAwait(false); await _connection.DisposeAsync().ConfigureAwait(false); }
}

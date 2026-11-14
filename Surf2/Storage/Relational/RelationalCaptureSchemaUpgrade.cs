using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Storage.Relational;

/// <summary>Resumable conversion of generated capture tables into revision-owned JSON documents.</summary>
public sealed class RelationalCaptureSchemaUpgrade
{
    public async Task UpgradeAsync(string connectionString, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var session = new RelationalSession(connectionString);
        await using var connection = await session.OpenAsync(cancellationToken);
        await UpgradeOwnedAsync(connection,session,progress,cancellationToken);
    }

    internal async Task UpgradeOwnedAsync(SqlConnection connection,RelationalSession session,IProgress<string>? progress,CancellationToken cancellationToken)
    {
        await using var lease = Command(connection, null, """
DECLARE @Result int;
EXEC @Result=sys.sp_getapplock @Resource=N'Surf2.FullTableData.Upgrade',
    @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=-1;
IF @Result < 0 THROW 51100, 'Could not acquire the table-data migration lock.', 1;
""");
        using var cancelLease = RelationalSession.CancelCommand(lease, cancellationToken);
        progress?.Report("Checking captured table storage...");
        try
        {
            await lease.ExecuteNonQueryAsync(cancellationToken);
            var format = await new PersistenceFormatProbe().ProbeConnectionAsync(connection, cancellationToken);
            if (format.Format is not (PersistenceFormat.Relational or PersistenceFormat.Incomplete) ||
                format.SchemaVersion is not (1 or 2))
                throw new InvalidOperationException("The selected database has an unsupported table-data schema.");

            await PrepareAsync(connection, cancellationToken);
            var datasets = new List<long>();
            await using (var list = Command(connection, null, "SELECT DataSetKey FROM surf.DataSet ORDER BY DataSetKey;"))
            {
                await using var reader = await list.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) datasets.Add(reader.GetInt64(0));
            }
            var store = new RelationalCaptureStore(session);
            for (int i = 0; i < datasets.Count; i++)
            {
                progress?.Report($"Migrating captured table data {i + 1:N0} of {datasets.Count:N0}...");
                await ConvertDataSetAsync(connection, store, datasets[i], cancellationToken);
            }
            progress?.Report("Finishing captured table migration...");
            await CompleteAsync(connection, cancellationToken);
            progress?.Report("Captured table migration complete.");
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var release = Command(connection, null, """
IF APPLOCK_MODE(N'public', N'Surf2.FullTableData.Upgrade', N'Session') <> N'NoLock'
    EXEC sys.sp_releaseapplock @Resource=N'Surf2.FullTableData.Upgrade', @LockOwner='Session';
""");
                release.CommandTimeout = 30;
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private static async Task PrepareAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Raise the compatibility floor before committing any converted payload. Older
        // application sessions will fail their readiness check instead of using stale tables.
        await using var command = Command(connection, transaction, """
UPDATE surf.StorageFormatInfo SET MinimumReaderVersion=2, MinimumWriterVersion=2 WHERE Singleton=1;
SELECT OBJECT_ID(N'surf.FullTableData', N'U');
""");
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        object? exists = await command.ExecuteScalarAsync(ct);
        if (exists is null or DBNull)
        {
            command.CommandText = RelationalSchemaInstaller.ReadScripts(2)[^1];
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task ConvertDataSetAsync(SqlConnection connection, RelationalCaptureStore store,
        long dataSetKey, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        long layoutKey;
        long rowCount;
        await using (var select = Command(connection, transaction, """
SELECT d.LayoutKey, d.ActualRowCount,
    (SELECT COUNT_BIG(*) FROM surf.FullTableData j WHERE j.DataSetKey=d.DataSetKey),
    (SELECT COUNT_BIG(*) FROM surf.FullTableData j CROSS APPLY OPENJSON(j.JsonPayload) r WHERE j.DataSetKey=d.DataSetKey)
FROM surf.DataSet d WITH (UPDLOCK,HOLDLOCK) WHERE d.DataSetKey=@DataSet;
""", RelationalSession.Parameter("@DataSet", SqlDbType.BigInt, dataSetKey)))
        {
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidDataException("A captured dataset disappeared during migration.");
            if (reader.GetInt64(2) == 1 && reader.GetInt64(3) == reader.GetInt64(1)) return;
            layoutKey = reader.GetInt64(0);
            rowCount = reader.GetInt64(1);
        }
        var layout = await RelationalCaptureStore.ReadLayoutAsync(connection, transaction, layoutKey, ct);
        // An older writer may have appended to a Writing dataset after a prior
        // conversion committed. Rebuild it under the same lock used by append.
        await using (var remove = Command(connection, transaction,
            "DELETE FROM surf.FullTableData WHERE DataSetKey=@DataSet;",
            RelationalSession.Parameter("@DataSet", SqlDbType.BigInt, dataSetKey)))
            await remove.ExecuteNonQueryAsync(ct);
        string path = Path.Combine(Path.GetTempPath(), "surf-capture-" + Guid.NewGuid().ToString("N") + ".json");
        // DeleteOnClose also cleans up when conversion is cancelled or SQL upload fails.
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        await using (var writer = new StreamWriter(file, new UTF8Encoding(false, true), 64 * 1024, leaveOpen: true))
        {
            await writer.WriteAsync("[".AsMemory(), ct);
            long count = 0;
            await foreach (var row in store.StreamLegacyRowsAsync(connection, transaction, dataSetKey, layout, rowCount, ct))
            {
                if (count != 0) await writer.WriteAsync(",".AsMemory(), ct);
                await writer.WriteAsync(row.Value.GetRawText().AsMemory(), ct);
                count++;
            }
            if (count != rowCount) throw new InvalidDataException("Captured row counts differ during migration.");
            await writer.WriteAsync("]".AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        file.Position = 0;
        byte[] expectedHash = await SHA256.HashDataAsync(file, ct);
        file.Position = 0;
        using (var text = new StreamReader(file, new UTF8Encoding(false, true), false, 64 * 1024, leaveOpen: true))
        await using (var insert = Command(connection, transaction,
            "INSERT surf.FullTableData(DataSetKey,JsonPayload) VALUES (@DataSet,@Json);",
            RelationalSession.Parameter("@DataSet", SqlDbType.BigInt, dataSetKey),
            RelationalSession.Parameter("@Json", SqlDbType.NVarChar, text, -1)))
        {
            using var cancelInsert = RelationalSession.CancelCommand(insert, ct);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var verify = Command(connection, transaction,
            "SELECT CONVERT(varbinary(max),JsonPayload) FROM surf.FullTableData WHERE DataSetKey=@DataSet;",
            RelationalSession.Parameter("@DataSet", SqlDbType.BigInt, dataSetKey)))
        {
            using var cancelVerify = RelationalSession.CancelCommand(verify, ct);
            await using var reader = await verify.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidDataException("Converted capture is missing.");
            await using var bytes = reader.GetStream(0);
            await using var utf8 = Encoding.CreateTranscodingStream(bytes, Encoding.Unicode, Encoding.UTF8, leaveOpen: true);
            byte[] storedHash = await SHA256.HashDataAsync(utf8, ct);
            if (!expectedHash.AsSpan().SequenceEqual(storedHash))
                throw new InvalidDataException("Converted capture content verification failed.");
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task CompleteAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var command = Command(connection, transaction, """
IF EXISTS(SELECT 1 FROM surf.DataSet d WITH (UPDLOCK,HOLDLOCK)
    WHERE NOT EXISTS(SELECT 1 FROM surf.FullTableData j WHERE j.DataSetKey=d.DataSetKey)
    OR d.ActualRowCount<>(SELECT COUNT_BIG(*) FROM surf.FullTableData j
        CROSS APPLY OPENJSON(j.JsonPayload) r WHERE j.DataSetKey=d.DataSetKey))
    THROW 51101, 'Some captured datasets have not been converted.', 1;
SELECT LayoutKey FROM surf.DataLayout ORDER BY LayoutKey;
""");
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        var layouts = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) layouts.Add(reader.GetInt64(0));
        // Retain all original rows until every payload has been committed and verified.
        foreach (long layoutKey in layouts)
        {
            command.CommandText = $"DROP TABLE IF EXISTS {CaptureLayout.QualifiedTable(layoutKey)};";
            await command.ExecuteNonQueryAsync(ct);
        }
        command.CommandText = """
DELETE FROM surf.DataValueException;
IF NOT EXISTS(SELECT 1 FROM surf.SchemaMigration WHERE SchemaVersion=2)
    INSERT surf.SchemaMigration(SchemaVersion,ScriptChecksum) VALUES(2,@Checksum);
UPDATE surf.StorageFormatInfo SET SchemaVersion=2,MinimumReaderVersion=2,MinimumWriterVersion=2 WHERE Singleton=1;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Checksum", SqlDbType.Binary,
            RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(2)), 32));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql,
        params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 0;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }
}

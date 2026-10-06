using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Migration;

/// <summary>Serializes conversion/import into one destination without locking the source.</summary>
public sealed class MigrationTargetLease : IAsyncDisposable
{
    private SqlConnection? _connection;
    private MigrationTargetLease(SqlConnection connection) => _connection = connection;

    public static async Task<MigrationTargetLease> AcquireAsync(RelationalSession session, CancellationToken ct = default)
    {
        var connection = await session.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
DECLARE @Result int;
EXEC @Result=sys.sp_getapplock @Resource=N'Surf2.Relational.Migration',
    @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0;
SELECT @Result;
""";
            using var cancel = RelationalSession.CancelCommand(command, ct);
            int result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (result < 0) throw new InvalidOperationException("Another conversion or import is using this destination. Wait for it to finish before resuming.");
            return new(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection == null) return;
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "DECLARE @Result int; EXEC @Result=sys.sp_releaseapplock @Resource=N'Surf2.Relational.Migration', @LockOwner='Session'; SELECT @Result;";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None)) < 0)
                    SqlConnection.ClearPool(connection);
            }
        }
        catch (SqlException)
        {
            SqlConnection.ClearPool(connection);
        }
        finally { await connection.DisposeAsync(); }
    }

    public async Task EnsureHeldAsync(CancellationToken ct = default)
    {
        var connection = _connection ?? throw new ObjectDisposedException(nameof(MigrationTargetLease));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT APPLOCK_MODE(N'public',N'Surf2.Relational.Migration',N'Session');";
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not string mode || mode != "Exclusive")
            throw new InvalidOperationException("The destination conversion lock was lost. Resume from the retained staging files.");
    }
}

using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Services.RelationalSnapshots;

internal readonly record struct HistoryLineCountKey(long ResourceKey, int Kind, string InputHash);
internal readonly record struct StoredHistoryLineCount(long ResourceKey, int Kind, string InputHash, int Added, int Removed);

/// <summary>Optional derived counts. A user with read access can still calculate history without the extension.</summary>
internal sealed class HistoryCountStore(RelationalSession session)
{
    private const int Available = 1, Unavailable = 2;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private int _status;

    private async Task<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _status) != 0) return _status == Available;
        if (session.IsMigrationValidation) return false;
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_status != 0) return _status == Available;
            var assembly = typeof(HistoryCountStore).Assembly;
            string name = assembly.GetManifestResourceNames().Single(value => value.EndsWith(".002.HistoryCounts.sql", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var scriptReader = new StreamReader(stream);
            string script = await scriptReader.ReadToEndAsync(ct).ConfigureAwait(false);
            byte[] checksum = RelationalSchemaInstaller.ScriptChecksum([script]);
            await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
            // A deployed extension can be read by users without DDL permissions.
            var exists = (int)(await ScalarAsync(connection, null, """
                SELECT CASE WHEN OBJECT_ID(N'surf.HistoryCountExtension',N'U') IS NULL THEN 0 ELSE 1 END;
                """, ct).ConfigureAwait(false))! == 1;
            if (!exists)
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
                await using var command = Command(connection, transaction, """
                    DECLARE @Lock int;
                    EXEC @Lock=sys.sp_getapplock @Resource=N'Surf2.HistoryCounts.v1', @LockMode='Exclusive',
                        @LockOwner='Transaction', @LockTimeout=10000;
                    IF @Lock<0 THROW 51112,'History count installation lock unavailable.',1;
                    IF NOT EXISTS(SELECT 1 FROM surf.StorageFormatInfo WHERE Singleton=1 AND State='Ready')
                        THROW 51113,'History counts require a ready database.',1;
                    SELECT CASE WHEN OBJECT_ID(N'surf.HistoryCountExtension',N'U') IS NULL THEN 0 ELSE 1 END;
                    """);
                using var cancel = RelationalSession.CancelCommand(command, ct);
                if ((int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0)
                {
                    command.CommandText = script;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    command.CommandText = "INSERT surf.HistoryCountExtension(Singleton,Version,Checksum) VALUES(1,1,@Checksum);";
                    command.Parameters.Add(Hash("@Checksum", checksum));
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            bool valid = Equals(await ScalarAsync(connection, null, """
                SELECT COUNT_BIG(*) FROM surf.HistoryCountExtension WHERE Singleton=1 AND Version=1 AND Checksum=@Checksum;
                """, ct, Hash("@Checksum", checksum)).ConfigureAwait(false), 1L);
            Volatile.Write(ref _status, valid ? Available : Unavailable);
            return valid;
        }
        catch (SqlException error) when (!ct.IsCancellationRequested)
        {
            Disable(error);
            return false;
        }
        finally { _installGate.Release(); }
    }

    public async Task<IReadOnlyDictionary<HistoryLineCountKey, (int Added, int Removed)>> ReadLinesAsync(
        SnapshotVersionSummary version, IReadOnlyList<HistoryLineCountKey> keys, CancellationToken ct)
    {
        var result = new Dictionary<HistoryLineCountKey, (int Added, int Removed)>();
        if (version.VersionKey <= 0 || keys.Count == 0 || !await EnsureAvailableAsync(ct).ConfigureAwait(false)) return result;
        try
        {
            await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
            await using var command = Command(connection, null, """
                SELECT c.ResourceKey,c.Kind,c.InputHash,c.AddedLines,c.RemovedLines
                FROM OPENJSON(@Files) WITH (ResourceKey bigint, Kind int, InputHash varchar(64)) f
                JOIN surf.HistoryFileLineCount c ON c.ResourceKey=f.ResourceKey AND c.Kind=f.Kind AND c.InputHash=CONVERT(binary(32),f.InputHash,2)
                WHERE c.VersionKey=@Version AND c.SnapshotKey=@Snapshot;
                """, Key("@Version", version.VersionKey), Key("@Snapshot", version.SnapshotKey), Text("@Files", JsonSerializer.Serialize(keys)));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(new(reader.GetInt64(0), reader.GetInt32(1), Convert.ToHexString((byte[])reader.GetValue(2))),
                    (reader.GetInt32(3), reader.GetInt32(4)));
        }
        catch (SqlException error) when (!ct.IsCancellationRequested) { Disable(error); }
        return result;
    }

    public Task WriteLinesAsync(SnapshotVersionSummary version, byte[] head, IReadOnlyList<StoredHistoryLineCount> rows, CancellationToken ct) =>
        rows.Count == 0 ? Task.CompletedTask : WriteAsync(version, head, """
            INSERT surf.HistoryFileLineCount(VersionKey,SnapshotKey,ResourceKey,Kind,InputHash,AddedLines,RemovedLines)
            SELECT @Version,@Snapshot,f.ResourceKey,f.Kind,CONVERT(binary(32),f.InputHash,2),f.Added,f.Removed
            FROM OPENJSON(@Files) WITH (ResourceKey bigint, Kind int, InputHash varchar(64), Added int, Removed int) f
            WHERE NOT EXISTS(SELECT 1 FROM surf.HistoryFileLineCount c WITH(UPDLOCK,HOLDLOCK)
                WHERE c.VersionKey=@Version AND c.ResourceKey=f.ResourceKey AND c.Kind=f.Kind AND c.InputHash=CONVERT(binary(32),f.InputHash,2));
            """, Text("@Files", JsonSerializer.Serialize(rows)), ct);

    public async Task<long?> ReadInitialAsync(SnapshotVersionSummary version, byte[] dependency, CancellationToken ct)
    {
        if (version.VersionKey <= 0 || !await EnsureAvailableAsync(ct).ConfigureAwait(false)) return null;
        try
        {
            await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
            return await ScalarAsync(connection, null, """
                SELECT FileCount FROM surf.HistoryInitialFileCount
                WHERE VersionKey=@Version AND SnapshotKey=@Snapshot AND ProjectionHash=@Projection;
                """, ct, Key("@Version", version.VersionKey), Key("@Snapshot", version.SnapshotKey), Hash("@Projection", dependency)).ConfigureAwait(false) as long?;
        }
        catch (SqlException error) when (!ct.IsCancellationRequested) { Disable(error); return null; }
    }

    public Task WriteInitialAsync(SnapshotVersionSummary version, byte[] head, byte[] dependency, long count, CancellationToken ct) =>
        WriteAsync(version, head, """
            INSERT surf.HistoryInitialFileCount(VersionKey,SnapshotKey,ProjectionHash,FileCount)
            SELECT @Version,@Snapshot,@Projection,@Count
            WHERE NOT EXISTS(SELECT 1 FROM surf.HistoryInitialFileCount WITH(UPDLOCK,HOLDLOCK)
                WHERE VersionKey=@Version AND ProjectionHash=@Projection);
            """, Hash("@Projection", dependency), ct, Key("@Count", count));

    private async Task WriteAsync(SnapshotVersionSummary version, byte[] head, string insert, SqlParameter value, CancellationToken ct,
        params SqlParameter[] extra)
    {
        if (version.VersionKey <= 0 || !await EnsureAvailableAsync(ct).ConfigureAwait(false)) return;
        try
        {
            await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            if (await ScalarAsync(connection, transaction, """
                SELECT SnapshotKey FROM surf.DatabaseSnapshot WITH(HOLDLOCK)
                WHERE SnapshotKey=@Snapshot AND RowVersion=@Head AND IsPublished=1;
                """, ct, Key("@Snapshot", version.SnapshotKey), Token("@Head", head)).ConfigureAwait(false) == null)
                throw new SnapshotConcurrencyException();
            await ScalarAsync(connection, transaction, insert, ct,
                [Key("@Version", version.VersionKey), Key("@Snapshot", version.SnapshotKey), value, .. extra]).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException error) when (!ct.IsCancellationRequested) { Disable(error); }
    }

    private void Disable(SqlException error)
    {
        if (Interlocked.Exchange(ref _status, Unavailable) != Unavailable)
            InternalLogService.Warning("Saved history line counts are unavailable; counts will be calculated when requested.", ("SQL error", error.Number));
    }
}

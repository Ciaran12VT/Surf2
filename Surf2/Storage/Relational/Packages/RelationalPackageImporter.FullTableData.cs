using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageImporter
{
    private async Task<long> LoadFullTableDataAsync(SqlConnection connection, RelationalPackageTableStream stream,
        PackageInput package, Guid import, byte[] fingerprint, Guid database, CancellationToken ct)
    {
        await ExecuteAsync(connection, null, "CREATE TABLE #PackagePayload (Bytes varbinary(max) NOT NULL);", ct);
        try
        {
            await using var input = package.Entries[stream.Entry].Open();
            using var verified = new PackageEntryReadStream(input, stream.ByteCount, PackageRowDecoding.Hash(stream.Sha256));
            var decoder = new PackageRowDecoding(verified, _limits);
            long count = 0, previous = 0;
            while (true)
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
                long? key = await decoder.ReadFullTableDataAsync(async (dataSet, length, token) =>
                {
                    if (dataSet <= previous || count >= stream.RowCount)
                        throw PackageRowDecoding.Bad("Captured JSON keys or counts are out of order");
                    await RequireImportAsync(connection, transaction, import, fingerprint, database, token);
                    await ExecuteAsync(connection, transaction, "INSERT #PackagePayload(Bytes) VALUES(0x);", token);
                }, async (bytes, token) =>
                {
                    await ExecuteAsync(connection, transaction, "UPDATE #PackagePayload SET Bytes.WRITE(@Chunk,NULL,NULL);", token,
                        RelationalSession.Parameter("@Chunk", SqlDbType.VarBinary, bytes.ToArray(), -1));
                }, ct);
                if (key == null) break;
                await ExecuteAsync(connection, transaction, """
INSERT surf.FullTableData(DataSetKey,JsonPayload)
 SELECT @Key,CONVERT(nvarchar(max),Bytes) FROM #PackagePayload;
DELETE FROM #PackagePayload;
""", ct, RelationalSession.Parameter("@Key", SqlDbType.BigInt, key.Value));
                await transaction.CommitAsync(ct);
                previous = key.Value;
                count++;
            }
            if (count != stream.RowCount) throw PackageRowDecoding.Bad("Captured JSON row count differs from the manifest");
            verified.Finish();
            return count;
        }
        finally { await ExecuteAsync(connection, null, "DROP TABLE #PackagePayload;", CancellationToken.None); }
    }

    private async Task ValidateFullTableDataAsync(SqlConnection connection, SqlTransaction transaction,
        RelationalPackageTableStream stream, PackageInput package, CancellationToken ct)
    {
        await using var input = package.Entries[stream.Entry].Open();
        using var verified = new PackageEntryReadStream(input, stream.ByteCount, PackageRowDecoding.Hash(stream.Sha256));
        var decoder = new PackageRowDecoding(verified, _limits);
        await using var command = Command(connection, transaction, """
SELECT DataSetKey,CONVERT(bigint,DATALENGTH(JsonPayload)),CONVERT(varbinary(max),JsonPayload)
FROM surf.FullTableData ORDER BY DataSetKey;
""");
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        Stream? stored = null;
        byte[] buffer = new byte[65536];
        long count = 0, previous = 0;
        try
        {
            while (await decoder.ReadFullTableDataAsync(async (key, length, token) =>
            {
                if (key <= previous || count >= stream.RowCount || !await reader.ReadAsync(token) ||
                    reader.GetInt64(0) != key || reader.GetInt64(1) != length)
                    throw PackageRowDecoding.Bad("Restored JSON capture identity or length differs from the package");
                previous = key;
                stored = reader.GetStream(2);
            }, async (bytes, token) =>
            {
                await stored!.ReadExactlyAsync(buffer.AsMemory(0, bytes.Length), token);
                if (!buffer.AsSpan(0, bytes.Length).SequenceEqual(bytes.Span))
                    throw PackageRowDecoding.Bad("Restored JSON capture bytes differ from the package");
            }, ct) != null)
            {
                if (await stored!.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
                    throw PackageRowDecoding.Bad("Restored JSON capture exceeds the package payload");
                await stored.DisposeAsync();
                stored = null;
                count++;
            }
            if (count != stream.RowCount || await reader.ReadAsync(ct))
                throw PackageRowDecoding.Bad("Restored JSON capture count differs from the package");
            verified.Finish();
        }
        finally { if (stored != null) await stored.DisposeAsync(); }
    }
}

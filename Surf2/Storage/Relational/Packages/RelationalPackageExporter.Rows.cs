using System.Data;
using System.Data.SqlTypes;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageExporter
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    private async Task<RelationalPackageTableStream> WriteTableAsync(SqlConnection connection, SqlTransaction transaction,
        PackageTableSpec spec, RelationalPackageTable table, ZipArchive archive, PackageBudget budget, CancellationToken ct)
    {
        var columns = table.Columns.Where(column => column.StreamOrdinal.HasValue).ToArray();
        budget.AddEntry(spec.Entry);
        var entry = archive.CreateEntry(spec.Entry, CompressionLevel.Fastest);
        await using var output = entry.Open();
        using var hash = new PackageHashingStream(output, budget, _limits.MaxEntryBytes);
        using var writer = new Utf8JsonWriter(hash);
        byte[] buffer = new byte[_limits.ChunkBytes];
        long count = 0;
        await using (var command = Command(connection, transaction, PackageTableCatalogue.SelectSql(spec, table)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, ct);
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                writer.WriteStartArray();
                int sourceOrdinal = 0;
                long rowBytes = 0;
                foreach (var column in columns)
                {
                    bool chunked = PackageRowEncoding.IsChunked(column.Encoding!);
                    bool isNull = await reader.IsDBNullAsync(sourceOrdinal, ct);
                    if (isNull)
                        writer.WriteNullValue();
                    else if (chunked)
                    {
                        long length = reader.GetInt64(sourceOrdinal);
                        if (length < 0 || length > _limits.MaxCellBytes)
                            throw new RelationalPackageLimitException("An authoritative SQL cell exceeds the package cell-byte limit.");
                        rowBytes = checked(rowBytes + length);
                        if (rowBytes > _limits.MaxRowBytes) throw new RelationalPackageLimitException("Package source-row byte limit exceeded.");
                        await using var input = reader.GetStream(sourceOrdinal + 1);
                        await PackageRowEncoding.WriteChunksAsync(writer, input, length, buffer, ct);
                    }
                    else
                    {
                        rowBytes = checked(rowBytes + 256);
                        if (rowBytes > _limits.MaxRowBytes) throw new RelationalPackageLimitException("Package source-row byte limit exceeded.");
                        PackageRowEncoding.WriteScalar(writer, column.SqlType, await ReadScalarAsync(reader, sourceOrdinal, column, ct));
                    }
                    sourceOrdinal += chunked ? 2 : 1;
                    await writer.FlushAsync(ct);
                }
                writer.WriteEndArray();
                await writer.FlushAsync(ct);
                await hash.WriteAsync(NewLine, ct);
                writer.Reset(hash);
                count = checked(count + 1);
            }
        }
        await writer.FlushAsync(ct);
        return new(spec.Entry, table, count, hash.Bytes, hash.Finish());
    }

    private static async Task<object> ReadScalarAsync(SqlDataReader reader, int ordinal, RelationalPackageColumn column, CancellationToken ct) => column.SqlType switch
    {
        "bigint" => await reader.GetFieldValueAsync<long>(ordinal, ct),
        "int" => await reader.GetFieldValueAsync<int>(ordinal, ct),
        "smallint" => await reader.GetFieldValueAsync<short>(ordinal, ct),
        "tinyint" => await reader.GetFieldValueAsync<byte>(ordinal, ct),
        "bit" => await reader.GetFieldValueAsync<bool>(ordinal, ct),
        "decimal" or "numeric" => await reader.GetFieldValueAsync<SqlDecimal>(ordinal, ct),
        "money" or "smallmoney" => await reader.GetFieldValueAsync<SqlMoney>(ordinal, ct),
        "float" when column.Encoding == "float32-bits-v1" => await reader.GetFieldValueAsync<float>(ordinal, ct),
        "float" => await reader.GetFieldValueAsync<double>(ordinal, ct),
        "real" => await reader.GetFieldValueAsync<float>(ordinal, ct),
        "uniqueidentifier" => await reader.GetFieldValueAsync<Guid>(ordinal, ct),
        "date" or "datetime" or "datetime2" or "smalldatetime" => await reader.GetFieldValueAsync<DateTime>(ordinal, ct),
        "datetimeoffset" => await reader.GetFieldValueAsync<DateTimeOffset>(ordinal, ct),
        "time" => await reader.GetFieldValueAsync<TimeSpan>(ordinal, ct),
        _ => throw new InvalidDataException("Unsupported SQL scalar in package stream.")
    };

    private async Task ValidateCompleteCapturesAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"""
SELECT CONVERT(bit,CASE WHEN EXISTS (SELECT 1 FROM surf.TableDataRevision t WHERE ({PackageTableCatalogue.Revision("t.RevisionKey")})
AND NOT EXISTS (SELECT 1 FROM surf.DataSet d WHERE d.RevisionKey=t.RevisionKey AND ({PackageTableCatalogue.DataSet("d")}))) THEN 1 ELSE 0 END);
""");
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        if ((bool)(await command.ExecuteScalarAsync(ct) ?? true)) throw new InvalidDataException("A published sealed table-data revision has no complete dataset.");
    }

    private async Task ValidateCaptureCountsAsync(SqlConnection connection, SqlTransaction transaction,
        PackageTableSpec spec, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"""
SELECT d.ActualRowCount,d.ExpectedActualRowCount,r.[RowCount],r.FirstRow,r.LastRow
FROM surf.DataSet d OUTER APPLY (SELECT COUNT_BIG(*) [RowCount],MIN(RowOrdinal) FirstRow,MAX(RowOrdinal) LastRow
FROM {spec.Qualified} t WHERE t.DataSetKey=d.DataSetKey) r
WHERE d.LayoutKey=@Layout AND ({PackageTableCatalogue.DataSet("d")});
""", RelationalSession.Parameter("@Layout", SqlDbType.BigInt, spec.CaptureLayoutKey!.Value));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long actual = reader.GetInt64(0), stored = reader.GetInt64(2);
            if (actual < 0 || reader.IsDBNull(1) || reader.GetInt64(1) != actual || stored != actual ||
                (stored > 0 && (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.GetInt64(3) != 0 || reader.GetInt64(4) != stored - 1)))
                throw new InvalidDataException("Captured export count or stable ordinals do not match the Ready dataset catalogue.");
        }
    }
}

using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Capture;

public sealed partial class RelationalCaptureStore
{
    // Only the schema upgrade reads generated tables. Its caller owns the
    // transaction and holds the dataset lock throughout conversion.
    internal async IAsyncEnumerable<CaptureRow> StreamLegacyRowsAsync(SqlConnection connection,
        SqlTransaction transaction, long dataSetKey, CaptureLayout layout, long expectedRowCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var layoutCommand = Command(connection, transaction,
            "SELECT LayoutKey FROM surf.DataSet WHERE DataSetKey = @DataSet;", Key("@DataSet", dataSetKey));
        using var cancellation = RelationalSession.CancelCommand(layoutCommand, cancellationToken);
        long layoutKey = (long)(await layoutCommand.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Legacy captured dataset is missing."));
        long count = 0;
        while (true)
        {
            var batch = await ReadLegacyBatchAsync(connection, transaction, dataSetKey, layoutKey, layout,
                count - 1, cancellationToken);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                if (row.RowOrdinal != count || count >= expectedRowCount)
                    throw new InvalidDataException("Legacy captured rows have invalid ordinals or counts.");
                yield return row;
                count++;
            }
        }
        if (count != expectedRowCount)
            throw new InvalidDataException("Legacy captured row count differs from its recorded count.");
    }

    private async Task<List<CaptureRow>> ReadLegacyBatchAsync(SqlConnection connection, SqlTransaction transaction,
        long dataSetKey, long layoutKey, CaptureLayout layout, long afterOrdinal, CancellationToken cancellationToken)
    {
        int rowBudget = _limits.ReadBatchRows;
        long byteBudget = _limits.ReadBatchBytes;
        string table = CaptureLayout.QualifiedTable(layoutKey);
        string columns = string.Concat(Enumerable.Range(0, layout.Columns.Length).Select(i => ", r.[" + CaptureLayout.ColumnName(i) + "]"));
        var encodedRows = new List<(long Ordinal, EncodedCaptureRow Row)>();
        var minimumEstimates = new Dictionary<long, long>();
        var tokenBytesByRow = new Dictionary<long, long>();
        var storedEstimates = new Dictionary<long, long>();
        // Bound metadata candidates first, then fetch LOB columns for the byte-
        // bounded range. No OFFSET or SQL collation can omit a .NET match.
        await using (var command = Command(connection, transaction, $"""
WITH Candidates AS (
    SELECT TOP (@Take) RowOrdinal, EstimatedBytes FROM {table}
    WHERE DataSetKey = @DataSet AND RowOrdinal > @After ORDER BY RowOrdinal
), Budgeted AS (
    SELECT RowOrdinal, SUM(EstimatedBytes) OVER (ORDER BY RowOrdinal ROWS UNBOUNDED PRECEDING) AS BytesSoFar,
           ROW_NUMBER() OVER (ORDER BY RowOrdinal) AS Position FROM Candidates
)
SELECT r.RowOrdinal, r.RowKind, r.EstimatedBytes, r.PropertyOrder{columns}
FROM {table} AS r INNER JOIN Budgeted AS b ON r.RowOrdinal = b.RowOrdinal
WHERE r.DataSetKey = @DataSet AND (b.Position = 1 OR b.BytesSoFar <= @Budget)
ORDER BY r.RowOrdinal;
""", RelationalSession.Parameter("@Take", SqlDbType.Int, rowBudget),
            Key("@DataSet", dataSetKey), Key("@After", afterOrdinal), Key("@Budget", byteBudget)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            long bytes = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                long ordinal = reader.GetInt64(0);
                var kind = (JsonValueKind)reader.GetByte(1);
                long estimate = reader.GetInt64(2);
                if (kind is < JsonValueKind.Object or > JsonValueKind.Null || ordinal <= afterOrdinal ||
                    estimate is <= 0 or > CaptureLimits.MaximumEstimatedRowBytes ||
                    bytes + estimate > byteBudget || encodedRows.Count >= rowBudget)
                    throw new CaptureLimitException("Stored rows exceed the bounded read contract.");
                var shape = await reader.GetFieldValueAsync<byte[]>(3, cancellationToken);
                if (shape.Length > CaptureLimits.MaximumShapeBytes) throw new InvalidDataException("Oversized property descriptor.");
                var tokens = new string?[layout.Columns.Length];
                long minimumEstimate = shape.Length * 4L + 4096 + tokens.Length * 256L;
                long tokenBytes = 0;
                for (int i = 0; i < tokens.Length; i++)
                    if (!await reader.IsDBNullAsync(i + 4, cancellationToken))
                    {
                        tokens[i] = await reader.GetFieldValueAsync<string>(i + 4, cancellationToken);
                        tokenBytes += Encoding.UTF8.GetByteCount(tokens[i]!);
                        minimumEstimate += tokens[i]!.Length * 8L;
                        if (tokenBytes > CaptureLimits.MaximumRowUtf8Bytes || minimumEstimate > estimate)
                            throw new CaptureLimitException("Stored scalar payload exceeds its validated row budget.");
                    }
                if (minimumEstimate > estimate) throw new CaptureLimitException("Stored descriptor exceeds its validated row budget.");
                minimumEstimates.Add(ordinal, minimumEstimate);
                tokenBytesByRow.Add(ordinal, tokenBytes);
                storedEstimates.Add(ordinal, estimate);
                encodedRows.Add((ordinal, new(kind, shape, tokens, [], estimate)));
                bytes += estimate;
                afterOrdinal = ordinal;
            }
        }
        if (encodedRows.Count == 0) return [];
        var exceptions = new Dictionary<long, List<CaptureValueException>>();
        var exceptionBytes = new Dictionary<long, long>();
        await using (var command = Command(connection, transaction, """
SELECT RowOrdinal, PropertyOrdinal, ValueKind, RawToken FROM surf.DataValueException
WHERE DataSetKey = @DataSet AND RowOrdinal BETWEEN @First AND @Last ORDER BY RowOrdinal, PropertyOrdinal;
""", Key("@DataSet", dataSetKey), Key("@First", encodedRows[0].Ordinal), Key("@Last", encodedRows[^1].Ordinal)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long ordinal = reader.GetInt64(0);
                int propertyOrdinal = reader.GetInt32(1);
                var kind = (JsonValueKind)reader.GetByte(2);
                string token = await reader.GetFieldValueAsync<string>(3, cancellationToken);
                if (!exceptions.TryGetValue(ordinal, out var values)) exceptions[ordinal] = values = [];
                long bytes = exceptionBytes.GetValueOrDefault(ordinal) + Encoding.UTF8.GetByteCount(token);
                if (values.Count >= CaptureLimits.MaximumExceptionsPerRow || bytes > CaptureLimits.MaximumRowUtf8Bytes)
                    throw new CaptureLimitException("Stored exception values exceed the per-row budget.");
                if (!minimumEstimates.TryGetValue(ordinal, out long minimumEstimate))
                    throw new InvalidDataException("An exceptional value has no captured row in its ordinal range.");
                minimumEstimate += token.Length * 8L;
                if (bytes + tokenBytesByRow[ordinal] > CaptureLimits.MaximumRowUtf8Bytes || minimumEstimate > storedEstimates[ordinal])
                    throw new CaptureLimitException("Stored exception payload exceeds its validated row budget.");
                minimumEstimates[ordinal] = minimumEstimate;
                exceptionBytes[ordinal] = bytes;
                values.Add(new(propertyOrdinal, kind, token));
            }
        }
        var result = new List<CaptureRow>(encodedRows.Count);
        foreach (var encoded in encodedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = encoded.Row with { Exceptions = exceptions.GetValueOrDefault(encoded.Ordinal)?.ToArray() ?? [] };
            result.Add(new(encoded.Ordinal, CaptureRowCodec.Decode(layout, row), row.EstimatedBytes));
        }
        return result;
    }

}

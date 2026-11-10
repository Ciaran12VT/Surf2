using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Capture;

public sealed partial class RelationalCaptureStore
{
    public async Task<CaptureDataSetDescriptor> GetDescriptorAsync(long dataSetKey,
        CancellationToken cancellationToken = default)
    {
        if (dataSetKey <= 0) throw new ArgumentOutOfRangeException(nameof(dataSetKey));
        await _session.RequireReadyAsync(cancellationToken);
        await using var connection = await _session.OpenAsync(cancellationToken);
        return await ReadDescriptorAsync(connection, dataSetKey, cancellationToken);
    }

    // One selected revision's catalogue/headers, with no captured-row reads.
    public async Task<CaptureDataSetDescriptor?> GetForRevisionAsync(long revisionKey,
        CancellationToken cancellationToken = default)
    {
        if (revisionKey <= 0) throw new ArgumentOutOfRangeException(nameof(revisionKey));
        await _session.RequireReadyAsync(cancellationToken);
        await using var connection = await _session.OpenAsync(cancellationToken);
        await using var command = Command(connection, null,
            "SELECT DataSetKey FROM surf.DataSet WHERE RevisionKey = @Revision AND State = 'Ready';", Key("@Revision", revisionKey));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        object? key = await command.ExecuteScalarAsync(cancellationToken);
        return key is long value ? await ReadDescriptorAsync(connection, value, cancellationToken) : null;
    }

    private async Task<CaptureDataSetDescriptor> ReadDescriptorAsync(SqlConnection connection, long dataSetKey,
        CancellationToken cancellationToken)
    {
        CaptureDataSetSummary summary;
        await using (var command = Command(connection, null,
            $"SELECT {SummaryProjection} FROM surf.DataSet WHERE DataSetKey = @DataSet AND State = 'Ready';", Key("@DataSet", dataSetKey)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Ready captured dataset not found.");
            summary = ReadSummary(reader);
        }
        if (summary.DisplayFormatVersion != CaptureDisplay.FormatVersion) throw new InvalidDataException("Unsupported display format version.");
        var layout = await ReadLayoutAsync(connection, null, summary.LayoutKey, cancellationToken);
        return new(_session.Epoch, summary, Array.AsReadOnly(layout.Columns));
    }

    public async IAsyncEnumerable<CaptureRow> StreamRowsAsync(long dataSetKey, CaptureQuery? query = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = FrozenCaptureQuery.Snapshot(query);
        var descriptor = await GetDescriptorAsync(dataSetKey, cancellationToken);
        var frozen = new FrozenCaptureQuery(snapshot, descriptor.Columns.Count);
        await foreach (var row in StreamQueryAsync(descriptor, frozen, -1, cancellationToken).ConfigureAwait(false))
            yield return row;
    }

    private IAsyncEnumerable<CaptureRow> StreamQueryAsync(CaptureDataSetDescriptor descriptor, FrozenCaptureQuery query,
        long afterOrdinal, CancellationToken cancellationToken)
    {
        var source = StreamFilteredAsync(descriptor, query, afterOrdinal, cancellationToken);
        return query.Sorts.Length == 0 ? source : CaptureExternalSort.SortAsync(source, query, descriptor.Columns, _limits, cancellationToken);
    }

    private async IAsyncEnumerable<CaptureRow> StreamFilteredAsync(CaptureDataSetDescriptor descriptor,
        FrozenCaptureQuery query, long afterOrdinal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layout = new CaptureLayout(descriptor.Columns);
        long after = afterOrdinal;
        while (true)
        {
            var batch = await ReadBatchAsync(descriptor, layout, after, cancellationToken);
            if (batch.Count == 0) yield break;
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                after = row.RowOrdinal;
                if (query.Matches(row.Value, descriptor.Columns)) yield return row;
            }
        }
    }

    private async Task<List<CaptureRow>> ReadBatchAsync(CaptureDataSetDescriptor descriptor, CaptureLayout layout,
        long afterOrdinal, CancellationToken cancellationToken, int? requestedRows = null, long? requestedBytes = null)
    {
        int rowBudget = Math.Min(requestedRows ?? _limits.ReadBatchRows, _limits.ReadBatchRows);
        long byteBudget = Math.Min(requestedBytes ?? _limits.ReadBatchBytes, _limits.ReadBatchBytes);
        long dataSetKey = descriptor.Summary.DataSetKey;
        string table = CaptureLayout.QualifiedTable(descriptor.Summary.LayoutKey);
        string columns = string.Concat(Enumerable.Range(0, layout.Columns.Length).Select(i => ", r.[" + CaptureLayout.ColumnName(i) + "]"));
        var encodedRows = new List<(long Ordinal, EncodedCaptureRow Row)>();
        var minimumEstimates = new Dictionary<long, long>();
        var tokenBytesByRow = new Dictionary<long, long>();
        var storedEstimates = new Dictionary<long, long>();
        await using var connection = await _session.OpenAsync(cancellationToken);
        // Bound metadata candidates first, then fetch LOB columns for the byte-
        // bounded range. No OFFSET or SQL collation can omit a .NET match.
        await using (var command = Command(connection, null, $"""
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
        await using (var command = Command(connection, null, """
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

    // Sorted cursors use a verified position in a freshly built immutable exact
    // result. Unsorted cursors seek RowOrdinal. No cursor means position zero.
    // This deliberately bounded fallback may rescan/re-sort on each page; a UI
    // can retain its own operation-owned result index rather than repeat work.
    public async Task<CapturePage> ReadPageAsync(long dataSetKey, CaptureQuery? query = null,
        CapturePageCursor? cursor = null, int? maxRows = null, long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        int rowLimit = maxRows ?? _limits.MaxPageRows;
        long byteLimit = maxBytes ?? _limits.MaxPageBytes;
        if (rowLimit <= 0 || rowLimit > _limits.MaxPageRows || byteLimit <= 0 || byteLimit > _limits.MaxPageBytes)
            throw new ArgumentOutOfRangeException(nameof(maxRows), "Invalid page budget.");
        var snapshot = FrozenCaptureQuery.Snapshot(query);
        var descriptor = await GetDescriptorAsync(dataSetKey, cancellationToken);
        var frozen = new FrozenCaptureQuery(snapshot, descriptor.Columns.Count);
        if (cursor != null && (cursor.Epoch != _session.Epoch || cursor.DataSetKey != dataSetKey ||
            cursor.QueryFingerprint != frozen.Fingerprint || cursor.NextOffset < 0 || cursor.AfterRowOrdinal < -1 ||
            cursor.NextOffset > descriptor.Summary.ActualRowCount || cursor.AfterRowOrdinal >= descriptor.Summary.ActualRowCount))
            throw new ArgumentException("The page cursor does not match this immutable dataset/query/connection epoch.", nameof(cursor));
        long offset = cursor?.NextOffset ?? 0;
        long skip = frozen.Sorts.Length == 0 ? 0 : offset;
        long after = frozen.Sorts.Length == 0 ? cursor?.AfterRowOrdinal ?? -1 : -1;
        if (frozen.Sorts.Length == 0 && frozen.EffectiveFilters.Length == 0)
        {
            var batch = await ReadBatchAsync(descriptor, new CaptureLayout(descriptor.Columns), after,
                cancellationToken, rowLimit, byteLimit);
            if (batch.Count == 0 && after + 1 < descriptor.Summary.ActualRowCount)
                throw new InvalidDataException("Published dataset has a missing ordinal range.");
            long batchBytes = batch.Sum(row => row.EstimatedBytes);
            bool more = batch.Count > 0 && batch[^1].RowOrdinal < descriptor.Summary.ActualRowCount - 1;
            return new(batch.AsReadOnly(), more ? new(_session.Epoch, dataSetKey, frozen.Fingerprint,
                checked(offset + batch.Count), batch[^1].RowOrdinal) : null, batchBytes);
        }
        var rows = new List<CaptureRow>();
        long bytes = 0;
        bool hasMore = false;
        await foreach (var row in StreamQueryAsync(descriptor, frozen, after, cancellationToken).ConfigureAwait(false))
        {
            if (skip > 0) { skip--; continue; }
            if (row.EstimatedBytes > byteLimit && rows.Count == 0)
                throw new CaptureLimitException("One row exceeds the requested page byte budget.");
            if (rows.Count == rowLimit || bytes + row.EstimatedBytes > byteLimit) { hasMore = true; break; }
            rows.Add(row);
            bytes += row.EstimatedBytes;
        }
        return new(rows.AsReadOnly(), hasMore ? new(_session.Epoch, dataSetKey, frozen.Fingerprint,
            checked(offset + rows.Count), rows[^1].RowOrdinal) : null, bytes);
    }

    public async Task<long> CountRowsAsync(long dataSetKey, CaptureQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = FrozenCaptureQuery.Snapshot(query);
        var descriptor = await GetDescriptorAsync(dataSetKey, cancellationToken);
        var frozen = new FrozenCaptureQuery(snapshot, descriptor.Columns.Count);
        if (frozen.EffectiveFilters.Length == 0) return descriptor.Summary.ActualRowCount;
        long count = 0;
        await foreach (var row in StreamFilteredAsync(descriptor, frozen, -1, cancellationToken).ConfigureAwait(false))
            count = checked(count + 1);
        return count;
    }

    public async Task<IReadOnlyList<int>> FindMatchingColumnsAsync(long dataSetKey, string literal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(literal);
        if (literal.Length > 4096) throw new CaptureLimitException("Search text exceeds 4096 characters.");
        var descriptor = await GetDescriptorAsync(dataSetKey, cancellationToken);
        var query = new FrozenCaptureQuery(null, descriptor.Columns.Count);
        var matching = new HashSet<int>();
        await foreach (var row in StreamFilteredAsync(descriptor, query, -1, cancellationToken).ConfigureAwait(false))
        {
            for (int i = 0; i < descriptor.Columns.Count; i++)
                if (!matching.Contains(i) && CaptureDisplay.Cell(row.Value, descriptor.Columns[i].SourceName)
                    .IndexOf(literal, StringComparison.OrdinalIgnoreCase) >= 0) matching.Add(i);
            if (matching.Count == descriptor.Columns.Count) break;
        }
        return Array.AsReadOnly(matching.Order().ToArray());
    }

    // The caller owns destination publication/partial-file cleanup. This method
    // streams the full pinned query and never creates a clipboard-sized string.
    // Display exports omit non-object rows like the legacy table document;
    // StreamRowsAsync always retains the original row shapes and ordinals.
    public async Task<long> WriteDelimitedAsync(long dataSetKey, Stream destination, CaptureQuery? query = null,
        IReadOnlyList<int>? columnProjection = null, char delimiter = ',', CancellationToken cancellationToken = default,
        bool skipNonObjectRows = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (delimiter is not (',' or '\t')) throw new ArgumentOutOfRangeException(nameof(delimiter));
        var snapshot = FrozenCaptureQuery.Snapshot(query);
        if (columnProjection?.Count > CaptureLimits.MaximumColumns) throw new CaptureLimitException("Too many projected columns.");
        int[]? selectedColumns = columnProjection?.ToArray();
        var descriptor = await GetDescriptorAsync(dataSetKey, cancellationToken);
        var frozen = new FrozenCaptureQuery(snapshot, descriptor.Columns.Count);
        int[] projection = selectedColumns ?? Enumerable.Range(0, descriptor.Columns.Count).ToArray();
        if (projection.Length > CaptureLimits.MaximumColumns || projection.Any(i => i < 0 || i >= descriptor.Columns.Count))
            throw new ArgumentOutOfRangeException(nameof(columnProjection));
        return await CaptureDelimitedWriter.WriteAsync(StreamQueryAsync(descriptor, frozen, -1, cancellationToken),
            destination, descriptor.Columns, projection, delimiter, cancellationToken, skipNonObjectRows);
    }
}

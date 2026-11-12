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
        cancellationToken.ThrowIfCancellationRequested();
        if (FindMemory(dataSetKey) is { } memory) return memory.Descriptor;
        await _session.RequireReadyAsync(cancellationToken);
        await using var connection = await _session.OpenAsync(cancellationToken);
        return await ReadDescriptorAsync(connection, dataSetKey, cancellationToken);
    }

    // One selected revision's catalogue/headers, with no captured-row reads.
    public async Task<CaptureDataSetDescriptor?> GetForRevisionAsync(long revisionKey,
        CancellationToken cancellationToken = default)
    {
        if (revisionKey <= 0) throw new ArgumentOutOfRangeException(nameof(revisionKey));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var memory in _session.SnapshotMemory.All())
        {
            var captured = memory.Captures.Values.FirstOrDefault(c => c.Descriptor.Summary.RevisionKey == revisionKey);
            if (captured != null) return captured.Descriptor;
            if (memory.DataMetadata.ContainsKey(revisionKey)) return null;
        }
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
        if (FindMemory(descriptor.Summary.DataSetKey) is { } memory)
            return StreamMemoryAsync(memory, query, afterOrdinal, cancellationToken);
        var source = StreamFilteredAsync(descriptor, query, afterOrdinal, cancellationToken);
        return query.Sorts.Length == 0 ? source : CaptureExternalSort.SortAsync(source, query, descriptor.Columns, _limits, cancellationToken);
    }

    private async IAsyncEnumerable<CaptureRow> StreamFilteredAsync(CaptureDataSetDescriptor descriptor,
        FrozenCaptureQuery query, long afterOrdinal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in StreamJsonRowsAsync(descriptor, cancellationToken).ConfigureAwait(false))
            if (row.RowOrdinal > afterOrdinal && query.Matches(row.Value, descriptor.Columns))
                yield return row;
    }

    private async IAsyncEnumerable<CaptureRow> StreamJsonRowsAsync(CaptureDataSetDescriptor descriptor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FindMemory(descriptor.Summary.DataSetKey) is { } memory)
        {
            foreach (var row in CheckedMemoryRows(memory, cancellationToken)) yield return row;
            yield break;
        }
        await using var connection = await _session.OpenAsync(cancellationToken);
        await using var command = Command(connection, null,
            "SELECT CONVERT(varbinary(max), JsonPayload) FROM surf.FullTableData WHERE DataSetKey = @DataSet;",
            Key("@DataSet", descriptor.Summary.DataSetKey));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Captured JSON payload is missing.");
        await using var sqlStream = reader.GetStream(0);
        await using var utf8 = Encoding.CreateTranscodingStream(sqlStream, Encoding.Unicode,
            new UTF8Encoding(false, true), leaveOpen: true);
        var layout = new CaptureLayout(descriptor.Columns);
        long ordinal = 0;
        await foreach (var value in CaptureJsonArray.ReadAsync(utf8, _limits.MaxRowUtf8Bytes, cancellationToken).ConfigureAwait(false))
        {
            var encoded = CaptureRowCodec.Encode(layout, value, _limits);
            if (encoded.EstimatedBytes > _limits.ReadBatchBytes)
                throw new CaptureLimitException("One row exceeds the read memory budget.");
            if (ordinal >= descriptor.Summary.ActualRowCount)
                throw new InvalidDataException("Captured JSON contains more rows than its recorded count.");
            yield return new(ordinal++, value, encoded.EstimatedBytes);
        }
        if (ordinal != descriptor.Summary.ActualRowCount)
            throw new InvalidDataException("Captured JSON count differs from its recorded count.");
    }

    // Sorted cursors use a verified position in a freshly built immutable exact
    // result. Unsorted cursors skip preceding ordinals. No cursor means position zero.
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

using System.Runtime.CompilerServices;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Storage.Relational.Capture;

public sealed partial class RelationalCaptureStore
{
    private CapturedTableMemory? FindMemory(long dataSetKey)
    {
        foreach (var memory in _session.SnapshotMemory.All())
            if (memory.Captures.TryGetValue(dataSetKey, out var captured)) return captured;
        return null;
    }

    private async IAsyncEnumerable<CaptureRow> StreamMemoryAsync(CapturedTableMemory memory,
        FrozenCaptureQuery query, long afterOrdinal, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        IEnumerable<CaptureRow> rows = CheckedMemoryRows(memory, ct)
            .Where(row => row.RowOrdinal > afterOrdinal && query.Matches(row.Value, memory.Descriptor.Columns));
        if (query.Sorts.Length != 0)
        {
            var keyed = new List<(CaptureRow Row, string[] Keys)>();
            foreach (var row in rows) { ct.ThrowIfCancellationRequested(); keyed.Add((row, query.SortKeys(row, memory.Descriptor.Columns))); }
            keyed.Sort((a, b) => query.Compare(a.Keys, a.Row.RowOrdinal, b.Keys, b.Row.RowOrdinal));
            rows = keyed.Select(item => item.Row);
        }
        foreach (var row in rows) { ct.ThrowIfCancellationRequested(); yield return row; }
    }

    private IEnumerable<CaptureRow> CheckedMemoryRows(CapturedTableMemory memory, CancellationToken ct)
    {
        bool customRowLimits = _limits.MaxRowUtf8Bytes < CaptureLimits.MaximumRowUtf8Bytes ||
            _limits.MaxTokenUtf8Bytes < CaptureLimits.MaximumRowUtf8Bytes ||
            _limits.MaxExceptionUtf8BytesPerRow < CaptureLimits.MaximumRowUtf8Bytes ||
            _limits.MaxExceptionsPerRow < CaptureLimits.MaximumExceptionsPerRow;
        var layout = customRowLimits ? new CaptureLayout(memory.Descriptor.Columns) : null;
        foreach (var row in memory.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (row.EstimatedBytes > _limits.ReadBatchBytes) throw new CaptureLimitException("One row exceeds the read memory budget.");
            if (layout != null) _ = CaptureRowCodec.Encode(layout, row.Value, _limits);
            yield return row;
        }
    }
}

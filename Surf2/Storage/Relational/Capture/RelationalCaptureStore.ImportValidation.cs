using System.IO;
using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

public sealed partial class RelationalCaptureStore
{
    /// <summary>
    /// Migration-only fidelity check for one completed dataset before database
    /// publication. Verifies decoded rows against the pinned full source stream;
    /// does not expose an unguarded runtime row reader or change any stored state.
    /// </summary>
    public async Task<long> ValidateImportRowsAsync(CaptureWriteHandle handle,
        IAsyncEnumerable<JsonElement> expectedRows, CancellationToken cancellationToken = default)
    {
        ValidateHandle(handle);
        ArgumentNullException.ThrowIfNull(expectedRows);
        cancellationToken.ThrowIfCancellationRequested();
        CaptureDataSetSummary summary;
        CaptureLayout layout;
        await using (var connection = await _session.OpenAsync(cancellationToken))
        {
            await using (var command = Command(connection, null,
                $"SELECT {SummaryProjection}, ExpectedActualRowCount FROM surf.DataSet WHERE DataSetKey = @DataSet AND LayoutKey = @Layout;",
                Key("@DataSet", handle.DataSetKey), Key("@Layout", handle.LayoutKey)))
            {
                using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("Captured dataset not found for import validation.");
                summary = ReadSummary(reader);
                if (summary.State != "Ready") throw new InvalidOperationException("Complete the captured dataset before verifying import fidelity.");
                if (summary.ActualRowCount < 0 || reader.IsDBNull(8) || reader.GetInt64(8) != summary.ActualRowCount ||
                    summary.DisplayFormatVersion != CaptureDisplay.FormatVersion)
                    throw new InvalidDataException("Captured import metadata is not a validated completed dataset.");
            }
            layout = await ReadLayoutAsync(connection, null, handle.LayoutKey, cancellationToken);
        }
        var descriptor = new CaptureDataSetDescriptor(_session.Epoch, summary, Array.AsReadOnly(layout.Columns));
        await using var source = expectedRows.GetAsyncEnumerator(cancellationToken);
        long count = 0;
        long after = -1;
        while (true)
        {
            var batch = await ReadBatchAsync(descriptor, layout, after, cancellationToken);
            if (batch.Count == 0) break;
            foreach (var stored in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stored.RowOrdinal != count || count >= summary.ActualRowCount)
                    throw new InvalidDataException("Captured import has missing, extra, or non-contiguous stored row ordinals.");
                if (!await source.MoveNextAsync()) throw new InvalidDataException("Captured import contains more rows than the pinned source.");
                // Enforce source token/shape limits independently of the caller.
                _ = CaptureRowCodec.Encode(layout, source.Current, _limits);
                if (!CaptureRowFidelity.Matches(source.Current, stored.Value))
                    throw new InvalidDataException($"Captured import row fidelity differs at ordinal {count}.");
                after = stored.RowOrdinal;
                count = checked(count + 1);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (await source.MoveNextAsync()) throw new InvalidDataException("Captured import contains fewer rows than the pinned source.");
        if (count != summary.ActualRowCount) throw new InvalidDataException("Captured import count does not match its decoded rows.");
        return count;
    }
}

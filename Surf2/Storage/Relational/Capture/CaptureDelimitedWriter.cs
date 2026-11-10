using System.IO;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

internal static class CaptureDelimitedWriter
{
    internal static async Task<long> WriteAsync(IAsyncEnumerable<CaptureRow> rows, Stream destination,
        IReadOnlyList<CaptureColumnDefinition> columns, IReadOnlyList<int> projection, char delimiter,
        CancellationToken cancellationToken, bool skipNonObjectRows = true)
    {
        using var writer = new StreamWriter(destination, new UTF8Encoding(false, true), 16 * 1024, leaveOpen: true);
        await WriteRecordAsync(projection.Select(i => columns[i].SourceName));
        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipNonObjectRows && row.Value.ValueKind != JsonValueKind.Object) continue;
            await WriteRecordAsync(projection.Select(i => CaptureDisplay.Cell(row.Value, columns[i].SourceName)));
            count = checked(count + 1);
        }
        await writer.FlushAsync(cancellationToken);
        return count;

        async Task WriteRecordAsync(IEnumerable<string> values)
        {
            bool first = true;
            foreach (string value in values)
            {
                if (!first) await writer.WriteAsync(delimiter.ToString().AsMemory(), cancellationToken);
                await writer.WriteAsync(CaptureDisplay.EscapeDelimited(value, delimiter).AsMemory(), cancellationToken);
                first = false;
            }
            await writer.WriteAsync(writer.NewLine.AsMemory(), cancellationToken);
        }
    }
}

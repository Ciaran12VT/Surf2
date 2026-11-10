using System.IO;
using System.Runtime.CompilerServices;

namespace Surf2.Services.RelationalGrid;

public static partial class GridOutput
{
    // Keyboard copy follows spreadsheet conventions: selected cells, without headers.
    // The query is owned by this output operation, after committing the active editor.
    public static async Task<GridClipboardResult> CopySelectionAsync(IGridQuerySession query, GridCellRange range,
        IReadOnlyList<int> columns, long? maxClipboardBytes = null, CancellationToken cancellationToken = default)
    {
        if (range.FirstRow < 0 || range.LastRow < range.FirstRow) throw new ArgumentOutOfRangeException(nameof(range));
        long maximum = maxClipboardBytes ?? (query is GridQuerySession owned ? owned.ClipboardLimit : 16 * 1024 * 1024);
        if (maximum is < 2 or > 128 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxClipboardBytes));
        int[] projection = Projection(query, columns, '\t');
        long start = range.AllRows ? 0 : range.FirstRow;
        long? end = range.AllRows || range.LastRow == long.MaxValue ? null : range.LastRow + 1;
        var rows = query is GridQuerySession current ? current.StreamRangeAsync(start, end, cancellationToken)
            : ReadSelectionPagesAsync(query, start, end, cancellationToken);
        using var writer = new ClipboardWriter(maximum);
        long count = 0;
        await foreach (var row in rows.ConfigureAwait(false))
        {
            for (int column = 0; column < projection.Length; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (column != 0) await writer.WriteAsync("\t".AsMemory(), cancellationToken).ConfigureAwait(false);
                string value = row.Cells[projection[column]];
                bool quote = value.IndexOfAny(['\t', '"', '\r', '\n']) >= 0;
                if (quote) await writer.WriteAsync("\"".AsMemory(), cancellationToken).ConfigureAwait(false);
                for (int position = 0; position < value.Length;)
                {
                    int next = value.IndexOf('"', position);
                    int stop = next < 0 ? value.Length : next;
                    while (position < stop)
                    {
                        int take = Math.Min(4096, stop - position);
                        await writer.WriteAsync(value.AsMemory(position, take), cancellationToken).ConfigureAwait(false);
                        position += take;
                    }
                    if (next < 0) break;
                    await writer.WriteAsync("\"\"".AsMemory(), cancellationToken).ConfigureAwait(false);
                    position++;
                }
                if (quote) await writer.WriteAsync("\"".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            count++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(writer.GetText(), count, projection.Length);
    }

    private static async IAsyncEnumerable<GridRow> ReadSelectionPagesAsync(IGridQuerySession query, long start, long? end,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Custom providers expose the same bounded page contract; no collection enumeration.
        long count = await query.Completion.WaitAsync(ct).ConfigureAwait(false);
        long stop = Math.Min(count, end ?? count);
        while (start < stop)
        {
            var page = await query.ReadPageAsync(new(start, (int)Math.Min(128, stop - start), 8 * 1024 * 1024), ct).ConfigureAwait(false);
            if (page.Rows.Count == 0) throw new InvalidDataException("The selected row range is incomplete.");
            foreach (var row in page.Rows) { ct.ThrowIfCancellationRequested(); yield return row; }
            start += page.Rows.Count;
        }
    }
}

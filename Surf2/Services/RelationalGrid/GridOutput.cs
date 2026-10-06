using System.IO;
using System.Text;

namespace Surf2.Services.RelationalGrid;

public static class GridOutput
{
    // The caller commits its active editor before creating the pinned query.
    // visibleColumns are captured in UI DisplayIndex order, never viewport rows.
    public static async Task<long> WriteDelimitedAsync(IGridQuerySession query, Stream destination,
        IReadOnlyList<int>? visibleColumns = null, char delimiter = ',', IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int[] projection = Projection(query, visibleColumns, delimiter);
        using var writer = new StreamWriter(destination, new UTF8Encoding(false, false), 16 * 1024, leaveOpen: true);
        long count = await WriteCoreAsync(query, writer, projection, delimiter, progress, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public static Task<GridExportResult> ExportAsync(IGridQuerySession query, string destination,
        IReadOnlyList<int>? visibleColumns = null, bool replaceExisting = false,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        int[] projection = Projection(query, visibleColumns, ',');
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string final = Path.GetFullPath(destination);
        return Task.Run(() => ExportCoreAsync(query, final, projection, replaceExisting, progress, cancellationToken), cancellationToken);
    }

    private static async Task<GridExportResult> ExportCoreAsync(IGridQuerySession query, string final,
        int[] projection, bool replaceExisting, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (query is GridQuerySession owned) owned.ValidateDestination(final);
        string directory = Path.GetDirectoryName(final)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The selected export directory does not exist.");
        if (!replaceExisting && File.Exists(final)) throw new IOException("The export destination already exists.");
        string pending = Path.Combine(directory, ".surf-" + Guid.NewGuid().ToString("N") + ".grid-pending");
        bool ownsPending = false;
        try
        {
            long count;
            long bytes;
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous))
            {
                ownsPending = true;
                count = await WriteDelimitedAsync(query, file, projection, ',', progress, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                bytes = file.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (query is GridQuerySession current) current.ValidateDestination(final);
            if (replaceExisting && File.Exists(final)) File.Replace(pending, final, null);
            else File.Move(pending, final);
            ownsPending = false;
            return new(final, count, projection.Length, bytes);
        }
        catch (Exception error)
        {
            if (ownsPending)
                try { File.Delete(pending); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { error.Data["GridExportCleanupFailed"] = cleanup; }
            throw;
        }
    }

    public static async Task<GridClipboardResult> CopyAsync(IGridQuerySession query,
        IReadOnlyList<int>? visibleColumns = null, long? maxClipboardBytes = null,
        CancellationToken cancellationToken = default)
    {
        long maximum = maxClipboardBytes ?? (query is GridQuerySession owned ? owned.ClipboardLimit : 16 * 1024 * 1024);
        if (maximum is < 2 or > 128 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxClipboardBytes));
        int[] projection = Projection(query, visibleColumns, '\t');
        using var writer = new ClipboardWriter(maximum);
        long count = await WriteCoreAsync(query, writer, projection, '\t', null, cancellationToken).ConfigureAwait(false);
        return new(writer.GetText(), count, projection.Length);
    }

    private static int[] Projection(IGridQuerySession query, IReadOnlyList<int>? visibleColumns, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (delimiter is not (',' or '\t')) throw new ArgumentOutOfRangeException(nameof(delimiter));
        if (visibleColumns?.Count > query.Descriptor.Columns.Count) throw new ArgumentException("Invalid projection.", nameof(visibleColumns));
        int[] selected = visibleColumns?.ToArray() ?? Enumerable.Range(0, query.Descriptor.Columns.Count).ToArray();
        if (selected.Length == 0 || selected.Any(i => i < 0 || i >= query.Descriptor.Columns.Count) || selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("At least one distinct visible column is required.", nameof(visibleColumns));
        return selected;
    }

    private static async Task<long> WriteCoreAsync(IGridQuerySession query, TextWriter writer, int[] columns,
        char delimiter, IProgress<long>? progress, CancellationToken ct)
    {
        await RecordAsync(columns.Select(i => string.IsNullOrWhiteSpace(query.Descriptor.Columns[i].Header) ? $"Column {i + 1}" : query.Descriptor.Columns[i].Header));
        long count = 0;
        await foreach (var row in query.StreamAsync(ct).ConfigureAwait(false))
        {
            await RecordAsync(columns.Select(i => row.Cells[i]));
            if (++count % 128 == 0) progress?.Report(count);
        }
        progress?.Report(count);
        return count;

        async Task RecordAsync(IEnumerable<string> values)
        {
            bool first = true;
            foreach (string value in values)
            {
                ct.ThrowIfCancellationRequested();
                if (!first) await writer.WriteAsync(delimiter.ToString().AsMemory(), ct).ConfigureAwait(false);
                bool quote = value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0;
                if (quote) await writer.WriteAsync("\"".AsMemory(), ct).ConfigureAwait(false);
                int start = 0;
                while (true)
                {
                    int next = value.IndexOf('"', start);
                    int end = next < 0 ? value.Length : next;
                    // Do not create an escaped whole-cell string or hand an
                    // unbounded cell-sized temporary to the clipboard builder.
                    while (start < end)
                    {
                        int size = Math.Min(4096, end - start);
                        await writer.WriteAsync(value.AsMemory(start, size), ct).ConfigureAwait(false);
                        start += size;
                    }
                    if (next < 0) break;
                    await writer.WriteAsync("\"\"".AsMemory(), ct).ConfigureAwait(false);
                    start = next + 1;
                }
                if (quote) await writer.WriteAsync("\"".AsMemory(), ct).ConfigureAwait(false);
                first = false;
            }
            await writer.WriteAsync(Environment.NewLine.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private sealed class ClipboardWriter(long maximumBytes) : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.Unicode;
        public override Task WriteAsync(ReadOnlyMemory<char> value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (value.Length > maximumBytes / 2 - _text.Length)
                throw new GridLimitException("Full matching clipboard text exceeds the reviewed UTF-16 clipboard limit. Use Export; nothing was truncated.");
            _text.Append(value.Span);
            return Task.CompletedTask;
        }
        internal string GetText() => _text.ToString();
    }
}

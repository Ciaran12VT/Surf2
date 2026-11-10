using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

internal static class CaptureExternalSort
{
    private sealed record Item(CaptureRow Row, string[] Keys);

    internal static async IAsyncEnumerable<CaptureRow> SortAsync(IAsyncEnumerable<CaptureRow> source,
        FrozenCaptureQuery query, IReadOnlyList<CaptureColumnDefinition> columns, CaptureLimits limits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comparer = Comparer<Item>.Create((a, b) => query.Compare(a.Keys, a.Row.RowOrdinal, b.Keys, b.Row.RowOrdinal));
        string parent = Path.GetFullPath(limits.StagingDirectory ?? Path.Combine(Path.GetTempPath(), "Surf2", "capture-query"));
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long diskBytes = 0;
        int fileOrdinal = 0;
        try
        {
            var buffer = new List<Item>();
            long bufferBytes = 0;
            var runs = new List<string>();
            await foreach (var row in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (row.EstimatedBytes > limits.SortRunBytes)
                    throw new CaptureLimitException("One row exceeds the sort-run memory budget.");
                if (buffer.Count > 0 && (buffer.Count >= limits.SortRunRows || bufferBytes + row.EstimatedBytes > limits.SortRunBytes))
                    FlushRun();
                buffer.Add(new(row, query.SortKeys(row, columns)));
                bufferBytes += row.EstimatedBytes;
            }
            if (buffer.Count > 0) FlushRun();
            while (runs.Count > 1)
            {
                var next = new List<string>();
                for (int i = 0; i < runs.Count; i += limits.MergeFanIn)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var inputs = runs.Skip(i).Take(limits.MergeFanIn).ToArray();
                    if (inputs.Length == 1) { next.Add(inputs[0]); continue; }
                    string output = NewFile();
                    Merge(inputs, output);
                    next.Add(output);
                    foreach (string input in inputs) Delete(input);
                }
                runs = next;
            }
            if (runs.Count == 0) yield break;
            using var final = OpenReader(runs[0]);
            while (final.BaseStream.Position < final.BaseStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Read(final);
            }

            void FlushRun()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (runs.Count >= limits.MaxSortRuns) throw new CaptureLimitException("Too many sort runs; increase the reviewed sort budget.");
                buffer.Sort(comparer);
                string path = NewFile();
                using (var writer = CreateWriter(path))
                    foreach (var item in buffer) Write(writer, item.Row, path);
                runs.Add(path);
                buffer.Clear();
                bufferBytes = 0;
            }

            void Merge(string[] inputs, string output)
            {
                // At most MergeFanIn records/readers are retained in this pass.
                var readers = new List<BinaryReader>();
                try
                {
                    var heap = new PriorityQueue<(int Input, Item Item), Item>(comparer);
                    foreach (string input in inputs)
                    {
                        var reader = OpenReader(input);
                        int index = readers.Count;
                        readers.Add(reader);
                        if (reader.BaseStream.Position < reader.BaseStream.Length)
                        {
                            var row = Read(reader);
                            var item = new Item(row, query.SortKeys(row, columns));
                            heap.Enqueue((index, item), item);
                        }
                    }
                    using var writer = CreateWriter(output);
                    while (heap.TryDequeue(out var entry, out _))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Write(writer, entry.Item.Row, output);
                        var reader = readers[entry.Input];
                        if (reader.BaseStream.Position < reader.BaseStream.Length)
                        {
                            var row = Read(reader);
                            var item = new Item(row, query.SortKeys(row, columns));
                            heap.Enqueue((entry.Input, item), item);
                        }
                    }
                }
                finally { foreach (var reader in readers) reader.Dispose(); }
            }

            string NewFile()
            {
                string path = Path.Combine(directory, (++fileOrdinal).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".run");
                return path;
            }

            BinaryWriter CreateWriter(string path)
            {
                // Track only after CreateNew succeeds: a collision must not
                // make cleanup delete a file this operation did not create.
                var writer = OpenWriter(path);
                files.Add(path, 0);
                return writer;
            }

            void Write(BinaryWriter writer, CaptureRow row, string path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] token = Encoding.UTF8.GetBytes(row.Value.GetRawText());
                if (token.Length > CaptureLimits.MaximumRowUtf8Bytes * 6)
                    throw new CaptureLimitException("A reconstructed sort record exceeds the spool-record budget.");
                long size = 20L + token.Length;
                if (diskBytes + size > limits.MaxSpoolBytes)
                    throw new CaptureLimitException("The sort spool exceeds its disk quota (including merge workspace).");
                writer.Write(row.RowOrdinal);
                writer.Write(row.EstimatedBytes);
                writer.Write(token.Length);
                writer.Write(token);
                diskBytes += size;
                files[path] += size;
            }

            void Delete(string path)
            {
                File.Delete(path);
                diskBytes -= files[path];
                files.Remove(path);
            }
        }
        finally
        {
            // Delete only tracked files and this empty operation directory.
            // Never recursively clean a caller-provided staging parent.
            foreach (string path in files.Keys)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(directory, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static BinaryReader OpenReader(string path) => new(new FileStream(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan), Encoding.UTF8);

    private static BinaryWriter OpenWriter(string path) => new(new FileStream(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan), Encoding.UTF8);

    private static CaptureRow Read(BinaryReader reader)
    {
        long ordinal = reader.ReadInt64();
        long estimate = reader.ReadInt64();
        int length = reader.ReadInt32();
        if (ordinal < 0 || estimate is <= 0 or > CaptureLimits.MaximumEstimatedRowBytes ||
            length is < 0 or > CaptureLimits.MaximumRowUtf8Bytes * 6)
            throw new InvalidDataException("Invalid sort record.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Incomplete sort record.");
        using var document = JsonDocument.Parse(bytes);
        return new(ordinal, document.RootElement.Clone(), estimate);
    }
}

using System.IO;
using System.Text;

namespace Surf2.Services.RelationalGrid;

internal sealed record GridSortItem(GridRowReference Reference, string[] Keys)
{
    internal long EstimatedBytes => GridValues.Estimate(Keys) + 64;
}

internal static class GridExternalSort
{
    internal static async Task BuildAsync(IAsyncEnumerable<GridSortItem> source, FrozenGridQuery query,
        GridAppendIndex output, GridOwnedWorkspace workspace, GridLimits limits, CancellationToken ct)
    {
        var comparer = Comparer<GridSortItem>.Create((a, b) => query.Compare(a.Keys, a.Reference.Ordinal, b.Keys, b.Reference.Ordinal));
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new List<GridSortItem>();
        var runs = new List<string>();
        long bytes = 0;
        try
        {
            await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            {
                if (item.EstimatedBytes > limits.SortRunBytes) throw new GridLimitException("One sort key exceeds the sort-run budget.");
                if (buffer.Count > 0 && (buffer.Count >= limits.SortRunRows || bytes + item.EstimatedBytes > limits.SortRunBytes)) await FlushAsync();
                buffer.Add(item);
                bytes += item.EstimatedBytes;
            }
            if (buffer.Count > 0) await FlushAsync();
            while (runs.Count > 1)
            {
                var next = new List<string>();
                for (int i = 0; i < runs.Count; i += limits.MergeFanIn)
                {
                    ct.ThrowIfCancellationRequested();
                    string[] group = runs.Skip(i).Take(limits.MergeFanIn).ToArray();
                    if (group.Length == 1) { next.Add(group[0]); continue; }
                    string merged = await MergeAsync(group);
                    next.Add(merged);
                    foreach (string path in group) { workspace.Delete(path); owned.Remove(path); }
                }
                runs = next;
            }
            if (runs.Count == 0) return;
            using var final = OpenReader(runs[0]);
            long count = 0;
            while (final.BaseStream.Position < final.BaseStream.Length)
            {
                ct.ThrowIfCancellationRequested();
                var item = Read(final, limits, query.Sorts.Length);
                await output.AppendAsync(item.Reference, ct).ConfigureAwait(false);
                if (++count == 1 || count % limits.PageRows == 0) await output.PublishAsync(ct).ConfigureAwait(false);
            }

            async Task FlushAsync()
            {
                ct.ThrowIfCancellationRequested();
                if (runs.Count >= limits.MaxSortRuns) throw new GridLimitException("Too many grid sort runs; increase the reviewed run budget or narrow the query.");
                buffer.Sort(comparer);
                await using var file = workspace.Create(".sort");
                owned.Add(file.Name);
                foreach (var item in buffer) await WriteAsync(file, item, workspace, ct).ConfigureAwait(false);
                runs.Add(file.Name);
                buffer.Clear();
                bytes = 0;
            }

            async Task<string> MergeAsync(string[] inputs)
            {
                var readers = new List<BinaryReader>();
                try
                {
                    var heap = new PriorityQueue<(int Input, GridSortItem Item), GridSortItem>(comparer);
                    foreach (string path in inputs)
                    {
                        var reader = OpenReader(path);
                        int index = readers.Count;
                        readers.Add(reader);
                        if (reader.BaseStream.Position < reader.BaseStream.Length)
                        {
                            var item = Read(reader, limits, query.Sorts.Length);
                            heap.Enqueue((index, item), item);
                        }
                    }
                    await using var outputFile = workspace.Create(".sort");
                    owned.Add(outputFile.Name);
                    while (heap.TryDequeue(out var entry, out _))
                    {
                        ct.ThrowIfCancellationRequested();
                        await WriteAsync(outputFile, entry.Item, workspace, ct).ConfigureAwait(false);
                        var reader = readers[entry.Input];
                        if (reader.BaseStream.Position < reader.BaseStream.Length)
                        {
                            var next = Read(reader, limits, query.Sorts.Length);
                            heap.Enqueue((entry.Input, next), next);
                        }
                    }
                    return outputFile.Name;
                }
                finally { foreach (var reader in readers) reader.Dispose(); }
            }
        }
        finally { foreach (string path in owned) workspace.Delete(path); }
    }

    private static BinaryReader OpenReader(string path) => new(new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 16 * 1024, FileOptions.SequentialScan), Encoding.UTF8);

    private static async Task WriteAsync(FileStream file, GridSortItem item, GridOwnedWorkspace workspace, CancellationToken ct)
    {
        using var memory = new MemoryStream(checked(28 + item.Keys.Sum(s => 4 + s.Length * 2)));
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(item.Reference.Encode());
            writer.Write(item.Keys.Length);
            foreach (string key in item.Keys) GridValues.WriteString(writer, key);
        }
        workspace.Reserve(file.Name, memory.Length);
        await file.WriteAsync(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)), ct).ConfigureAwait(false);
    }

    private static GridSortItem Read(BinaryReader reader, GridLimits limits, int expectedKeys)
    {
        byte[] bytes = reader.ReadBytes(GridRowReference.Size);
        if (bytes.Length != GridRowReference.Size) throw new EndOfStreamException("Incomplete grid sort reference.");
        var reference = GridRowReference.Decode(bytes);
        int count = reader.ReadInt32();
        if (count != expectedKeys || count > limits.MaxColumns) throw new InvalidDataException("Invalid grid sort-key count.");
        var keys = new string[count];
        long estimate = GridValues.RowOverhead + 64 + count * GridValues.CellOverhead;
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = GridValues.ReadString(reader, limits);
            estimate += keys[i].Length * 4L;
            if (estimate > limits.SortRunBytes) throw new GridLimitException("Stored grid sort keys exceed their memory budget.");
        }
        return new(reference, keys);
    }
}

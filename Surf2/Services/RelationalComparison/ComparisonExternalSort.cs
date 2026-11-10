using System.IO;
using System.Runtime.CompilerServices;

namespace Surf2.Services.RelationalComparison;

internal sealed record SortEntry<T>(string Key, long Ordinal, T Value);

internal sealed class ComparisonExternalSort<T>(ComparisonScratch scratch, SpoolCodec<T> valueCodec, IComparer<string>? keyComparer = null)
{
    private readonly SpoolCodec<SortEntry<T>> _codec = new((w, x) =>
    {
        SpoolBinary.WriteString(w, x.Key); w.Write(x.Ordinal); valueCodec.Write(w, x.Value);
    }, r => new(SpoolBinary.ReadString(r), r.ReadInt64(), valueCodec.Read(r)));

    public async Task<string> BuildAsync(IAsyncEnumerable<SortEntry<T>> values, CancellationToken ct)
    {
        var buffer = new List<SortEntry<T>>();
        var runs = new List<string>();
        long bytes = 0;
        await foreach (var value in values.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            // Include both decoded objects and encoded frames in the run budget.
            long size = checked(256L + SpoolBinary.Encode(value, _codec, scratch.Limits.MaximumRecordBytes).Length * 3L);
            if (size > scratch.Limits.SortBufferBytes)
                throw new ComparisonLimitException("One comparison row exceeds the sort-run memory budget.");
            if (buffer.Count != 0 && (size > scratch.Limits.SortBufferBytes - bytes || buffer.Count == 1024)) Flush();
            buffer.Add(value); bytes += size;
        }
        if (buffer.Count != 0 || runs.Count == 0) Flush();
        while (runs.Count > 1)
        {
            var next = new List<string>();
            for (int i = 0; i < runs.Count; i += scratch.Limits.MergeFanIn)
            {
                ct.ThrowIfCancellationRequested();
                string[] group = runs.Skip(i).Take(scratch.Limits.MergeFanIn).ToArray();
                string output = scratch.NewFile();
                using (var destination = File.Open(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var inputs = group.Select(p => File.OpenRead(p)).ToArray();
                    try
                    {
                        var queue = new PriorityQueue<(int Stream, SortEntry<T> Entry), SortEntry<T>>(new EntryComparer(keyComparer ?? StringComparer.OrdinalIgnoreCase));
                        for (int j = 0; j < inputs.Length; j++)
                            if (SpoolBinary.TryRead(inputs[j], _codec, scratch.Limits.MaximumRecordBytes, out var row)) queue.Enqueue((j, row), row);
                        while (queue.TryDequeue(out var item, out _))
                        {
                            ct.ThrowIfCancellationRequested();
                            SpoolBinary.Append(destination, output, item.Entry, _codec, scratch);
                            if (SpoolBinary.TryRead(inputs[item.Stream], _codec, scratch.Limits.MaximumRecordBytes, out var row)) queue.Enqueue((item.Stream, row), row);
                        }
                    }
                    finally { foreach (var input in inputs) input.Dispose(); }
                }
                foreach (string path in group) scratch.Delete(path);
                next.Add(output);
            }
            runs = next;
        }
        return runs[0];

        void Flush()
        {
            ct.ThrowIfCancellationRequested();
            if (runs.Count == scratch.Limits.MaximumRuns) throw new ComparisonLimitException("Comparison exceeds its external-sort run limit.");
            buffer.Sort(new EntryComparer(keyComparer ?? StringComparer.OrdinalIgnoreCase));
            string path = scratch.NewFile();
            using (var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                foreach (var row in buffer) SpoolBinary.Append(stream, path, row, _codec, scratch);
            runs.Add(path); buffer.Clear(); bytes = 0;
        }
    }
    public IEnumerable<SortEntry<T>> Read(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        while (SpoolBinary.TryRead(stream, _codec, scratch.Limits.MaximumRecordBytes, out var item))
        { ct.ThrowIfCancellationRequested(); yield return item; }
    }
    private sealed class EntryComparer(IComparer<string> comparer) : IComparer<SortEntry<T>>
    {
        public int Compare(SortEntry<T>? x, SortEntry<T>? y)
        {
            int key = comparer.Compare(x!.Key, y!.Key);
            return key != 0 ? key : x.Ordinal.CompareTo(y.Ordinal);
        }
    }
}

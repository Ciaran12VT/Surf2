using System.IO;
using System.Runtime.CompilerServices;

namespace Surf2.Services.RelationalComparison;

public sealed record ComparisonCollectionEntry(string RelativePath, bool IsCollection,
    string Digest, RelationalComparisonTarget Target);

public static class ComparisonCollectionEngine
{
    private static readonly SpoolCodec<ComparisonCollectionEntry> EntryCodec = new((w, x) =>
    {
        SpoolBinary.WriteString(w, x.RelativePath); w.Write(x.IsCollection); SpoolBinary.WriteString(w, x.Digest);
        ComparisonCodecs.WriteTarget(w, x.Target);
    }, r => new(SpoolBinary.ReadString(r), r.ReadBoolean(), SpoolBinary.ReadString(r), ComparisonCodecs.ReadTarget(r)!));

    public static async Task<ComparisonResultStore> BuildAsync(IAsyncEnumerable<ComparisonCollectionEntry> left,
        IAsyncEnumerable<ComparisonCollectionEntry> right, ComparisonLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new(); var scratch = new ComparisonScratch(limits);
        try
        {
            var sorter = new ComparisonExternalSort<ComparisonCollectionEntry>(scratch, EntryCodec, HierarchyComparer.Instance);
            string leftPath = await sorter.BuildAsync(Entries(left), ct).ConfigureAwait(false);
            string rightPath = await sorter.BuildAsync(Entries(right), ct).ConfigureAwait(false);
            var resultSorter = new ComparisonExternalSort<ComparisonResultRow>(scratch, ComparisonCodecs.Result);
            string sortedResult = await resultSorter.BuildAsync(Match(), ct).ConfigureAwait(false);
            scratch.Delete(leftPath); scratch.Delete(rightPath);
            string resultPath = scratch.NewFile(); long count = 0;
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var output = File.Open(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                foreach (var row in resultSorter.Read(sortedResult, ct))
                {
                    SpoolBinary.Append(output, resultPath, row.Value, ComparisonCodecs.Result, scratch);
                    count++; counts[row.Value.Status] = counts.GetValueOrDefault(row.Value.Status) + 1;
                }
            scratch.Delete(sortedResult);
            return ComparisonResultStore.Complete(scratch, resultPath, count, counts);

            async IAsyncEnumerable<SortEntry<ComparisonResultRow>> Match([EnumeratorCancellation] CancellationToken token = default)
            {
                using var a = sorter.Read(leftPath, ct).GetEnumerator();
                using var b = sorter.Read(rightPath, ct).GetEnumerator();
                bool hasA = a.MoveNext(), hasB = b.MoveNext();
                long ordinal = 0;
                var folders = new Stack<ComparisonResultRow>();
                while (hasA || hasB)
                {
                    token.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
                    int order = !hasA ? 1 : !hasB ? -1 : HierarchyComparer.Instance.Compare(a.Current.Key, b.Current.Key);
                    var l = hasA && order <= 0 ? a.Current.Value : null;
                    var r = hasB && order >= 0 ? b.Current.Value : null;
                    string key = l?.RelativePath ?? r!.RelativePath;
                    while (folders.Count != 0 && !key.StartsWith(folders.Peek().Key + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        var folder = folders.Pop(); Propagate(folder);
                        yield return new(folder.Key, ordinal++, folder);
                    }
                    string status = l == null ? "Missing left" : r == null ? "Missing right" :
                        l.IsCollection != r.IsCollection || l.Digest != r.Digest ? "Different" : "Identical";
                    var row = new ComparisonResultRow(key, status, "", l?.Target.Resource.DisplayName ?? "", r?.Target.Resource.DisplayName ?? "",
                        l?.IsCollection == true || r?.IsCollection == true, l?.Target, r?.Target);
                    if (row.IsCollection)
                    {
                        if (folders.Count == 128) throw new ComparisonLimitException("Collection comparison exceeds its hierarchy depth limit.");
                        folders.Push(row);
                    }
                    else { Propagate(row); yield return new(key, ordinal++, row); }
                    if (order <= 0) { do { hasA = a.MoveNext(); } while (hasA && a.Current.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); }
                    if (order >= 0) { do { hasB = b.MoveNext(); } while (hasB && b.Current.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); }
                }
                while (folders.Count != 0)
                {
                    var folder = folders.Pop(); Propagate(folder); yield return new(folder.Key, ordinal++, folder);
                }
                await Task.CompletedTask;
                void Propagate(ComparisonResultRow row)
                {
                    if (row.Status == "Identical" || folders.Count == 0 || folders.Peek().Status != "Identical") return;
                    var parent = folders.Pop(); folders.Push(parent with { Status = "Different" });
                }
            }
        }
        catch (Exception failure)
        {
            try { scratch.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Collection comparison failed and scratch cleanup also failed.", failure, cleanup); }
            throw;
        }

        async IAsyncEnumerable<SortEntry<ComparisonCollectionEntry>> Entries(IAsyncEnumerable<ComparisonCollectionEntry> input,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            long ordinal = 0;
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            { token.ThrowIfCancellationRequested(); yield return new(item.RelativePath, ordinal++, item); }
        }
    }

    // Keep each subtree contiguous while propagating status. Ordinary lexical
    // order would place "a.txt" between folder "a" and "a/file.sql".
    private sealed class HierarchyComparer : IComparer<string>
    {
        internal static readonly HierarchyComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            ReadOnlySpan<char> a = x.AsSpan(), b = y.AsSpan();
            while (true)
            {
                int ai = a.IndexOf('/'), bi = b.IndexOf('/');
                int c = (ai < 0 ? a : a[..ai]).CompareTo(bi < 0 ? b : b[..bi], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                if (ai < 0 || bi < 0) return ai < 0 ? bi < 0 ? 0 : -1 : 1;
                a = a[(ai + 1)..]; b = b[(bi + 1)..];
            }
        }
    }
}

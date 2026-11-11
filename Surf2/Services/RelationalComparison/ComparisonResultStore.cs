using System.IO;
using System.Text;

namespace Surf2.Services.RelationalComparison;

/// <summary>One completed comparison, with bounded memory results or indexed disk paging.</summary>
public sealed class ComparisonResultStore : IAsyncDisposable
{
    private readonly ComparisonScratch? _scratch;
    private readonly string? _path;
    private readonly ComparisonResultRow[]? _memory;
    private readonly ComparisonLimits _limits;
    private PageFilter? _pageFilter;
    private string? _pageIndex;
    private int[]? _memoryIndex;
    private long _matchingCount;
    internal long PageScanCount { get; private set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed, _cleanupCompleted;
    internal ComparisonResultStore(ComparisonScratch scratch, string path, long count,
        IReadOnlyDictionary<string, long> counts, ComparisonTableLayout? tableLayout)
    { _scratch = scratch; _path = path; _limits = scratch.Limits; Count = count; StatusCounts = counts; TableLayout = tableLayout; }
    private ComparisonResultStore(ComparisonResultRow[] rows, ComparisonLimits limits, IReadOnlyDictionary<string, long> counts)
    { _memory = rows; _limits = limits; Count = rows.Length; StatusCounts = counts; }
    public long Count { get; }
    public IReadOnlyDictionary<string, long> StatusCounts { get; }
    public ComparisonTableLayout? TableLayout { get; }
    public int PageSize => _limits.PageSize;
    public Task<ComparisonResultPage> ReadPageAsync(long offset = 0, string search = "",
        bool differencesOnly = false, IReadOnlySet<string>? excluded = null, CancellationToken ct = default,
        IReadOnlySet<string>? collapsed = null)
    {
        if (offset < 0 || search.Length > 65536) throw new ArgumentOutOfRangeException(nameof(offset));
        var filter = new PageFilter(search, differencesOnly, Ordered(excluded), Ordered(collapsed));
        return ExecuteAsync(() =>
        {
            EnsurePageIndex(filter, ct);
            var rows = new List<ComparisonResultRow>(); long pageBytes = 0;
            if (_memory != null)
            {
                for (long i = offset; i < _matchingCount && rows.Count < PageSize; i++)
                {
                    var row = _memory[_memoryIndex![(int)i]];
                    if (!TryAdd(row, SpoolBinary.Encode(row, ComparisonCodecs.Result, _limits.MaximumRecordBytes).Length)) break;
                }
            }
            else if (offset < _matchingCount)
            {
                using var index = File.OpenRead(_pageIndex!);
                using var reader = new BinaryReader(index);
                using var data = File.OpenRead(_path!);
                index.Position = checked(offset * sizeof(long));
                while (offset + rows.Count < _matchingCount && rows.Count < PageSize)
                {
                    long position = reader.ReadInt64();
                    data.Position = position;
                    if (!SpoolBinary.TryRead(data, ComparisonCodecs.Result, _limits.MaximumRecordBytes, out var row))
                        throw new InvalidDataException("Invalid comparison page index.");
                    if (!TryAdd(row, data.Position - position)) break;
                }
            }
            return new ComparisonResultPage(rows, offset, _matchingCount, _matchingCount > offset + rows.Count);
            bool TryAdd(ComparisonResultRow row, long encodedBytes)
            {
                ct.ThrowIfCancellationRequested();
                long size = encodedBytes * 3L + 256 + (row.TableCells?.Count ?? 0) * 24L;
                if (size > _limits.MaximumPageBytes - pageBytes)
                {
                    if (rows.Count != 0) return false;
                    throw new ComparisonLimitException("A comparison row exceeds the display memory budget. Use Export or narrower search.");
                }
                rows.Add(row);
                pageBytes += size;
                return true;
            }
        }, ct);
    }
    private static string[] Ordered(IReadOnlySet<string>? values) => values?.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    private sealed record PageFilter(string Search, bool DifferencesOnly, string[] Excluded, string[] Collapsed)
    {
        public bool SameAs(PageFilter other) => DifferencesOnly == other.DifferencesOnly &&
            StringComparer.OrdinalIgnoreCase.Equals(Search, other.Search) &&
            Excluded.SequenceEqual(other.Excluded, StringComparer.OrdinalIgnoreCase) && Collapsed.SequenceEqual(other.Collapsed, StringComparer.OrdinalIgnoreCase);
        public bool Includes(ComparisonResultRow row) => Matches(row, Search, DifferencesOnly, Excluded) &&
            !Collapsed.Any(folder => row.Key.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
    }
    private void EnsurePageIndex(PageFilter filter, CancellationToken ct)
    {
        if (_pageFilter?.SameAs(filter) == true) return;
        _pageFilter = null; _memoryIndex = null;
        if (_pageIndex != null) { _scratch!.Delete(_pageIndex); _pageIndex = null; }
        long matching = 0;
        if (_memory != null)
        {
            var index = new List<int>();
            for (int i = 0; i < _memory.Length; i++)
            { ct.ThrowIfCancellationRequested(); PageScanCount++; if (filter.Includes(_memory[i])) index.Add(i); }
            _memoryIndex = index.ToArray(); matching = index.Count;
        }
        else
        {
            string path = _scratch!.NewFile();
            try
            {
                using (var input = File.OpenRead(_path!))
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(output))
                {
                    while (input.Position < input.Length)
                    {
                        ct.ThrowIfCancellationRequested(); long position = input.Position;
                        if (!SpoolBinary.TryRead(input, ComparisonCodecs.Result, _limits.MaximumRecordBytes, out var row)) break;
                        PageScanCount++;
                        if (!filter.Includes(row)) continue;
                        _scratch.Reserve(path, sizeof(long)); writer.Write(position); matching++;
                    }
                }
                _pageIndex = path;
            }
            catch { _scratch.Delete(path); throw; }
        }
        _matchingCount = matching; _pageFilter = filter;
    }
    public Task<ComparisonDocumentChoices> ReadDocumentChoicesAsync(string search = "", bool differencesOnly = false,
        IReadOnlySet<string>? excluded = null, CancellationToken ct = default)
    {
        string[] exclusions = excluded?.ToArray() ?? [];
        return ExecuteAsync(() =>
        {
            var left = new List<ComparisonDocumentChoice>(); var right = new List<ComparisonDocumentChoice>();
            long bytes = 0; int count = 0; bool complete = true;
            foreach (var row in Read(ct))
            {
                if (row.IsCollection || !Matches(row, search, differencesOnly, exclusions)) continue;
                foreach (var side in new[] { (Target: row.Left, Choices: left), (Target: row.Right, Choices: right) })
                {
                    if (side.Target == null) continue;
                    long size = SpoolBinary.Encode(side.Target, TargetCodec, _limits.MaximumRecordBytes).Length * 3L + 512 + row.Key.Length * 2L;
                    if (count == _limits.MaximumCandidates || size > _limits.MaximumCandidateBytes - bytes)
                    { complete = false; break; }
                    var target = side.Target;
                    side.Choices.Add(new(new(row.Key, target.Resource.DisplayName, target.Resource.Kind, false, "", target.Resource.SyntaxPath, target.Resource), target));
                    bytes += size; count++;
                }
                if (!complete) break;
            }
            return new ComparisonDocumentChoices(left, right, complete);
        }, ct);
    }
    private static readonly SpoolCodec<RelationalComparisonTarget> TargetCodec =
        new(ComparisonCodecs.WriteTarget, r => ComparisonCodecs.ReadTarget(r)!);
    public async Task ExportAsync(string destination, string search = "", bool differencesOnly = false,
        IReadOnlySet<string>? excluded = null, CancellationToken ct = default)
    {
        string target = Path.GetFullPath(destination);
        string pending = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".pending");
        string[] exclusions = excluded?.ToArray() ?? [];
        await ExecuteAsync(() =>
        {
            bool created = false;
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (var writer = new StreamWriter(file, new UTF8Encoding(true), 16384, true))
                    {
                        writer.WriteLine("Key,Status,Changed Columns,Left,Right");
                        foreach (var row in Read(ct))
                        {
                            if (!Matches(row, search, differencesOnly, exclusions)) continue;
                            writer.WriteLine(string.Join(",", new[] { row.Key, row.Status, row.ChangedColumns, row.LeftPreview, row.RightPreview }.Select(Csv)));
                        }
                        writer.Flush();
                    }
                    file.Flush(true);
                }
                ct.ThrowIfCancellationRequested();
                if (File.Exists(target)) File.Replace(pending, target, null); else File.Move(pending, target);
                created = false; return true;
            }
            finally { if (created) File.Delete(pending); }
        }, ct).ConfigureAwait(false);
    }
    private IEnumerable<ComparisonResultRow> Read(CancellationToken ct)
    {
        if (_memory != null)
        { foreach (var row in _memory) { ct.ThrowIfCancellationRequested(); yield return row; } yield break; }
        using var stream = File.OpenRead(_path!);
        while (SpoolBinary.TryRead(stream, ComparisonCodecs.Result, _limits.MaximumRecordBytes, out var row))
        { ct.ThrowIfCancellationRequested(); yield return row; }
    }
    private static bool Matches(ComparisonResultRow row, string search, bool differencesOnly, string[] excluded) =>
        (!differencesOnly || row.Status != "Identical") &&
        (search.Length == 0 || row.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.LeftPreview.Contains(search, StringComparison.OrdinalIgnoreCase) || row.RightPreview.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.TableCells?.Any(cell => cell.Left.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                cell.Right.Contains(search, StringComparison.OrdinalIgnoreCase)) == true) &&
        !excluded.Any(x => row.Key.Equals(x, StringComparison.OrdinalIgnoreCase) || row.Key.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase));
    private static string Csv(string x) => x.IndexOfAny([',', '"', '\r', '\n']) < 0 ? x : "\"" + x.Replace("\"", "\"\"") + "\"";
    private async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await Task.Run(action, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (!_cleanupCompleted)
            {
                if (_scratch != null) await Task.Run(_scratch.Dispose).ConfigureAwait(false);
                _memoryIndex = null;
                _cleanupCompleted = true;
            }
        }
        finally { _gate.Release(); }
    }
    internal static ComparisonResultStore Complete(ComparisonScratch scratch, string path,
        long count, Dictionary<string, long> counts, ComparisonTableLayout? tableLayout = null) => new(scratch, path, count,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(counts), tableLayout);
    internal static ComparisonResultStore FromMemory(ComparisonResultRow[] rows, ComparisonLimits limits) => new(rows, limits,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(rows.GroupBy(row => row.Status, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal)));
}

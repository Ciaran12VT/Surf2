using System.IO;
using System.Text;

namespace Surf2.Services.RelationalComparison;

/// <summary>One completed comparison, paged from disk. Export streams all matching rows, not the displayed page.</summary>
public sealed class ComparisonResultStore : IAsyncDisposable
{
    private readonly ComparisonScratch _scratch;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed, _cleanupCompleted;
    internal ComparisonResultStore(ComparisonScratch scratch, string path, long count,
        IReadOnlyDictionary<string, long> counts)
    { _scratch = scratch; _path = path; Count = count; StatusCounts = counts; }
    public long Count { get; }
    public IReadOnlyDictionary<string, long> StatusCounts { get; }
    public int PageSize => _scratch.Limits.PageSize;
    public Task<ComparisonResultPage> ReadPageAsync(long offset = 0, string search = "",
        bool differencesOnly = false, IReadOnlySet<string>? excluded = null, CancellationToken ct = default,
        IReadOnlySet<string>? collapsed = null)
    {
        if (offset < 0 || search.Length > 65536) throw new ArgumentOutOfRangeException(nameof(offset));
        string[] exclusions = excluded?.ToArray() ?? [];
        string[] closedFolders = collapsed?.ToArray() ?? [];
        return ExecuteAsync(() =>
        {
            var rows = new List<ComparisonResultRow>(); long matching = 0, pageBytes = 0;
            foreach (var row in Read(ct))
            {
                if (!Matches(row, search, differencesOnly, exclusions)) continue;
                if (closedFolders.Any(x => row.Key.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase))) continue;
                if (matching >= offset && rows.Count < PageSize)
                {
                    long size = SpoolBinary.Encode(row, ComparisonCodecs.Result, _scratch.Limits.MaximumRecordBytes).Length * 3L + 256;
                    if (size > _scratch.Limits.MaximumPageBytes - pageBytes)
                        throw new ComparisonLimitException("This comparison page exceeds its decoded byte budget. Use Export or narrower search.");
                    rows.Add(row); pageBytes += size;
                }
                matching++;
            }
            return new ComparisonResultPage(rows, offset, matching, matching > offset + rows.Count);
        }, ct);
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
                    long size = SpoolBinary.Encode(side.Target, TargetCodec, _scratch.Limits.MaximumRecordBytes).Length * 3L + 512 + row.Key.Length * 2L;
                    if (count == _scratch.Limits.MaximumCandidates || size > _scratch.Limits.MaximumCandidateBytes - bytes)
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
        using var stream = File.OpenRead(_path);
        while (SpoolBinary.TryRead(stream, ComparisonCodecs.Result, _scratch.Limits.MaximumRecordBytes, out var row))
        { ct.ThrowIfCancellationRequested(); yield return row; }
    }
    private static bool Matches(ComparisonResultRow row, string search, bool differencesOnly, string[] excluded) =>
        (!differencesOnly || row.Status != "Identical") &&
        (search.Length == 0 || row.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.LeftPreview.Contains(search, StringComparison.OrdinalIgnoreCase) || row.RightPreview.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
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
                await Task.Run(_scratch.Dispose).ConfigureAwait(false);
                _cleanupCompleted = true;
            }
        }
        finally { _gate.Release(); }
    }
    internal static ComparisonResultStore Complete(ComparisonScratch scratch, string path,
        long count, Dictionary<string, long> counts) => new(scratch, path, count,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(counts));
}

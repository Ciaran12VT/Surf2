using System.IO;

namespace Surf2.Services.RelationalGrid;

public abstract class GridSourceBase : IDataGridSource
{
    private readonly object _gate = new();
    private readonly Dictionary<(long Row, int Column), string> _edits = [];
    private readonly HashSet<GridQuerySession> _queries = [];
    private long _overlayGeneration;
    private long _overlayBytes;
    private long _queryGeneration;
    private int _invalidated;
    private Task? _disposal;
    private Task _build = Task.CompletedTask;
    internal CancellationTokenSource Lifetime { get; } = new();
    internal GridOwnedWorkspace Workspace { get; }
    internal GridDiskStore Store { get; }
    internal GridLimits Limits { get; }
    internal long SkipRecords { get; }
    internal Func<bool, CancellationToken, Task> ValidateSource { get; }
    public GridDescriptor Descriptor { get; }
    public Task<long> DisplayRowCount { get; }
    public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;
    public long OverlayGeneration { get { lock (_gate) return _overlayGeneration; } }
    public long OverlayBytes { get { lock (_gate) return _overlayBytes; } }

    internal GridSourceBase(GridDescriptor descriptor, GridLimits limits, GridOwnedWorkspace workspace,
        GridDiskStore store, long skipRecords = 0, Func<bool, CancellationToken, Task>? validateSource = null)
    {
        if (descriptor.Columns.Count > limits.MaxColumns || descriptor.Columns.Where((c, i) => c == null || c.Ordinal != i ||
            c.Header == null || c.SourceName == null || c.Header.Length > limits.MaxCellCharacters || c.SourceName.Length > limits.MaxCellCharacters).Any() ||
            128 + descriptor.Columns.Count * 64L + descriptor.Columns.Sum(c => (c.Header.Length + (long)c.SourceName.Length) * 4L) > limits.MaxHeaderBytes)
            throw new GridLimitException("Grid headers exceed the pinned metadata budget or have invalid column identities.");
        Descriptor = descriptor with { Columns = Array.AsReadOnly(descriptor.Columns.ToArray()) };
        Limits = limits;
        Workspace = workspace;
        Store = store;
        SkipRecords = skipRecords;
        DisplayRowCount = CountDisplayRowsAsync(store.Index.Completion, skipRecords);
        _ = DisplayRowCount.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ValidateSource = validateSource ?? ((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    }

    private static async Task<long> CountDisplayRowsAsync(Task<long> count, long skipRecords) =>
        checked(await count.ConfigureAwait(false) - skipRecords);

    internal void StartBuild(Func<CancellationToken, Task> build)
    {
        _build = Task.Run(async () =>
        {
            try { await build(Lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) { Store.Index.Fail(e); }
        });
    }

    public void SetCell(GridRow row, int columnOrdinal, string? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        value ??= string.Empty;
        lock (_gate)
        {
            ThrowIfClosed();
            if (row.SourceId != Descriptor.SourceId || row.RowOrdinal < 0 || row.Cells.Count != Descriptor.Columns.Count ||
                columnOrdinal < 0 || columnOrdinal >= Descriptor.Columns.Count ||
                row.SourceCellLengths == null || row.SourceCellLengths.Count != row.Cells.Count ||
                (Descriptor.SourceRowCount is long count && row.RowOrdinal >= count))
                throw new ArgumentException("The edited row/column does not belong to this pinned source.", nameof(row));
            // Use immutable source lengths, not a potentially older query's
            // effective values (particularly after ClearEdits). Keep no base
            // strings or row-sized payload in the sparse overlay.
            long characters = 0;
            for (int i = 0; i < row.Cells.Count; i++)
            {
                int length = row.SourceCellLengths[i];
                if (_edits.TryGetValue((row.RowOrdinal, i), out string? edited)) length = edited.Length;
                if (i == columnOrdinal) length = value.Length;
                if (length < 0 || length > Limits.MaxCellCharacters) throw new GridLimitException("Edited cell exceeds its character budget.");
                characters += length;
            }
            if (GridValues.RowOverhead + row.Cells.Count * GridValues.CellOverhead + characters * 4 > Limits.MaxRowBytes)
                throw new GridLimitException("The effective edited row exceeds its memory budget; previous edits were retained.");
            var key = (row.RowOrdinal, columnOrdinal);
            bool existed = _edits.TryGetValue(key, out string? previous);
            long bytes = checked(_overlayBytes - (existed ? EditBytes(previous!) : 0) + EditBytes(value));
            if ((!existed && _edits.Count >= Limits.MaxEditCells) || bytes > Limits.MaxEditBytes)
                throw new GridLimitException("Session edits exceed the separate overlay budget. Edits were not evicted; export or close explicitly.");
            if (existed && string.Equals(previous, value, StringComparison.Ordinal)) return;
            _edits[key] = value;
            _overlayBytes = bytes;
            _overlayGeneration = checked(_overlayGeneration + 1);
        }
    }

    public void ClearEdits()
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (_edits.Count == 0) return;
            _edits.Clear();
            _overlayBytes = 0;
            _overlayGeneration = checked(_overlayGeneration + 1);
        }
    }

    public Task<IGridQuerySession> CreateQueryAsync(GridQuery? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfClosed();
            if (_queries.Count >= Limits.MaxQueries) throw new GridLimitException("Too many pinned grid queries; dispose obsolete query sessions.");
            var frozen = new FrozenGridQuery(query, Descriptor, _overlayGeneration);
            var session = new GridQuerySession(this, checked(++_queryGeneration), _overlayGeneration, frozen,
                new(_edits), cancellationToken);
            _queries.Add(session);
            return Task.FromResult<IGridQuerySession>(session);
        }
    }

    internal static void Apply(Dictionary<(long Row, int Column), string> edits, long ordinal, string[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
            if (edits.TryGetValue((ordinal, i), out string? value)) cells[i] = value;
    }

    internal void Release(GridQuerySession query) { lock (_gate) _queries.Remove(query); }
    internal virtual void ValidateDestination(string path) { }
    private static long EditBytes(string value) => 96L + value.Length * 4L;
    internal void Invalidate()
    {
        lock (_gate)
        {
            if (_disposal != null || IsInvalidated) return;
            Volatile.Write(ref _invalidated, 1);
            Lifetime.Cancel();
        }
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposal != null, this);
        if (IsInvalidated) throw new GridSourceChangedException("Grid source fingerprint changed; reopen before requesting another view.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= Task.Run(CloseAsync));
    }

    private async Task CloseAsync()
    {
        Lifetime.Cancel();
        GridQuerySession[] queries;
        lock (_gate) queries = _queries.ToArray();
        List<Exception> failures = [];
        foreach (var query in queries)
            try { await query.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failures.Add(e); }
        await _build.ConfigureAwait(false);
        try { await Store.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failures.Add(e); }
        try { Workspace.Dispose(); } catch (Exception e) { failures.Add(e); }
        lock (_gate) { _edits.Clear(); _overlayBytes = 0; }
        Lifetime.Dispose();
        if (failures.Count > 0) throw new AggregateException("Grid logical-close cleanup failed.", failures);
    }
}

using System.IO;
using System.Runtime.CompilerServices;

namespace Surf2.Services.RelationalGrid;

internal sealed class GridQuerySession : IGridQuerySession
{
    private readonly object _gate = new();
    private readonly GridSourceBase _source;
    private readonly FrozenGridQuery _query;
    private readonly Dictionary<(long Row, int Column), string> _edits;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly GridAppendIndex _index;
    private readonly SemaphoreSlim _readSlots;
    private readonly Dictionary<GridPageRequest, PageReadWork> _inFlight = [];
    private readonly HashSet<PageReadWork> _reads = [];
    private readonly HashSet<Task> _validations = [];
    private readonly Dictionary<GridPageRequest, LinkedListNode<GridPage>> _cache = [];
    private readonly LinkedList<GridPage> _lru = [];
    private readonly Task _build;
    private long _cacheBytes;
    private int _streams;
    private Task? _disposal;
    public GridDescriptor Descriptor => _source.Descriptor;
    public long Generation { get; }
    public long OverlayGeneration { get; }
    public string Fingerprint => _query.Fingerprint;
    public GridCount Count => _index.Count;
    public Task<long> Completion => _index.Completion;
    internal void ValidateDestination(string path) => _source.ValidateDestination(path);
    internal long ClipboardLimit => _source.Limits.MaxClipboardBytes;
    public GridCacheStatistics CacheStatistics { get { lock (_gate) return new(_cache.Count, _cacheBytes, _reads.Count); } }

    private sealed class PageReadWork(CancellationToken token)
    {
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal Task<GridPage> Task { get; set; } = null!;
        internal int Consumers;
        internal bool Finished;
    }

    internal GridQuerySession(GridSourceBase source, long generation, long overlayGeneration,
        FrozenGridQuery query, Dictionary<(long Row, int Column), string> edits, CancellationToken ct)
    {
        _source = source;
        _query = query;
        _edits = edits;
        Generation = generation;
        OverlayGeneration = overlayGeneration;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(source.Lifetime.Token, ct);
        _token = _lifetime.Token;
        _readSlots = new(source.Limits.MaxConcurrentPageReads);
        try { _index = new(source.Workspace); }
        catch
        {
            _readSlots.Dispose();
            _lifetime.Dispose();
            throw;
        }
        _build = Task.Run(BuildAsync);
    }

    private async Task BuildAsync()
    {
        var ct = _token;
        try
        {
            await ValidateSourceAsync(false, ct).ConfigureAwait(false);
            var candidates = CandidatesAsync(ct);
            if (_query.Sorts.Length == 0)
            {
                long count = 0;
                await foreach (var item in candidates.ConfigureAwait(false))
                {
                    await _index.AppendAsync(item.Reference, ct).ConfigureAwait(false);
                    if (++count == 1 || count % _source.Limits.PageRows == 0) await _index.PublishAsync(ct).ConfigureAwait(false);
                }
            }
            else await GridExternalSort.BuildAsync(candidates, _query, _index, _source.Workspace, _source.Limits, ct).ConfigureAwait(false);
            await ValidateSourceAsync(false, ct).ConfigureAwait(false);
            await _index.FinishAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) { _index.Fail(e); }
    }

    private async IAsyncEnumerable<GridSortItem> CandidatesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var rows = GridAppendIndex.OpenReader(_source.Store.RowsPath);
        long previous = -1;
        await foreach (var sourceRef in _source.Store.Index.StreamAsync(_source.SkipRecords, ct).ConfigureAwait(false))
        {
            var row = sourceRef with { Ordinal = sourceRef.Ordinal - _source.SkipRecords };
            if (row.Ordinal <= previous) throw new InvalidDataException("Source row ordinals must be unique and increasing.");
            previous = row.Ordinal;
            string[] cells = await _source.Store.ReadAsync(rows, row, Descriptor.Columns.Count, ct).ConfigureAwait(false);
            GridSourceBase.Apply(_edits, row.Ordinal, cells);
            GridValues.Validate(cells, _source.Limits);
            if (_query.Matches(cells)) yield return new(row, _query.Keys(cells));
        }
    }

    public bool TryGetCachedPage(GridPageRequest request, out GridPage? page)
    {
        lock (_gate)
        {
            if (_disposal == null && !_token.IsCancellationRequested && !_source.IsInvalidated && !Completion.IsFaulted && _cache.TryGetValue(request, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                page = node.Value;
                return true;
            }
            page = null;
            return false;
        }
    }

    public Task<GridPage> ReadPageAsync(GridPageRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal != null, this);
            if (_source.IsInvalidated) throw new GridSourceChangedException("Grid source fingerprint changed; reopen the source.");
            _token.ThrowIfCancellationRequested();
            if (!_inFlight.TryGetValue(request, out var work) || work.Cancellation.IsCancellationRequested)
            {
                if (_reads.Count >= _source.Limits.MaxPendingPageReads) throw new GridLimitException("Grid page queue is full; coalesce stale viewport requests.");
                work = new(_token);
                var created = work;
                work.Task = Task.Run(() => ReadTrackedAsync(request, created));
                _ = work.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _inFlight[request] = work;
                _reads.Add(work);
            }
            if (work.Consumers >= _source.Limits.MaxPendingPageReads) throw new GridLimitException("Too many consumers of one grid page request.");
            work.Consumers++;
            return AwaitConsumerAsync(work, cancellationToken);
        }
    }

    private async Task<GridPage> AwaitConsumerAsync(PageReadWork work, CancellationToken ct)
    {
        try { return await work.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                work.Consumers--;
                if (work.Consumers == 0 && !work.Finished) work.Cancellation.Cancel();
            }
        }
    }

    private async Task<GridPage> ReadTrackedAsync(GridPageRequest request, PageReadWork work)
    {
        var ct = work.Cancellation.Token;
        bool entered = false;
        try
        {
            // Far scrollbar jumps wait for construction without occupying a
            // disk-read slot needed by an already available visible range.
            await _index.WaitForAsync(request.Start, ct).ConfigureAwait(false);
            await _readSlots.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var page = await ReadCoreAsync(request, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposal == null && !_token.IsCancellationRequested)
                {
                    // Do not freeze a partial construction page in the cache.
                    // Subsequent requests may have more rows at the same position.
                    if (page.IsRangeComplete)
                    {
                        if (_cache.Remove(request, out var previous))
                        {
                            _lru.Remove(previous);
                            _cacheBytes -= previous.Value.EstimatedBytes + 256;
                        }
                        var node = _lru.AddFirst(page);
                        _cache[request] = node;
                        _cacheBytes += page.EstimatedBytes + 256;
                        while (_cache.Count > _source.Limits.CachePages || _cacheBytes > _source.Limits.CacheBytes)
                        {
                            var last = _lru.Last!;
                            _cache.Remove(last.Value.Request);
                            _cacheBytes -= last.Value.EstimatedBytes + 256;
                            _lru.RemoveLast();
                        }
                    }
                }
            }
            return page;
        }
        finally
        {
            if (entered) _readSlots.Release();
            lock (_gate)
            {
                if (_inFlight.TryGetValue(request, out var current) && ReferenceEquals(current, work)) _inFlight.Remove(request);
                _reads.Remove(work);
                work.Finished = true;
                work.Cancellation.Dispose();
            }
        }
    }

    private async Task<GridPage> ReadCoreAsync(GridPageRequest request, CancellationToken ct)
    {
        await ValidateSourceAsync(false, ct).ConfigureAwait(false);
        long available = await _index.WaitForAsync(request.Start, ct).ConfigureAwait(false);
        if (TryGetCachedPage(request, out var cached))
        {
            var current = Count;
            return cached! with { HasMore = !current.IsComplete || request.Start + cached.Rows.Count < current.AvailableRows };
        }
        var rows = new List<GridRow>();
        long bytes = 0;
        bool byteLimited = false;
        await using var index = GridAppendIndex.OpenReader(_index.Path);
        await using var sourceRows = GridAppendIndex.OpenReader(_source.Store.RowsPath);
        long end = Math.Min(available, checked(request.Start + request.MaxRows));
        for (long i = request.Start; i < end; i++)
        {
            ct.ThrowIfCancellationRequested();
            var reference = await GridAppendIndex.ReadReferenceAsync(index, i, ct).ConfigureAwait(false);
            string[] cells = await _source.Store.ReadAsync(sourceRows, reference, Descriptor.Columns.Count, ct).ConfigureAwait(false);
            int[] sourceLengths = cells.Select(s => s.Length).ToArray();
            GridSourceBase.Apply(_edits, reference.Ordinal, cells);
            GridValues.Validate(cells, _source.Limits);
            long estimate = GridValues.Estimate(cells);
            if (estimate > request.MaxBytes && rows.Count == 0) throw new GridLimitException("One effective row exceeds the requested page budget.");
            if (bytes + estimate > request.MaxBytes) { byteLimited = true; break; }
            rows.Add(new(Descriptor.SourceId, reference.Ordinal, Array.AsReadOnly(cells), estimate)
                { SourceCellLengths = Array.AsReadOnly(sourceLengths) });
            bytes += estimate;
        }
        await ValidateSourceAsync(false, ct).ConfigureAwait(false);
        var count = Count;
        return new(Descriptor.SourceId, Generation, OverlayGeneration, request, rows.AsReadOnly(), bytes,
            !count.IsComplete || request.Start + rows.Count < count.AvailableRows)
        {
            IsRangeComplete = rows.Count == request.MaxRows || byteLimited || (count.IsComplete && request.Start + rows.Count >= count.AvailableRows)
        };
    }

    public async IAsyncEnumerable<GridRow> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        var ct = linked.Token;
        if (_source.IsInvalidated) throw new GridSourceChangedException("Grid source fingerprint changed; output was stopped.");
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal != null, this);
            if (_streams >= _source.Limits.MaxConcurrentStreams) throw new GridLimitException("Too many concurrent grid streams; finish or cancel another output operation.");
            _streams++;
        }
        try
        {
            await ValidateSourceAsync(true, ct).ConfigureAwait(false);
            long position = 0;
            while (true)
            {
                var page = await ReadPageAsync(new(position, _source.Limits.PageRows, _source.Limits.PageBytes), ct).ConfigureAwait(false);
                foreach (var row in page.Rows) { ct.ThrowIfCancellationRequested(); yield return row; }
                position += page.Rows.Count;
                if (!page.HasMore) break;
                if (page.Rows.Count == 0) await Completion.WaitAsync(ct).ConfigureAwait(false);
            }
            await ValidateSourceAsync(true, ct).ConfigureAwait(false);
        }
        finally { lock (_gate) _streams--; }
    }

    private async Task ValidateSourceAsync(bool strong, CancellationToken ct)
    {
        Task validation;
        lock (_gate)
        {
            _token.ThrowIfCancellationRequested();
            validation = Task.Run(() => _source.ValidateSource(strong, ct), ct);
            _validations.Add(validation);
        }
        try { await validation.ConfigureAwait(false); }
        catch (GridSourceChangedException) { _source.Invalidate(); throw; }
        finally { lock (_gate) _validations.Remove(validation); }
    }

    private void ValidateRequest(GridPageRequest request)
    {
        if (request.Start < 0 || request.Start > long.MaxValue - request.MaxRows || request.MaxRows < 1 ||
            request.MaxRows > _source.Limits.PageRows || request.MaxBytes < 1 || request.MaxBytes > _source.Limits.PageBytes)
            throw new ArgumentOutOfRangeException(nameof(request));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= Task.Run(CloseAsync));
    }

    private async Task CloseAsync()
    {
        _lifetime.Cancel();
        await _build.ConfigureAwait(false);
        Task[] reads;
        lock (_gate) reads = _reads.Select(r => (Task)r.Task).Concat(_validations).ToArray();
        try { await Task.WhenAll(reads).ConfigureAwait(false); } catch { /* The request tasks retain their original failures. */ }
        try { await _index.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            lock (_gate) { _cache.Clear(); _lru.Clear(); _cacheBytes = 0; _edits.Clear(); }
            _source.Release(this);
            _lifetime.Dispose();
            _readSlots.Dispose();
        }
    }
}

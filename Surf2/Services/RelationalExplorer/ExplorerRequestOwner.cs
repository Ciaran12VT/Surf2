using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Surf2.Storage.Relational.Access;

namespace Surf2.Services.RelationalExplorer;

// The UI owns this lifetime and checks Accepts again at dispatcher publication.
public sealed class ExplorerRequestOwner : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private AccessCancellation? _active;
    private Guid _identity;
    private bool _disposed;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private Exception? _cancellationFailure;
    public bool Accepts(Guid identity) { lock (_gate) return !_disposed && identity != Guid.Empty && _identity == identity && (_active == null || !_active.Token.IsCancellationRequested); }
    public async IAsyncEnumerable<ExplorerSearchBatch> SearchAsync(RelationalExplorerService explorer,
        IExplorerSearchSources sources, ExplorerSearchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        AccessCancellation active;
        CancellationTokenRegistration bridge;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (request.RequestIdentity == Guid.Empty || _identity == request.RequestIdentity)
                throw new ArgumentException("Each search uses a new nonempty request identity.");
            QueueCancellationLocked();
            active = new(() => { lock (_gate) _cancellationFailure ??= new InvalidOperationException("Explorer cancellation callback failed."); });
            bridge = ct.Register(() => { _ = active.Cancel(); });
            _active = active; _identity = request.RequestIdentity;
        }
        bool finished = false;
        bool changed = false;
        try
        {
            await foreach (var batch in explorer.SearchAsync(request, sources, active.Token).ConfigureAwait(false))
            {
                active.Token.ThrowIfCancellationRequested();
                if (!Accepts(batch.RequestIdentity)) throw new OperationCanceledException(active.Token);
                changed |= batch.Coverage.GenerationChanged;
                yield return batch;
            }
            finished = true;
        }
        finally
        {
            Task callbacks;
            lock (_gate) callbacks = _cancellationCallbacks;
            await callbacks.ConfigureAwait(false);
            bridge.Dispose();
            await active.RetireAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_active, active))
                {
                    if (active.Token.IsCancellationRequested || !finished || changed) _identity = Guid.Empty;
                    _active = null;
                }
            }
        }
    }
    public void Cancel()
    {
        lock (_gate) { _identity = Guid.Empty; QueueCancellationLocked(); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _identity = Guid.Empty; QueueCancellationLocked(); }
    }
    private void QueueCancellationLocked()
    {
        if (_active == null) return;
        Task previous = _cancellationCallbacks, next = _active.Cancel();
        _cancellationCallbacks = ObserveAsync();
        async Task ObserveAsync()
        {
            try { await previous.ConfigureAwait(false); await next.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _cancellationFailure ??= ex; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task callbacks; lock (_gate) callbacks = _cancellationCallbacks;
        await callbacks.ConfigureAwait(false);
        lock (_gate)
            if (_cancellationFailure != null) throw new AggregateException("An explorer cancellation callback failed.", _cancellationFailure);
    }
}

// Shares a selected child request, not a library/cache. A consumer cancelling its wait does not
// cancel another consumer; the last departing consumer cancels queued/active work.
public sealed class ExplorerChildrenLoader : IAsyncDisposable
{
    private sealed record Key(ExplorerScope Scope, string Occurrence);
    private sealed class Flight
    {
        internal readonly AccessCancellation Cancellation = new();
        internal readonly TaskCompletionSource<ExplorerChildrenBatch> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Consumers;
        internal bool Finished, Cancelling, CancelFinished, Disposed;
        internal Task Runner = Task.CompletedTask;
    }
    private readonly object _gate = new();
    private readonly Dictionary<Key, Flight> _flights = [];
    private readonly HashSet<Flight> _running = [];
    private readonly SemaphoreSlim _workers;
    private readonly RelationalExplorerService _explorer;
    private readonly int _maximumPending;
    private bool _disposed;
    public ExplorerChildrenLoader(RelationalExplorerService explorer, int maximumConcurrency = 2, int maximumPending = 32)
    {
        if (maximumConcurrency is < 1 or > 8 || maximumPending < maximumConcurrency || maximumPending > 128)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        _explorer = explorer; _workers = new(maximumConcurrency, maximumConcurrency); _maximumPending = maximumPending;
    }
    public async Task<ExplorerChildrenBatch> ReadAsync(ExplorerScope scope, ExplorerNodeSummary parent, CancellationToken ct = default)
    {
        var key = new Key(scope, parent.OccurrenceKey);
        Flight flight;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_flights.TryGetValue(key, out flight!))
            {
                if (_running.Count == _maximumPending) throw new ExplorerLimitException("The child-load queue is full.");
                flight = new(); _flights.Add(key, flight); _running.Add(flight);
                // Yield in RunAsync prevents provider work while the owner lock is held.
                flight.Runner = RunAsync(key, flight, parent);
            }
            flight.Consumers++;
        }
        try { return await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally { await ReleaseAsync(key, flight).ConfigureAwait(false); }
    }
    private async Task RunAsync(Key key, Flight flight, ExplorerNodeSummary parent)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        bool acquired = false;
        try
        {
            await _workers.WaitAsync(flight.Cancellation.Token).ConfigureAwait(false); acquired = true;
            var nodes = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
            ExplorerChildrenBatch? final = null;
            await foreach (var batch in _explorer.GetChildrenAsync(key.Scope, parent, flight.Cancellation.Token).ConfigureAwait(false))
            {
                nodes.AddRange(batch.Nodes); final = batch;
            }
            if (final == null) throw new InvalidOperationException("A child load ended without an outcome.");
            flight.Completion.TrySetResult(final with { Nodes = nodes.ToImmutable() });
        }
        catch (OperationCanceledException) { flight.Completion.TrySetCanceled(); }
        catch (Exception ex) { flight.Completion.TrySetException(ex); _ = flight.Completion.Task.Exception; }
        finally
        {
            if (acquired) _workers.Release();
            lock (_gate)
            {
                flight.Finished = true; _running.Remove(flight);
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
                DisposeIfUnused(flight);
            }
        }
    }
    private async Task ReleaseAsync(Key key, Flight flight)
    {
        bool cancel;
        lock (_gate)
        {
            flight.Consumers--;
            cancel = flight.Consumers == 0 && !flight.Finished && !flight.Cancelling;
            if (cancel)
            {
                flight.Cancelling = true;
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            }
            DisposeIfUnused(flight);
        }
        if (!cancel) return;
        try { await flight.Cancellation.Cancel().ConfigureAwait(false); }
        finally { lock (_gate) { flight.CancelFinished = true; DisposeIfUnused(flight); } }
    }
    private static void DisposeIfUnused(Flight flight)
    {
        if (!flight.Disposed && flight.Finished && flight.Consumers == 0 && (!flight.Cancelling || flight.CancelFinished))
        { flight.Disposed = true; _ = flight.Cancellation.RetireAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        Flight[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; pending = _running.ToArray();
            foreach (var flight in pending) { flight.Cancelling = true; flight.Consumers++; }
            _flights.Clear();
        }
        Exception? cancellationFailure = null;
        try
        {
            foreach (var flight in pending)
            {
                try { await flight.Cancellation.Cancel().ConfigureAwait(false); }
                catch (Exception ex) { cancellationFailure ??= ex; }
                finally { lock (_gate) { flight.CancelFinished = true; DisposeIfUnused(flight); } }
            }
            await Task.WhenAll(pending.Select(f => f.Runner)).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                foreach (var flight in pending) { flight.Consumers--; DisposeIfUnused(flight); }
            _workers.Dispose();
        }
        if (cancellationFailure != null) throw new AggregateException("A child-load cancellation callback failed.", cancellationFailure);
    }
}

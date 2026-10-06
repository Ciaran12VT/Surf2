namespace Surf2.Storage.Relational.Access;

public sealed record QueryStamp(Guid Epoch, Guid OwnerId, long? ScopeKey, long ContextGeneration, long RequestGeneration);

/// <summary>One logical workflow owner; advance context for membership, aliases, unloaded state or mutable-head changes.</summary>
public sealed class QueryLifetime : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly HashSet<QueryRequest> _requests = [];
    private Guid _epoch;
    private long? _scopeKey;
    private long _contextGeneration, _requestGeneration;
    private QueryRequest? _current;
    private bool _disposed;
    private long _cancellationCallbackFailures;

    public QueryLifetime(Guid epoch, long? scopeKey = null)
    {
        ValidateContext(epoch, scopeKey);
        _epoch = epoch;
        _scopeKey = scopeKey;
    }

    public int ActiveRequests { get { lock (_gate) return _requests.Count; } }
    public long CancellationCallbackFailures => Interlocked.Read(ref _cancellationCallbackFailures);
    internal void ReportCancellationCallbackFailure() => Interlocked.Increment(ref _cancellationCallbackFailures);

    public QueryRequest BeginRequest()
    {
        QueryRequest? previous;
        QueryRequest next;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long generation = checked(_requestGeneration + 1);
            next = new(this, new(_epoch, _ownerId, _scopeKey, _contextGeneration, generation));
            previous = _current;
            _requests.Add(next);
            _current = next;
            _requestGeneration = generation;
        }
        previous?.Cancel();
        return next;
    }

    public void ChangeContext(Guid epoch, long? scopeKey)
    {
        ValidateContext(epoch, scopeKey);
        QueryRequest? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _contextGeneration = checked(_contextGeneration + 1);
            _epoch = epoch;
            _scopeKey = scopeKey;
            previous = _current;
            _current = null;
        }
        previous?.Cancel();
    }

    public void InvalidateContext()
    {
        QueryRequest? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _contextGeneration = checked(_contextGeneration + 1);
            previous = _current;
            _current = null;
        }
        previous?.Cancel();
    }

    public bool IsCurrent(QueryStamp stamp)
    {
        lock (_gate) return IsCurrentCore(stamp);
    }

    private bool IsCurrentCore(QueryStamp stamp) => !_disposed && _current != null &&
        _current.Stamp == stamp && !_current.CancellationToken.IsCancellationRequested;

    /// <summary>Invoke on the dispatcher with a short synchronous callback; do not reenter this owner or perform I/O.</summary>
    public bool TryPublish(QueryStamp stamp, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_gate)
        {
            if (!IsCurrentCore(stamp)) return false;
            publish();
            return true;
        }
    }

    internal void Release(QueryRequest request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, request)) _current = null;
        }
        request.Cancel();
        _ = CompleteReleaseAsync(request);
    }

    private async Task CompleteReleaseAsync(QueryRequest request)
    {
        await request.RetireCancellationAsync().ConfigureAwait(false);
        lock (_gate) _requests.Remove(request);
        request.CompleteDisposal();
    }

    public void Dispose()
    {
        foreach (var request in Close()) request.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        QueryRequest[] requests = Close();
        foreach (var request in requests) request.Cancel();
        // Callers dispose each request in the real operation's finally block.
        // Closing an owner cancels, but never pretends those operations exited.
        await Task.WhenAll(requests.Select(r => r.DisposalCompletion)).ConfigureAwait(false);
    }

    private QueryRequest[] Close()
    {
        lock (_gate)
        {
            _disposed = true;
            _current = null;
            return _requests.ToArray();
        }
    }

    private static void ValidateContext(Guid epoch, long? scopeKey)
    {
        if (epoch == Guid.Empty || scopeKey <= 0) throw new ArgumentException("A valid epoch and optional positive scope key are required.");
    }
}

/// <summary>Dispose in finally after the real operation completes, not merely when cancellation is requested.</summary>
public sealed class QueryRequest : IDisposable, IAsyncDisposable
{
    private QueryLifetime? _owner;
    private readonly AccessCancellation _cancellation;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal QueryRequest(QueryLifetime owner, QueryStamp stamp)
    {
        _owner = owner;
        _cancellation = new(owner.ReportCancellationCallbackFailure);
        Stamp = stamp;
        CancellationToken = _cancellation.Token;
    }

    public QueryStamp Stamp { get; }
    public CancellationToken CancellationToken { get; }
    internal Task DisposalCompletion => _disposed.Task;
    internal bool IsCancellationDisposed => _cancellation.IsDisposed;
    internal void Cancel() => _ = _cancellation.Cancel();
    internal Task RetireCancellationAsync() => _cancellation.RetireAsync();
    internal void CompleteDisposal() => _disposed.TrySetResult();
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(this);

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await DisposalCompletion.ConfigureAwait(false);
    }
}

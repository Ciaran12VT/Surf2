using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access.State;

/// <summary>
/// One successfully loaded owner, with detached models and serialized optimistic saves.
/// Replace is synchronous: call it on the dispatcher when capturing mutable UI state.
/// </summary>
public sealed class StateEditSession<T> : IAsyncDisposable where T : class
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Func<T, T> _copy;
    private readonly Func<T, StateToken, Guid, CancellationToken, Task<StateToken>> _save;
    private readonly Func<CancellationToken, Task<StateToken?>> _probe;
    private T? _value;
    private StateToken _token;
    private long _generation, _savedGeneration;
    private StateEditStatus _status = StateEditStatus.Clean;
    private Pending? _pending;
    private sealed record Pending(long Generation, StateToken Expected, Guid Publication);
    private sealed record Captured(long Generation, T Value);

    internal StateEditSession(string kind, long subjectKey, SelectedState<T> loaded, Func<T, T> copy,
        Func<T, StateToken, Guid, CancellationToken, Task<StateToken>> save, Func<CancellationToken, Task<StateToken?>> probe)
    {
        Kind = kind; SubjectKey = subjectKey; _token = loaded.Token; _copy = copy; _save = save; _probe = probe;
        _value = copy(loaded.Value);
    }

    public string Kind { get; }
    public long SubjectKey { get; }
    public Guid Epoch => _token.Epoch;
    public StateToken ExpectedToken { get { lock (_sync) return _token; } }
    public StateEditStatus Status { get { lock (_sync) return _status; } }
    public long Generation { get { lock (_sync) return _generation; } }
    public bool IsDirty { get { lock (_sync) return _generation > _savedGeneration; } }
    public Guid? PendingPublicationId { get { lock (_sync) return _pending?.Publication; } }

    public T Snapshot()
    {
        lock (_sync) { Open(); return _copy(_value!); }
    }

    public long Replace(T capturedOnDispatcher)
    {
        ArgumentNullException.ThrowIfNull(capturedOnDispatcher);
        lock (_sync)
        {
            Open(); var copy = _copy(capturedOnDispatcher);
            long generation = checked(_generation + 1);
            _value = copy; _generation = generation;
            if (_status == StateEditStatus.Clean) _status = StateEditStatus.Dirty;
            return generation;
        }
    }

    public Task<StateSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Captured captured;
        lock (_sync)
        {
            Writable();
            // Private snapshots never escape: later Replace calls cannot mutate this generation.
            captured = new(_generation, _value!);
        }
        return SaveCapturedAsync(captured, cancellationToken);
    }

    private async Task<StateSaveResult> SaveCapturedAsync(Captured captured, CancellationToken ct)
    {
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StateToken expected; Guid publication;
            lock (_sync)
            {
                Writable();
                if (captured.Generation <= _savedGeneration)
                    return new(StateSaveDisposition.NoChanges, _token, _savedGeneration, _generation > _savedGeneration);
                expected = _token; publication = Guid.NewGuid();
                _pending = new(captured.Generation, expected, publication); _status = StateEditStatus.Saving;
            }
            StateToken saved;
            try
            {
                saved = await _save(captured.Value, expected, publication, ct).ConfigureAwait(false);
                ValidateToken(saved, expected);
                if (saved.PublicationId != publication) throw new InvalidOperationException("The returned owner does not identify this publication.");
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    if (_status != StateEditStatus.Closed)
                        _status = error is StateConflictException ? StateEditStatus.Conflict : StateEditStatus.OutcomeUnknown;
                    if (error is StateConflictException) _pending = null;
                }
                throw;
            }
            lock (_sync)
            {
                _token = saved; _savedGeneration = captured.Generation; _pending = null;
                if (_status != StateEditStatus.Closed) _status = _generation > _savedGeneration ? StateEditStatus.Dirty : StateEditStatus.Clean;
                return new(StateSaveDisposition.Saved, _token, _savedGeneration, _generation > _savedGeneration);
            }
        }
        finally { _writes.Release(); }
    }

    /// <summary>Resolve a lost acknowledgement by owner publication identity before permitting another save.</summary>
    public async Task<StateRecoveryDisposition> RecoverAsync(CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Pending? pending;
            lock (_sync) { Open(); pending = _pending; }
            if (pending == null) return StateRecoveryDisposition.NoPendingSave;
            var observed = await _probe(ct).ConfigureAwait(false);
            lock (_sync)
            {
                Open();
                if (observed != null && observed.Key == pending.Expected.Key && observed.Epoch == pending.Expected.Epoch && observed.PublicationId == pending.Publication)
                {
                    _token = observed; _savedGeneration = pending.Generation; _pending = null;
                    _status = _generation > _savedGeneration ? StateEditStatus.Dirty : StateEditStatus.Clean;
                    return StateRecoveryDisposition.Committed;
                }
                if (observed != null && SameToken(observed, pending.Expected))
                {
                    _pending = null; _status = StateEditStatus.Dirty;
                    return StateRecoveryDisposition.NotCommitted;
                }
                _pending = null; _status = StateEditStatus.Conflict;
                return StateRecoveryDisposition.Conflict;
            }
        }
        finally { _writes.Release(); }
    }

    internal static bool SameToken(StateToken left, StateToken right) => left.Key == right.Key && left.Epoch == right.Epoch &&
        left.PublicationId == right.PublicationId && left.Version.AsSpan().SequenceEqual(right.Version);
    private static void ValidateToken(StateToken token, StateToken expected)
    { if (token.Key != expected.Key || token.Epoch != expected.Epoch) throw new InvalidOperationException("A save returned a different owner/epoch."); }
    private void Open() { if (_status == StateEditStatus.Closed) throw new ObjectDisposedException(nameof(StateEditSession<T>)); }
    private void Writable()
    {
        Open();
        if (_status is StateEditStatus.Conflict or StateEditStatus.OutcomeUnknown)
            throw new InvalidOperationException("Reload a conflicted owner or recover the pending publication before saving again.");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { _status = StateEditStatus.Closed; _value = null; }
        // A visual Unloaded event is not a logical close; the parent decides when to call this.
        await _writes.WaitAsync().ConfigureAwait(false);
        _writes.Release();
    }
    public override string ToString() => Kind + ": " + Status;
}

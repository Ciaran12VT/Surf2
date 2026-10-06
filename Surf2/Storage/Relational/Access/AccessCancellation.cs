namespace Surf2.Storage.Relational.Access;

// Owns the source and the FIRST CancelAsync task. Later CancelAsync calls alone
// need not wait for callbacks already running, so disposal must use this task.
internal sealed class AccessCancellation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private Action? _reportFailure;
    private Task? _callbacks, _retirement;
    private bool _retiring;
    private int _disposed;

    internal AccessCancellation(Action? reportFailure = null)
    {
        _reportFailure = reportFailure;
        Token = _source.Token;
    }

    internal CancellationToken Token { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal Task Cancel()
    {
        lock (_gate)
        {
            if (_retiring) return _callbacks ?? Task.CompletedTask;
            // IsCancellationRequested changes here, before this method returns;
            // provider callbacks execute elsewhere, not on the transition caller.
            return _callbacks ??= ObserveCallbacksAsync(_source.CancelAsync());
        }
    }

    private async Task ObserveCallbacksAsync(Task callbacks)
    {
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception)
        {
            // Observe the entire callback batch without retaining/logging error
            // messages. Internal owner counters do not call external sinks.
            _reportFailure?.Invoke();
        }
    }

    internal Task RetireAsync()
    {
        lock (_gate)
        {
            if (_retirement != null) return _retirement;
            _retiring = true;
            return _retirement = RetireCoreAsync(_callbacks ?? Task.CompletedTask);
        }
    }

    private async Task RetireCoreAsync(Task callbacks)
    {
        await callbacks.ConfigureAwait(false);
        _source.Dispose();
        _reportFailure = null;
        Volatile.Write(ref _disposed, 1);
    }
}

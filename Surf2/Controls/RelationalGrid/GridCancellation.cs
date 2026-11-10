namespace Surf2.Controls.RelationalGrid;

// Retain the first callback task: later CancelAsync calls need not wait for an
// already-running callback batch. Parent cancellation also stays off the UI thread.
internal sealed class GridCancellation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _parent;
    private Task? _callbacks, _retirement;
    private bool _retiring;

    internal GridCancellation(CancellationToken parent = default)
    {
        Token = _source.Token;
        if (parent.CanBeCanceled)
            _parent = parent.UnsafeRegister(static state => { _ = ((GridCancellation)state!).Cancel(); }, this);
    }

    internal CancellationToken Token { get; }
    internal bool IsCancellationRequested => Token.IsCancellationRequested;

    internal Task Cancel()
    {
        lock (_gate)
        {
            if (_retiring) return _callbacks ?? Task.CompletedTask;
            if (_callbacks != null) return _callbacks;
            _callbacks = _source.CancelAsync();
            _ = _callbacks.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _callbacks;
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
        try
        {
            await _parent.DisposeAsync().ConfigureAwait(false);
            await callbacks.ConfigureAwait(false);
        }
        finally { _source.Dispose(); }
    }
}

using Surf2.Storage.Relational.Access;

namespace Surf2.Services.RelationalExplorer;

// Parent tokens signal the owned source without running provider callbacks on their caller.
internal sealed class ExplorerCancellationLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AccessCancellation _source = new();
    private readonly CancellationTokenRegistration[] _parents;
    private Task? _retirement;

    internal ExplorerCancellationLifetime(params CancellationToken[] parents)
    {
        _parents = parents.Where(token => token.CanBeCanceled).Select(token => token.Register(static state =>
        {
            _ = ((AccessCancellation)state!).Cancel();
        }, _source)).ToArray();
    }

    internal CancellationToken Token => _source.Token;
    internal bool IsCancellationRequested => Token.IsCancellationRequested;
    internal bool IsDisposed => _source.IsDisposed;
    internal Task Cancel() => _source.Cancel();
    internal Task RetireAsync()
    {
        lock (_gate) return _retirement ??= RetireCoreAsync();
    }
    private async Task RetireCoreAsync()
    {
        var callbacks = _source.Cancel();
        await Task.WhenAll(_parents.Select(parent => parent.DisposeAsync().AsTask())).ConfigureAwait(false);
        await callbacks.ConfigureAwait(false);
        await _source.RetireAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => new(RetireAsync());
}

using System.Runtime.ExceptionServices;

namespace Surf2.Storage.Relational.Access;

public sealed class QueryCapacityException() : InvalidOperationException("The bounded query owner has no available capacity.");

/// <summary>The same key must describe the same read, including epoch, revision and formatting/query policy.</summary>
public sealed class SingleFlight<TKey, TValue> : IAsyncDisposable where TKey : notnull
{
    private sealed record Outcome(TValue? Value, ExceptionDispatchInfo? Error, bool Cancelled);
    private sealed class Flight(Action reportCancellationFailure)
    {
        internal readonly AccessCancellation Cancellation = new(reportCancellationFailure);
        internal CancellationToken Token => Cancellation.Token;
        internal readonly TaskCompletionSource<Outcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Consumers;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Flight> _joinable;
    private readonly HashSet<Flight> _running = [];
    private readonly int _maximumFlights, _maximumConsumers;
    private bool _disposed;
    private long _cancellationCallbackFailures;

    public SingleFlight(int maximumFlights, int maximumConsumersPerFlight = 128, IEqualityComparer<TKey>? comparer = null)
    {
        if (maximumFlights <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFlights));
        if (maximumConsumersPerFlight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConsumersPerFlight));
        _maximumFlights = maximumFlights;
        _maximumConsumers = maximumConsumersPerFlight;
        _joinable = new(comparer);
    }

    /// <summary>Includes cancelled providers and callback retirement until both actually exit.</summary>
    public int ActiveCount { get { lock (_gate) return _running.Count; } }
    public long CancellationCallbackFailures => Interlocked.Read(ref _cancellationCallbackFailures);

    public async Task<TValue> RunAsync(TKey key, Func<CancellationToken, Task<TValue>> read, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(read);
        ct.ThrowIfCancellationRequested();
        Flight flight;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_joinable.TryGetValue(key, out flight!))
            {
                if (_running.Count >= _maximumFlights) throw new QueryCapacityException();
                flight = new(() => Interlocked.Increment(ref _cancellationCallbackFailures));
                _joinable.Add(key, flight);
                _running.Add(flight);
                start = true;
            }
            if (flight.Consumers >= _maximumConsumers) throw new QueryCapacityException();
            flight.Consumers++;
        }
        if (start) _ = ExecuteAsync(key, flight, read);
        try
        {
            var outcome = await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (outcome.Cancelled) throw new OperationCanceledException(flight.Token);
            outcome.Error?.Throw();
            return outcome.Value!;
        }
        finally { Release(key, flight); }
    }

    private async Task ExecuteAsync(TKey key, Flight flight, Func<CancellationToken, Task<TValue>> read)
    {
        Outcome outcome;
        try
        {
            TValue value = await read(flight.Token).ConfigureAwait(false);
            outcome = flight.Token.IsCancellationRequested ? new(default, null, true) : new(value, null, false);
        }
        catch (OperationCanceledException ex) when (flight.Token.IsCancellationRequested || ex.CancellationToken == flight.Token)
        {
            outcome = new(default, null, true);
        }
        catch (Exception ex) { outcome = new(default, ExceptionDispatchInfo.Capture(ex), false); }
        Task retirement;
        lock (_gate)
        {
            // An abandoned flight may have been replaced under this same key.
            if (_joinable.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _joinable.Remove(key);
            retirement = flight.Cancellation.RetireAsync();
        }
        await retirement.ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || flight.Token.IsCancellationRequested || flight.Consumers == 0) outcome = new(default, null, true);
            _running.Remove(flight);
            flight.Completion.SetResult(outcome);
        }
    }

    private void Release(TKey key, Flight flight)
    {
        bool cancel = false;
        lock (_gate)
        {
            if (--flight.Consumers == 0 && _running.Contains(flight))
            {
                if (_joinable.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _joinable.Remove(key);
                cancel = true;
            }
        }
        if (cancel) _ = flight.Cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Flight[] remaining;
        lock (_gate)
        {
            _disposed = true;
            _joinable.Clear();
            remaining = _running.ToArray();
        }
        foreach (var flight in remaining) _ = flight.Cancellation.Cancel();
        // Completion carries an outcome instead of a faulted task, so abandoned
        // consumers do not leave unobserved exceptions and disposal drains all jobs.
        await Task.WhenAll(remaining.Select(f => f.Completion.Task)).ConfigureAwait(false);
    }
}

namespace Surf2.Storage.Relational.Access;

public sealed record ByteCacheUsage(long RetainedBytes, int RetainedEntries, int VisibleEntries, int PinnedEntries);

/// <summary>Charge includes the retained key, value and entry overhead; values must be immutable.</summary>
public sealed class ByteBoundedCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private sealed class Entry(TKey key, TValue value, long bytes)
    {
        internal readonly TKey Key = key;
        internal readonly TValue Value = value;
        internal readonly long Bytes = bytes;
        internal LinkedListNode<Entry>? Node;
        internal int Pins;
        internal bool Retired;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly LinkedList<Entry> _order = new();
    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private long _bytes;
    private int _count, _pinned;
    private bool _disposed;

    public ByteBoundedCache(long maximumBytes, int maximumEntries, IEqualityComparer<TKey>? comparer = null)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumBytes = maximumBytes;
        _maximumEntries = maximumEntries;
        _entries = new(comparer);
    }

    public ByteCacheUsage Usage
    {
        get { lock (_gate) return new(_bytes, _count, _entries.Count, _pinned); }
    }

    public bool TryStore(TKey key, TValue value, long retainedBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (retainedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(retainedBytes));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (retainedBytes > _maximumBytes) return false;
            _entries.TryGetValue(key, out var existing);
            if (existing?.Pins > 0) return false;
            long projectedBytes = _bytes - (existing?.Bytes ?? 0);
            int projectedCount = _count - (existing == null ? 0 : 1);
            var evictions = new List<Entry>();
            // Plan first: a failed admission must not evict unrelated entries.
            for (var node = _order.Last; projectedBytes > _maximumBytes - retainedBytes || projectedCount >= _maximumEntries; node = node?.Previous)
            {
                if (node == null) return false;
                if (node.Value == existing || node.Value.Pins != 0) continue;
                evictions.Add(node.Value);
                projectedBytes -= node.Value.Bytes;
                projectedCount--;
            }
            if (existing != null) Retire(existing);
            foreach (var entry in evictions) Retire(entry);
            var added = new Entry(key, value, retainedBytes);
            added.Node = _order.AddFirst(added);
            _entries.Add(key, added);
            _bytes += retainedBytes;
            _count++;
            return true;
        }
    }

    public CacheLease<TValue>? TryAcquire(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.Pins == int.MaxValue) throw new InvalidOperationException("The cache entry has too many leases.");
            if (entry.Pins++ == 0) _pinned++;
            _order.Remove(entry.Node!);
            _order.AddFirst(entry.Node!);
            return new(entry.Value, () => Release(entry));
        }
    }

    public bool Invalidate(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry)) return false;
            Retire(entry);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearCore();
        }
    }

    private void Retire(Entry entry)
    {
        _entries.Remove(entry.Key);
        _order.Remove(entry.Node!);
        entry.Node = null;
        entry.Retired = true;
        if (entry.Pins == 0) Uncharge(entry);
    }

    private void Uncharge(Entry entry) { _bytes -= entry.Bytes; _count--; }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Pins != 0) return;
            _pinned--;
            if (entry.Retired) Uncharge(entry);
        }
    }

    private void ClearCore()
    {
        while (_order.First != null) Retire(_order.First.Value);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearCore();
        }
    }
}

/// <summary>Logical close releases a pin; WPF Unloaded alone must not dispose this lease.</summary>
public sealed class CacheLease<TValue> : IDisposable
{
    private sealed record State(TValue Value, Action Release);
    private State? _state;

    internal CacheLease(TValue value, Action release) => _state = new(value, release);

    public TValue Value => Volatile.Read(ref _state) is { } state ? state.Value :
        throw new ObjectDisposedException(nameof(CacheLease<TValue>));

    public void Dispose() => Interlocked.Exchange(ref _state, null)?.Release();
}

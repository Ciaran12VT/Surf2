namespace Surf2.Storage.Relational.Access;

public sealed record QueryMetricsSnapshot(int Active, long Completed, long Failed, long Cancelled, long Stale,
    long Abandoned, long RowsRead, long BytesRead, long Queries, long CacheHits, TimeSpan Duration,
    IReadOnlyList<QueryDiagnostic> Recent);

/// <summary>Bounded, redacted fetch metrics. Byte/row meanings belong to the measured operation, not server network traffic.</summary>
public sealed class RelationalQueryMetrics
{
    private const int MaximumRecent = 256;
    private readonly object _gate = new();
    private readonly Queue<QueryDiagnostic> _recent = [];
    private long _completed, _failed, _cancelled, _stale, _abandoned, _rows, _bytes, _queries, _hits, _ticks;
    public RelationalQueryMetrics() => Diagnostics = new(Record);
    public QueryDiagnostics Diagnostics { get; }

    private void Record(QueryDiagnostic item)
    {
        lock (_gate)
        {
            switch (item.Outcome)
            {
                case QueryOutcome.Completed: _completed++; break;
                case QueryOutcome.Failed: _failed++; break;
                case QueryOutcome.Cancelled: _cancelled++; break;
                case QueryOutcome.Stale: _stale++; break;
                default: _abandoned++; break;
            }
            _rows += item.RowsRead; _bytes += item.BytesRead; _queries += item.QueryCount;
            _hits += item.CacheHits; _ticks += item.Duration.Ticks;
            if (_recent.Count == MaximumRecent) _recent.Dequeue();
            _recent.Enqueue(item);
        }
    }

    public QueryMetricsSnapshot Snapshot()
    {
        lock (_gate) return new(Diagnostics.ActiveOperations, _completed, _failed, _cancelled, _stale, _abandoned,
            _rows, _bytes, _queries, _hits, TimeSpan.FromTicks(_ticks), Array.AsReadOnly(_recent.ToArray()));
    }
}

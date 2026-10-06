using System.Security.Cryptography;
using System.Text;

namespace Surf2.Storage.Relational.Access;

public enum QueryOperation
{
    CataloguePage, LocatorResolution, DefinitionRead, TableMetadataRead,
    DataSetDescriptor, DataSetPage, IndexMetadata, SearchBatch, StateRead, AssetRead
}

public enum QueryOutcome { Completed, Cancelled, Failed, Stale, Abandoned }

/// <summary>Only a 32-byte digest can enter a diagnostic; no raw SQL or arbitrary labels are retained.</summary>
public readonly record struct QueryFingerprint
{
    private QueryFingerprint(string value) => Value = value;
    public string? Value { get; }

    public static QueryFingerprint Parse(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A SHA-256 hexadecimal fingerprint is required.", nameof(sha256));
        return new(sha256.ToUpperInvariant());
    }

    public static QueryFingerprint FromTemplate(string parameterizedQueryTemplate)
    {
        ArgumentNullException.ThrowIfNull(parameterizedQueryTemplate);
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parameterizedQueryTemplate))));
    }

    public override string ToString() => Value ?? "";
}

public sealed record QueryDiagnostic(QueryOperation Operation, QueryFingerprint Fingerprint, Guid Epoch,
    TimeSpan Duration, long RowsRead, long BytesRead, long QueryCount, long CacheHits, QueryOutcome Outcome);

/// <summary>No internal result/log queue. The supplied sink must be bounded and nonblocking.</summary>
public sealed class QueryDiagnostics
{
    private readonly Action<QueryDiagnostic> _sink;
    private readonly TimeProvider _time;
    private int _active;
    private long _sinkFailures;

    public QueryDiagnostics(Action<QueryDiagnostic> sink, TimeProvider? timeProvider = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = timeProvider ?? TimeProvider.System;
    }

    public int ActiveOperations => Volatile.Read(ref _active);
    public long SinkFailures => Interlocked.Read(ref _sinkFailures);

    public QueryMeasurement Begin(QueryOperation operation, QueryFingerprint fingerprint, Guid epoch)
    {
        if (!Enum.IsDefined(operation) || fingerprint.Value == null || epoch == Guid.Empty)
            throw new ArgumentException("A known operation, fingerprint and epoch are required.");
        var measurement = new QueryMeasurement(this, operation, fingerprint, epoch, _time);
        Interlocked.Increment(ref _active);
        return measurement;
    }

    internal void Emit(QueryDiagnostic diagnostic)
    {
        Interlocked.Decrement(ref _active);
        try { _sink(diagnostic); }
        catch (Exception) { Interlocked.Increment(ref _sinkFailures); }
    }
}

/// <summary>Complete only after provider resources exit; an unfinished disposed measurement is Abandoned, not successful.</summary>
public sealed class QueryMeasurement : IDisposable
{
    private readonly object _gate = new();
    private readonly QueryDiagnostics _owner;
    private readonly QueryOperation _operation;
    private readonly QueryFingerprint _fingerprint;
    private readonly Guid _epoch;
    private readonly TimeProvider _time;
    private readonly long _started;
    private long _rows, _bytes, _queries, _hits;
    private bool _completed;

    internal QueryMeasurement(QueryDiagnostics owner, QueryOperation operation, QueryFingerprint fingerprint, Guid epoch, TimeProvider time)
    {
        _owner = owner;
        _operation = operation;
        _fingerprint = fingerprint;
        _epoch = epoch;
        _time = time;
        _started = time.GetTimestamp();
    }

    public void RecordRead(long rows, long bytes, long queryCount = 1)
    {
        if (rows < 0 || bytes < 0 || queryCount < 0) throw new ArgumentOutOfRangeException(nameof(rows));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            long nextRows = checked(_rows + rows), nextBytes = checked(_bytes + bytes), nextQueries = checked(_queries + queryCount);
            _rows = nextRows;
            _bytes = nextBytes;
            _queries = nextQueries;
        }
    }

    public void RecordCacheHit()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _hits = checked(_hits + 1);
        }
    }

    public void Complete(QueryOutcome outcome = QueryOutcome.Completed)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        QueryDiagnostic diagnostic;
        lock (_gate)
        {
            if (_completed) return;
            diagnostic = new(_operation, _fingerprint, _epoch, _time.GetElapsedTime(_started), _rows, _bytes, _queries, _hits, outcome);
            _completed = true;
        }
        _owner.Emit(diagnostic);
    }

    public void Dispose() => Complete(QueryOutcome.Abandoned);
}

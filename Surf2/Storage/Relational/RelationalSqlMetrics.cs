using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Surf2.Storage.Relational.Access;

namespace Surf2.Storage.Relational;

public sealed record SqlCommandMeasurement(QueryFingerprint Fingerprint, Guid Epoch, TimeSpan ExecutionDuration, QueryOutcome Outcome);
public sealed record SqlMetricsSnapshot(long Started, long Completed, long Failed, long Cancelled,
    int ActiveCommands, long DroppedMeasurements, TimeSpan TotalExecutionDuration, IReadOnlyList<SqlCommandMeasurement> Recent);

/// <summary>Command execution timings, not reader consumption or SQL Server CPU/reads. Stores no SQL, parameters or exceptions.</summary>
public sealed class RelationalSqlMetrics
{
    private const int MaximumActive = 1024, MaximumRecent = 256;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pending> _active = [];
    private readonly Queue<SqlCommandMeasurement> _recent = [];
    private readonly Guid _epoch;
    private long _started, _completed, _failed, _cancelled, _dropped, _elapsedTicks;
    private bool _enabled;
    private sealed record Pending(QueryFingerprint Fingerprint, long StartedAt);

    internal RelationalSqlMetrics(Guid epoch) => _epoch = epoch;
    public bool IsEnabled { get { lock (_gate) return _enabled; } }
    public void Enable() { lock (_gate) _enabled = true; }

    internal void Track(SqlConnection connection)
    {
        if (IsEnabled) RelationalSqlTelemetry.Track(connection, this);
    }

    internal void Begin(Guid id, string template)
    {
        try
        {
            // Hash before retaining anything. Dynamically generated identifiers are never stored as labels.
            var fingerprint = QueryFingerprint.FromTemplate(template);
            lock (_gate)
            {
                _started++;
                if (_active.Count >= MaximumActive || _active.ContainsKey(id)) { _dropped++; return; }
                _active.Add(id, new(fingerprint, Stopwatch.GetTimestamp()));
            }
        }
        catch (Exception) { lock (_gate) _dropped++; }
    }

    internal void Finish(Guid id, QueryOutcome outcome)
    {
        lock (_gate)
        {
            if (!_active.Remove(id, out var pending)) return;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(pending.StartedAt);
            _elapsedTicks += elapsed.Ticks;
            if (outcome == QueryOutcome.Completed) _completed++;
            else if (outcome == QueryOutcome.Cancelled) _cancelled++;
            else _failed++;
            if (_recent.Count == MaximumRecent) _recent.Dequeue();
            _recent.Enqueue(new(pending.Fingerprint, _epoch, elapsed, outcome));
        }
    }

    public SqlMetricsSnapshot Snapshot()
    {
        lock (_gate) return new(_started, _completed, _failed, _cancelled, _active.Count, _dropped,
            TimeSpan.FromTicks(_elapsedTicks), Array.AsReadOnly(_recent.ToArray()));
    }
}

// One process-wide listener with weak connection ownership. It never retains a runtime,
// command, connection string or returned payload after the owning connection is collectible.
internal sealed class RelationalSqlTelemetry : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly ConditionalWeakTable<SqlConnection, RelationalSqlMetrics> Owners = new();
    private static readonly Lazy<RelationalSqlTelemetry> Instance = new(() => new());
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();
    private readonly IDisposable _listeners;

    private RelationalSqlTelemetry() => _listeners = DiagnosticListener.AllListeners.Subscribe(this);
    internal static void Track(SqlConnection connection, RelationalSqlMetrics owner)
    {
        _ = Instance.Value;
        Owners.Add(connection, owner);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "SqlClientDiagnosticListener") return;
        lock (_gate) _subscriptions.Add(listener.Subscribe(this, name => name == SqlClientCommandBefore.Name ||
            name == SqlClientCommandAfter.Name || name == SqlClientCommandError.Name));
    }

    public void OnNext(KeyValuePair<string, object?> item)
    {
        // Instrumentation is observational: its failure cannot fail the user's command.
        try
        {
            switch (item.Value)
            {
                case SqlClientCommandBefore before when Owner(before.Command) is { } owner:
                    owner.Begin(before.OperationId, before.Command.CommandText); break;
                case SqlClientCommandAfter after when Owner(after.Command) is { } owner:
                    owner.Finish(after.OperationId, QueryOutcome.Completed); break;
                case SqlClientCommandError error when Owner(error.Command) is { } owner:
                    owner.Finish(error.OperationId, error.Exception is OperationCanceledException ? QueryOutcome.Cancelled : QueryOutcome.Failed); break;
            }
        }
        catch (Exception) { }
    }
    private static RelationalSqlMetrics? Owner(SqlCommand command) => command.Connection is { } connection &&
        Owners.TryGetValue(connection, out var owner) ? owner : null;
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}

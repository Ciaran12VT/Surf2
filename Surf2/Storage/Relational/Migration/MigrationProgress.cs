using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("Surf2.RegressionTests")]

namespace Surf2.Storage.Relational.Migration;

public sealed record MigrationProgressUpdate(string Phase, string Detail, long Completed, long? Total,
    string UnitName, TimeSpan Elapsed, TimeSpan PhaseElapsed, TimeSpan? EstimatedRemaining,
    TimeSpan LastActivityElapsed, string? LogPath);

/// <summary>Bounded observations only: neither heartbeats nor logging are migration work.</summary>
internal sealed class MigrationProgressReporter : IDisposable
{
    internal const long MaxLogBytes = 4 * 1024 * 1024;
    private const int TerminalLogReserve = 32 * 1024;
    private const int MaxTextLength = 1024;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumMeasurement = TimeSpan.FromSeconds(3);
    private static readonly Regex SensitiveDetail = new(
        @"(?:connection\s*string|(?:server|data\s*source|initial\s*catalog|database|integrated\s*security|user\s*id|uid|password|pwd|access\s*token|token|secret|api\s*key|credential)\s*[=:])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private readonly object _gate = new();
    private readonly IProgress<string>? _messages;
    private readonly IProgress<MigrationProgressUpdate>? _updates;
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private readonly long _started;
    private long _phaseStarted, _lastActivity, _lastUpdate, _lastLog;
    private long _completed;
    private long? _total;
    private int _measurements;
    private string _phase = "", _detail = "", _unitName = "units";
    private TimeSpan? _remaining;
    private FileStream? _log;
    private string? _logPath;
    private bool _hasPhase, _finished, _disposed, _logAttempted, _logLimited;

    public MigrationProgressReporter(IProgress<string>? messages = null,
        IProgress<MigrationProgressUpdate>? updates = null) : this(messages, updates, TimeProvider.System) { }

    internal MigrationProgressReporter(IProgress<string>? messages,
        IProgress<MigrationProgressUpdate>? updates, TimeProvider clock)
    {
        _messages = messages;
        _updates = updates;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _started = _phaseStarted = _lastActivity = _lastUpdate = _lastLog = clock.GetTimestamp();
        _timer = clock.CreateTimer(_ => Heartbeat(), null, UpdateInterval, UpdateInterval);
    }

    public void StartPhase(string phase, long? total = null, string unitName = "units")
    {
        ArgumentNullException.ThrowIfNull(phase);
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        lock (_gate)
        {
            if (_disposed || _finished) return;
            long now = _clock.GetTimestamp();
            if (_hasPhase) WriteLog("PhaseEnd", Snapshot(now), now, force: true);
            _phase = Bound(phase);
            _detail = "";
            _unitName = Bound(unitName, 64);
            _total = total;
            _completed = 0;
            _measurements = 0;
            _remaining = total == 0 ? TimeSpan.Zero : null;
            _phaseStarted = _lastActivity = now;
            _hasPhase = true;
            // Legacy InlineProgress consumers use these exact phase prefixes to cancel.
            ReportMessage(phase);
            Emit(now, "Phase", force: true);
        }
    }

    public void Advance(string detail, long count = 1)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (_disposed || _finished) return;
            long now = _clock.GetTimestamp();
            _detail = Bound(detail);
            if (count > 0)
            {
                _completed = count > long.MaxValue - _completed ? long.MaxValue : _completed + count;
                _lastActivity = now;
                if (_measurements < 2) _measurements++;
                _remaining = Estimate(now);
            }
            Emit(now, "Progress");
        }
    }

    public void SetDetail(string detail)
    {
        lock (_gate)
        {
            if (_disposed || _finished) return;
            long now = _clock.GetTimestamp();
            _detail = Bound(detail);
            _lastActivity = now;
            Emit(now, "Detail");
        }
    }

    public void AttachLog(string stageDirectory, Guid migrationIdentity)
    {
        lock (_gate)
        {
            if (_disposed || _finished || _logAttempted) return;
            _logAttempted = true;
            try
            {
                if (!Directory.Exists(stageDirectory)) return;
                string path = Path.Combine(Path.GetFullPath(stageDirectory),
                    $"migration-{migrationIdentity:N}-attempt-{Guid.NewGuid():N}.progress.jsonl");
                _log = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                _logPath = path;
                Emit(_clock.GetTimestamp(), "Attached", force: true);
            }
            catch (Exception) { CloseLog(); }
        }
    }

    public void Finish(string detail)
    {
        lock (_gate)
        {
            if (_disposed || _finished) return;
            _finished = true;
            _detail = Bound(detail);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ReportMessage(detail);
            // Finish may describe cancellation/failure. Never invent remaining completed units.
            Emit(_clock.GetTimestamp(), "Finished", force: true, terminal: true);
            CloseLog();
        }
    }

    private void Heartbeat()
    {
        lock (_gate)
        {
            if (_disposed || _finished || !_hasPhase) return;
            Emit(_clock.GetTimestamp(), "Heartbeat");
        }
    }

    private TimeSpan? Estimate(long now)
    {
        if (_total == null) return null;
        if (_completed >= _total.Value) return TimeSpan.Zero;
        TimeSpan measured = Age(_phaseStarted, now);
        if (_measurements < 2 || _completed == 0 || measured < MinimumMeasurement) return null;
        double ticks = (_total.Value - _completed) * ((double)measured.Ticks / _completed);
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    private TimeSpan Age(long timestamp, long now)
    {
        TimeSpan elapsed = _clock.GetElapsedTime(timestamp, now);
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }

    private MigrationProgressUpdate Snapshot(long now) => new(_phase, _detail, _completed, _total,
        _unitName, Age(_started, now), Age(_phaseStarted, now), _remaining, Age(_lastActivity, now), _logPath);

    private void Emit(long now, string kind, bool force = false, bool terminal = false)
    {
        bool deliver = force || Age(_lastUpdate, now) >= UpdateInterval;
        bool logDue = _log != null && (!_logLimited || terminal) && (force || Age(_lastLog, now) >= LogInterval);
        if (!deliver && !logDue) return;
        MigrationProgressUpdate update = Snapshot(now);
        WriteLog(kind, update, now, force, terminal);
        if (!deliver) return;
        _lastUpdate = now;
        // Serialize delivery with state changes so timer/work callbacks cannot reorder phases.
        try { _updates?.Report(update); }
        catch (Exception) { /* A presentation failure must not affect preservation work. */ }
    }

    private void ReportMessage(string message)
    {
        try { _messages?.Report(message); }
        catch (Exception) { /* Progress observers do not own migration success. */ }
    }

    private void WriteLog(string kind, MigrationProgressUpdate update, long now,
        bool force = false, bool terminal = false)
    {
        if (_log == null || (_logLimited && !terminal) || (!force && Age(_lastLog, now) < LogInterval)) return;
        try
        {
            byte[] entry = JsonSerializer.SerializeToUtf8Bytes(new
            {
                TimestampUtc = _clock.GetUtcNow(), Kind = kind,
                Phase = LogText(update.Phase), Detail = LogText(update.Detail),
                update.Completed, update.Total, UnitName = LogText(update.UnitName),
                update.Elapsed, update.PhaseElapsed, PhaseEstimatedRemaining = update.EstimatedRemaining,
                update.LastActivityElapsed
            });
            long limit = terminal ? MaxLogBytes : MaxLogBytes - TerminalLogReserve;
            if (_log.Length + entry.Length + 1 > limit) { _logLimited = true; return; }
            _log.Write(entry);
            _log.WriteByte((byte)'\n');
            _log.Flush();
            _lastLog = now;
        }
        catch (Exception) { CloseLog(); }
    }

    private static string Bound(string? text, int maximum = MaxTextLength) =>
        text == null ? "" : text.Length <= maximum ? text : text[..maximum];

    private static string LogText(string text)
    {
        try { return SensitiveDetail.IsMatch(text) ? "[redacted sensitive detail]" : text; }
        catch (RegexMatchTimeoutException) { return "[redacted detail]"; }
    }

    private void CloseLog()
    {
        try { _log?.Dispose(); }
        catch (Exception) { /* Best-effort diagnostics only. */ }
        finally { _log = null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            if (!_finished) WriteLog("StoppedBeforeFinish", Snapshot(_clock.GetTimestamp()),
                _clock.GetTimestamp(), force: true, terminal: true);
            CloseLog();
        }
    }
}

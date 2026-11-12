using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Services.RelationalSnapshots;

internal sealed partial class HistoryCountStore
{
    private readonly object _memoryGate = new();
    private readonly Dictionary<long, MemoryCounts> _memoryCounts = [];
    private sealed class MemoryCounts
    {
        public Dictionary<(long Version, HistoryLineCountKey Key), (int Added, int Removed)> Lines { get; } = [];
        public Dictionary<(long Version, HistoryLineCountKey Key), string> Errors { get; } = [];
        public Dictionary<(long Version, string Hash), long> Initial { get; } = [];
        public Dictionary<long, (SnapshotVersionSummary Version, byte[] Head, Dictionary<HistoryLineCountKey, StoredHistoryLineCount> Rows)> PendingLines { get; } = [];
        public Dictionary<(long Version, string Hash), (SnapshotVersionSummary Version, byte[] Head, byte[] Hash, long Count)> PendingInitial { get; } = [];
    }

    internal void ClearMemory() { lock (_memoryGate) _memoryCounts.Clear(); }
    internal void RemoveMemory(long snapshot) { lock (_memoryGate) _memoryCounts.Remove(snapshot); }
    internal void RetainMemory(IReadOnlySet<long> snapshots)
    {
        lock (_memoryGate)
            foreach (long key in _memoryCounts.Keys.Where(key => !snapshots.Contains(key)).ToArray()) _memoryCounts.Remove(key);
    }

    internal async Task LoadMemoryAsync(long snapshot, CancellationToken ct)
    {
        var result = new MemoryCounts();
        if (await EnsureAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
                await using var command = Command(connection, null, """
                    SELECT VersionKey,ResourceKey,Kind,InputHash,AddedLines,RemovedLines
                    FROM surf.HistoryFileLineCount WHERE SnapshotKey=@Snapshot;
                    SELECT VersionKey,ProjectionHash,FileCount FROM surf.HistoryInitialFileCount WHERE SnapshotKey=@Snapshot;
                    """, Key("@Snapshot", snapshot));
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    result.Lines.Add((reader.GetInt64(0), new(reader.GetInt64(1), reader.GetInt32(2), Convert.ToHexString((byte[])reader.GetValue(3)))),
                        (reader.GetInt32(4), reader.GetInt32(5)));
                await reader.NextResultAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    result.Initial.Add((reader.GetInt64(0), Convert.ToHexString((byte[])reader.GetValue(1))), reader.GetInt64(2));
            }
            catch (SqlException error) when (!ct.IsCancellationRequested) { Disable(error); }
        }
        ct.ThrowIfCancellationRequested();
        lock (_memoryGate) _memoryCounts[snapshot] = result;
    }

    private bool TryReadMemoryLines(SnapshotVersionSummary version, IReadOnlyList<HistoryLineCountKey> keys,
        out IReadOnlyDictionary<HistoryLineCountKey, (int Added, int Removed)> result)
    {
        var selected = new Dictionary<HistoryLineCountKey, (int Added, int Removed)>(); result = selected;
        lock (_memoryGate)
        {
            if (!_memoryCounts.TryGetValue(version.SnapshotKey, out var memory)) return false;
            foreach (var key in keys)
                if (memory.Lines.TryGetValue((version.VersionKey, key), out var counts)) selected[key] = counts;
            return true;
        }
    }

    private bool StoreMemoryLines(SnapshotVersionSummary version, byte[] head, IReadOnlyList<StoredHistoryLineCount> rows)
    {
        lock (_memoryGate)
        {
            if (!_memoryCounts.TryGetValue(version.SnapshotKey, out var memory)) return false;
            if (!memory.PendingLines.TryGetValue(version.VersionKey, out var pending))
                memory.PendingLines[version.VersionKey] = pending = (version, head.ToArray(), []);
            foreach (var row in rows)
            {
                var key = new HistoryLineCountKey(row.ResourceKey, row.Kind, row.InputHash);
                memory.Lines[(version.VersionKey, key)] = (row.Added, row.Removed);
                memory.Errors.Remove((version.VersionKey, key));
                pending.Rows[key] = row;
            }
            return true;
        }
    }

    internal string? ReadMemoryError(SnapshotVersionSummary version, HistoryLineCountKey? key)
    {
        lock (_memoryGate)
            return key.HasValue && _memoryCounts.TryGetValue(version.SnapshotKey, out var memory)
                ? memory.Errors.GetValueOrDefault((version.VersionKey, key.Value)) : null;
    }

    internal void StoreMemoryError(SnapshotVersionSummary version, HistoryLineCountKey? key, string error)
    {
        lock (_memoryGate)
            if (key.HasValue && _memoryCounts.TryGetValue(version.SnapshotKey, out var memory))
                memory.Errors[(version.VersionKey, key.Value)] = error;
    }

    private bool TryReadMemoryInitial(SnapshotVersionSummary version, byte[] dependency, out long? result)
    {
        lock (_memoryGate)
        {
            result = null;
            if (!_memoryCounts.TryGetValue(version.SnapshotKey, out var memory)) return false;
            if (memory.Initial.TryGetValue((version.VersionKey, Convert.ToHexString(dependency)), out long value)) result = value;
            return true;
        }
    }

    private bool StoreMemoryInitial(SnapshotVersionSummary version, byte[] head, byte[] dependency, long count)
    {
        lock (_memoryGate)
        {
            if (!_memoryCounts.TryGetValue(version.SnapshotKey, out var memory)) return false;
            var key = (version.VersionKey, Convert.ToHexString(dependency));
            memory.Initial[key] = count;
            memory.PendingInitial[key] = (version, head.ToArray(), dependency.ToArray(), count);
            return true;
        }
    }

    // Persistence happens during background preload. Opening and navigating history never writes SQL.
    internal async Task FlushMemoryAsync(long snapshot, CancellationToken ct)
    {
        (SnapshotVersionSummary Version, byte[] Head, StoredHistoryLineCount[] Rows)[] lines;
        (SnapshotVersionSummary Version, byte[] Head, byte[] Hash, long Count)[] initial;
        lock (_memoryGate)
        {
            if (!_memoryCounts.TryGetValue(snapshot, out var memory)) return;
            lines = memory.PendingLines.Values.Select(value => (value.Version, value.Head, value.Rows.Values.ToArray())).ToArray();
            initial = memory.PendingInitial.Values.ToArray();
            memory.PendingLines.Clear(); memory.PendingInitial.Clear();
        }
        foreach (var value in lines)
            foreach (var batch in value.Rows.Chunk(256))
                await PersistLinesAsync(value.Version, value.Head, batch, ct).ConfigureAwait(false);
        foreach (var value in initial)
            await PersistInitialAsync(value.Version, value.Head, value.Hash, value.Count, ct).ConfigureAwait(false);
    }
}

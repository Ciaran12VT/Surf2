using System.Diagnostics;
using System.IO;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services;

public sealed partial class RelationalRuntime
{
    private readonly object _snapshotMemoryGate = new();
    private readonly SemaphoreSlim _snapshotMemoryLoader = new(1, 1);
    private readonly Dictionary<long, byte[]> _snapshotMemoryReady = [];
    private long _snapshotMemoryGeneration;

    public bool HasSnapshotMemory { get { lock (_snapshotMemoryGate) return _snapshotMemoryReady.Keys.Any(Session.SnapshotMemory.ContainsSnapshot); } }
    public bool IsSnapshotMemoryReady(IReadOnlyCollection<long> keys)
    {
        lock (_snapshotMemoryGate)
            return keys.All(key => _snapshotMemoryReady.TryGetValue(key, out var head) &&
                Session.SnapshotMemory.TryGetSnapshot(key, out var snapshot) && head.AsSpan().SequenceEqual(snapshot.RowVersion));
    }

    public bool IsSnapshotMemoryReady(ExplorerScope scope)
    {
        if (scope.Context.Epoch != Session.Epoch) return false;
        var snapshots = scope.Resources.Where(resource => resource.IsLoaded && resource.Snapshot != null)
            .Select(resource => resource.Snapshot!).ToArray();
        return IsSnapshotMemoryReady(snapshots.Select(snapshot => snapshot.SnapshotKey).ToArray()) && snapshots.All(snapshot =>
            Session.SnapshotMemory.TryGetSnapshot(snapshot.SnapshotKey, out var cached) && cached.RowVersion.AsSpan().SequenceEqual(snapshot.RowVersion));
    }

    public void RetainSnapshotMemory(IReadOnlyCollection<long> keys)
    {
        var keep = keys.ToHashSet();
        lock (_snapshotMemoryGate)
            foreach (long key in _snapshotMemoryReady.Keys.Where(key => !keep.Contains(key)).ToArray()) _snapshotMemoryReady.Remove(key);
        Session.SnapshotMemory.Retain(keys);
        HistoryCounts.RetainMemory(keep);
    }

    public void InvalidateSnapshotMemory()
    {
        lock (_snapshotMemoryGate) { _snapshotMemoryGeneration++; _snapshotMemoryReady.Clear(); }
        Session.SnapshotMemory.Clear();
        HistoryCounts.ClearMemory();
    }

    public void InvalidateSnapshotMemory(long snapshotKey)
    {
        lock (_snapshotMemoryGate) { _snapshotMemoryGeneration++; _snapshotMemoryReady.Remove(snapshotKey); }
        Session.SnapshotMemory.Invalidate(snapshotKey);
        HistoryCounts.RemoveMemory(snapshotKey);
    }

    public async Task PreloadSnapshotMemoryAsync(IReadOnlyCollection<long> keys,
        IProgress<SnapshotMemoryProgress>? progress = null, CancellationToken ct = default)
    {
        await _snapshotMemoryLoader.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = Stopwatch.StartNew();
            long sqlBefore = Session.Metrics.Snapshot().Started;
            InternalLogService.Info("Preloading scope snapshot history and comparisons.", ("Snapshots", keys.Count));
            long generation;
            lock (_snapshotMemoryGate) generation = _snapshotMemoryGeneration;
            RetainSnapshotMemory(keys);
            await Session.SnapshotMemory.PreloadAsync(keys, progress, ct).ConfigureAwait(false);
            var service = new RelationalSnapshotHistoryService(this);
            int completed = 0;
            foreach (long key in keys.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                if (IsSnapshotMemoryReady([key])) { completed++; continue; }
                if (!Session.SnapshotMemory.TryGetSnapshot(key, out var snapshot)) throw new SnapshotConcurrencyException();
                await HistoryCounts.LoadMemoryAsync(key, ct).ConfigureAwait(false);
                var versions = await service.GetDisplayedVersionsAsync(snapshot, ct).ConfigureAwait(false);
                for (int index = 0; index < versions.Count; index++)
                {
                    var version = versions[index];
                    progress?.Report(new($"{snapshot.DisplayName}: preparing history counts {index + 1}/{versions.Count}", completed, keys.Count));
                    try
                    {
                        await service.CountVersionFilesAsync(snapshot, version, ct).ConfigureAwait(false);
                        var files = await service.ListFileRowsAsync(version, ct).ConfigureAwait(false);
                        await foreach (var result in service.CountFileLinesAsync(files, ct).ConfigureAwait(false))
                            ct.ThrowIfCancellationRequested();
                    }
                    catch (Exception error) when (error is not SnapshotConcurrencyException and not RelationalSessionChangedException &&
                        error is InvalidOperationException or IOException)
                    {
                        // All source data is already resident. A history renderer's existing limits or
                        // ambiguous legacy file identities must not prevent unrelated comparisons.
                        // Opening that version still reports the original error, entirely from memory.
                        InternalLogService.Warning("History counts could not be prepared for a loaded version.",
                            ("Snapshot", snapshot.SnapshotKey), ("Version", version.VersionKey), ("Reason", error.Message));
                    }
                    await HistoryCounts.FlushMemoryAsync(key, ct).ConfigureAwait(false);
                }
                lock (_snapshotMemoryGate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (generation != _snapshotMemoryGeneration) throw new OperationCanceledException("Snapshot preload was superseded.", ct);
                    if (!Session.SnapshotMemory.TryGetSnapshot(key, out var current) || !current.RowVersion.AsSpan().SequenceEqual(snapshot.RowVersion))
                        throw new SnapshotConcurrencyException();
                    _snapshotMemoryReady[key] = snapshot.RowVersion.ToArray();
                }
                progress?.Report(new($"History and comparisons ready for {snapshot.DisplayName}", ++completed, keys.Count));
            }
            InternalLogService.Info("Scope snapshot history and comparisons are ready in memory.",
                ("Snapshots", completed), ("Milliseconds", elapsed.Elapsed.TotalMilliseconds),
                ("SqlCommands", Session.Metrics.Snapshot().Started - sqlBefore));
        }
        finally { _snapshotMemoryLoader.Release(); }
    }
}

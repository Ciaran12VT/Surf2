using System.Windows;
using Surf2.Models;
using Surf2.Services.LiveSql;
using Surf2.Services.RemoteFolders;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.SourceResources;

/// <summary>Pairs concrete members without merging independent snapshot histories.</summary>
public static class SourceSnapshotLinker
{
    public static async Task<int> LinkMatchingAsync(RelationalRuntime runtime, Scope scope,
        ScopedResource? addedMember = null, CancellationToken ct = default)
    {
        scope.EnsureResourceContainers();
        var snapshots = await SnapshotsAsync(runtime, ct);
        var liveOrigins = new Dictionary<Guid, DatabaseOrigin>();
        foreach (var live in scope.Resources.Where(r => r.Kind == ResourceKind.LiveDatabase && r.Source != null))
        {
            ct.ThrowIfCancellationRequested();
            string? connection = RemoteFolderServices.Default.Credentials.Read(live.Source!);
            if (string.IsNullOrWhiteSpace(connection)) continue;
            try { liveOrigins[live.PublicId] = await new LiveSqlService(connection).OriginAsync(live.Source!.SourceId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { InternalLogService.Error(ex, "Cannot verify a live database origin for automatic pairing."); }
        }
        int changed = 0;
        foreach (var snapshot in scope.Resources.Where(r => r.Kind == ResourceKind.DatabaseSnapshot && r.PairedResourceId == null))
        {
            if (addedMember != null && addedMember.Kind == ResourceKind.DatabaseSnapshot && snapshot.PublicId != addedMember.PublicId) continue;
            if (!snapshots.TryGetValue(snapshot.Path, out var saved)) continue;
            var origin = await runtime.Snapshots.ReadSnapshotOriginAsync(saved.SnapshotKey, ct);
            if (origin?.IsComplete != true) continue;
            var matches = scope.Resources.Where(r => liveOrigins.TryGetValue(r.PublicId, out var evidence) && origin.AgreesWith(evidence)).ToArray();
            if (matches.Length != 1 || addedMember?.Kind == ResourceKind.LiveDatabase && matches[0].PublicId != addedMember.PublicId) continue;
            var live = matches[0]; snapshot.PairedResourceId = live.PublicId;
            // Additional histories remain distinct nodes, with a link for Refresh Snapshot.
            if (live.PairedResourceId == null)
            {
                live.PairedResourceId = snapshot.PublicId;
                snapshot.ContainerId = live.ContainerId;
            }
            changed++;
        }
        scope.EnsureResourceContainers(); return changed;
    }

    public static async Task<bool> VerifyExplicitAsync(Window owner, RelationalRuntime runtime,
        ScopedResource snapshotResource, ScopedResource liveSource, CancellationToken ct = default)
    {
        var snapshots = await SnapshotsAsync(runtime, ct);
        DatabaseOrigin? saved = snapshots.TryGetValue(snapshotResource.Path, out var snapshot)
            ? await runtime.Snapshots.ReadSnapshotOriginAsync(snapshot.SnapshotKey, ct) : null;
        string? connection = liveSource.Source == null ? null : RemoteFolderServices.Default.Credentials.Read(liveSource.Source);
        if (string.IsNullOrWhiteSpace(connection)) return false;
        var live = await new LiveSqlService(connection).OriginAsync(liveSource.Source!.SourceId, ct);
        if (saved?.AgreesWith(live) == true) return true;
        return MessageBox.Show(owner,
            $"The snapshot's saved origin and this connection do not fully agree.\n\nSnapshot: {saved?.Server}/{saved?.Database} ({saved?.Identifier ?? "unknown"})\nConnection: {live.Server}/{live.Database} ({live.Identifier})\n\nLink these as two views of the same database? Existing snapshot history will be preserved.",
            "Confirm snapshot source", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static async Task<Dictionary<string, SnapshotSummary>> SnapshotsAsync(RelationalRuntime runtime, CancellationToken ct)
    {
        var values = new Dictionary<string, SnapshotSummary>(StringComparer.Ordinal);
        SnapshotCursor? cursor = null;
        do
        {
            var page = await runtime.Snapshots.ListSnapshotsAsync(pageSize: 100, cursor: cursor, ct: ct).ConfigureAwait(false);
            foreach (var snapshot in page.Items) values.TryAdd(snapshot.SnapshotId, snapshot);
            if (values.Count > 100_000) throw new InvalidOperationException("The snapshot catalogue exceeds the pairing review limit.");
            cursor = page.Next;
        } while (cursor != null);
        return values;
    }
}

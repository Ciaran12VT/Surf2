using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

public sealed partial class RelationalDocumentService
{
    /// <summary>Resolve a comparison locator only within the already loaded scope snapshot models.</summary>
    public async Task<SnapshotResourceSummary> ResolveComparisonResourceAsync(string path, ExplorerScope scope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_runtime.IsSnapshotMemoryReady(scope))
            throw new InvalidOperationException("Snapshot history and comparison data are still loading.");
        if (!DatabaseDocumentService.TryParseDocumentPath(path, out var reference))
            throw Missing("The database document address is invalid.");
        var snapshots = scope.Resources.Where(resource => resource.IsLoaded && resource.Snapshot != null)
            .Select(resource => resource.Snapshot!).DistinctBy(snapshot => snapshot.SnapshotKey).ToArray();
        SnapshotSummary selected;
        long? resourceKey = null;
        if (TryParseIdentityPath(reference, out long snapshotKey, out long parsedResourceKey))
        {
            selected = snapshots.FirstOrDefault(snapshot => snapshot.SnapshotKey == snapshotKey)
                ?? throw Missing("The comparison snapshot is not loaded in this scope.");
            resourceKey = parsedResourceKey;
        }
        else
        {
            var matches = snapshots.Select(snapshot => (Snapshot: snapshot, Rank: SnapshotMatchRank(snapshot, reference.SnapshotId)))
                .Where(candidate => candidate.Rank != int.MaxValue).OrderBy(candidate => candidate.Rank).ToArray();
            if (matches.Length == 0) throw Missing("The comparison snapshot is not loaded in this scope.");
            if (matches.Length > 1 && matches[0].Rank == matches[1].Rank)
                throw new DocumentTargetException(SavedDocumentTargetState.Ambiguous,
                    "The snapshot locator has multiple loaded owners. Select a bound explorer item.");
            selected = matches[0].Snapshot;
        }
        var kind = RequestedKind(reference);
        SnapshotResourceSummary? result = null;
        SnapshotCursor? cursor = null;
        do
        {
            // The selected owner is fully cached. Missing resource IDs therefore do not fall back to SQL.
            if (!_runtime.Session.SnapshotMemory.ContainsSnapshot(selected.SnapshotKey)) throw new SnapshotConcurrencyException();
            var page = await _runtime.Snapshots.ListResourcesAsync(selected.SnapshotKey, kind,
                pageSize: 1000, cursor: cursor, ct: ct).ConfigureAwait(false);
            foreach (var resource in page.Items)
            {
                bool match = resourceKey.HasValue ? resource.ResourceKey == resourceKey.Value :
                    SqlName.FormatPlainMultipartName(resource.SchemaName, resource.ObjectName)
                        .Equals(reference.FullName, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                if (result != null) throw new DocumentTargetException(SavedDocumentTargetState.Ambiguous,
                    "The document locator has multiple resources. Select an identity-bound explorer item.");
                result = resource;
            }
            cursor = page.Next;
        } while (cursor != null);
        return result ?? throw Missing("The comparison resource is not in the loaded snapshot.");
    }
}

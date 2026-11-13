using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    private sealed record WindowLink(string Path, SavedDocumentTargetState State, long? Snapshot, long? Resource);
    private sealed record ConnectionLink(string Id, string Path, SavedDocumentTargetState State, long? Snapshot, long? Resource);

    // Preservation belongs to this detached save operation only. A path can remain unresolved only
    // when the identical descriptor was already stored by this owner before its children are replaced.
    private async Task<IReadOnlyDictionary<object, DocumentLinkPreservation>> ReadLayoutLinkPreservationAsync(
        SqlConnection connection, SqlTransaction transaction, string ownerColumn, long owner,
        IEnumerable<OpenDocumentState> windows, WorkbenchState? workbench, CancellationToken ct)
    {
        var preserved = new Dictionary<object, DocumentLinkPreservation>(ReferenceEqualityComparer.Instance);
        var pendingWindows = new List<OpenDocumentState>();
        foreach (var window in windows)
            if (window.TargetState is not (SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) &&
                await NeedsLinkPreservationAsync(window.FilePath, window.BoundSnapshotKey, window.BoundResourceKey))
                pendingWindows.Add(window);
        if (pendingWindows.Count > 0)
        {
            var existing = (await ChildrenAsync(connection, transaction, StateMaps.Window, ownerColumn, owner,
                new StateBudget(_limits), ct)).Select(row => Window(row.Value)).ToHashSet();
            foreach (var window in pendingWindows)
                if (existing.Contains(Window(window))) preserved[window] = new(Document: true);
        }
        if (workbench == null) return preserved;

        var pendingConnections = new List<(ReferenceConnectionLineState Line, bool Source, bool Target)>();
        foreach (var line in workbench.ReferenceConnectionLines)
        {
            bool source = line.SourceTargetState is not (SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) &&
                await NeedsLinkPreservationAsync(line.SourceFilePath, line.SourceBoundSnapshotKey, line.SourceBoundResourceKey);
            bool target = line.TargetTargetState is not (SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) &&
                await NeedsLinkPreservationAsync(line.TargetFilePath, line.TargetBoundSnapshotKey, line.TargetBoundResourceKey);
            if (source || target) pendingConnections.Add((line, source, target));
        }
        if (pendingConnections.Count > 0)
        {
            var existing = (await ChildrenAsync(connection, transaction, StateMaps.Connection, "WorkbenchKey", owner,
                new StateBudget(_limits), ct)).Select(row => row.Value).ToArray();
            var sources = existing.Select(Source).ToHashSet();
            var targets = existing.Select(Target).ToHashSet();
            foreach (var pending in pendingConnections)
            {
                bool source = pending.Source && sources.Contains(Source(pending.Line));
                bool target = pending.Target && targets.Contains(Target(pending.Line));
                if (source || target) preserved[pending.Line] = new(Source: source, Target: target);
            }
        }
        if (await NeedsLinkPreservationAsync(workbench.ActiveDocumentPath, null, null))
        {
            await using var command = Command(connection, transaction, """
SELECT CONVERT(bigint,DATALENGTH(ActiveDocumentPath)),ActiveDocumentPath
FROM surf.Workbench WHERE WorkbenchKey=@Key AND ProfileKey=1;
""", Key(owner));
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (await reader.ReadAsync(ct) &&
                string.Equals(await StringAsync(reader, 0, new StateBudget(_limits), ct), workbench.ActiveDocumentPath, StringComparison.Ordinal))
                preserved[workbench] = new(Active: true);
        }
        return preserved;

        static WindowLink Window(OpenDocumentState value) => new(value.FilePath, value.TargetState, value.BoundSnapshotKey, value.BoundResourceKey);
        static ConnectionLink Source(ReferenceConnectionLineState value) => new(value.ConnectionId, value.SourceFilePath,
            value.SourceTargetState, value.SourceBoundSnapshotKey, value.SourceBoundResourceKey);
        static ConnectionLink Target(ReferenceConnectionLineState value) => new(value.ConnectionId, value.TargetFilePath,
            value.TargetTargetState, value.TargetBoundSnapshotKey, value.TargetBoundResourceKey);
    }

    private async Task<bool> NeedsLinkPreservationAsync(string link, long? snapshot, long? resource)
    {
        bool needs = false;
        await DurableDocumentLink.RewriteAsync(link, path =>
        {
            if (path.StartsWith("db://@", StringComparison.OrdinalIgnoreCase))
                needs |= !DurableDocumentLink.TryParseSessionPath(path, out var identity) || identity.Epoch != _session.Epoch ||
                    (snapshot.HasValue || resource.HasValue) && (snapshot != identity.SnapshotKey || resource != identity.ResourceKey);
            return Task.FromResult(path);
        });
        return needs;
    }
}

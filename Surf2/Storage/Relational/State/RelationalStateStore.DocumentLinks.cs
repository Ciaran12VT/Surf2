using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;

namespace Surf2.Storage.Relational.State;

public sealed record DocumentLinkRepairResult(long Inspected, long Repaired, long Unresolved);

public sealed partial class RelationalStateStore
{
    private sealed record DocumentLinkPreservation(bool Document = false, bool Source = false, bool Target = false, bool Active = false);
    /// <summary>Convert a current runtime address before it is copied or saved outside that runtime.</summary>
    public async Task<string> NormalizeDocumentLinkAsync(string link, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!await HasTemporaryDocumentAddressAsync(link)) return link;
        return await ReadyAsync((connection, transaction) => NormalizeStoredLinkAsync(connection, transaction, link,
            allowPreviousSession: false, preserveUnresolved: false, null, null, ct), ct);
    }

    // Every caller of InsertAsync/UpdateAsync supplies a detached state copy. Normalization therefore
    // cannot change the live window identity or a caller's graph, and shares the owner's transaction.
    private async Task NormalizeStateDocumentLinksAsync<T>(SqlConnection connection, SqlTransaction transaction,
        T value, CancellationToken ct, DocumentLinkPreservation? preservation = null)
    {
        switch (value)
        {
            case OpenDocumentState window:
                if (window.TargetState is SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) break;
                window.FilePath = await NormalizeStoredLinkAsync(connection, transaction, window.FilePath,
                    window.TargetState == SavedDocumentTargetState.Resolved, preservation?.Document == true,
                    window.BoundSnapshotKey, window.BoundResourceKey, ct);
                break;
            case ReferenceConnectionLineState line:
                if (line.SourceTargetState is not (SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous))
                    line.SourceFilePath = await NormalizeStoredLinkAsync(connection, transaction, line.SourceFilePath,
                        line.SourceTargetState == SavedDocumentTargetState.Resolved, preservation?.Source == true,
                        line.SourceBoundSnapshotKey, line.SourceBoundResourceKey, ct);
                if (line.TargetTargetState is not (SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous))
                    line.TargetFilePath = await NormalizeStoredLinkAsync(connection, transaction, line.TargetFilePath,
                        line.TargetTargetState == SavedDocumentTargetState.Resolved, preservation?.Target == true,
                        line.TargetBoundSnapshotKey, line.TargetBoundResourceKey, ct);
                break;
            case WorkbenchState workbench:
                var active = workbench.OpenDocuments.FirstOrDefault(candidate =>
                    string.Equals(candidate.FilePath, workbench.ActiveDocumentPath, StringComparison.OrdinalIgnoreCase));
                if (active?.TargetState is SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) break;
                workbench.ActiveDocumentPath = await NormalizeStoredLinkAsync(connection, transaction,
                    workbench.ActiveDocumentPath, active?.TargetState == SavedDocumentTargetState.Resolved, preservation?.Active == true,
                    active?.BoundSnapshotKey, active?.BoundResourceKey, ct);
                break;
        }
    }

    private Task<string> NormalizeStoredLinkAsync(SqlConnection connection, SqlTransaction transaction, string link,
        bool allowPreviousSession, bool preserveUnresolved, long? boundSnapshot, long? boundResource,
        CancellationToken ct,
        Dictionary<(long Snapshot, long Resource, DatabaseVersionedResourceKind Kind), string?>? mappings = null) => DurableDocumentLink.RewriteAsync(link, async path =>
    {
        ct.ThrowIfCancellationRequested();
        if (!path.StartsWith("db://@", StringComparison.OrdinalIgnoreCase)) return path;
        if (!DurableDocumentLink.TryParseSessionPath(path, out var identity))
            return Unresolved("The temporary database document address is malformed.");
        if (!allowPreviousSession && identity.Epoch != _session.Epoch)
            return Unresolved("Cannot save a temporary document link from another Surf2 session. Select the resource again.");
        if ((boundSnapshot.HasValue || boundResource.HasValue) &&
            (boundSnapshot != identity.SnapshotKey || boundResource != identity.ResourceKey))
            return Unresolved("The temporary document address disagrees with its saved resource binding.");
        var key = (identity.SnapshotKey, identity.ResourceKey, identity.Kind);
        if (mappings != null && mappings.TryGetValue(key, out var cached))
            return cached ?? Unresolved("The temporary document address has no matching snapshot, resource and object kind.");
        await using var command = Command(connection, transaction, """
SELECT s.PublicId,r.PublicId
FROM surf.SnapshotResource r JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
WHERE r.ResourceKey=@Resource AND r.SnapshotKey=@Snapshot AND r.Kind=@Kind AND s.UserKey=1;
""", Key(identity.ResourceKey, "@Resource"), Key(identity.SnapshotKey, "@Snapshot"),
            RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)identity.Kind));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            if (mappings != null) mappings[key] = null;
            return Unresolved("The temporary document address has no matching snapshot, resource and object kind.");
        }
        Guid snapshot = reader.GetGuid(0), resource = reader.GetGuid(1);
        if (snapshot == Guid.Empty || resource == Guid.Empty)
            return Unresolved("The selected resource has no durable identity.");
        string durable = DurableDocumentLink.Create(snapshot, resource, identity.Kind);
        if (mappings != null) mappings[key] = durable;
        return durable;

        string Unresolved(string reason) => preserveUnresolved ? path : throw new InvalidOperationException(reason);
    });

    private async Task NormalizeDiagramDocumentLinksAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, long? diagramKey, long? workbenchKey, CancellationToken ct)
    {
        var temporary = new List<DiagramObjectSnapshot>();
        foreach (var item in diagram.Objects)
            if (await HasTemporaryDocumentAddressAsync(item.Metadata.Link)) temporary.Add(item);
        if (temporary.Count == 0) return;

        var unchanged = new HashSet<(string Id, string Link)>();
        var candidates = temporary.Select(item => (item.Id, item.Metadata.Link)).ToHashSet();
        // A failed repair must not block unrelated edits to an existing diagram. Preserve only an
        // exact link on the same object from this owner's preceding revision; new copies are rejected.
        if (diagramKey.HasValue || workbenchKey.HasValue)
        {
            string owner = diagramKey.HasValue
                ? "SELECT CurrentRevisionKey FROM surf.Diagram WHERE DiagramKey=@Owner AND ProfileKey=1"
                : "SELECT EmbeddedDiagramRevisionKey FROM surf.Workbench WHERE WorkbenchKey=@Owner AND ProfileKey=1";
            await using var command = Command(connection, transaction, $"""
SELECT CONVERT(bigint,DATALENGTH(s.Id)),s.Id,
 CONVERT(bigint,DATALENGTH(s.MetadataLink)),s.MetadataLink
FROM surf.DiagramObject s WHERE s.DiagramRevisionKey=({owner})
 AND (s.MetadataLink LIKE N'%db://@%' OR s.MetadataLink LIKE N'%surf-search:%');
""", Key(diagramKey ?? workbenchKey!.Value, "@Owner"));
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                var budget = new StateBudget(_limits);
                string id = (await StringAsync(reader, 0, budget, ct))!;
                string link = (await StringAsync(reader, 2, budget, ct))!;
                if (candidates.Contains((id, link))) unchanged.Add((id, link));
            }
        }
        var mappings = new Dictionary<(long, long, DatabaseVersionedResourceKind), string?>();
        foreach (var item in temporary)
            item.Metadata.Link = await NormalizeStoredLinkAsync(connection, transaction, item.Metadata.Link,
                false, unchanged.Contains((item.Id, item.Metadata.Link)), null, null, ct, mappings);
    }
}

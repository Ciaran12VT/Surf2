using System.Data;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class RelationalSnapshotStore
{
    public async Task<SnapshotResourceSummary?> ResolvePublicResourceAsync(Guid snapshotId, Guid resourceId,
        DatabaseVersionedResourceKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var memory = session.SnapshotMemory.All().FirstOrDefault(item => item.Header.PublicId == snapshotId);
        if (memory != null)
        {
            var resident = memory.Resources.FirstOrDefault(item => item.PublicId == resourceId && item.Kind == kind);
            return resident == null ? null : resident with { RowVersion = resident.RowVersion.ToArray() };
        }
        // Public GUIDs, unlike bigint keys or process epochs, survive both restarts and package restores.
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT " + ResourceMetadataBytes + ", " + ResourceColumns + " " + ResourceFrom +
            " JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey AND s.IsPublished=1" +
            " WHERE s.PublicId=@SnapshotId AND r.PublicId=@ResourceId AND r.Kind=@Kind AND chosen.RevisionKey IS NOT NULL AND rr.IsSealed=1;",
            RelationalSession.Parameter("@SnapshotId", SqlDbType.UniqueIdentifier, snapshotId),
            RelationalSession.Parameter("@ResourceId", SqlDbType.UniqueIdentifier, resourceId),
            RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)kind));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        return MapResource(reader);
    }
}

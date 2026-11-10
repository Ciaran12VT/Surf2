using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalExplorerMetadataQueries
{
    private async Task<ImmutableDictionary<long, ImmutableArray<ExplorerDatabaseItem>>> ReadCatalogueAsync(
        IReadOnlyList<long> snapshotKeys, CancellationToken ct)
    {
        if (snapshotKeys.Count == 0) return ImmutableDictionary<long, ImmutableArray<ExplorerDatabaseItem>>.Empty;
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, null, """
SELECT CONVERT(bigint, 256) + DATALENGTH(COALESCE(o.SchemaName,t.SchemaName,data.SchemaName,r.SchemaName))
 + DATALENGTH(COALESCE(o.ObjectName,t.TableName,data.TableName,r.ObjectName)) + DATALENGTH(r.OriginalResourceKey),
 r.ResourceKey, r.PublicId, r.SnapshotKey, r.Kind, COALESCE(o.SchemaName,t.SchemaName,data.SchemaName,r.SchemaName),
 COALESCE(o.ObjectName,t.TableName,data.TableName,r.ObjectName), r.OriginalResourceKey,
 r.CurrentRevisionKey, r.CurrentSortOrdinal, r.RowVersion, o.ObjectKind, t.HasFullData, t.FullDataRowCount
FROM OPENJSON(@Snapshots) WITH (SnapshotKey bigint '$') requested
JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=requested.SnapshotKey AND s.IsPublished=1
JOIN surf.SnapshotResource r ON r.SnapshotKey=s.SnapshotKey
JOIN surf.SnapshotResourceRevision revision ON revision.RevisionKey=r.CurrentRevisionKey AND revision.IsSealed=1
LEFT JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=revision.RevisionKey
LEFT JOIN surf.TableMetadataRevision t ON t.RevisionKey=revision.RevisionKey
LEFT JOIN surf.TableDataRevision data ON data.RevisionKey=revision.RevisionKey
ORDER BY r.SnapshotKey, r.CurrentSortOrdinal, r.ResourceKey;
""");
        IndexSql.Add(command, "@Snapshots", SqlDbType.NVarChar, JsonSerializer.Serialize(snapshotKeys), -1);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var budget = new ExplorerMetadataBudget(new());
        var groups = snapshotKeys.ToDictionary(key => key, _ => ImmutableArray.CreateBuilder<ExplorerDatabaseItem>());
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            SnapshotReadGuard.RequireBytes(reader.GetInt64(0), RelationalSnapshotStore.MaximumMetadataPageBytes, command);
            var resource = new SnapshotResourceSummary(reader.GetInt64(1), reader.GetGuid(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
                reader.GetInt64(9), (byte[])reader.GetValue(10));
            var objectKind = reader.IsDBNull(11) ? (SqlDatabaseObjectKind?)null : (SqlDatabaseObjectKind)reader.GetInt32(11);
            bool hasData = !reader.IsDBNull(12) && reader.GetBoolean(12);
            long count = reader.IsDBNull(13) ? 0 : reader.GetInt64(13);
            budget.Add(resource.SchemaName, resource.ObjectName, resource.OriginalResourceKey);
            groups[resource.SnapshotKey].Add(new(resource, objectKind, hasData, count));
        }
        return groups.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutable());
    }
}

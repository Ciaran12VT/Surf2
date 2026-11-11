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
        var limits = new ExplorerLimits();
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        // Stream the selected current rows from the snapshot index and revision keys.
        // Sorting or hashing joined nvarchar(max) metadata on SQL can require a large
        // memory grant before the first row. Restore source order in the bounded catalogue.
        await using var command = IndexSql.Command(connection, null, """
SELECT TOP (@Take) CONVERT(bigint, 256) + DATALENGTH(COALESCE(o.SchemaName,t.SchemaName,data.SchemaName,r.SchemaName))
 + DATALENGTH(COALESCE(o.ObjectName,t.TableName,data.TableName,r.ObjectName)) + DATALENGTH(r.OriginalResourceKey),
 r.ResourceKey, r.PublicId, r.SnapshotKey, r.Kind, COALESCE(o.SchemaName,t.SchemaName,data.SchemaName,r.SchemaName),
 COALESCE(o.ObjectName,t.TableName,data.TableName,r.ObjectName), r.OriginalResourceKey,
 r.CurrentRevisionKey, r.CurrentSortOrdinal, r.RowVersion, o.ObjectKind, t.HasFullData, t.FullDataRowCount
FROM OPENJSON(@Snapshots) WITH (SnapshotKey bigint '$') requested
JOIN surf.DatabaseSnapshot s WITH (FORCESEEK) ON s.SnapshotKey=requested.SnapshotKey AND s.IsPublished=1
JOIN surf.SnapshotResource r WITH (FORCESEEK) ON r.SnapshotKey=s.SnapshotKey
JOIN surf.SnapshotResourceRevision revision WITH (FORCESEEK) ON revision.RevisionKey=r.CurrentRevisionKey AND revision.IsSealed=1
LEFT JOIN surf.DatabaseObjectRevision o WITH (FORCESEEK) ON o.RevisionKey=revision.RevisionKey
LEFT JOIN surf.TableMetadataRevision t WITH (FORCESEEK) ON t.RevisionKey=revision.RevisionKey
LEFT JOIN surf.TableDataRevision data WITH (FORCESEEK) ON data.RevisionKey=revision.RevisionKey
OPTION (LOOP JOIN, FORCE ORDER, RECOMPILE);
""");
        IndexSql.Add(command, "@Snapshots", SqlDbType.NVarChar, JsonSerializer.Serialize(snapshotKeys), -1);
        // The extra row makes exceeding the catalogue budget an explicit failure.
        IndexSql.Add(command, "@Take", SqlDbType.Int, checked(limits.MaximumMetadataRows + 1));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var budget = new ExplorerMetadataBudget(limits);
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
        foreach (var group in groups.Values)
        {
            ct.ThrowIfCancellationRequested();
            group.Sort(static (left, right) =>
            {
                int order = left.Resource.SortOrdinal.CompareTo(right.Resource.SortOrdinal);
                return order != 0 ? order : left.Resource.ResourceKey.CompareTo(right.Resource.ResourceKey);
            });
        }
        return groups.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutable());
    }
}

using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public interface IExplorerDomainFence
{
    Task<bool> IsDomainCurrentAsync(IndexRequestContext context, CancellationToken ct);
}

// Index publication must not invalidate pure browsing. Search/reference requests still fence the
// derived generation; child metadata fences only epoch, scope and authoritative catalogue heads.
public sealed class RelationalExplorerMetadataQueries : IExplorerMetadataQueries, IExplorerDomainFence
{
    private readonly RelationalSession _session;
    private readonly RelationalIndexStore _index;
    private readonly RelationalExplorerMetadataAdapter _scopeReader;
    private readonly RelationalSnapshotStore _snapshots;
    public RelationalExplorerMetadataQueries(RelationalSession session, RelationalIndexStore index)
    {
        _session = session; _index = index; _scopeReader = new(session, index);
        _snapshots = new(session, new RelationalContentStore());
    }
    public async Task<ExplorerScope> ReadScopeAsync(long scopeKey, IReadOnlySet<string>? unloadedResourceIds,
        string? sortCultureName, CancellationToken ct)
    {
        // Do not let older read helpers capture a GUI synchronization context, including registration disposal after cancellation.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        return await _scopeReader.ReadScopeAsync(scopeKey, unloadedResourceIds, sortCultureName, ct).ConfigureAwait(false);
    }
    public async Task<bool> IsCurrentAsync(IndexRequestContext context, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        return await _index.IsContextCurrentAsync(context, ct).ConfigureAwait(false);
    }
    public async Task<bool> IsDomainCurrentAsync(IndexRequestContext context, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (context.Epoch != _session.Epoch) return false;
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, null, """
SELECT s.Version,sh.RowVersion,COALESCE(dg.Version,CONVERT(binary(8),0))
FROM surf.Scope s JOIN surf.SnapshotCatalogueHead sh ON sh.UserKey=s.ProfileKey
LEFT JOIN surf.StateCatalogueGeneration dg ON dg.ProfileKey=s.ProfileKey AND dg.Kind=1
WHERE s.ScopeKey=@Scope;
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, context.ScopeKey);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) && IndexSql.Hex((byte[])reader[0]) == context.ScopeVersion &&
            IndexSql.Hex((byte[])reader[1]) == context.SnapshotCatalogueVersion && IndexSql.Hex((byte[])reader[2]) == context.DiagramCatalogueVersion;
    }
    public async IAsyncEnumerable<ExplorerDatabaseItem> ReadDatabaseCategoryAsync(ExplorerScope scope,
        ExplorerResource resource, ExplorerCategory category, [EnumeratorCancellation] CancellationToken ct)
    {
        if (resource.Snapshot == null) yield break;
        await RequireDomainAsync(scope.Context, ct).ConfigureAwait(false);
        SnapshotCursor? cursor = null;
        do
        {
            if (category == ExplorerCategory.Tables)
            {
                var page = await _snapshots.ListTablesAsync(resource.Snapshot.SnapshotKey, pageSize: 64, cursor: cursor, ct: ct).ConfigureAwait(false);
                foreach (var item in page.Items) yield return new(item.Resource, null, item.HasFullData, item.FullDataRowCount);
                cursor = page.Next;
            }
            else
            {
                var page = await _snapshots.ListObjectsAsync(resource.Snapshot.SnapshotKey, SnapshotIdentity.ResourceKind(ExplorerCompatibility.ObjectKind(category)),
                    pageSize: 64, cursor: cursor, ct: ct).ConfigureAwait(false);
                foreach (var item in page.Items) yield return new(item.Resource, item.ObjectKind);
                cursor = page.Next;
            }
            await RequireDomainAsync(scope.Context, ct).ConfigureAwait(false);
        } while (cursor != null);
    }
    private async Task RequireDomainAsync(IndexRequestContext context, CancellationToken ct)
    {
        if (!await IsDomainCurrentAsync(context, ct).ConfigureAwait(false)) throw new IndexGenerationChangedException();
    }
}

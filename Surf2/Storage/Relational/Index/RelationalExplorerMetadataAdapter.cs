using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Storage.Relational.Index;

public sealed class RelationalExplorerMetadataAdapter : IExplorerMetadataQueries
{
    private readonly RelationalSession _session;
    private readonly RelationalIndexStore _index;
    private readonly RelationalSnapshotStore _snapshots;
    private readonly ExplorerLimits _limits;
    public RelationalExplorerMetadataAdapter(RelationalSession session, RelationalIndexStore index, ExplorerLimits? limits = null)
    {
        _session = session; _index = index; _snapshots = new(session, new RelationalContentStore());
        _limits = limits ?? new(); _limits.Validate();
    }
    public Task<bool> IsCurrentAsync(IndexRequestContext context, CancellationToken ct) => _index.IsContextCurrentAsync(context, ct);

    public async Task<ExplorerScope> ReadScopeAsync(long scopeKey, IReadOnlySet<string>? unloadedResourceIds,
        string? sortCultureName, CancellationToken ct)
    {
        string culture = CultureInfo.GetCultureInfo(sortCultureName ?? CultureInfo.CurrentCulture.Name).Name;
        var context = await _index.CaptureContextAsync(scopeKey, token: ct).ConfigureAwait(false);
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, transaction, """
SELECT LEFT(ScopeId,65537),LEFT(Name,65537) FROM surf.Scope WHERE ScopeKey=@Scope;
SELECT TOP (@Take) sr.ScopeResourceKey,sr.SortOrdinal,LEFT(sr.ResourceId,65537),sr.Kind,LEFT(sr.Path,65537),
 LEFT(sr.DisplayNameOverride,65537),sr.IncludeChildren,
 s.SnapshotKey,s.PublicId,LEFT(s.OriginalSnapshotId,65537),LEFT(s.DisplayName,65537),LEFT(s.DatabaseName,65537),
 s.ImportedAtUtc,s.SortOrdinal,s.CurrentVersionKey,s.RowVersion,
 d.DiagramKey,d.CurrentRevisionKey,LEFT(d.Name,65537),
 CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM surf.QueryItem q WHERE q.DiagramRevisionKey=d.CurrentRevisionKey
   AND q.DiagramObjectKey IS NOT NULL AND q.Status=0) THEN 1 ELSE 0 END)
FROM surf.ScopeResource sr LEFT JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=sr.SnapshotKey AND s.IsPublished=1
LEFT JOIN surf.Diagram d ON d.DiagramKey=sr.DiagramKey
WHERE sr.ScopeKey=@Scope ORDER BY sr.SortOrdinal,sr.ScopeResourceKey;
SELECT TOP (@Take) VirtualFolderKey,SortOrdinal,LEFT(VirtualFolderId,65537),LEFT(Name,65537),LEFT(ParentNodeKey,65537)
FROM surf.VirtualFolder WHERE ScopeKey=@Scope ORDER BY SortOrdinal,VirtualFolderKey;
SELECT TOP (@Take) m.VirtualFolderKey,m.SortOrdinal,LEFT(m.ChildNodeKey,65537)
FROM surf.VirtualFolderMember m JOIN surf.VirtualFolder f ON f.VirtualFolderKey=m.VirtualFolderKey
WHERE m.ScopeKey=@Scope ORDER BY f.SortOrdinal,f.VirtualFolderKey,m.SortOrdinal;
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, scopeKey);
        IndexSql.Add(command, "@Take", SqlDbType.Int, checked(_limits.MaximumMetadataRows + 1));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Selected scope is missing.");
        string scopeId = Text(reader, 0), name = Text(reader, 1);
        var budget = new ExplorerMetadataBudget(_limits);
        budget.Add(scopeId, name);
        var resources = ImmutableArray.CreateBuilder<ExplorerResource>();
        var unloaded = ImmutableArray.CreateBuilder<long>();
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long key = reader.GetInt64(0);
            string id = Text(reader, 2), path = Text(reader, 4), alias = Text(reader, 5);
            bool loaded = unloadedResourceIds == null || string.IsNullOrWhiteSpace(id) || !unloadedResourceIds.Contains(id);
            if (!loaded) unloaded.Add(key);
            SnapshotSummary? snapshot = reader.IsDBNull(7) ? null : new(reader.GetInt64(7), reader.GetGuid(8),
                Text(reader, 9), Text(reader, 10), Text(reader, 11), reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetInt64(13), Key(reader, 14), (byte[])reader[15]);
            string? diagramName = reader.IsDBNull(18) ? null : Text(reader, 18);
            budget.Add(id, path, alias, snapshot?.SnapshotId ?? "", snapshot?.DisplayName ?? "", snapshot?.DatabaseName ?? "", diagramName ?? "");
            resources.Add(new(key, reader.GetInt64(1), id, (ResourceKind)reader.GetInt32(3), path, alias,
                reader.GetBoolean(6), loaded, snapshot, Key(reader, 16), Key(reader, 17), diagramName, reader.GetBoolean(19)));
        }
        var folders = new List<ExplorerVirtualFolder>();
        var folderChildren = new Dictionary<long, ImmutableArray<string>.Builder>();
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long key = reader.GetInt64(0);
            string id = Text(reader, 2), folderName = Text(reader, 3), parent = Text(reader, 4);
            budget.Add(id, folderName, parent);
            folders.Add(new(key, reader.GetInt64(1), id, folderName, parent, []));
            folderChildren.Add(key, ImmutableArray.CreateBuilder<string>());
        }
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string child = Text(reader, 2); budget.Add(child);
            folderChildren[reader.GetInt64(0)].Add(child);
        }
        if (unloaded.Count > IndexSql.MaximumBatch) throw new ExplorerLimitException("The index context supports at most 512 unloaded resource keys.");
        context = context with { UnloadedScopeResourceKeys = unloaded.ToImmutable() };
        return new(context, scopeId, name, resources.ToImmutable(), folders.Select(f => f with
            { ChildNodeKeys = folderChildren[f.Key].ToImmutable() }).ToImmutableArray(), culture);
    }

    public async IAsyncEnumerable<ExplorerDatabaseItem> ReadDatabaseCategoryAsync(ExplorerScope scope,
        ExplorerResource resource, ExplorerCategory category, [EnumeratorCancellation] CancellationToken ct)
    {
        if (resource.Snapshot == null) yield break;
        if (!await IsCurrentAsync(scope.Context, ct).ConfigureAwait(false)) throw new IndexGenerationChangedException();
        SnapshotCursor? cursor = null;
        do
        {
            if (category == ExplorerCategory.Tables)
            {
                var page = await _snapshots.ListTablesAsync(resource.Snapshot.SnapshotKey, pageSize: _limits.PageSize,
                    cursor: cursor, ct: ct).ConfigureAwait(false);
                foreach (var table in page.Items) yield return new(table.Resource, null, table.HasFullData, table.FullDataRowCount);
                cursor = page.Next;
            }
            else
            {
                var kind = SnapshotIdentity.ResourceKind(ExplorerCompatibility.ObjectKind(category));
                var page = await _snapshots.ListObjectsAsync(resource.Snapshot.SnapshotKey, kind, pageSize: _limits.PageSize,
                    cursor: cursor, ct: ct).ConfigureAwait(false);
                foreach (var item in page.Items) yield return new(item.Resource, item.ObjectKind);
                cursor = page.Next;
            }
        } while (cursor != null);
        if (!await IsCurrentAsync(scope.Context, ct).ConfigureAwait(false)) throw new IndexGenerationChangedException();
    }
    private static string Text(SqlDataReader reader, int ordinal)
    {
        string value = reader.GetString(ordinal);
        if (value.Length > 65536) throw new ExplorerLimitException("An explorer metadata field exceeds its supported size.");
        return value;
    }
    private static long? Key(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}

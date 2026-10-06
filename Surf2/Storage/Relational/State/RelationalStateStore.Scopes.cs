using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    public async Task<StateToken> ImportScopeAsync(SqlConnection connection, SqlTransaction transaction, Scope scope,
        long sourceOrdinal, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId, sourceOrdinal);
        var copy = StateCopies.Scope(scope, new StateBudget(_limits));
        return await ImportUnitAsync(connection, transaction,
            () => InsertScopeAsync(connection, transaction, copy, sourceOrdinal, publicationId, cancellationToken), cancellationToken);
    }

    public Task<StateToken> CreateScopeAsync(Scope scope, long sortOrdinal, Guid publicationId, CancellationToken cancellationToken = default)
    {
        _session.RejectValidationWrite();
        if (sortOrdinal < 0 || publicationId == Guid.Empty) throw new ArgumentException("Invalid ordinal or publication.");
        var copy = StateCopies.Scope(scope, new StateBudget(_limits));
        return ReadyAsync((c,t) => InsertScopeAsync(c,t,copy,sortOrdinal,publicationId,cancellationToken), cancellationToken);
    }

    private async Task<StateToken> InsertScopeAsync(SqlConnection connection, SqlTransaction transaction, Scope scope,
        long ordinal, Guid publication, CancellationToken ct)
    {
        await BumpAsync(connection, transaction, 0, publication, ct);
        long key = await InsertAsync(connection, transaction, StateMaps.Scope, scope, ct,
            ("ProfileKey", SqlDbType.BigInt, 1L), ("SortOrdinal", SqlDbType.BigInt, ordinal),
            ("PublicationId", SqlDbType.UniqueIdentifier, publication));
        await ScopeChildrenAsync(connection, transaction, key, scope, ct);
        return await TokenAsync(connection, transaction, "Scope", "ScopeKey", key, ct);
    }

    public Task<StateToken> SaveScopeAsync(Scope scope, StateToken expected, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        var copy = StateCopies.Scope(scope, new StateBudget(_limits));
        return ReadyAsync(async (connection, transaction) =>
        {
            await BumpAsync(connection, transaction, 0, publicationId, cancellationToken);
            await UpdateAsync(connection, transaction, StateMaps.Scope, copy, expected, publicationId, cancellationToken);
            await ReplaceScopeChildrenAsync(connection, transaction, expected.Key, copy, cancellationToken);
            return await TokenAsync(connection, transaction, "Scope", "ScopeKey", expected.Key, cancellationToken);
        }, cancellationToken);
    }

    private sealed record ExistingScopeChild(long Key, long Ordinal, string LegacyId, int Kind = -1, string? Path = null);

    private async Task<List<ExistingScopeChild>> ExistingScopeChildrenAsync(SqlConnection connection, SqlTransaction transaction,
        long scope, bool folders, CancellationToken ct)
    {
        string sql = folders
            ? "SELECT TOP (@Limit) VirtualFolderKey, SortOrdinal, CONVERT(bigint, DATALENGTH(VirtualFolderId)), VirtualFolderId FROM surf.VirtualFolder WHERE ScopeKey=@Key ORDER BY SortOrdinal;"
            : "SELECT TOP (@Limit) ScopeResourceKey, SortOrdinal, CONVERT(bigint, DATALENGTH(ResourceId)), ResourceId, Kind, CONVERT(bigint, DATALENGTH(Path)), Path FROM surf.ScopeResource WHERE ScopeKey=@Key ORDER BY SortOrdinal;";
        await using var command = Command(connection, transaction, sql, Key(scope), Key((long)_limits.MaximumRows + 1, "@Limit"));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var result = new List<ExistingScopeChild>();
        var budget = new StateBudget(_limits);
        while (await reader.ReadAsync(ct))
        {
            budget.Row();
            long key = reader.GetInt64(0);
            long ordinal = reader.GetInt64(1);
            string id = (await StringAsync(reader, 2, budget, ct))!;
            int kind = folders ? -1 : reader.GetInt32(4);
            string? path = folders ? null : await StringAsync(reader, 5, budget, ct);
            result.Add(new(key, ordinal, id, kind, path));
        }
        return result;
    }

    private async Task UpdateScopeChildAsync<T>(SqlConnection connection, SqlTransaction transaction,
        StateRowMap<T> map, T value, long key, long ordinal, bool clearTarget, CancellationToken ct) where T : new()
    {
        await using var command = Command(connection, transaction,
            $"UPDATE surf.[{map.Table}] SET {map.Assignments}, SortOrdinal=@Ordinal" +
            (clearTarget ? ", SnapshotKey=NULL, DiagramKey=NULL" : "") + $" OUTPUT INSERTED.[{map.Key}] WHERE [{map.Key}]=@Key;",
            Key(key), Key(ordinal, "@Ordinal"));
        await map.AddParametersAsync(command, value, _content, connection, transaction, ct);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new InvalidDataException("A selected scope child is missing.");
    }

    private async Task ReplaceScopeChildrenAsync(SqlConnection connection, SqlTransaction transaction, long scope, Scope value, CancellationToken ct)
    {
        var resources = await ExistingScopeChildrenAsync(connection, transaction, scope, false, ct);
        var folders = await ExistingScopeChildrenAsync(connection, transaction, scope, true, ct);
        // Derived links must not prevent removal/reordering of selected children; resolve again after all rows exist.
        await ExecuteAsync(connection, transaction, """
UPDATE surf.VirtualFolder SET ParentVirtualFolderKey=NULL, ParentScopeResourceKey=NULL, ParentResourceKind=NULL, ParentResolution=0 WHERE ScopeKey=@Key;
UPDATE surf.VirtualFolderMember SET ChildVirtualFolderKey=NULL, ChildScopeResourceKey=NULL, ChildResourceKind=NULL, ChildResolution=0 WHERE ScopeKey=@Key;
""", ct, Key(scope));
        // Move old ordinals above both ranges before reordering, without negative sentinel values or uniqueness collisions.
        foreach (var (table, keyColumn, children) in new[] {
            ("ScopeResource", "ScopeResourceKey", resources), ("VirtualFolder", "VirtualFolderKey", folders) })
        {
            long temporary = checked((children.Count == 0 ? 0 : children.Max(x => x.Ordinal)) +
                Math.Max(value.Resources.Count, value.VirtualFolders.Count) + 1L);
            _ = checked(temporary + children.Count);
            foreach (var child in children)
                await ExecuteAsync(connection, transaction,
                    $"UPDATE surf.[{table}] SET SortOrdinal=@Ordinal WHERE [{keyColumn}]=@Key;", ct, Key(child.Key), Key(temporary++, "@Ordinal"));
        }
        var resourceIds = resources.GroupBy(x => x.LegacyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<ExistingScopeChild>(g), StringComparer.Ordinal);
        var folderIds = folders.GroupBy(x => x.LegacyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<ExistingScopeChild>(g), StringComparer.Ordinal);
        var keptResources = new HashSet<long>();
        var keptFolders = new HashSet<long>();
        var folderKeys = new List<long>();
        var resourceKeys = new List<long>();
        for (int i = 0; i < value.Resources.Count; i++)
        {
            var resource = value.Resources[i];
            if (resourceIds.TryGetValue(resource.ResourceId, out var matches) && matches.TryDequeue(out var old))
            {
                bool changedTarget = old.Kind != (int)resource.Kind || !string.Equals(old.Path, resource.Path, StringComparison.Ordinal);
                await UpdateScopeChildAsync(connection, transaction, StateMaps.Resource, resource, old.Key, i, changedTarget, ct);
                keptResources.Add(old.Key);
                resourceKeys.Add(old.Key);
            }
            else resourceKeys.Add(await InsertAsync(connection, transaction, StateMaps.Resource, resource, ct,
                ("ScopeKey", SqlDbType.BigInt, scope), ("SortOrdinal", SqlDbType.BigInt, (long)i)));
        }
        for (int i = 0; i < value.VirtualFolders.Count; i++)
        {
            var folder = value.VirtualFolders[i];
            long folderKey;
            if (folderIds.TryGetValue(folder.VirtualFolderId, out var matches) && matches.TryDequeue(out var old))
            {
                folderKey = old.Key;
                await UpdateScopeChildAsync(connection, transaction, StateMaps.Folder, folder, folderKey, i, false, ct);
                await ExecuteAsync(connection, transaction, "DELETE FROM surf.VirtualFolderMember WHERE VirtualFolderKey=@Key;", ct, Key(folderKey));
                keptFolders.Add(folderKey);
            }
            else folderKey = await InsertAsync(connection, transaction, StateMaps.Folder, folder, ct,
                ("ScopeKey", SqlDbType.BigInt, scope), ("SortOrdinal", SqlDbType.BigInt, (long)i));
            folderKeys.Add(folderKey);
            for (int m = 0; m < folder.ChildNodeKeys.Count; m++)
                await ExecuteAsync(connection, transaction,
                    "INSERT surf.VirtualFolderMember(ScopeKey, VirtualFolderKey, SortOrdinal, ChildNodeKey) VALUES (@Scope, @Key, @Ordinal, @Node);",
                    ct, Key(scope, "@Scope"), Key(folderKey), Key(m, "@Ordinal"), Text(folder.ChildNodeKeys[m], "@Node"));
        }
        foreach (var old in resources.Where(x => !keptResources.Contains(x.Key)))
            await ExecuteAsync(connection, transaction, "DELETE FROM surf.ScopeResource WHERE ScopeResourceKey=@Key;", ct, Key(old.Key));
        foreach (var old in folders.Where(x => !keptFolders.Contains(x.Key)))
            await ExecuteAsync(connection, transaction, """
DELETE FROM surf.VirtualFolderMember WHERE VirtualFolderKey=@Key;
DELETE FROM surf.VirtualFolder WHERE VirtualFolderKey=@Key;
""", ct, Key(old.Key));
        await WriteFolderRelationshipsAsync(connection, transaction, scope, value, folderKeys, resourceKeys, ct);
    }

    private async Task ScopeChildrenAsync(SqlConnection connection, SqlTransaction transaction, long key, Scope scope, CancellationToken ct)
    {
        var folderKeys = new List<long>();
        var resourceKeys = new List<long>();
        for (int i = 0; i < scope.Resources.Count; i++)
            resourceKeys.Add(await InsertAsync(connection, transaction, StateMaps.Resource, scope.Resources[i], ct,
                ("ScopeKey", SqlDbType.BigInt, key), ("SortOrdinal", SqlDbType.BigInt, (long)i)));
        for (int i = 0; i < scope.VirtualFolders.Count; i++)
        {
            var folder = scope.VirtualFolders[i];
            long folderKey = await InsertAsync(connection, transaction, StateMaps.Folder, folder, ct,
                ("ScopeKey", SqlDbType.BigInt, key), ("SortOrdinal", SqlDbType.BigInt, (long)i));
            folderKeys.Add(folderKey);
            for (int m = 0; m < folder.ChildNodeKeys.Count; m++)
                await ExecuteAsync(connection, transaction,
                    "INSERT surf.VirtualFolderMember(ScopeKey, VirtualFolderKey, SortOrdinal, ChildNodeKey) VALUES (@Scope, @Key, @Ordinal, @Node);",
                    ct, Key(key, "@Scope"), Key(folderKey), Key(m, "@Ordinal"), Text(folder.ChildNodeKeys[m], "@Node"));
        }
        await WriteFolderRelationshipsAsync(connection, transaction, key, scope, folderKeys, resourceKeys, ct);
    }

    public Task<SelectedState<Scope>?> ReadScopeAsync(long scopeKey, CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            var budget = new StateBudget(_limits);
            var selected = await HeadAsync(connection, transaction, StateMaps.Scope, scopeKey, budget, cancellationToken);
            if (selected == null) return null;
            foreach (var (_, value) in await ChildrenAsync(connection, transaction, StateMaps.Resource, "ScopeKey", scopeKey, budget, cancellationToken))
                selected.Value.Resources.Add(value);
            foreach (var (key, value) in await ChildrenAsync(connection, transaction, StateMaps.Folder, "ScopeKey", scopeKey, budget, cancellationToken))
            {
                await using var command = Command(connection, transaction,
                    "SELECT TOP (@Limit) CONVERT(bigint, DATALENGTH(ChildNodeKey)), ChildNodeKey FROM surf.VirtualFolderMember WHERE VirtualFolderKey=@Key ORDER BY SortOrdinal;",
                    Key(key), Key((long)_limits.MaximumRows + 1, "@Limit"));
                using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    budget.Row();
                    value.ChildNodeKeys.Add((await StringAsync(reader, 0, budget, cancellationToken))!);
                }
                selected.Value.VirtualFolders.Add(value);
            }
            return selected;
        }, cancellationToken);

    /// <summary>Explicit second-pass target resolution. Original paths and contextual IDs are never replaced.</summary>
    public async Task<StateToken> ResolveScopeResourceTargetsAsync(SqlConnection connection, SqlTransaction transaction,
        StateToken expected, IReadOnlyList<ScopeResourceTarget> targets, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId);
        Expected(expected, publicationId);
        var copy = targets.ToArray();
        if (copy.Length > _limits.MaximumRows || copy.Select(t => t.ResourceOrdinal).Distinct().Count() != copy.Length ||
            copy.Any(t => t.ResourceOrdinal < 0 || (t.SnapshotKey.HasValue && t.DiagramKey.HasValue)))
            throw new ArgumentException("Invalid target resolution batch.");
        await BumpAsync(connection, transaction, 0, publicationId, cancellationToken);
        await using (var command = Command(connection, transaction,
            "UPDATE surf.Scope SET PublicationId=@Publication OUTPUT INSERTED.ScopeKey WHERE ScopeKey=@Key AND ProfileKey=1 AND Version=@Version;",
            Key(expected.Key), Publication(publicationId), RelationalSession.Parameter("@Version", SqlDbType.Binary, expected.Version, 8)))
        {
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long) throw new StateConflictException("scope");
        }
        foreach (var target in copy)
        {
            await using var command = Command(connection, transaction, """
UPDATE surf.ScopeResource SET SnapshotKey=@Snapshot, DiagramKey=@Diagram
OUTPUT INSERTED.ScopeResourceKey
WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal
  AND (@Snapshot IS NULL OR Kind=2) AND (@Diagram IS NULL OR Kind=3);
""", Key(expected.Key), Key(target.ResourceOrdinal, "@Ordinal"),
                RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, target.SnapshotKey),
                RelationalSession.Parameter("@Diagram", SqlDbType.BigInt, target.DiagramKey));
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long) throw new InvalidDataException("A target resolution does not match a scope resource.");
        }
        return await TokenAsync(connection, transaction, "Scope", "ScopeKey", expected.Key, cancellationToken);
    }

    public async Task<StateToken> ImportScopeSelectionAsync(SqlConnection connection, SqlTransaction transaction,
        int schemaVersion, string? lastActiveScopeId, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId);
        if (lastActiveScopeId != null) new StateBudget(_limits).Text((long)lastActiveScopeId.Length * 2);
        return await ImportUnitAsync(connection, transaction, async () =>
        {
            await ExecuteAsync(connection, transaction, """
INSERT surf.ScopeCatalogueState(ProfileKey, SchemaVersion, PublicationId) VALUES (1, @SchemaVersion, @Publication);
UPDATE surf.UserProfile SET LastActiveScopeId=@ScopeId WHERE ProfileKey=1;
""", cancellationToken, RelationalSession.Parameter("@SchemaVersion", SqlDbType.Int, schemaVersion),
                Text(lastActiveScopeId, "@ScopeId"), Publication(publicationId));
            return await TokenAsync(connection, transaction, "ScopeCatalogueState", "ProfileKey", 1, cancellationToken);
        }, cancellationToken);
    }

    public Task<SelectedState<ScopeSelection>?> ReadScopeSelectionAsync(CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, """
SELECT s.Version, s.PublicationId, s.SchemaVersion, CONVERT(bigint, DATALENGTH(p.LastActiveScopeId)), p.LastActiveScopeId
FROM surf.ScopeCatalogueState s JOIN surf.UserProfile p ON p.ProfileKey=s.ProfileKey WHERE s.ProfileKey=1;
""");
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var token = new StateToken(1, _session.Epoch, (byte[])reader.GetValue(0), reader.GetGuid(1));
            int version = reader.GetInt32(2);
            return new SelectedState<ScopeSelection>(new(version,
                await StringAsync(reader, 3, new StateBudget(_limits), cancellationToken)), token);
        }, cancellationToken);

    public Task<StateToken> SaveScopeSelectionAsync(ScopeSelection selection, StateToken expected, Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        if (expected.Key != 1) throw new ArgumentException("Invalid profile token.");
        if (selection.LastActiveScopeId != null) new StateBudget(_limits).Text((long)selection.LastActiveScopeId.Length * 2);
        return ReadyAsync(async (connection, transaction) =>
        {
            await using (var command = Command(connection, transaction, """
UPDATE surf.ScopeCatalogueState SET SchemaVersion=@SchemaVersion, PublicationId=@Publication OUTPUT INSERTED.ProfileKey WHERE ProfileKey=1 AND Version=@Version;
""", RelationalSession.Parameter("@SchemaVersion", SqlDbType.Int, selection.SchemaVersion), Publication(publicationId),
                RelationalSession.Parameter("@Version", SqlDbType.Binary, expected.Version, 8)))
            {
                using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                if (await command.ExecuteScalarAsync(cancellationToken) is not long) throw new StateConflictException("scope selection");
            }
            await ExecuteAsync(connection, transaction, "UPDATE surf.UserProfile SET LastActiveScopeId=@Id WHERE ProfileKey=1;",
                cancellationToken, Text(selection.LastActiveScopeId, "@Id"));
            return await TokenAsync(connection, transaction, "ScopeCatalogueState", "ProfileKey", 1, cancellationToken);
        }, cancellationToken);
    }
}

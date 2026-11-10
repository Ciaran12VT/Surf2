using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    private static SqlParameter Link(long? key, string name) => RelationalSession.Parameter(name, SqlDbType.BigInt, key);
    private static SqlParameter Resolution(StateLinkResolution status, string name = "@Resolution") =>
        RelationalSession.Parameter(name, SqlDbType.Int, (int)status);

    private async Task WriteRevisionRelationshipsAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, long revision, IReadOnlyList<long> objects, IReadOnlyList<long> workflows,
        IReadOnlyList<long[]> items, CancellationToken ct)
    {
        var plan = StateRelationshipResolver.Workflows(diagram);
        foreach (var binding in plan.Bindings)
            await ExecuteAsync(connection, transaction, """
INSERT surf.DiagramWorkflowBinding(DiagramRevisionKey, DiagramObjectKey, WorkflowKey, WorkflowItemKey, WorkflowResolution, ItemResolution)
VALUES (@Revision, @Object, @Workflow, @Item, @WorkflowResolution, @ItemResolution);
""", ct, Key(revision, "@Revision"), Key(objects[binding.ObjectOrdinal], "@Object"),
                Link(binding.WorkflowOrdinal.HasValue ? workflows[binding.WorkflowOrdinal.Value] : null, "@Workflow"),
                Link(binding.ItemOrdinal.HasValue ? items[binding.WorkflowOrdinal!.Value][binding.ItemOrdinal.Value] : null, "@Item"),
                Resolution(binding.WorkflowResolution, "@WorkflowResolution"), Resolution(binding.ItemResolution, "@ItemResolution"));
        foreach (var marker in plan.Markers)
            await ExecuteAsync(connection, transaction, """
INSERT surf.WorkflowItemMarker(DiagramRevisionKey, WorkflowKey, WorkflowItemKey, MarkerDiagramObjectKey, Resolution)
VALUES (@Revision, @Workflow, @Item, @Object, @Resolution);
""", ct, Key(revision, "@Revision"), Key(workflows[marker.WorkflowOrdinal], "@Workflow"),
                Key(items[marker.WorkflowOrdinal][marker.ItemOrdinal], "@Item"),
                Link(marker.ObjectOrdinal.HasValue ? objects[marker.ObjectOrdinal.Value] : null, "@Object"), Resolution(marker.Resolution));
        await WritePortalTargetsAsync(connection, transaction, diagram, revision, objects, true, ct);
    }

    private async Task WritePortalTargetsAsync(SqlConnection connection, SqlTransaction transaction, DiagramDocument diagram,
        long revision, IReadOnlyList<long> objects, bool insert, CancellationToken ct)
    {
        var logicalTargets = new Dictionary<string, (long? Key, StateLinkResolution Status)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in StateRelationshipResolver.Portals(diagram))
        {
            long? logicalKey = null;
            var status = target.Resolution;
            if (target.LogicalDiagramId != null)
            {
                if (!logicalTargets.TryGetValue(target.LogicalDiagramId, out var match))
                {
                    match = await LogicalTargetAsync(connection, transaction, false, target.LogicalDiagramId, ct);
                    logicalTargets.Add(target.LogicalDiagramId, match);
                }
                (logicalKey, status) = match;
            }
            await using var command = Command(connection, transaction, insert ? """
INSERT surf.DiagramPortalTarget(DiagramRevisionKey, DiagramObjectKey, TargetDiagramKey, TargetRevisionKey, TargetObjectKey, Resolution)
OUTPUT INSERTED.DiagramObjectKey VALUES (@Revision, @Object, @Diagram, @TargetRevision, @TargetObject, @Resolution);
""" : """
UPDATE surf.DiagramPortalTarget SET TargetDiagramKey=@Diagram, TargetRevisionKey=@TargetRevision,
    TargetObjectKey=@TargetObject, Resolution=@Resolution
OUTPUT INSERTED.DiagramObjectKey WHERE DiagramRevisionKey=@Revision AND DiagramObjectKey=@Object;
""", Key(revision, "@Revision"), Key(objects[target.ObjectOrdinal], "@Object"), Link(logicalKey, "@Diagram"),
                Link(target.TargetObjectOrdinal.HasValue ? revision : null, "@TargetRevision"),
                Link(target.TargetObjectOrdinal.HasValue ? objects[target.TargetObjectOrdinal.Value] : null, "@TargetObject"), Resolution(status));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            if (await command.ExecuteScalarAsync(ct) is not long) throw new InvalidDataException("A selected portal relationship is missing.");
        }
    }

    private async Task<(long? Key, StateLinkResolution Status)> LogicalTargetAsync(SqlConnection connection,
        SqlTransaction transaction, bool scope, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return (null, StateLinkResolution.None);
        long? match = null;
        long after = 0;
        string table = scope ? "Scope" : "Diagram";
        string keyColumn = scope ? "ScopeKey" : "DiagramKey";
        string idColumn = scope ? "ScopeId" : "DiagramId";
        while (true)
        {
            await using var command = Command(connection, transaction,
                $"SELECT TOP (256) [{keyColumn}], {StateText.Projection($"[{idColumn}]")} FROM surf.[{table}] " +
                $"WHERE ProfileKey=1 AND [{keyColumn}]>@After" + (scope ? "" : " AND CurrentRevisionKey IS NOT NULL") +
                $" ORDER BY [{keyColumn}];", Key(after, "@After"));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            int rows = 0;
            while (await reader.ReadAsync(ct))
            {
                rows++;
                after = reader.GetInt64(0);
                // Only one identifier is retained; catalogue size is not a selected-aggregate row budget.
                string candidate = (await StringAsync(reader, 1, new StateBudget(_limits), ct))!;
                if (!string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase)) continue;
                if (match.HasValue) return (null, StateLinkResolution.Ambiguous);
                match = after;
            }
            if (rows < 256) return match.HasValue ? (match, StateLinkResolution.Resolved) : (null, StateLinkResolution.Missing);
        }
    }

    private async Task WriteFolderRelationshipsAsync(SqlConnection connection, SqlTransaction transaction,
        long scope, Scope value, IReadOnlyList<long> folders, IReadOnlyList<long> resources, CancellationToken ct)
    {
        foreach (var link in StateRelationshipResolver.Folders(value))
        {
            await using var command = Command(connection, transaction, link.MemberOrdinal.HasValue ? """
UPDATE surf.VirtualFolderMember SET ChildVirtualFolderKey=@Target, ChildScopeResourceKey=@Resource, ChildResourceKind=@Kind, ChildResolution=@Resolution
OUTPUT INSERTED.VirtualFolderKey WHERE ScopeKey=@Scope AND VirtualFolderKey=@Key AND SortOrdinal=@Ordinal;
""" : """
UPDATE surf.VirtualFolder SET ParentVirtualFolderKey=@Target, ParentScopeResourceKey=@Resource, ParentResourceKind=@Kind, ParentResolution=@Resolution
OUTPUT INSERTED.VirtualFolderKey WHERE ScopeKey=@Scope AND VirtualFolderKey=@Key;
""", Key(scope, "@Scope"), Key(folders[link.FolderOrdinal]), Key(link.MemberOrdinal ?? 0, "@Ordinal"),
                Link(link.TargetOrdinal.HasValue ? folders[link.TargetOrdinal.Value] : null, "@Target"),
                Link(link.ResourceOrdinal.HasValue ? resources[link.ResourceOrdinal.Value] : null, "@Resource"),
                RelationalSession.Parameter("@Kind", SqlDbType.Int, link.ResourceOrdinal.HasValue ? 0 : null), Resolution(link.Resolution));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            if (await command.ExecuteScalarAsync(ct) is not long) throw new InvalidDataException("A selected virtual-folder relationship is missing.");
        }
    }

    private async Task ResolveWorkbenchScopeCoreAsync(SqlConnection connection, SqlTransaction transaction, long key,
        string scopeId, CancellationToken ct)
    {
        var (scope, status) = await LogicalTargetAsync(connection, transaction, true, scopeId, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE surf.Workbench SET ResolvedScopeKey=@Scope, ScopeResolution=@Resolution WHERE WorkbenchKey=@Key AND ProfileKey=1;",
            ct, Key(key), Link(scope, "@Scope"), Resolution(status));
    }

    public Task<StateToken> ResolveDiagramReferencesAsync(StateToken expected, Guid publicationId, CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        return ReadyAsync((c,t) => ResolveDiagramReferencesAsync(c,t,expected,publicationId,cancellationToken), cancellationToken);
    }

    /// <summary>Deferred selected-head resolution after all logical diagrams are imported. Does not hydrate other diagrams.</summary>
    public async Task<StateToken> ResolveDiagramReferencesAsync(SqlConnection connection, SqlTransaction transaction,
        StateToken expected, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId);
        Expected(expected, publicationId);
        return await ImportUnitAsync(connection, transaction, async () =>
        {
            await TouchReferencesAsync(connection, transaction, "Diagram", "DiagramKey", expected, publicationId, 1, cancellationToken);
            await using var command = Command(connection, transaction,
                "SELECT CurrentRevisionKey FROM surf.Diagram WHERE DiagramKey=@Key AND ProfileKey=1;", Key(expected.Key));
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long revision)
                throw new InvalidDataException("The selected diagram has no current revision.");
            await ResolveRevisionPortalsAsync(connection, transaction, revision, cancellationToken);
            return await TokenAsync(connection, transaction, "Diagram", "DiagramKey", expected.Key, cancellationToken);
        }, cancellationToken);
    }

    public Task<StateToken> ResolveWorkbenchReferencesAsync(StateToken expected, Guid publicationId, CancellationToken cancellationToken = default)
    {
        Expected(expected, publicationId);
        return ReadyAsync((c,t) => ResolveWorkbenchReferencesAsync(c,t,expected,publicationId,cancellationToken), cancellationToken);
    }

    /// <summary>Deferred scope and embedded-portal resolution; the embedded copy never binds to a current revision.</summary>
    public async Task<StateToken> ResolveWorkbenchReferencesAsync(SqlConnection connection, SqlTransaction transaction,
        StateToken expected, Guid publicationId, CancellationToken cancellationToken = default)
    {
        ImportArguments(connection, transaction, publicationId);
        Expected(expected, publicationId);
        return await ImportUnitAsync(connection, transaction, async () =>
        {
            await TouchReferencesAsync(connection, transaction, "Workbench", "WorkbenchKey", expected, publicationId, 2, cancellationToken);
            string scopeId;
            long? revision;
            await using (var command = Command(connection, transaction,
                "SELECT EmbeddedDiagramRevisionKey, CONVERT(bigint, DATALENGTH(ScopeId)), ScopeId FROM surf.Workbench WHERE WorkbenchKey=@Key AND ProfileKey=1;", Key(expected.Key)))
            {
                using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("The selected workbench is missing.");
                revision = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                scopeId = (await StringAsync(reader, 1, new StateBudget(_limits), cancellationToken))!;
            }
            await ResolveWorkbenchScopeCoreAsync(connection, transaction, expected.Key, scopeId, cancellationToken);
            if (revision.HasValue) await ResolveRevisionPortalsAsync(connection, transaction, revision.Value, cancellationToken);
            return await TokenAsync(connection, transaction, "Workbench", "WorkbenchKey", expected.Key, cancellationToken);
        }, cancellationToken);
    }

    private async Task TouchReferencesAsync(SqlConnection connection, SqlTransaction transaction, string table, string keyColumn,
        StateToken expected, Guid publication, int catalogue, CancellationToken ct)
    {
        await BumpAsync(connection, transaction, catalogue, publication, ct);
        await using var command = Command(connection, transaction,
            $"UPDATE surf.[{table}] SET PublicationId=@Publication OUTPUT INSERTED.[{keyColumn}] " +
            $"WHERE [{keyColumn}]=@Key AND ProfileKey=1 AND Version=@Version;", Key(expected.Key), Publication(publication),
            RelationalSession.Parameter("@Version", SqlDbType.Binary, expected.Version, 8));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new StateConflictException(table);
    }

    private async Task ResolveRevisionPortalsAsync(SqlConnection connection, SqlTransaction transaction, long revision, CancellationToken ct)
    {
        var budget = new StateBudget(_limits);
        var diagram = new DiagramDocument();
        await using (var command = Command(connection, transaction,
            "SELECT CONVERT(bigint, DATALENGTH(DiagramId)), DiagramId FROM surf.DiagramRevision WHERE DiagramRevisionKey=@Key;", Key(revision)))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidDataException("The selected diagram revision is missing.");
            diagram.DiagramId = (await StringAsync(reader, 0, budget, ct))!;
        }
        var keys = new List<long>();
        await using (var command = Command(connection, transaction, """
SELECT TOP (@Limit) DiagramObjectKey, ObjectType, CONVERT(bigint, DATALENGTH(Id)), Id,
    CONVERT(bigint, DATALENGTH(PairedPortalDiagramId)), PairedPortalDiagramId,
    CONVERT(bigint, DATALENGTH(PairedPortalObjectId)), PairedPortalObjectId
FROM surf.DiagramObject WHERE DiagramRevisionKey=@Key ORDER BY SortOrdinal, DiagramObjectKey;
""", Key(revision), Key((long)_limits.MaximumRows + 1, "@Limit")))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Row();
                keys.Add(reader.GetInt64(0));
                var type = (DiagramObjectType)reader.GetInt32(1);
                if (!Enum.IsDefined(type)) throw new InvalidDataException("The selected portal projection has an unknown object type.");
                diagram.Objects.Add(new DiagramObjectSnapshot {
                    ObjectType=type, Id=(await StringAsync(reader, 2, budget, ct))!,
                    PairedPortalDiagramId=(await StringAsync(reader, 4, budget, ct))!,
                    PairedPortalObjectId=(await StringAsync(reader, 6, budget, ct))! });
            }
        }
        await WritePortalTargetsAsync(connection, transaction, diagram, revision, keys, false, ct);
    }

    public Task<DiagramRelationships> ReadDiagramRelationshipsAsync(long revisionKey, CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            await RequireRelationshipOwnerAsync(connection, transaction, "DiagramRevision", "DiagramRevisionKey", revisionKey, cancellationToken);
            var budget = new StateBudget(_limits);
            var bindings = new List<DiagramWorkflowBinding>();
            var markers = new List<WorkflowItemMarker>();
            var portals = new List<DiagramPortalTarget>();
            await using var command = Command(connection, transaction, """
SELECT TOP (@Limit) DiagramObjectKey, WorkflowKey, WorkflowItemKey, WorkflowResolution, ItemResolution
FROM surf.DiagramWorkflowBinding WHERE DiagramRevisionKey=@Key ORDER BY DiagramObjectKey;
SELECT TOP (@Limit) WorkflowKey, WorkflowItemKey, MarkerDiagramObjectKey, Resolution
FROM surf.WorkflowItemMarker WHERE DiagramRevisionKey=@Key ORDER BY WorkflowKey, WorkflowItemKey;
SELECT TOP (@Limit) DiagramObjectKey, TargetDiagramKey, TargetRevisionKey, TargetObjectKey, Resolution
FROM surf.DiagramPortalTarget WHERE DiagramRevisionKey=@Key ORDER BY DiagramObjectKey;
""", Key(revisionKey), Key((long)_limits.MaximumRows + 1, "@Limit"));
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Row();
                bindings.Add(new(reader.GetInt64(0), NullableKey(reader,1), NullableKey(reader,2), ReadResolution(reader,3), ReadResolution(reader,4)));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Row();
                markers.Add(new(reader.GetInt64(0), reader.GetInt64(1), NullableKey(reader,2), ReadResolution(reader,3)));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Row();
                portals.Add(new(reader.GetInt64(0), NullableKey(reader,1), NullableKey(reader,2), NullableKey(reader,3), ReadResolution(reader,4)));
            }
            return new DiagramRelationships(revisionKey, bindings, markers, portals);
        }, cancellationToken);

    public Task<ScopeFolderRelationships> ReadScopeFolderRelationshipsAsync(long scopeKey, CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            await RequireRelationshipOwnerAsync(connection, transaction, "Scope", "ScopeKey", scopeKey, cancellationToken);
            var budget = new StateBudget(_limits);
            var parents = new List<VirtualFolderParent>();
            var members = new List<VirtualFolderChild>();
            await using var command = Command(connection, transaction, """
SELECT TOP (@Limit) VirtualFolderKey, ParentVirtualFolderKey, ParentScopeResourceKey, ParentResolution
FROM surf.VirtualFolder WHERE ScopeKey=@Key ORDER BY SortOrdinal, VirtualFolderKey;
SELECT TOP (@Limit) m.VirtualFolderKey, m.SortOrdinal, m.ChildVirtualFolderKey, m.ChildScopeResourceKey, m.ChildResolution
FROM surf.VirtualFolderMember m JOIN surf.VirtualFolder f ON f.ScopeKey=m.ScopeKey AND f.VirtualFolderKey=m.VirtualFolderKey
WHERE m.ScopeKey=@Key ORDER BY f.SortOrdinal, m.SortOrdinal;
""", Key(scopeKey), Key((long)_limits.MaximumRows + 1, "@Limit"));
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Row();
                parents.Add(new(reader.GetInt64(0), NullableKey(reader,1), NullableKey(reader,2), ReadResolution(reader,3)));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Row();
                members.Add(new(reader.GetInt64(0), reader.GetInt64(1), NullableKey(reader,2), NullableKey(reader,3), ReadResolution(reader,4)));
            }
            return new ScopeFolderRelationships(scopeKey, parents, members);
        }, cancellationToken);

    public Task<WorkbenchScopeTarget?> ReadWorkbenchScopeTargetAsync(long workbenchKey, CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction,
                "SELECT Version, PublicationId, ResolvedScopeKey, ScopeResolution FROM surf.Workbench WHERE WorkbenchKey=@Key AND ProfileKey=1;", Key(workbenchKey));
            using var cancel = RelationalSession.CancelCommand(command, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var token = new StateToken(workbenchKey, _session.Epoch, (byte[])reader.GetValue(0), reader.GetGuid(1));
            return new WorkbenchScopeTarget(token, NullableKey(reader,2), ReadResolution(reader,3));
        }, cancellationToken);

    private static async Task RequireRelationshipOwnerAsync(SqlConnection connection, SqlTransaction transaction,
        string table, string keyColumn, long key, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            $"SELECT [{keyColumn}] FROM surf.[{table}] WHERE [{keyColumn}]=@Key;", Key(key));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new KeyNotFoundException("The selected relationship owner is missing.");
    }

    private static long? NullableKey(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static StateLinkResolution ReadResolution(SqlDataReader reader, int ordinal)
    {
        var value = (StateLinkResolution)reader.GetInt32(ordinal);
        return Enum.IsDefined(value) ? value : throw new InvalidDataException("An owned relationship has an unknown resolution state.");
    }
}

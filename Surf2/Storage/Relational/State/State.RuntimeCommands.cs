using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Storage.Relational.State;

public enum RuntimeStateCatalogue { Scopes, Diagrams, Workbenches }

public sealed class RuntimeCatalogueToken
{
    private readonly byte[] _version;
    internal RuntimeCatalogueToken(Guid epoch, RuntimeStateCatalogue kind, byte[] version)
    { Epoch = epoch; Kind = kind; _version = version.ToArray(); }
    public Guid Epoch { get; }
    public RuntimeStateCatalogue Kind { get; }
    public byte[] Version => _version.ToArray();
}

public sealed record RuntimeDiagramPublication(StateToken Diagram, StateToken Scope);
public sealed record RuntimeWorkbenchTarget(WorkbenchSummary? Summary, RuntimeCatalogueToken Catalogue);
public sealed record RuntimeWorkbenchCursor(Guid Epoch, DateTimeOffset Updated, long Ordinal, long Key, byte[] Generation);
public sealed record RuntimeWorkbenchPage(IReadOnlyList<WorkbenchSummary> Items, RuntimeWorkbenchCursor? Next);
public sealed record PreferenceChildChange<T>(long? Key, T? Value) where T : class;
public sealed record PreferenceImageMove(long Key, long AdjacentKey);
public sealed record RuntimePortalTarget(long? DiagramKey, long? RevisionKey, long? ObjectOrdinal, StateLinkResolution Resolution);
public sealed record RuntimeDiagramPairPublication(StateToken First, StateToken Second);
public sealed record RuntimeImageRule(long Key, string Id, string Name, string NameRegex, string ContentRegex,
    string LegacyRegex, string MatchTarget, string ResourceTypeFilter);
public sealed record RuntimeImageRuleCursor(StateToken Owner, long EffectiveOrder, long Ordinal, long Key);
public sealed record RuntimeImageRulePage(IReadOnlyList<RuntimeImageRule> Items, RuntimeImageRuleCursor? Next);
public sealed record RuntimeScopeResourceOrigin(Guid Epoch, string ScopeCatalogueVersion, string SnapshotCatalogueVersion,
    string DiagramCatalogueVersion, long? SourceScopeKey, long? SourceScopeResourceKey, long? SnapshotKey, long? DiagramKey);
public sealed record RuntimeScopeResourceRemovalPublication(StateToken Scope, StateToken? Diagram);
public sealed record RuntimeScopeMergePublication(StateToken Token, Scope Scope);

public sealed partial class RelationalStateStore
{
    public Task<RuntimeCatalogueToken> ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue kind, CancellationToken ct = default) =>
        ReadyAsync((c, t) => RuntimeCatalogueAsync(c, t, kind, ct), ct);

    private async Task<RuntimeCatalogueToken> RuntimeCatalogueAsync(SqlConnection c, SqlTransaction t,
        RuntimeStateCatalogue kind, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        await using var command = Command(c, t,
            "SELECT Version FROM surf.StateCatalogueGeneration WITH (UPDLOCK,HOLDLOCK) WHERE ProfileKey=1 AND Kind=@Kind;",
            RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)kind));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        return new(_session.Epoch, kind, await command.ExecuteScalarAsync(ct) as byte[] ?? new byte[8]);
    }

    private async Task RequireRuntimeCatalogueAsync(SqlConnection c, SqlTransaction t, RuntimeCatalogueToken expected,
        RuntimeStateCatalogue kind, CancellationToken ct)
    {
        if (expected.Epoch != _session.Epoch || expected.Kind != kind) throw new ArgumentException("Invalid catalogue owner/epoch.");
        var actual = await RuntimeCatalogueAsync(c, t, kind, ct);
        if (!actual.Version.AsSpan().SequenceEqual(expected.Version)) throw new StateConflictException("catalogue");
    }

    private async Task<long> NextRuntimeOrdinalAsync(SqlConnection c, SqlTransaction t, RuntimeStateCatalogue kind, CancellationToken ct)
    {
        string table = kind switch { RuntimeStateCatalogue.Scopes => "Scope", RuntimeStateCatalogue.Diagrams => "Diagram",
            RuntimeStateCatalogue.Workbenches => "Workbench", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        await using var command = Command(c, t, $"SELECT COALESCE(MAX(SortOrdinal),CONVERT(bigint,-1)) FROM surf.[{table}] WHERE ProfileKey=1;");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        return checked((long)(await command.ExecuteScalarAsync(ct))! + 1);
    }

    private async Task RequireNewIdentityAsync(SqlConnection c, SqlTransaction t, RuntimeStateCatalogue kind,
        string id, string? name, CancellationToken ct)
    {
        string table = kind switch { RuntimeStateCatalogue.Scopes => "Scope", RuntimeStateCatalogue.Diagrams => "Diagram",
            RuntimeStateCatalogue.Workbenches => "Workbench", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        string column = kind switch { RuntimeStateCatalogue.Scopes => "ScopeId", RuntimeStateCatalogue.Diagrams => "DiagramId", _ => "WorkbenchId" };
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A new owner requires an explicit nonempty identity.");
        await using var command = Command(c, t, $"SELECT TOP (@Limit) CONVERT(bigint,DATALENGTH([{column}])),[{column}],CONVERT(bigint,DATALENGTH(Name)),Name FROM surf.[{table}] WHERE ProfileKey=1 ORDER BY SortOrdinal;",
            Key((long)_limits.MaximumRows + 1, "@Limit"));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var budget = new StateBudget(_limits);
        while (await reader.ReadAsync(ct))
        {
            budget.Row();
            string existingId = (await StringAsync(reader, 0, budget, ct))!;
            string existingName = (await StringAsync(reader, 2, budget, ct))!;
            if (string.Equals(existingId, id, StringComparison.OrdinalIgnoreCase))
                throw new StateConflictException("new owner identity");
            if (name != null && string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That name is already in use.");
        }
    }

    public Task<StateToken> CreateScopeExpectedAsync(Scope value, RuntimeCatalogueToken expectedCatalogue,
        Guid publication, CancellationToken ct = default)
    {
        _session.RejectValidationWrite();
        if (publication == Guid.Empty) throw new ArgumentException("A publication identity is required.");
        var copy = StateCopies.Scope(value, new(_limits));
        return ReadyAsync(async (c, t) =>
        {
            await RequireRuntimeCatalogueAsync(c, t, expectedCatalogue, RuntimeStateCatalogue.Scopes, ct);
            await RequireNewIdentityAsync(c, t, RuntimeStateCatalogue.Scopes, copy.ScopeId, copy.Name, ct);
            return await InsertScopeAsync(c, t, copy, await NextRuntimeOrdinalAsync(c, t, RuntimeStateCatalogue.Scopes, ct), publication, ct);
        }, ct);
    }

    /// <summary>Publishes one selected scope and an explicitly chosen snapshot membership atomically.</summary>
    public Task<StateToken> SaveScopeWithSnapshotTargetAsync(Scope value, StateToken expectedScope, int resourceOrdinal,
        SnapshotRuntimeToken expectedSnapshot, Guid publication, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentNullException.ThrowIfNull(value);
        if (expectedSnapshot.Epoch != _session.Epoch || resourceOrdinal < 0 || resourceOrdinal >= value.Resources.Count)
            throw new ArgumentException("Invalid snapshot owner/epoch or scope occurrence.");
        string originalId = value.Resources[resourceOrdinal].Path;
        return SaveRuntimeScopeTargetAsync(value, expectedScope, resourceOrdinal, ResourceKind.DatabaseSnapshot,
            originalId, expectedSnapshot.SnapshotKey, publication, async (c, t) =>
            {
                await using var command = Command(c, t, """
SELECT SnapshotKey FROM surf.DatabaseSnapshot WITH (UPDLOCK,HOLDLOCK)
WHERE SnapshotKey=@Target AND UserKey=1 AND IsPublished=1 AND RowVersion=@TargetVersion
  AND DATALENGTH(OriginalSnapshotId)=DATALENGTH(@Original)
  AND OriginalSnapshotId COLLATE Latin1_General_100_BIN2=@Original COLLATE Latin1_General_100_BIN2;
""", Key(expectedSnapshot.SnapshotKey, "@Target"), Text(originalId, "@Original"),
                    RelationalSession.Parameter("@TargetVersion", SqlDbType.Binary, expectedSnapshot.Version, 8));
                using var cancel = RelationalSession.CancelCommand(command, ct);
                if (await command.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("selected snapshot membership");
            }, ct);
    }

    /// <summary>Publishes one selected scope and an explicitly chosen diagram membership atomically.</summary>
    public Task<StateToken> SaveScopeWithDiagramTargetAsync(Scope value, StateToken expectedScope, int resourceOrdinal,
        DiagramSummary expectedDiagram, Guid publication, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedDiagram);
        Expected(expectedDiagram.Token, publication);
        return SaveRuntimeScopeTargetAsync(value, expectedScope, resourceOrdinal, ResourceKind.Diagram,
            expectedDiagram.DiagramId, expectedDiagram.Token.Key, publication, async (c, t) =>
            {
                // StateMaps.Diagram describes immutable DiagramRevision scalars, not the logical owner head.
                await using var command = Command(c, t, """
SELECT DiagramKey FROM surf.Diagram WITH (UPDLOCK,HOLDLOCK)
WHERE DiagramKey=@Target AND ProfileKey=1 AND Version=@TargetVersion AND PublicationId=@TargetPublication AND CurrentRevisionKey=@Revision
  AND DATALENGTH(DiagramId)=DATALENGTH(@Original)
  AND DiagramId COLLATE Latin1_General_100_BIN2=@Original COLLATE Latin1_General_100_BIN2;
""", Key(expectedDiagram.Token.Key, "@Target"), Key(expectedDiagram.RevisionKey, "@Revision"), Text(expectedDiagram.DiagramId, "@Original"),
                    RelationalSession.Parameter("@TargetVersion", SqlDbType.Binary, expectedDiagram.Token.Version, 8),
                    RelationalSession.Parameter("@TargetPublication", SqlDbType.UniqueIdentifier, expectedDiagram.Token.PublicationId));
                using var cancel = RelationalSession.CancelCommand(command, ct);
                if (await command.ExecuteScalarAsync(ct) is not long)
                    throw new StateConflictException("selected diagram membership");
            }, ct);
    }

    private Task<StateToken> SaveRuntimeScopeTargetAsync(Scope value, StateToken expectedScope, int ordinal,
        ResourceKind kind, string originalId, long targetKey, Guid publication,
        Func<SqlConnection, SqlTransaction, Task> validateTarget, CancellationToken ct)
    {
        Expected(expectedScope, publication);
        var copy = StateCopies.Scope(value, new(_limits));
        if (ordinal < 0 || ordinal >= copy.Resources.Count || copy.Resources[ordinal].Kind != kind ||
            !string.Equals(copy.Resources[ordinal].Path, originalId, StringComparison.Ordinal))
            throw new ArgumentException("The selected typed target disagrees with its scope resource occurrence.");
        return ReadyAsync(async (c, t) =>
        {
            await validateTarget(c, t);
            await BumpAsync(c, t, 0, publication, ct);
            await UpdateAsync(c, t, StateMaps.Scope, copy, expectedScope, publication, ct);
            await ReplaceScopeChildrenAsync(c, t, expectedScope.Key, copy, ct);
            await using var command = Command(c, t, """
UPDATE surf.ScopeResource SET SnapshotKey=@Snapshot, DiagramKey=@Diagram OUTPUT INSERTED.ScopeResourceKey
WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal AND Kind=@Kind;
""", Key(expectedScope.Key), Key(ordinal, "@Ordinal"), RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)kind),
                RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, kind == ResourceKind.DatabaseSnapshot ? targetKey : null),
                RelationalSession.Parameter("@Diagram", SqlDbType.BigInt, kind == ResourceKind.Diagram ? targetKey : null));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            if (await command.ExecuteScalarAsync(ct) is not long) throw new InvalidDataException("The selected scope occurrence is missing.");
            return await TokenAsync(c, t, "Scope", "ScopeKey", expectedScope.Key, ct);
        }, ct);
    }

    /// <summary>Appends one catalogue-selected membership; source headers and typed targets are revalidated atomically.</summary>
    public Task<StateToken> AddScopeResourceFromCatalogueAsync(Scope value, StateToken expectedScope, ScopedResource resource,
        RuntimeScopeResourceOrigin origin, Guid publication, CancellationToken ct = default)
    {
        Expected(expectedScope, publication);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(resource);
        if (origin.Epoch != _session.Epoch || origin.SourceScopeKey.HasValue != origin.SourceScopeResourceKey.HasValue ||
            origin.SourceScopeKey is <= 0 || origin.SourceScopeResourceKey is <= 0 || origin.SnapshotKey is <= 0 || origin.DiagramKey is <= 0 ||
            origin.SnapshotKey.HasValue && origin.DiagramKey.HasValue ||
            resource.Kind == ResourceKind.DatabaseSnapshot && !origin.SnapshotKey.HasValue ||
            resource.Kind == ResourceKind.Diagram && !origin.DiagramKey.HasValue ||
            origin.SnapshotKey.HasValue && resource.Kind != ResourceKind.DatabaseSnapshot ||
            origin.DiagramKey.HasValue && resource.Kind != ResourceKind.Diagram ||
            !origin.SourceScopeKey.HasValue && !origin.SnapshotKey.HasValue && !origin.DiagramKey.HasValue)
            throw new ArgumentException("Invalid catalogue source/target context.");
        byte[] Version(string value) => value.Length == 16 ? Convert.FromHexString(value) : throw new ArgumentException("Invalid catalogue revision.");
        byte[] scopes = Version(origin.ScopeCatalogueVersion), snapshots = Version(origin.SnapshotCatalogueVersion), diagrams = Version(origin.DiagramCatalogueVersion);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Scope(value, budget); var member = StateMaps.Resource.Copy(resource, budget);
        int ordinal = copy.Resources.Count; copy.Resources.Add(member);
        return ReadyAsync(async (c, t) =>
        {
            await using (var heads = Command(c, t, """
SELECT COALESCE(s.Version,CONVERT(binary(8),0)),h.RowVersion,COALESCE(d.Version,CONVERT(binary(8),0))
FROM surf.SnapshotCatalogueHead h LEFT JOIN surf.StateCatalogueGeneration s ON s.ProfileKey=h.UserKey AND s.Kind=0
LEFT JOIN surf.StateCatalogueGeneration d ON d.ProfileKey=h.UserKey AND d.Kind=1 WHERE h.UserKey=1;
"""))
            {
                using var cancel = RelationalSession.CancelCommand(heads, ct);
                await using var reader = await heads.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || !scopes.AsSpan().SequenceEqual((byte[])reader.GetValue(0)) ||
                    !snapshots.AsSpan().SequenceEqual((byte[])reader.GetValue(1)) || !diagrams.AsSpan().SequenceEqual((byte[])reader.GetValue(2)))
                    throw new StateConflictException("resource catalogue");
            }
            if (origin.SourceScopeKey.HasValue)
            {
                await using var source = Command(c, t, """
SELECT r.Kind,r.IncludeChildren,r.SnapshotKey,r.DiagramKey,CONVERT(bigint,DATALENGTH(r.Path)),r.Path,
 CONVERT(bigint,DATALENGTH(r.DisplayNameOverride)),r.DisplayNameOverride,CONVERT(bigint,DATALENGTH(d.Text)),d.Text
FROM surf.ScopeResource r JOIN surf.Scope s ON s.ScopeKey=r.ScopeKey
JOIN surf.TextContent d ON d.ContentKey=r.DetailsOverrideContentKey
WHERE r.ScopeKey=@SourceScope AND r.ScopeResourceKey=@SourceResource AND s.ProfileKey=1;
""", Key(origin.SourceScopeKey.Value, "@SourceScope"), Key(origin.SourceScopeResourceKey!.Value, "@SourceResource"));
                using var cancel = RelationalSession.CancelCommand(source, ct);
                await using var reader = await source.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                if (!await reader.ReadAsync(ct)) throw new StateConflictException("source scope membership");
                int kind = reader.GetInt32(0); bool children = reader.GetBoolean(1);
                long? snapshot = NullableKey(reader, 2), diagram = NullableKey(reader, 3);
                string path = (await StringAsync(reader, 4, budget, ct))!, alias = (await StringAsync(reader, 6, budget, ct))!,
                    details = (await StringAsync(reader, 8, budget, ct))!;
                if (kind != (int)member.Kind || children != member.IncludeChildren || snapshot != origin.SnapshotKey || diagram != origin.DiagramKey ||
                    path != member.Path || alias != member.DisplayNameOverride || details != member.DetailsOverride)
                    throw new StateConflictException("source scope membership");
            }
            if (origin.SnapshotKey.HasValue || origin.DiagramKey.HasValue)
            {
                string sql = origin.SnapshotKey.HasValue ? """
SELECT SnapshotKey FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Target AND UserKey=1 AND IsPublished=1
AND DATALENGTH(OriginalSnapshotId)=DATALENGTH(@Original) AND OriginalSnapshotId COLLATE Latin1_General_100_BIN2=@Original COLLATE Latin1_General_100_BIN2;
""" : """
SELECT DiagramKey FROM surf.Diagram WHERE DiagramKey=@Target AND ProfileKey=1 AND CurrentRevisionKey IS NOT NULL
AND DATALENGTH(DiagramId)=DATALENGTH(@Original) AND DiagramId COLLATE Latin1_General_100_BIN2=@Original COLLATE Latin1_General_100_BIN2;
""";
                await using var target = Command(c, t, sql, Key((origin.SnapshotKey ?? origin.DiagramKey)!.Value, "@Target"), Text(member.Path, "@Original"));
                using var cancel = RelationalSession.CancelCommand(target, ct);
                if (await target.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("selected resource target");
            }
            await BumpAsync(c, t, 0, publication, ct);
            await UpdateAsync(c, t, StateMaps.Scope, copy, expectedScope, publication, ct);
            await ReplaceScopeChildrenAsync(c, t, expectedScope.Key, copy, ct);
            await ExecuteAsync(c, t, "UPDATE surf.ScopeResource SET SnapshotKey=@Snapshot,DiagramKey=@Diagram WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal;",
                ct, Key(expectedScope.Key), Key(ordinal, "@Ordinal"), RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, origin.SnapshotKey),
                RelationalSession.Parameter("@Diagram", SqlDbType.BigInt, origin.DiagramKey));
            return await TokenAsync(c, t, "Scope", "ScopeKey", expectedScope.Key, ct);
        }, ct);
    }

    /// <summary>Removes one exact membership and optionally publishes one selected diagram, never saved workbench copies.</summary>
    public Task<RuntimeScopeResourceRemovalPublication> RemoveScopeResourceExpectedAsync(Scope value, StateToken expectedScope,
        int resourceOrdinal, long scopeResourceKey, DiagramState? selectedDiagram, StateToken? expectedDiagram,
        Guid publication, CancellationToken ct = default)
    {
        Expected(expectedScope, publication);
        if ((selectedDiagram == null) != (expectedDiagram == null)) throw new ArgumentException("A selected diagram requires its expected owner token.");
        if (expectedDiagram != null) Expected(expectedDiagram, publication);
        var budget = new StateBudget(_limits); var copy = StateCopies.Scope(value, budget);
        if (resourceOrdinal < 0 || resourceOrdinal >= copy.Resources.Count || scopeResourceKey < 1) throw new ArgumentException("Invalid selected membership.");
        var removed = copy.Resources[resourceOrdinal]; copy.Resources.RemoveAt(resourceOrdinal);
        DiagramState? graph = null;
        if (selectedDiagram != null)
        {
            var document = StateCopies.Diagram(selectedDiagram.Document, budget);
            var pasted = StateCopies.PastedImages(document, selectedDiagram.PastedImages, budget);
            graph = new(document, pasted, StateCopies.FallbackImages(document, pasted, selectedDiagram.PastedImageFallbacks, budget));
        }
        return ReadyAsync(async (c, t) =>
        {
            await using (var membership = Command(c, t, """
SELECT ScopeResourceKey FROM surf.ScopeResource WHERE ScopeResourceKey=@Resource AND ScopeKey=@Key AND SortOrdinal=@Ordinal AND Kind=@Kind
AND DATALENGTH(ResourceId)=DATALENGTH(@OriginalId) AND ResourceId COLLATE Latin1_General_100_BIN2=@OriginalId COLLATE Latin1_General_100_BIN2
AND DATALENGTH(Path)=DATALENGTH(@Path) AND Path COLLATE Latin1_General_100_BIN2=@Path COLLATE Latin1_General_100_BIN2;
""", Key(expectedScope.Key), Key(scopeResourceKey, "@Resource"), Key(resourceOrdinal, "@Ordinal"),
                RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)removed.Kind), Text(removed.ResourceId, "@OriginalId"), Text(removed.Path, "@Path")))
            {
                using var cancel = RelationalSession.CancelCommand(membership, ct);
                if (await membership.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("selected scope membership");
            }
            await BumpAsync(c, t, 0, publication, ct);
            await UpdateAsync(c, t, StateMaps.Scope, copy, expectedScope, publication, ct);
            // Delete the exact key first; raw duplicate IDs must not transfer its key to a different surviving occurrence.
            await ExecuteAsync(c, t, """
UPDATE surf.VirtualFolder SET ParentScopeResourceKey=NULL,ParentResourceKind=NULL,ParentResolution=2 WHERE ScopeKey=@Key AND ParentScopeResourceKey=@Resource;
UPDATE surf.VirtualFolderMember SET ChildScopeResourceKey=NULL,ChildResourceKind=NULL,ChildResolution=2 WHERE ScopeKey=@Key AND ChildScopeResourceKey=@Resource;
DELETE FROM surf.ScopeResource WHERE ScopeKey=@Key AND ScopeResourceKey=@Resource;
""", ct, Key(expectedScope.Key), Key(scopeResourceKey, "@Resource"));
            await ReplaceScopeChildrenAsync(c, t, expectedScope.Key, copy, ct);
            StateToken? diagram = null;
            if (graph != null)
            {
                diagram = await PublishRuntimeDiagramAsync(c, t, graph, expectedDiagram!, publication, budget, ct);
                await BumpAsync(c, t, 1, publication, ct);
            }
            return new RuntimeScopeResourceRemovalPublication(await TokenAsync(c, t, "Scope", "ScopeKey", expectedScope.Key, ct), diagram);
        }, ct);
    }

    /// <summary>Merges one selected source graph, with fresh membership IDs and preserved typed targets.</summary>
    public Task<RuntimeScopeMergePublication> MergeScopeExpectedAsync(Scope target, StateToken expectedTarget,
        StateToken expectedSource, Guid publication, CancellationToken ct = default)
    {
        Expected(expectedTarget, publication); Expected(expectedSource, publication);
        if (expectedTarget.Key == expectedSource.Key) throw new ArgumentException("A scope cannot be merged into itself.");
        var targetCopy = StateCopies.Scope(target, new(_limits));
        return ReadyAsync(async (c, t) =>
        {
            var budget = new StateBudget(_limits);
            var source = await HeadAsync(c, t, StateMaps.Scope, expectedSource.Key, budget, ct);
            if (source == null || !SameRuntimeToken(source.Token, expectedSource)) throw new StateConflictException("merge source scope");
            foreach (var (_, resource) in await ChildrenAsync(c, t, StateMaps.Resource, "ScopeKey", expectedSource.Key, budget, ct))
                source.Value.Resources.Add(resource);
            foreach (var (key, folder) in await ChildrenAsync(c, t, StateMaps.Folder, "ScopeKey", expectedSource.Key, budget, ct))
            {
                await using var command = Command(c, t,
                    "SELECT TOP (@Limit) CONVERT(bigint,DATALENGTH(ChildNodeKey)),ChildNodeKey FROM surf.VirtualFolderMember WHERE VirtualFolderKey=@Key ORDER BY SortOrdinal;",
                    Key(key), Key((long)_limits.MaximumRows + 1, "@Limit"));
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct)) { budget.Row(); folder.ChildNodeKeys.Add((await StringAsync(reader, 0, budget, ct))!); }
                source.Value.VirtualFolders.Add(folder);
            }
            var merged = StateScopeMerge.Prepare(targetCopy, source.Value, _limits);
            var targets = new List<(long Ordinal, long? Snapshot, long? Diagram)>();
            await using (var command = Command(c, t,
                "SELECT TOP (@Limit) SortOrdinal,SnapshotKey,DiagramKey FROM surf.ScopeResource WHERE ScopeKey=@Key ORDER BY SortOrdinal;",
                Key(expectedSource.Key), Key((long)_limits.MaximumRows + 1, "@Limit")))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    budget.Row(); long ordinal = reader.GetInt64(0);
                    if (ordinal != targets.Count) throw new InvalidDataException("The merge source has noncontiguous resource ordering.");
                    targets.Add((ordinal, NullableKey(reader, 1), NullableKey(reader, 2)));
                }
            }
            if (targets.Count != source.Value.Resources.Count) throw new InvalidDataException("The merge source membership changed.");
            await BumpAsync(c, t, 0, publication, ct);
            await UpdateAsync(c, t, StateMaps.Scope, merged, expectedTarget, publication, ct);
            await ReplaceScopeChildrenAsync(c, t, expectedTarget.Key, merged, ct);
            foreach (var item in targets)
                await ExecuteAsync(c, t, "UPDATE surf.ScopeResource SET SnapshotKey=@Snapshot,DiagramKey=@Diagram WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal;",
                    ct, Key(expectedTarget.Key), Key(checked(targetCopy.Resources.Count + item.Ordinal), "@Ordinal"),
                    RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, item.Snapshot), RelationalSession.Parameter("@Diagram", SqlDbType.BigInt, item.Diagram));
            return new RuntimeScopeMergePublication(await TokenAsync(c, t, "Scope", "ScopeKey", expectedTarget.Key, ct), merged);
        }, ct);
    }

    /// <summary>Explicit create only: publishes a new graph and its selected scope membership together.</summary>
    public Task<RuntimeDiagramPublication> CreateDiagramInScopeAsync(DiagramState value, Scope scope,
        StateToken expectedScope, RuntimeCatalogueToken expectedCatalogue, Guid publication, CancellationToken ct = default)
    {
        Expected(expectedScope, publication);
        var budget = new StateBudget(_limits);
        var diagram = StateCopies.Diagram(value.Document, budget);
        var pasted = StateCopies.PastedImages(diagram, value.PastedImages, budget);
        var fallback = StateCopies.FallbackImages(diagram, pasted, value.PastedImageFallbacks, budget);
        var membership = StateCopies.Scope(scope, new(_limits));
        var matches = membership.Resources.Select((r, i) => (r, i)).Where(x => x.r.Kind == ResourceKind.Diagram &&
            string.Equals(x.r.Path, diagram.DiagramId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ArgumentException("A new diagram must have exactly one explicit selected scope membership.");
        int ordinal = matches[0].i;
        return ReadyAsync(async (c, t) =>
        {
            await RequireRuntimeCatalogueAsync(c, t, expectedCatalogue, RuntimeStateCatalogue.Diagrams, ct);
            await RequireNewIdentityAsync(c, t, RuntimeStateCatalogue.Diagrams, diagram.DiagramId, null, ct);
            await UpdateAsync(c, t, StateMaps.Scope, membership, expectedScope, publication, ct);
            var saved = await InsertDiagramAsync(c, t, diagram, pasted, fallback,
                await NextRuntimeOrdinalAsync(c, t, RuntimeStateCatalogue.Diagrams, ct), publication, budget, ct);
            await ReplaceScopeChildrenAsync(c, t, expectedScope.Key, membership, ct);
            await ExecuteAsync(c, t, "UPDATE surf.ScopeResource SET DiagramKey=@Diagram WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal AND Kind=3;",
                ct, Key(expectedScope.Key), Key(saved.Key, "@Diagram"), Key(ordinal, "@Ordinal"));
            await BumpAsync(c, t, 0, publication, ct);
            return new RuntimeDiagramPublication(saved, await TokenAsync(c, t, "Scope", "ScopeKey", expectedScope.Key, ct));
        }, ct);
    }

    public Task<StateToken> CreateWorkbenchExpectedAsync(WorkbenchAggregate value, RuntimeCatalogueToken expectedCatalogue,
        StateToken expectedScope, Guid publication, CancellationToken ct = default)
    {
        Expected(expectedScope, publication);
        var budget = new StateBudget(_limits);
        var copy = StateCopies.Workbench(value.Workbench, budget);
        var pasted = StateCopies.PastedImages(copy.ActiveDiagramSnapshot, value.PastedImages, budget);
        var fallback = StateCopies.FallbackImages(copy.ActiveDiagramSnapshot, pasted, value.PastedImageFallbacks, budget);
        return ReadyAsync(async (c, t) =>
        {
            await RequireRuntimeCatalogueAsync(c, t, expectedCatalogue, RuntimeStateCatalogue.Workbenches, ct);
            var scope = await TokenAsync(c, t, "Scope", "ScopeKey", expectedScope.Key, ct);
            if (!SameRuntimeToken(scope, expectedScope)) throw new StateConflictException("workbench scope");
            var scopeHead = await HeadAsync(c, t, StateMaps.Scope, expectedScope.Key, new(_limits), ct);
            if (scopeHead == null || !string.Equals(scopeHead.Value.ScopeId, copy.ScopeId, StringComparison.Ordinal))
                throw new ArgumentException("The workbench's original scope ID disagrees with its selected owner.");
            await RequireNewIdentityAsync(c, t, RuntimeStateCatalogue.Workbenches, copy.WorkbenchId,
                copy.IsDefaultForScope ? null : copy.Name, ct);
            if (copy.IsDefaultForScope && (await DefaultWorkbenchCoreAsync(c, t, expectedScope.Key, copy.ScopeId, ct)) != null)
                throw new StateConflictException("default workbench");
            var token = await InsertWorkbenchAsync(c, t, copy, pasted, fallback,
                await NextRuntimeOrdinalAsync(c, t, RuntimeStateCatalogue.Workbenches, ct), publication, budget, ct);
            await ExecuteAsync(c, t, "UPDATE surf.Workbench SET ResolvedScopeKey=@Scope,ScopeResolution=1 WHERE WorkbenchKey=@Key;",
                ct, Key(token.Key), Key(expectedScope.Key, "@Scope"));
            return await TokenAsync(c, t, "Workbench", "WorkbenchKey", token.Key, ct);
        }, ct);
    }

    public Task<RuntimeWorkbenchTarget> ReadDefaultWorkbenchTargetAsync(long scopeKey, string scopeId, CancellationToken ct = default) =>
        ReadyAsync(async (c, t) => new RuntimeWorkbenchTarget(await DefaultWorkbenchCoreAsync(c, t, scopeKey, scopeId, ct),
            await RuntimeCatalogueAsync(c, t, RuntimeStateCatalogue.Workbenches, ct)), ct);

    private async Task<WorkbenchSummary?> DefaultWorkbenchCoreAsync(SqlConnection c, SqlTransaction t,
        long scopeKey, string scopeId, CancellationToken ct)
    {
        if (scopeKey <= 0) throw new ArgumentOutOfRangeException(nameof(scopeKey));
        await using var command = Command(c, t, $"SELECT TOP (@Limit) {RuntimeWorkbenchProjection},s.ResolvedScopeKey,s.ScopeResolution FROM surf.Workbench s WHERE s.ProfileKey=1 AND s.IsDefaultForScope=1 ORDER BY s.SortOrdinal,s.WorkbenchKey;",
            Key((long)_limits.MaximumRows + 1, "@Limit"));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var budget = new StateBudget(_limits); WorkbenchSummary? found = null;
        while (await reader.ReadAsync(ct))
        {
            budget.Row(); var summary = await RuntimeWorkbenchSummaryAsync(reader, budget, ct);
            long? resolved = reader.IsDBNull(17) ? null : reader.GetInt64(17);
            var status = (StateLinkResolution)reader.GetInt32(18);
            bool sameRaw = string.Equals(summary.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase);
            if (resolved != scopeKey && !(resolved == null && sameRaw)) continue;
            if (resolved == null && status == StateLinkResolution.Ambiguous)
                throw new InvalidDataException("The default workbench's scope identity is ambiguous.");
            if (found != null) throw new InvalidDataException("More than one default workbench targets this scope. Select and resolve it explicitly.");
            found = summary;
        }
        return found;
    }

    private const string RuntimeWorkbenchProjection = """
s.WorkbenchKey,s.SortOrdinal,s.Version,s.PublicationId,s.IsDefaultForScope,s.CreatedAtUtc,s.UpdatedAtUtc,s.SavedAtUtc,s.EmbeddedDiagramRevisionKey,
CONVERT(bigint,DATALENGTH(s.WorkbenchId)),s.WorkbenchId,CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
CONVERT(bigint,DATALENGTH(s.ScopeId)),s.ScopeId,CONVERT(bigint,DATALENGTH(s.ScopeName)),s.ScopeName
""";

    private async Task<WorkbenchSummary> RuntimeWorkbenchSummaryAsync(SqlDataReader reader, StateBudget budget, CancellationToken ct)
    {
        long key = reader.GetInt64(0), ordinal = reader.GetInt64(1);
        var token = new StateToken(key, _session.Epoch, (byte[])reader.GetValue(2), reader.GetGuid(3));
        bool defaultForScope = reader.GetBoolean(4);
        var created = reader.GetFieldValue<DateTimeOffset>(5); var updated = reader.GetFieldValue<DateTimeOffset>(6);
        var saved = reader.GetFieldValue<DateTimeOffset>(7); long? revision = reader.IsDBNull(8) ? null : reader.GetInt64(8);
        string id = (await StringAsync(reader, 9, budget, ct))!, name = (await StringAsync(reader, 11, budget, ct))!;
        string scopeId = (await StringAsync(reader, 13, budget, ct))!, scopeName = (await StringAsync(reader, 15, budget, ct))!;
        return new(token, ordinal, id, name, scopeId, scopeName, defaultForScope, created, updated, saved, revision);
    }

    public Task<RuntimeWorkbenchPage> ListRecentWorkbenchesAsync(int size = 100, RuntimeWorkbenchCursor? after = null, CancellationToken ct = default) =>
        ReadyAsync(async (c, t) =>
        {
            if (size < 1 || size > _limits.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(size));
            var generation = await RuntimeCatalogueAsync(c, t, RuntimeStateCatalogue.Workbenches, ct);
            if (after != null && (after.Epoch != _session.Epoch || after.Ordinal < 0 || after.Key < 1 ||
                !after.Generation.AsSpan().SequenceEqual(generation.Version))) throw new StateConflictException("workbench catalogue");
            await using var command = Command(c, t, $"""
SELECT TOP (@Take) {RuntimeWorkbenchProjection} FROM surf.Workbench s
CROSS APPLY(SELECT CASE WHEN s.UpdatedAtUtc<>@Unset THEN s.UpdatedAtUtc WHEN s.SavedAtUtc<>@Unset THEN s.SavedAtUtc ELSE s.CreatedAtUtc END AS EffectiveUpdated) e
WHERE s.ProfileKey=1 AND (@First=1 OR e.EffectiveUpdated<@Updated OR
 (e.EffectiveUpdated=@Updated AND (s.SortOrdinal>@Ordinal OR (s.SortOrdinal=@Ordinal AND s.WorkbenchKey>@Key))))
ORDER BY e.EffectiveUpdated DESC,s.SortOrdinal,s.WorkbenchKey;
""", Key((long)size + 1, "@Take"), RelationalSession.Parameter("@First", SqlDbType.Bit, after == null),
                RelationalSession.Parameter("@Unset", SqlDbType.DateTimeOffset, default(DateTimeOffset)),
                RelationalSession.Parameter("@Updated", SqlDbType.DateTimeOffset, after?.Updated ?? default), Key(after?.Ordinal ?? 0, "@Ordinal"), Key(after?.Key ?? 0));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var items = new List<WorkbenchSummary>(); var budget = new StateBudget(_limits); RuntimeWorkbenchCursor? last = null;
            while (await reader.ReadAsync(ct))
            {
                if (items.Count == size) return new RuntimeWorkbenchPage(items.AsReadOnly(), last);
                budget.Row(); var item = await RuntimeWorkbenchSummaryAsync(reader, budget, ct); items.Add(item);
                var updated = item.UpdatedAtUtc != default ? item.UpdatedAtUtc : item.SavedAtUtc != default ? item.SavedAtUtc : item.CreatedAtUtc;
                last = new(_session.Epoch, updated, item.SortOrdinal, item.Token.Key, generation.Version);
            }
            return new RuntimeWorkbenchPage(items.AsReadOnly(), null);
        }, ct);

    public Task DeleteScopeExpectedAsync(StateToken expected, Guid publication, CancellationToken ct = default)
    {
        Expected(expected, publication);
        return ReadyAsync(async (c, t) =>
        {
            var actual = await TokenAsync(c, t, "Scope", "ScopeKey", expected.Key, ct);
            if (!SameRuntimeToken(actual, expected)) throw new StateConflictException("scope");
            // Saved workbenches survive deletion; only their derived scope target is detached.
            await ExecuteAsync(c, t, "UPDATE surf.Workbench SET ResolvedScopeKey=NULL,ScopeResolution=2,PublicationId=@Publication WHERE ResolvedScopeKey=@Key;",
                ct, Key(expected.Key), Publication(publication));
            await ExecuteAsync(c, t, """
DELETE FROM surf.VirtualFolderMember WHERE ScopeKey=@Key;
UPDATE surf.VirtualFolder SET ParentVirtualFolderKey=NULL,ParentScopeResourceKey=NULL,ParentResourceKind=NULL,ParentResolution=0 WHERE ScopeKey=@Key;
DELETE FROM surf.VirtualFolder WHERE ScopeKey=@Key;
DELETE FROM surf.ScopeResource WHERE ScopeKey=@Key;
DELETE FROM surf.Scope WHERE ScopeKey=@Key AND Version=@Version;
""", ct, Key(expected.Key), RelationalSession.Parameter("@Version", SqlDbType.Binary, expected.Version, 8));
            await BumpAsync(c, t, 0, publication, ct); await BumpAsync(c, t, 2, publication, ct);
            return true;
        }, ct);
    }

    public Task DeleteWorkbenchExpectedAsync(StateToken expected, Guid publication, CancellationToken ct = default)
    {
        Expected(expected, publication);
        return ReadyAsync(async (c, t) =>
        {
            var actual = await TokenAsync(c, t, "Workbench", "WorkbenchKey", expected.Key, ct);
            if (!SameRuntimeToken(actual, expected)) throw new StateConflictException("workbench");
            await ClearLayoutAsync(c, t, "WorkbenchKey", expected.Key, ct);
            await ExecuteAsync(c, t, """
DELETE FROM surf.ReferenceConnectionLine WHERE WorkbenchKey=@Key;
UPDATE surf.Workbench SET EmbeddedDiagramRevisionKey=NULL WHERE WorkbenchKey=@Key;
DELETE p FROM surf.DiagramPortalTarget p JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=p.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE m FROM surf.WorkflowItemMarker m JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=m.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE b FROM surf.DiagramWorkflowBinding b JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=b.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE q FROM surf.QueryItem q JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=q.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE i FROM surf.WorkflowItem i JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=i.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE w FROM surf.Workflow w JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=w.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE o FROM surf.DiagramObject o JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=o.DiagramRevisionKey WHERE r.WorkbenchKey=@Key;
DELETE FROM surf.DiagramRevision WHERE WorkbenchKey=@Key;
DELETE FROM surf.Workbench WHERE WorkbenchKey=@Key;
""", ct, Key(expected.Key));
            await BumpAsync(c, t, 2, publication, ct); return true;
        }, ct);
    }

    /// <summary>Atomically publishes one current diagram and its selected scope membership.</summary>
    public Task<RuntimeDiagramPublication> SaveDiagramInScopeAsync(DiagramState value, Scope scope,
        StateToken expectedDiagram, StateToken expectedScope, int membershipOrdinal, Guid publication, CancellationToken ct = default)
    {
        Expected(expectedDiagram, publication); Expected(expectedScope, publication);
        var budget = new StateBudget(_limits);
        var diagram = StateCopies.Diagram(value.Document, budget);
        var pasted = StateCopies.PastedImages(diagram, value.PastedImages, budget);
        var fallback = StateCopies.FallbackImages(diagram, pasted, value.PastedImageFallbacks, budget);
        var membership = StateCopies.Scope(scope, new(_limits));
        if (membershipOrdinal < 0 || membershipOrdinal >= membership.Resources.Count ||
            membership.Resources[membershipOrdinal].Kind != ResourceKind.Diagram ||
            !string.Equals(membership.Resources[membershipOrdinal].Path, diagram.DiagramId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The explicit diagram membership is outside its selected scope.");
        return ReadyAsync(async (c, t) =>
        {
            var actual = await TokenAsync(c, t, "Diagram", "DiagramKey", expectedDiagram.Key, ct);
            if (!SameRuntimeToken(actual, expectedDiagram)) throw new StateConflictException("diagram");
            await UpdateAsync(c, t, StateMaps.Scope, membership, expectedScope, publication, ct);
            await ExecuteAsync(c, t, "UPDATE surf.Diagram SET DiagramId=@Id,Name=@Name,PublicationId=@Publication WHERE DiagramKey=@Key;",
                ct, Key(expectedDiagram.Key), Text(diagram.DiagramId, "@Id"), Text(diagram.Name, "@Name"), Publication(publication));
            long revision = await WriteDiagramRevisionAsync(c, t, diagram, pasted, fallback, expectedDiagram.Key, null, publication, budget, ct);
            await ExecuteAsync(c, t, "UPDATE surf.Diagram SET CurrentRevisionKey=@Revision WHERE DiagramKey=@Key;",
                ct, Key(expectedDiagram.Key), Key(revision, "@Revision"));
            await ReplaceScopeChildrenAsync(c, t, expectedScope.Key, membership, ct);
            await ExecuteAsync(c, t, "UPDATE surf.ScopeResource SET DiagramKey=@Diagram WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal AND Kind=3;",
                ct, Key(expectedScope.Key), Key(expectedDiagram.Key, "@Diagram"), Key(membershipOrdinal, "@Ordinal"));
            await BumpAsync(c, t, 0, publication, ct); await BumpAsync(c, t, 1, publication, ct);
            return new RuntimeDiagramPublication(await TokenAsync(c, t, "Diagram", "DiagramKey", expectedDiagram.Key, ct),
                await TokenAsync(c, t, "Scope", "ScopeKey", expectedScope.Key, ct));
        }, ct);
    }

    /// <summary>Deletes only the selected logical owner; ambiguous raw references and saved embedded graphs survive.</summary>
    public Task DeleteDiagramExpectedAsync(StateToken expected, Guid publication, CancellationToken ct = default)
    {
        Expected(expected, publication);
        return ReadyAsync(async (c, t) =>
        {
            if (!SameRuntimeToken(await TokenAsync(c, t, "Diagram", "DiagramKey", expected.Key, ct), expected))
                throw new StateConflictException("diagram");
            await ExecuteAsync(c, t, """
UPDATE s SET PublicationId=@Publication FROM surf.Scope s WHERE EXISTS(SELECT 1 FROM surf.ScopeResource r WHERE r.ScopeKey=s.ScopeKey AND r.DiagramKey=@Key);
UPDATE f SET ParentScopeResourceKey=NULL,ParentResourceKind=NULL,ParentResolution=2 FROM surf.VirtualFolder f JOIN surf.ScopeResource r ON r.ScopeResourceKey=f.ParentScopeResourceKey WHERE r.DiagramKey=@Key;
UPDATE m SET ChildScopeResourceKey=NULL,ChildResourceKind=NULL,ChildResolution=2 FROM surf.VirtualFolderMember m JOIN surf.ScopeResource r ON r.ScopeResourceKey=m.ChildScopeResourceKey WHERE r.DiagramKey=@Key;
DELETE FROM surf.ScopeResource WHERE DiagramKey=@Key;
UPDATE d SET PublicationId=@Publication FROM surf.Diagram d WHERE EXISTS(SELECT 1 FROM surf.DiagramPortalTarget p JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=p.DiagramRevisionKey WHERE r.DiagramKey=d.DiagramKey AND p.TargetDiagramKey=@Key);
UPDATE w SET PublicationId=@Publication FROM surf.Workbench w WHERE EXISTS(SELECT 1 FROM surf.DiagramPortalTarget p JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=p.DiagramRevisionKey WHERE r.WorkbenchKey=w.WorkbenchKey AND p.TargetDiagramKey=@Key);
UPDATE surf.DiagramPortalTarget SET TargetDiagramKey=NULL,Resolution=2 WHERE TargetDiagramKey=@Key;
UPDATE surf.Diagram SET CurrentRevisionKey=NULL WHERE DiagramKey=@Key;
DELETE p FROM surf.DiagramPortalTarget p JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=p.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE m FROM surf.WorkflowItemMarker m JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=m.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE b FROM surf.DiagramWorkflowBinding b JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=b.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE q FROM surf.QueryItem q JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=q.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE i FROM surf.WorkflowItem i JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=i.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE w FROM surf.Workflow w JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=w.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE o FROM surf.DiagramObject o JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=o.DiagramRevisionKey WHERE r.DiagramKey=@Key;
DELETE FROM surf.DiagramRevision WHERE DiagramKey=@Key;
DELETE FROM surf.Diagram WHERE DiagramKey=@Key;
""", ct, Key(expected.Key), Publication(publication));
            await BumpAsync(c, t, 0, publication, ct); await BumpAsync(c, t, 1, publication, ct); await BumpAsync(c, t, 2, publication, ct);
            return true;
        }, ct);
    }

    public Task<RuntimeImageRulePage> ListRuntimeImageRulesAsync(StateToken expectedOwner, RuntimeImageRuleCursor? after = null,
        int pageSize = 100, CancellationToken ct = default) => ReadyAsync(async (c, t) =>
        {
            if (pageSize <= 0 || pageSize > _limits.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
            var owner = await TokenAsync(c, t, "ApplicationPreference", "ProfileKey", 1, ct);
            if (!SameRuntimeToken(owner, expectedOwner) || after != null && !SameRuntimeToken(owner, after.Owner)) throw new StateConflictException("image rule generation");
            await using var command = Command(c, t, """
SELECT TOP (@Take) s.DiagramImageDefinitionKey,s.SortOrdinal,s.EffectiveOrder,
CONVERT(bigint,DATALENGTH(s.Id)),s.Id,CONVERT(bigint,DATALENGTH(s.Name)),s.Name,
CONVERT(bigint,DATALENGTH(s.NameRegex)),s.NameRegex,CONVERT(bigint,DATALENGTH(s.ContentRegex)),s.ContentRegex,
CONVERT(bigint,DATALENGTH(s.Regex)),s.Regex,CONVERT(bigint,DATALENGTH(s.MatchTarget)),s.MatchTarget,
CONVERT(bigint,DATALENGTH(s.ResourceTypeFilter)),s.ResourceTypeFilter
FROM (SELECT DiagramImageDefinitionKey,SortOrdinal,Id,Name,NameRegex,ContentRegex,[Regex],MatchTarget,ResourceTypeFilter,
CASE WHEN SortOrder>0 THEN CONVERT(bigint,SortOrder) ELSE SortOrdinal+1 END EffectiveOrder FROM surf.DiagramImageDefinition WHERE ProfileKey=1 AND AssetKey IS NOT NULL) s
WHERE s.EffectiveOrder>@Order OR (s.EffectiveOrder=@Order AND (s.SortOrdinal>@Ordinal OR (s.SortOrdinal=@Ordinal AND s.DiagramImageDefinitionKey>@Key)))
ORDER BY s.EffectiveOrder,s.SortOrdinal,s.DiagramImageDefinitionKey;
""", Key((long)pageSize + 1, "@Take"), Key(after?.EffectiveOrder ?? -1, "@Order"), Key(after?.Ordinal ?? -1, "@Ordinal"), Key(after?.Key ?? 0));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var budget = new StateBudget(_limits); var rows = new List<RuntimeImageRule>(); RuntimeImageRuleCursor? last = null;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == pageSize) return new RuntimeImageRulePage(rows.AsReadOnly(), last);
                budget.Row(); long key = reader.GetInt64(0), ordinal = reader.GetInt64(1), order = reader.GetInt64(2);
                string id = (await StringAsync(reader, 3, budget, ct))!, name = (await StringAsync(reader, 5, budget, ct))!;
                string nameRegex = (await StringAsync(reader, 7, budget, ct))!, contentRegex = (await StringAsync(reader, 9, budget, ct))!;
                string legacy = (await StringAsync(reader, 11, budget, ct))!, target = (await StringAsync(reader, 13, budget, ct))!;
                string filter = (await StringAsync(reader, 15, budget, ct))!;
                rows.Add(new(key, id, name, nameRegex, contentRegex, legacy, target, filter)); last = new(owner, order, ordinal, key);
            }
            return new RuntimeImageRulePage(rows.AsReadOnly(), null);
        }, ct);

    public Task<IReadOnlyList<ScopeResourceTarget>> ReadRuntimeScopeTargetsAsync(StateToken expected, CancellationToken ct = default) =>
        ReadyAsync<IReadOnlyList<ScopeResourceTarget>>(async (c, t) =>
        {
            if (!SameRuntimeToken(await TokenAsync(c, t, "Scope", "ScopeKey", expected.Key, ct), expected)) throw new StateConflictException("scope targets");
            await using var command = Command(c, t, "SELECT TOP (@Limit) SortOrdinal,SnapshotKey,DiagramKey FROM surf.ScopeResource WHERE ScopeKey=@Key ORDER BY SortOrdinal;",
                Key(expected.Key), Key((long)_limits.MaximumRows + 1, "@Limit"));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<ScopeResourceTarget>(); var budget = new StateBudget(_limits);
            while (await reader.ReadAsync(ct)) { budget.Row(); rows.Add(new(checked((int)reader.GetInt64(0)), NullableKey(reader, 1), NullableKey(reader, 2))); }
            return rows.AsReadOnly();
        }, ct);

    public Task<RuntimePortalTarget?> ReadRuntimePortalTargetAsync(long revision, int objectOrdinal, CancellationToken ct = default) =>
        ReadyAsync(async (c, t) =>
        {
            if (revision <= 0 || objectOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            await RequireRelationshipOwnerAsync(c, t, "DiagramRevision", "DiagramRevisionKey", revision, ct);
            await using var command = Command(c, t, """
SELECT p.TargetDiagramKey,p.TargetRevisionKey,target.SortOrdinal,p.Resolution
FROM surf.DiagramObject source JOIN surf.DiagramPortalTarget p ON p.DiagramRevisionKey=source.DiagramRevisionKey AND p.DiagramObjectKey=source.DiagramObjectKey
LEFT JOIN surf.DiagramObject target ON target.DiagramRevisionKey=p.TargetRevisionKey AND target.DiagramObjectKey=p.TargetObjectKey
WHERE source.DiagramRevisionKey=@Key AND source.SortOrdinal=@Ordinal;
""", Key(revision), Key(objectOrdinal, "@Ordinal"));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? new RuntimePortalTarget(NullableKey(reader, 0), NullableKey(reader, 1), NullableKey(reader, 2), ReadResolution(reader, 3)) : null;
        }, ct);

    public Task<long> ReadCurrentDiagramRevisionKeyAsync(StateToken expected, CancellationToken ct = default) =>
        ReadyAsync(async (c, t) =>
        {
            if (!SameRuntimeToken(await TokenAsync(c, t, "Diagram", "DiagramKey", expected.Key, ct), expected)) throw new StateConflictException("diagram revision");
            await using var command = Command(c, t, "SELECT CurrentRevisionKey FROM surf.Diagram WHERE DiagramKey=@Key AND ProfileKey=1;", Key(expected.Key));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            return (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidDataException("Current revision is missing."));
        }, ct);

    /// <summary>Publishes two bounded selected diagram owners atomically; no raw-ID selection occurs here.</summary>
    public Task<RuntimeDiagramPairPublication> SaveDiagramPairAsync(DiagramState first, StateToken expectedFirst,
        DiagramState second, StateToken expectedSecond, Guid publication, CancellationToken ct = default)
    {
        Expected(expectedFirst, publication); Expected(expectedSecond, publication);
        if (expectedFirst.Key == expectedSecond.Key) throw new ArgumentException("A self pairing requires one selected aggregate save.");
        var budget = new StateBudget(_limits);
        DiagramState Copy(DiagramState value)
        {
            var document = StateCopies.Diagram(value.Document, budget);
            var pasted = StateCopies.PastedImages(document, value.PastedImages, budget);
            return new(document, pasted, StateCopies.FallbackImages(document, pasted, value.PastedImageFallbacks, budget));
        }
        var a = Copy(first); var b = Copy(second);
        return ReadyAsync(async (c, t) =>
        {
            var savedA = await PublishRuntimeDiagramAsync(c, t, a, expectedFirst, publication, budget, ct);
            var savedB = await PublishRuntimeDiagramAsync(c, t, b, expectedSecond, publication, budget, ct);
            await BumpAsync(c, t, 1, publication, ct);
            return new RuntimeDiagramPairPublication(savedA, savedB);
        }, ct);
    }

    private async Task<StateToken> PublishRuntimeDiagramAsync(SqlConnection c, SqlTransaction t, DiagramState value,
        StateToken expected, Guid publication, StateBudget budget, CancellationToken ct)
    {
        if (!SameRuntimeToken(await TokenAsync(c, t, "Diagram", "DiagramKey", expected.Key, ct), expected)) throw new StateConflictException("diagram pairing");
        await ExecuteAsync(c, t, "UPDATE surf.Diagram SET DiagramId=@Id,Name=@Name,PublicationId=@Publication WHERE DiagramKey=@Key;",
            ct, Key(expected.Key), Text(value.Document.DiagramId, "@Id"), Text(value.Document.Name, "@Name"), Publication(publication));
        long revision = await WriteDiagramRevisionAsync(c, t, value.Document, value.PastedImages,
            value.PastedImageFallbacks ?? new Dictionary<int, PastedImageFallback>(), expected.Key, null, publication, budget, ct);
        await ExecuteAsync(c, t, "UPDATE surf.Diagram SET CurrentRevisionKey=@Revision WHERE DiagramKey=@Key;", ct, Key(expected.Key), Key(revision, "@Revision"));
        return await TokenAsync(c, t, "Diagram", "DiagramKey", expected.Key, ct);
    }

    /// <summary>Scalar head plus explicitly edited children only. Unselected children are never read or replaced.</summary>
    public Task<StateToken> SavePreferenceSelectionAsync(AppSettings head, StateToken expected,
        IReadOnlyList<PreferenceChildChange<ExtensionBackcolorSetting>> extensions,
        IReadOnlyList<PreferenceChildChange<ReferenceHighlightStyleSetting>> styles,
        IReadOnlyList<PreferenceChildChange<DiagramImageDefinition>> images,
        IReadOnlyList<PreferenceImageMove> imageMoves, Guid publication, CancellationToken ct = default)
    {
        Expected(expected, publication);
        if (expected.Key != 1) throw new ArgumentException("Preferences belong to profile 1.");
        var budget = new StateBudget(_limits);
        var copy = StateMaps.Settings.Copy(head, budget);
        var extensionCopies = CopyPreferenceChanges(extensions, StateMaps.Extension, budget);
        var styleCopies = CopyPreferenceChanges(styles, StateMaps.Style, budget);
        var imageCopies = CopyPreferenceChanges(images, StateMaps.Image, budget);
        var moves = imageMoves.ToArray();
        foreach (var move in moves) { budget.Row(); if (move.Key <= 0 || move.AdjacentKey <= 0 || move.Key == move.AdjacentKey) throw new ArgumentException("Invalid image move."); }
        return ReadyAsync(async (c, t) =>
        {
            await UpdateAsync(c, t, StateMaps.Settings, copy, expected, publication, ct);
            await WritePreferenceChangesAsync(c, t, StateMaps.Extension, extensionCopies, budget, ct);
            await WritePreferenceChangesAsync(c, t, StateMaps.Style, styleCopies, budget, ct);
            await WritePreferenceChangesAsync(c, t, StateMaps.Image, imageCopies, budget, ct);
            foreach (var move in moves)
            {
                await ExecuteAsync(c, t, """
DECLARE @A bigint,@B bigint,@SA int,@SB int,@Temporary bigint;
SELECT @A=SortOrdinal,@SA=SortOrder FROM surf.DiagramImageDefinition WHERE ProfileKey=1 AND DiagramImageDefinitionKey=@Key;
SELECT @B=SortOrdinal,@SB=SortOrder FROM surf.DiagramImageDefinition WHERE ProfileKey=1 AND DiagramImageDefinitionKey=@Other;
IF @A IS NULL OR @B IS NULL THROW 51000,'Selected image move target is missing.',1;
SELECT @Temporary=MAX(SortOrdinal)+1 FROM surf.DiagramImageDefinition WHERE ProfileKey=1;
UPDATE surf.DiagramImageDefinition SET SortOrdinal=@Temporary WHERE DiagramImageDefinitionKey=@Key;
UPDATE surf.DiagramImageDefinition SET SortOrdinal=@A,SortOrder=@SA WHERE DiagramImageDefinitionKey=@Other;
UPDATE surf.DiagramImageDefinition SET SortOrdinal=@B,SortOrder=@SB WHERE DiagramImageDefinitionKey=@Key;
""", ct, Key(move.Key), Key(move.AdjacentKey, "@Other"));
            }
            return await TokenAsync(c, t, "ApplicationPreference", "ProfileKey", 1, ct);
        }, ct);
    }

    private static PreferenceChildChange<T>[] CopyPreferenceChanges<T>(IReadOnlyList<PreferenceChildChange<T>> changes,
        StateRowMap<T> map, StateBudget budget) where T : class, new()
    {
        var keys = new HashSet<long>();
        var copies = new List<PreferenceChildChange<T>>();
        foreach (var change in changes)
        {
            budget.Row();
            if (change.Key.HasValue && (change.Key <= 0 || !keys.Add(change.Key.Value))) throw new ArgumentException("Repeated or invalid selected preference key.");
            if (change.Key == null && change.Value == null) throw new ArgumentException("A create requires a value.");
            copies.Add(new(change.Key, change.Value == null ? null : map.Copy(change.Value, budget)));
        }
        return copies.ToArray();
    }

    private async Task WritePreferenceChangesAsync<T>(SqlConnection c, SqlTransaction t, StateRowMap<T> map,
        IReadOnlyList<PreferenceChildChange<T>> changes, StateBudget budget, CancellationToken ct) where T : class, new()
    {
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            if (change.Value == null)
            {
                await using var delete = Command(c, t, $"DELETE FROM surf.[{map.Table}] OUTPUT DELETED.[{map.Key}] WHERE ProfileKey=1 AND [{map.Key}]=@Key;", Key(change.Key!.Value));
                using var cancel = RelationalSession.CancelCommand(delete, ct);
                if (await delete.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("selected preference child");
                continue;
            }
            long key;
            if (change.Key.HasValue)
            {
                key = change.Key.Value;
                await using var update = Command(c, t, $"UPDATE surf.[{map.Table}] SET {map.Assignments} OUTPUT INSERTED.[{map.Key}] WHERE ProfileKey=1 AND [{map.Key}]=@Key;", Key(key));
                await map.AddParametersAsync(update, change.Value, _content, c, t, ct);
                using var cancel = RelationalSession.CancelCommand(update, ct);
                if (await update.ExecuteScalarAsync(ct) is not long) throw new StateConflictException("selected preference child");
            }
            else
            {
                await using var ordinal = Command(c, t, $"SELECT COALESCE(MAX(SortOrdinal),CONVERT(bigint,-1))+1 FROM surf.[{map.Table}] WHERE ProfileKey=1;");
                using var cancel = RelationalSession.CancelCommand(ordinal, ct);
                long next = (long)(await ordinal.ExecuteScalarAsync(ct))!;
                key = await InsertAsync(c, t, map, change.Value, ct, ("ProfileKey", SqlDbType.BigInt, 1L), ("SortOrdinal", SqlDbType.BigInt, next));
            }
            if (change.Value is DiagramImageDefinition image)
            {
                byte[]? bytes = StateImages.Decode(image.ImageDataBase64, _limits, budget, ct);
                long? asset = bytes == null ? null : await _content.PutAssetAsync(c, t, bytes, StateImages.Validate(bytes, _limits, cancellationToken: ct), ct);
                await ExecuteAsync(c, t, "UPDATE surf.DiagramImageDefinition SET AssetKey=@Asset WHERE ProfileKey=1 AND DiagramImageDefinitionKey=@Key;",
                    ct, Key(key), RelationalSession.Parameter("@Asset", SqlDbType.BigInt, asset));
            }
        }
    }

    private static bool SameRuntimeToken(StateToken a, StateToken b) => a.Key == b.Key && a.Epoch == b.Epoch &&
        a.PublicationId == b.PublicationId && a.Version.AsSpan().SequenceEqual(b.Version);
}

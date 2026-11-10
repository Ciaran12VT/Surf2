using System.Data;
using System.IO;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyIndexStorageAsync(RelationalSession session, RelationalSnapshotStore snapshots,
        RelationalStateStore state, SnapshotFixture snapshot, StateFixture stateFixture, Action<bool, string> check)
    {
        // Called only with the suite-owned destination, after Ready and the state mutation checks.
        await session.RequireReadyAsync();
        var head = Required(await snapshots.GetSnapshotAsync(snapshot.Key), "index fixture published snapshot");
        var resource = Required(await snapshots.ResolveResourceAsync(snapshot.CurrentObject.ResourceKey), "index fixture current object");
        if (resource.SnapshotKey != snapshot.Key || resource.RevisionKey != snapshot.CurrentObject.RevisionKey ||
            resource.Kind != DatabaseVersionedResourceKind.StoredProcedure)
            throw new InvalidDataException("Index fixture must use the existing current stored-procedure revision.");
        var definition = await snapshots.ReadObjectAsync(resource.RevisionKey);
        var scope = Required(await state.ReadScopeAsync(stateFixture.ScopeToken.Key), "index fixture existing scope");
        var scopeOwner = scope.Value.Resources.Select((value, ordinal) => (Value: value, Ordinal: ordinal))
            .Single(item => item.Value.Kind == ResourceKind.DatabaseSnapshot);
        // The state tests reorder duplicate resource IDs. Resolve by the fresh ordered kind, not an original ID or stale token.
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            await state.ResolveScopeResourceTargetsAsync(connection, transaction, scope.Token,
                [new(scopeOwner.Ordinal, snapshot.Key, null)], Guid.NewGuid());
        });

        long scopeResourceKey;
        await using (var connection = await session.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 120;
            command.CommandText = """
                SELECT sr.ScopeResourceKey FROM surf.ScopeResource sr
                JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=sr.SnapshotKey
                JOIN surf.SnapshotResource r ON r.SnapshotKey=s.SnapshotKey
                JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=r.CurrentRevisionKey AND rr.ResourceKey=r.ResourceKey
                WHERE sr.ScopeKey=@Scope AND sr.SortOrdinal=@Ordinal AND sr.Kind=@ScopeKind
                  AND s.SnapshotKey=@Snapshot AND s.IsPublished=1
                  AND r.ResourceKey=@Resource AND r.Kind=@ResourceKind
                  AND rr.RevisionKey=@Revision AND rr.IsSealed=1;
                """;
            command.Parameters.Add(RelationalSession.Parameter("@Scope", SqlDbType.BigInt, scope.Token.Key));
            command.Parameters.Add(RelationalSession.Parameter("@Ordinal", SqlDbType.BigInt, scopeOwner.Ordinal));
            command.Parameters.Add(RelationalSession.Parameter("@ScopeKind", SqlDbType.Int, (int)ResourceKind.DatabaseSnapshot));
            command.Parameters.Add(RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshot.Key));
            command.Parameters.Add(RelationalSession.Parameter("@Resource", SqlDbType.BigInt, resource.ResourceKey));
            command.Parameters.Add(RelationalSession.Parameter("@ResourceKind", SqlDbType.Int, (int)DatabaseVersionedResourceKind.StoredProcedure));
            command.Parameters.Add(RelationalSession.Parameter("@Revision", SqlDbType.BigInt, resource.RevisionKey));
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidDataException("Index fixture scope target is not the published sealed current source.");
            scopeResourceKey = reader.GetInt64(0);
            if (await reader.ReadAsync()) throw new InvalidDataException("Index fixture scope target is not unique.");
        }
        check(true, "Index SQL fixture resolves an existing scope resource to the published sealed current object");

        // Only the selected header is adapted for the established locator formatter; no full snapshot/file read.
        var locatorHeader = new DatabaseMetadataSnapshot
        {
            SnapshotId = head.SnapshotId, DisplayName = head.DisplayName,
            DatabaseName = head.DatabaseName, ImportedAtUtc = head.ImportedAtUtc
        };
        var fixture = new IndexStorageFixture(scope.Token.Key, scopeResourceKey, snapshot.Key, resource.ResourceKey,
            resource.RevisionKey, DatabaseDocumentService.CreateCanonicalObjectDocumentPath(locatorHeader, definition),
            DatabaseDocumentService.CreateObjectDocumentPath(locatorHeader, definition));
        check(true, FormattableString.Invariant(
            $"Index SQL fixture: ScopeKey={fixture.ScopeKey}; ScopeResourceKey={fixture.ScopeResourceKey}; ScopeResourceOrdinal={scopeOwner.Ordinal}; SnapshotKey={fixture.SnapshotKey}; ResourceKey={fixture.ResourceKey}; SourceRevisionKey={fixture.SourceRevisionKey}; canonical={JsonSerializer.Serialize(fixture.CanonicalLocator)}; readable={JsonSerializer.Serialize(fixture.ReadableLocator)}."));
        check(true,
            $"Index SQL legacy identities: snapshot_id={JsonSerializer.Serialize(head.SnapshotId)}; resource_key={JsonSerializer.Serialize(resource.OriginalResourceKey)}; scope_id={JsonSerializer.Serialize(scope.Value.ScopeId)}; scope_resource_id={JsonSerializer.Serialize(scopeOwner.Value.ResourceId)}; original_scope_path={JsonSerializer.Serialize(scopeOwner.Value.Path)}.");
        IReadOnlyList<string> passed = await IndexContractChecks.RunStorageAsync(session, fixture);
        check(passed.Count > 0, "Index storage contract hook executes SQL publication and read checks");
        foreach (string name in passed) check(true, "Index SQL: " + name);

        var index = new RelationalIndexStore(session);
        var context = await index.CaptureContextAsync(fixture.ScopeKey);
        var documents = await index.ReadDocumentsPageAsync(context);
        var document = documents.Items.Single(item => item.SourceRevisionKey == fixture.SourceRevisionKey &&
            item.ScopeResourceKey == fixture.ScopeResourceKey);
        var canonical = await index.ResolveLocatorPageAsync(context, fixture.CanonicalLocator);
        var readable = await index.ResolveLocatorPageAsync(context, fixture.ReadableLocator);
        check(canonical.Items.Single().DocumentKey == document.DocumentKey &&
            readable.Items.Single().DocumentKey == document.DocumentKey && document.Kind == IndexedDocumentKind.Definition,
            "Index SQL canonical and readable aliases resolve to the same selected typed document");
        long revisionKey = document.RevisionKey ?? throw new InvalidDataException("Index fixture did not retain a successful revision.");
        check(await index.ReadRevisionTextAsync(document.DocumentKey, revisionKey) == definition.Definition,
            "Index SQL selected revision retains exact definition after the failed refresh");
        check(true, FormattableString.Invariant(
            $"Index SQL published identities: DocumentKey={document.DocumentKey}; DocumentRevisionKey={revisionKey}; freshness={document.Freshness}."));
        SameModel(scope.Value, (await state.ReadScopeAsync(fixture.ScopeKey))!.Value, check,
            "Index target resolution and publication preserve original scope paths, duplicate IDs and ordered fields");
        SameModel(definition, await snapshots.ReadObjectAsync(fixture.SourceRevisionKey), check,
            "Index publication leaves the existing source object revision unchanged");
    }
}

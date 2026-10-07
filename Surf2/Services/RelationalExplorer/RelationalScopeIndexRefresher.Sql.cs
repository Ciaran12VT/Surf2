using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalScopeIndexRefresher
{
    private sealed record PublishedState(IndexFreshness Freshness, string? Fingerprint,
        IndexPolicy? Policy, long? SourceRevisionKey, string Language);
    private sealed record PreparedDocument(DocumentHandle Handle, PublishedState Published);

    private async Task<PreparedDocument> GetOrRegisterAsync(DocumentRegistration registration, CancellationToken ct)
    {
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        string? path = (registration.Owner as FileDocumentOwner)?.Path;
        var snapshot = registration.Owner as SnapshotDocumentOwner;
        var diagram = registration.Owner as DiagramDocumentOwner;
        var kind = path != null ? IndexedDocumentKind.File : snapshot != null ? snapshot.IsTableCode ? IndexedDocumentKind.TableCode
            : IndexedDocumentKind.Definition : IndexedDocumentKind.Diagram;
        string predicate = kind switch
        {
            IndexedDocumentKind.File => "d.Kind=0 AND (@Ascii=0 OR f.PathIsAscii=0 OR f.PathHash=@Hash)",
            IndexedDocumentKind.Definition or IndexedDocumentKind.TableCode => "d.Kind=@Kind AND d.SnapshotKey=@Snapshot AND d.SnapshotResourceKey=@Resource",
            _ => "d.Kind=3 AND d.DiagramRevisionKey=@Diagram"
        };
        await using var command = IndexSql.Command(connection, null, $"""
SELECT d.DocumentKey,d.Version,LEFT(f.OriginalPath,65537),d.Freshness,r.SourceFingerprint,
 r.ParserVersion,r.RendererVersion,r.PolicyVersion,r.SourceRevisionKey,d.Language
FROM surf.Document d LEFT JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey
LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
WHERE {predicate};
""");
        IndexSql.Add(command, "@Kind", SqlDbType.Int, (int)kind);
        IndexSql.Add(command, "@Ascii", SqlDbType.Bit, path != null && ReferenceMetadata.IsAscii(path));
        IndexSql.Add(command, "@Hash", SqlDbType.Binary, path == null ? null : IndexSql.Hash(path.ToUpperInvariant()), 32);
        IndexSql.Add(command, "@Snapshot", SqlDbType.BigInt, snapshot?.SnapshotKey);
        IndexSql.Add(command, "@Resource", SqlDbType.BigInt, snapshot?.ResourceKey);
        IndexSql.Add(command, "@Diagram", SqlDbType.BigInt, diagram?.DiagramRevisionKey);
        PreparedDocument? found = null;
        using (var cancel = RelationalSession.CancelCommand(command, ct))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (path != null)
                {
                    string original = reader.GetString(2);
                    if (original.Length > 65536) throw new ExplorerLimitException("A physical index locator exceeds its metadata limit.");
                    if (!string.Equals(path, original, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (found != null) throw new InvalidOperationException("The typed document identity is ambiguous.");
                found = new(new(reader.GetInt64(0), IndexSql.Hex((byte[])reader[1])), ReadPublished(reader, 3));
            }
        if (found != null) return found;
        var handle = await _index.RegisterAsync(registration, ct).ConfigureAwait(false);
        return new(handle, await ReadPublishedAsync(handle.DocumentKey, ct).ConfigureAwait(false));
    }

    private async Task<PublishedState> ReadPublishedAsync(long documentKey, CancellationToken ct)
    {
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, null, """
SELECT d.Freshness,r.SourceFingerprint,r.ParserVersion,r.RendererVersion,r.PolicyVersion,r.SourceRevisionKey,d.Language
FROM surf.Document d LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
WHERE d.DocumentKey=@Document;
""");
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Document not registered.");
        return ReadPublished(reader, 0);
    }

    private static PublishedState ReadPublished(SqlDataReader reader, int start) =>
        new((IndexFreshness)reader.GetInt32(start), reader.IsDBNull(start + 1) ? null : IndexSql.Hex((byte[])reader[start + 1]),
            reader.IsDBNull(start + 2) ? null : new(reader.GetString(start + 2), reader.GetString(start + 3), reader.GetString(start + 4)),
            reader.IsDBNull(start + 5) ? null : reader.GetInt64(start + 5), reader.GetString(start + 6));

    private async Task InvalidateDiscoveryAsync(ExplorerScope scope, CancellationToken ct)
    {
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        await using (var head = IndexSql.Command(connection, transaction,
            "SELECT Generation FROM surf.IndexCatalogueHead WITH(UPDLOCK,HOLDLOCK) WHERE Singleton=1;"))
        {
            using var headCancellation = RelationalSession.CancelCommand(head, ct);
            await head.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        await FenceDomainAsync(connection, transaction, scope.Context, ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, transaction, """
UPDATE surf.ScopeIndexState SET ReconciledScopeVersion=NULL,ReconciledAtUtc=NULL
 WHERE ScopeKey=@Scope AND ReconciledScopeVersion IS NOT NULL;
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, scope.Context.ScopeKey);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0)
            await IndexSql.MutateHeadAsync(connection, transaction, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> MembershipMatchesAsync(SqlConnection connection, SqlTransaction transaction, long scopeKey, long documentKey,
        ImmutableArray<DocumentMembership> memberships, ImmutableArray<IndexLocator> locators, CancellationToken ct)
    {
        await using var command = IndexSql.Command(connection, transaction, """
SELECT TOP(513) m.ScopeResourceKey,m.DisplayName,m.Locator,m.NodeKey,m.ParentNodeKey,m.SortOrdinal
FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
WHERE m.DocumentKey=@Document AND sr.ScopeKey=@Scope ORDER BY m.ScopeResourceKey;
SELECT TOP(513) l.ScopeResourceKey,l.Kind,l.OriginalLocator FROM surf.DocumentLocator l
JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=l.ScopeResourceKey WHERE l.DocumentKey=@Document AND sr.ScopeKey=@Scope
ORDER BY l.ScopeResourceKey,l.Kind;
""");
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, scopeKey);
        var existing = new List<DocumentMembership>(); var addresses = new List<IndexLocator>();
        var budget = new ExplorerMetadataBudget(_limits);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var m = new DocumentMembership(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5));
            budget.Add(m.DisplayName, m.Locator, m.NodeKey, m.ParentNodeKey); existing.Add(m);
        }
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var l = new IndexLocator(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2));
            budget.Add(l.Locator); addresses.Add(l);
        }
        return existing.SequenceEqual(memberships.OrderBy(m => m.ScopeResourceKey)) &&
            addresses.SequenceEqual(locators.OrderBy(l => l.ScopeResourceKey).ThenBy(l => l.Kind));
    }

    // The store's global replacement API is intentionally NOT used: another scope may own the same file.
    private async Task SetScopeMembershipAsync(ExplorerScope scope, long documentKey,
        ImmutableArray<DocumentMembership> memberships, ImmutableArray<IndexLocator> locators, CancellationToken ct)
    {
        if (memberships.Length > IndexSql.MaximumBatch || locators.Length > IndexSql.MaximumBatch ||
            memberships.Select(m => m.ScopeResourceKey).Distinct().Count() != memberships.Length ||
            memberships.Any(m => !scope.Resources.Any(r => r.ScopeResourceKey == m.ScopeResourceKey)) ||
            locators.Any(l => l.Kind is < 0 or > 2 || !memberships.Any(m => m.ScopeResourceKey == l.ScopeResourceKey)))
            throw new ExplorerLimitException("Document membership exceeds its scoped batch budget.");
        var budget = new ExplorerMetadataBudget(_limits);
        foreach (var m in memberships) budget.Add(m.DisplayName, m.Locator, m.NodeKey, m.ParentNodeKey);
        foreach (var l in locators) budget.Add(l.Locator);
        if (memberships.Any(m => m.DisplayName.Length > 65536 || m.Locator.Length > 65536 ||
            m.NodeKey.Length > 65536 || m.ParentNodeKey.Length > 65536 || m.SortOrdinal < 0) || locators.Any(l => l.Locator.Length > 65536))
            throw new IndexDocumentTooLargeException();
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        // Match the global writer lock order, but do not bump generation for unchanged memberships.
        await using (var head = IndexSql.Command(connection, transaction,
            "SELECT Generation FROM surf.IndexCatalogueHead WITH(UPDLOCK,HOLDLOCK) WHERE Singleton=1;"))
        {
            using var cancel = RelationalSession.CancelCommand(head, ct);
            await head.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        await FenceDomainAsync(connection, transaction, scope.Context, ct).ConfigureAwait(false);
        if (await MembershipMatchesAsync(connection, transaction, scope.Context.ScopeKey, documentKey, memberships, locators, ct).ConfigureAwait(false))
        { await transaction.CommitAsync(ct).ConfigureAwait(false); return; }
        await IndexSql.MutateHeadAsync(connection, transaction, ct).ConfigureAwait(false);
        await using (var clear = IndexSql.Command(connection, transaction, """
DELETE l FROM surf.DocumentLocator l JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=l.ScopeResourceKey
 WHERE l.DocumentKey=@Document AND sr.ScopeKey=@Scope;
DELETE m FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 WHERE m.DocumentKey=@Document AND sr.ScopeKey=@Scope;
UPDATE surf.ScopeIndexState SET ReconciledScopeVersion=NULL,ReconciledAtUtc=NULL WHERE ScopeKey=@Scope;
"""))
        {
            IndexSql.Add(clear, "@Document", SqlDbType.BigInt, documentKey);
            IndexSql.Add(clear, "@Scope", SqlDbType.BigInt, scope.Context.ScopeKey);
            using var cancel = RelationalSession.CancelCommand(clear, ct);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var m in memberships)
        {
            await using var command = IndexSql.Command(connection, transaction, """
IF NOT EXISTS(SELECT 1 FROM surf.ScopeResource sr CROSS JOIN surf.Document d
 LEFT JOIN surf.DiagramRevision dr ON dr.DiagramRevisionKey=d.DiagramRevisionKey
 WHERE sr.ScopeResourceKey=@Resource AND sr.ScopeKey=@Scope AND d.DocumentKey=@Document AND
 ((d.Kind=0 AND sr.Kind IN (0,1)) OR (d.Kind IN (1,2) AND sr.SnapshotKey=d.SnapshotKey)
 OR (d.Kind=3 AND sr.DiagramKey=dr.DiagramKey)))
 THROW 51100,'Document membership has a different typed owner.',1;
INSERT surf.ResourceDocument(ScopeResourceKey,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal)
 VALUES(@Resource,@Document,@Name,@Locator,@Node,@Parent,@Ordinal);
""");
            IndexSql.Add(command, "@Scope", SqlDbType.BigInt, scope.Context.ScopeKey);
            IndexSql.Add(command, "@Resource", SqlDbType.BigInt, m.ScopeResourceKey);
            IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
            IndexSql.Add(command, "@Name", SqlDbType.NVarChar, m.DisplayName, -1);
            IndexSql.Add(command, "@Locator", SqlDbType.NVarChar, m.Locator, -1);
            IndexSql.Add(command, "@Node", SqlDbType.NVarChar, m.NodeKey, -1);
            IndexSql.Add(command, "@Parent", SqlDbType.NVarChar, m.ParentNodeKey, -1);
            IndexSql.Add(command, "@Ordinal", SqlDbType.BigInt, m.SortOrdinal);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var l in locators)
        {
            await using var command = IndexSql.Command(connection, transaction, """
INSERT surf.DocumentLocator(DocumentKey,ScopeResourceKey,Kind,OriginalLocator,LocatorHash,IsAscii)
 VALUES(@Document,@Resource,@Kind,@Locator,@Hash,@Ascii);
""");
            IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
            IndexSql.Add(command, "@Resource", SqlDbType.BigInt, l.ScopeResourceKey);
            IndexSql.Add(command, "@Kind", SqlDbType.Int, l.Kind);
            IndexSql.Add(command, "@Locator", SqlDbType.NVarChar, l.Locator, -1);
            IndexSql.Add(command, "@Hash", SqlDbType.Binary, ReferenceMetadata.LookupHash(l.Locator), 32);
            IndexSql.Add(command, "@Ascii", SqlDbType.Bit, ReferenceMetadata.IsAscii(l.Locator));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    // Prune only successfully enumerated loaded resources, in bounded key batches.
    private async Task PruneAsync(ExplorerScope scope, long resourceKey, HashSet<(long Resource, long Document)> seen, CancellationToken ct)
    {
        long after = 0;
        while (true)
        {
            var page = new List<long>();
            await using (var connection = await _session.OpenAsync(ct).ConfigureAwait(false))
            await using (var command = IndexSql.Command(connection, null, """
SELECT TOP(64) DocumentKey FROM surf.ResourceDocument WHERE ScopeResourceKey=@Resource AND DocumentKey>@After ORDER BY DocumentKey;
"""))
            {
                IndexSql.Add(command, "@Resource", SqlDbType.BigInt, resourceKey);
                IndexSql.Add(command, "@After", SqlDbType.BigInt, after);
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) page.Add(reader.GetInt64(0));
            }
            if (page.Count == 0) break;
            after = page[^1];
            long[] obsolete = page.Where(document => !seen.Contains((resourceKey, document))).ToArray();
            if (obsolete.Length == 0) continue;
            await using var pruneConnection = await _session.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await pruneConnection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await IndexSql.MutateHeadAsync(pruneConnection, transaction, ct).ConfigureAwait(false);
            await FenceDomainAsync(pruneConnection, transaction, scope.Context, ct).ConfigureAwait(false);
            await using var prune = IndexSql.Command(pruneConnection, transaction, """
DELETE l FROM surf.DocumentLocator l WHERE l.ScopeResourceKey=@Resource
 AND EXISTS(SELECT 1 FROM @Documents d WHERE d.Id=l.DocumentKey);
DELETE m FROM surf.ResourceDocument m WHERE m.ScopeResourceKey=@Resource
 AND EXISTS(SELECT 1 FROM @Documents d WHERE d.Id=m.DocumentKey);
""");
            IndexSql.Add(prune, "@Resource", SqlDbType.BigInt, resourceKey);
            IndexSql.Keys(prune, "@Documents", obsolete);
            using var pruneCancel = RelationalSession.CancelCommand(prune, ct);
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task FenceDomainAsync(SqlConnection connection, SqlTransaction transaction, IndexRequestContext context, CancellationToken ct)
    {
        if (context.Epoch != _session.Epoch) throw new IndexGenerationChangedException();
        await using var command = IndexSql.Command(connection, transaction, """
SELECT s.Version,sh.RowVersion,COALESCE(dg.Version,CONVERT(binary(8),0))
FROM surf.Scope s JOIN surf.SnapshotCatalogueHead sh ON sh.UserKey=s.ProfileKey
LEFT JOIN surf.StateCatalogueGeneration dg ON dg.ProfileKey=s.ProfileKey AND dg.Kind=1 WHERE s.ScopeKey=@Scope;
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, context.ScopeKey);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || IndexSql.Hex((byte[])reader[0]) != context.ScopeVersion ||
            IndexSql.Hex((byte[])reader[1]) != context.SnapshotCatalogueVersion || IndexSql.Hex((byte[])reader[2]) != context.DiagramCatalogueVersion)
            throw new IndexGenerationChangedException();
    }
}

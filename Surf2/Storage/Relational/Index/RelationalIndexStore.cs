using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    private readonly RelationalSession _session;
    private readonly RelationalContentStore _content;
    public RelationalIndexStore(RelationalSession session, RelationalContentStore? content = null)
    {
        _session = session;
        _content = content ?? new RelationalContentStore();
    }

    public async Task<DocumentHandle> RegisterAsync(DocumentRegistration registration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(registration.Owner);
        if (registration.DisplayName.Length > 65536) throw new IndexDocumentTooLargeException();
        IndexSql.ShortValue(registration.Language, nameof(registration.Language));
        await using var connection = await _session.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await IndexSql.MutateHeadAsync(connection, transaction, token);
        long? fileKey = null, resourceKey = null, snapshotKey = null, diagramRevisionKey = null;
        IndexedDocumentKind kind;
        switch (registration.Owner)
        {
            case FileDocumentOwner file:
                string path = System.IO.Path.GetFullPath(file.Path);
                byte[] hash = IndexSql.Hash(path.ToUpperInvariant());
                await using (var find = IndexSql.Command(connection, transaction,
                    "SELECT FileSourceKey,OriginalPath FROM surf.FileSource WHERE @Ascii=0 OR PathIsAscii=0 OR PathHash=@Hash;"))
                {
                    IndexSql.Add(find, "@Hash", SqlDbType.Binary, hash, 32);
                    IndexSql.Add(find, "@Ascii", SqlDbType.Bit, ReferenceMetadata.IsAscii(path));
                    using var cancel = RelationalSession.CancelCommand(find, token);
                    await using var reader = await find.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                        if (string.Equals(path, reader.GetString(1), StringComparison.OrdinalIgnoreCase)) fileKey = reader.GetInt64(0);
                }
                if (fileKey == null)
                {
                    await using var insert = IndexSql.Command(connection, transaction,
                        "INSERT surf.FileSource(OriginalPath,PathHash,PathIsAscii) OUTPUT INSERTED.FileSourceKey VALUES(@Path,@Hash,@Ascii);");
                    IndexSql.Add(insert, "@Path", SqlDbType.NVarChar, path, -1);
                    IndexSql.Add(insert, "@Hash", SqlDbType.Binary, hash, 32);
                    IndexSql.Add(insert, "@Ascii", SqlDbType.Bit, ReferenceMetadata.IsAscii(path));
                    using var cancel = RelationalSession.CancelCommand(insert, token);
                    fileKey = (long)(await insert.ExecuteScalarAsync(token))!;
                }
                kind = IndexedDocumentKind.File;
                break;
            case SnapshotDocumentOwner snapshot when snapshot.ResourceKey > 0 && snapshot.SnapshotKey > 0:
                resourceKey = snapshot.ResourceKey;
                snapshotKey = snapshot.SnapshotKey;
                kind = snapshot.IsTableCode ? IndexedDocumentKind.TableCode : IndexedDocumentKind.Definition;
                break;
            case DiagramDocumentOwner diagram when diagram.DiagramRevisionKey > 0:
                diagramRevisionKey = diagram.DiagramRevisionKey;
                kind = IndexedDocumentKind.Diagram;
                break;
            default: throw new ArgumentException("Unsupported document owner.");
        }
        await using var command = IndexSql.Command(connection, transaction, """
DECLARE @Key bigint;
SELECT @Key=DocumentKey FROM surf.Document WHERE Kind=@Kind AND
    ((FileSourceKey=@File) OR (SnapshotResourceKey=@Resource AND SnapshotKey=@Snapshot) OR (DiagramRevisionKey=@Diagram));
IF @Key IS NULL
BEGIN
 INSERT surf.Document(Kind,FileSourceKey,SnapshotResourceKey,SnapshotKey,DiagramRevisionKey,DisplayName,Language)
 VALUES(@Kind,@File,@Resource,@Snapshot,@Diagram,@Name,@Language);
 SET @Key=SCOPE_IDENTITY();
END;
SELECT DocumentKey,Version FROM surf.Document WHERE DocumentKey=@Key;
""");
        IndexSql.Add(command, "@Kind", SqlDbType.Int, (int)kind);
        IndexSql.Add(command, "@File", SqlDbType.BigInt, fileKey);
        IndexSql.Add(command, "@Resource", SqlDbType.BigInt, resourceKey);
        IndexSql.Add(command, "@Snapshot", SqlDbType.BigInt, snapshotKey);
        IndexSql.Add(command, "@Diagram", SqlDbType.BigInt, diagramRevisionKey);
        IndexSql.Add(command, "@Name", SqlDbType.NVarChar, registration.DisplayName, -1);
        IndexSql.Add(command, "@Language", SqlDbType.NVarChar, registration.Language, 128);
        DocumentHandle result;
        using (var cancel = RelationalSession.CancelCommand(command, token))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Document was not registered.");
            result = new(reader.GetInt64(0), IndexSql.Hex((byte[])reader[1]));
        }
        await transaction.CommitAsync(token);
        return result;
    }

    // Replace only the explicitly supplied document's derived membership, never the scope.
    public async Task SetMembershipAsync(long documentKey, ImmutableArray<DocumentMembership> memberships,
        ImmutableArray<IndexLocator> locators, CancellationToken token = default)
    {
        if (memberships.IsDefault || locators.IsDefault || memberships.Length > IndexSql.MaximumBatch ||
            locators.Length > IndexSql.MaximumBatch || memberships.Select(m => m.ScopeResourceKey).Distinct().Count() != memberships.Length ||
            locators.Any(l => l.Kind is < 0 or > 2 || !memberships.Any(m => m.ScopeResourceKey == l.ScopeResourceKey)))
            throw new ArgumentException("Invalid document membership batch.");
        foreach (DocumentMembership member in memberships)
            if (member.DisplayName.Length > 65536 || member.Locator.Length > 65536 || member.NodeKey.Length > 65536 ||
                member.ParentNodeKey.Length > 65536 || member.SortOrdinal < 0) throw new IndexDocumentTooLargeException();
        if (locators.Any(l => l.Locator.Length > 65536)) throw new IndexDocumentTooLargeException();
        await using var connection = await _session.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await IndexSql.MutateHeadAsync(connection, transaction, token);
        await using (var clear = IndexSql.Command(connection, transaction, """
IF NOT EXISTS(SELECT 1 FROM surf.Document WHERE DocumentKey=@Document) THROW 51104,'Document not found.',1;
UPDATE i SET ReconciledScopeVersion=NULL,ReconciledAtUtc=NULL FROM surf.ScopeIndexState i
WHERE EXISTS(SELECT 1 FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 WHERE m.DocumentKey=@Document AND sr.ScopeKey=i.ScopeKey);
DELETE surf.DocumentLocator WHERE DocumentKey=@Document;
DELETE surf.ResourceDocument WHERE DocumentKey=@Document;
"""))
        {
            IndexSql.Add(clear, "@Document", SqlDbType.BigInt, documentKey);
            using var cancel = RelationalSession.CancelCommand(clear, token);
            await clear.ExecuteNonQueryAsync(token);
        }
        foreach (DocumentMembership membership in memberships)
        {
            await using var command = IndexSql.Command(connection, transaction, """
IF NOT EXISTS(SELECT 1 FROM surf.ScopeResource sr CROSS JOIN surf.Document d
 LEFT JOIN surf.DiagramRevision dr ON dr.DiagramRevisionKey=d.DiagramRevisionKey
 WHERE sr.ScopeResourceKey=@Resource AND d.DocumentKey=@Document AND
 ((d.Kind=0 AND sr.Kind IN (0,1)) OR (d.Kind IN (1,2) AND sr.SnapshotKey=d.SnapshotKey)
 OR (d.Kind=3 AND sr.DiagramKey=dr.DiagramKey)))
 THROW 51100,'Document membership has a different typed owner.',1;
INSERT surf.ResourceDocument(ScopeResourceKey,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal)
 VALUES(@Resource,@Document,@Name,@Locator,@Node,@Parent,@Ordinal);
UPDATE i SET ReconciledScopeVersion=NULL,ReconciledAtUtc=NULL FROM surf.ScopeIndexState i
 JOIN surf.ScopeResource sr ON sr.ScopeKey=i.ScopeKey WHERE sr.ScopeResourceKey=@Resource;
""");
            IndexSql.Add(command, "@Resource", SqlDbType.BigInt, membership.ScopeResourceKey);
            IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
            IndexSql.Add(command, "@Name", SqlDbType.NVarChar, membership.DisplayName, -1);
            IndexSql.Add(command, "@Locator", SqlDbType.NVarChar, membership.Locator, -1);
            IndexSql.Add(command, "@Node", SqlDbType.NVarChar, membership.NodeKey, -1);
            IndexSql.Add(command, "@Parent", SqlDbType.NVarChar, membership.ParentNodeKey, -1);
            IndexSql.Add(command, "@Ordinal", SqlDbType.BigInt, membership.SortOrdinal);
            using var cancel = RelationalSession.CancelCommand(command, token);
            await command.ExecuteNonQueryAsync(token);
        }
        foreach (IndexLocator locator in locators)
        {
            await using var command = IndexSql.Command(connection, transaction, """
INSERT surf.DocumentLocator(DocumentKey,ScopeResourceKey,Kind,OriginalLocator,LocatorHash,IsAscii)
VALUES(@Document,@Resource,@Kind,@Locator,@Hash,@Ascii);
""");
            IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
            IndexSql.Add(command, "@Resource", SqlDbType.BigInt, locator.ScopeResourceKey);
            IndexSql.Add(command, "@Kind", SqlDbType.Int, locator.Kind);
            IndexSql.Add(command, "@Locator", SqlDbType.NVarChar, locator.Locator, -1);
            IndexSql.Add(command, "@Hash", SqlDbType.Binary, ReferenceMetadata.LookupHash(locator.Locator), 32);
            IndexSql.Add(command, "@Ascii", SqlDbType.Bit, ReferenceMetadata.IsAscii(locator.Locator));
            using var cancel = RelationalSession.CancelCommand(command, token);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task<IndexRequestContext> CaptureContextAsync(long scopeKey, IEnumerable<long>? unloaded = null,
        IEnumerable<long>? documentKeys = null, CancellationToken token = default)
    {
        var unloadedKeys = (unloaded ?? []).Distinct().ToImmutableArray();
        var keys = (documentKeys ?? []).Distinct().ToImmutableArray();
        if (unloadedKeys.Length > IndexSql.MaximumBatch || keys.Length > IndexSql.MaximumBatch ||
            unloadedKeys.Any(k => k <= 0) || keys.Any(k => k <= 0)) throw new ArgumentException("Invalid context key batch.");
        await using var connection = await OpenReadAsync(token);
        await using var command = IndexSql.Command(connection, null, """
SELECT h.Generation,s.Version,sh.RowVersion,COALESCE(dg.Version,CONVERT(binary(8),0)),
CONVERT(bit,CASE WHEN i.ReconciledScopeVersion=s.Version AND i.ReconciledSnapshotVersion=sh.RowVersion
 AND i.ReconciledDiagramVersion=COALESCE(dg.Version,CONVERT(binary(8),0)) THEN 1 ELSE 0 END)
FROM surf.IndexCatalogueHead h CROSS JOIN surf.Scope s JOIN surf.SnapshotCatalogueHead sh ON sh.UserKey=s.ProfileKey
LEFT JOIN surf.StateCatalogueGeneration dg ON dg.ProfileKey=s.ProfileKey AND dg.Kind=1
LEFT JOIN surf.ScopeIndexState i ON i.ScopeKey=s.ScopeKey
WHERE h.Singleton=1 AND s.ScopeKey=@Scope;
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, scopeKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Scope not found.");
        return new(_session.Epoch, scopeKey, reader.GetInt64(0), IndexSql.Hex((byte[])reader[1]),
            IndexSql.Hex((byte[])reader[2]),IndexSql.Hex((byte[])reader[3]),reader.GetBoolean(4),
            unloadedKeys, keys, documentKeys != null);
    }

    // Only the coordinator that completed discovery may assert this, after all per-document writes.
    public async Task MarkDiscoveryReconciledAsync(IndexRequestContext context, CancellationToken token = default)
    {
        if (context.RestrictDocumentKeys || context.UnloadedScopeResourceKeys.Length != 0)
            throw new ArgumentException("Subset/unloaded requests cannot assert full scope discovery.");
        await using var connection = await _session.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, token);
        await IndexSql.MutateHeadAsync(connection, transaction, token);
        await using var command = IndexSql.Command(connection, transaction, """
UPDATE surf.ScopeIndexState SET ReconciledScopeVersion=@Version,ReconciledSnapshotVersion=@Snapshot,
 ReconciledDiagramVersion=@Diagram,ReconciledAtUtc=SYSDATETIMEOFFSET() WHERE ScopeKey=@Scope;
IF @@ROWCOUNT=0 INSERT surf.ScopeIndexState(ScopeKey,ReconciledScopeVersion,ReconciledSnapshotVersion,ReconciledDiagramVersion,ReconciledAtUtc)
 VALUES(@Scope,@Version,@Snapshot,@Diagram,SYSDATETIMEOFFSET());
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, context.ScopeKey);
        IndexSql.Add(command, "@Version", SqlDbType.Binary, IndexSql.Bytes(context.ScopeVersion, 8), 8);
        IndexSql.Add(command, "@Snapshot", SqlDbType.Binary, IndexSql.Bytes(context.SnapshotCatalogueVersion, 8), 8);
        IndexSql.Add(command, "@Diagram", SqlDbType.Binary, IndexSql.Bytes(context.DiagramCatalogueVersion, 8), 8);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }
    private async Task<SqlConnection> OpenReadAsync(CancellationToken token)
    {
        await _session.RequireReadyAsync(token);
        return await _session.OpenAsync(token);
    }
}

using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    public const int MaximumSymbolsPerDocument = 100000;
    public async Task<IndexWorkLease> BeginWorkAsync(DocumentHandle document, Guid publicationId,
        string sourceFingerprint, IndexPolicy policy, CancellationToken token = default)
    {
        IndexSql.Policy(policy);
        if (publicationId == Guid.Empty) throw new ArgumentException("Publication identity is required.");
        byte[] fingerprint = IndexSql.Bytes(sourceFingerprint, 32);
        Guid leaseId = Guid.NewGuid();
        await using var connection = await _session.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await IndexSql.MutateHeadAsync(connection, transaction, token);
        await using var command = IndexSql.Command(connection, transaction, """
IF EXISTS(SELECT 1 FROM surf.IndexWorkItem WHERE PublicationId=@Publication)
 THROW 51101,'Publication already exists; recover its outcome instead of creating new work.',1;
DECLARE @Versions TABLE(Version binary(8));
UPDATE surf.Document SET Freshness=CASE WHEN CurrentRevisionKey IS NULL THEN 0 ELSE 2 END
 OUTPUT INSERTED.Version INTO @Versions WHERE DocumentKey=@Document AND Version=@Version;
IF @@ROWCOUNT<>1 THROW 51102,'Document changed before indexing.',1;
INSERT surf.IndexWorkItem(DocumentKey,PublicationId,ExpectedDocumentVersion,SourceFingerprint,
 ParserVersion,RendererVersion,PolicyVersion,State,LeaseId,LeaseUntilUtc)
OUTPUT INSERTED.WorkItemKey,INSERTED.ExpectedDocumentVersion
SELECT @Document,@Publication,Version,@Fingerprint,@Parser,@Renderer,@Policy,1,@Lease,DATEADD(minute,10,SYSDATETIMEOFFSET())
FROM @Versions;
""");
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, document.DocumentKey);
        IndexSql.Add(command, "@Publication", SqlDbType.UniqueIdentifier, publicationId);
        IndexSql.Add(command, "@Version", SqlDbType.Binary, IndexSql.Bytes(document.Version, 8), 8);
        IndexSql.Add(command, "@Fingerprint", SqlDbType.Binary, fingerprint, 32);
        IndexSql.Add(command, "@Parser", SqlDbType.NVarChar, policy.ParserVersion, 128);
        IndexSql.Add(command, "@Renderer", SqlDbType.NVarChar, policy.RendererVersion, 128);
        IndexSql.Add(command, "@Policy", SqlDbType.NVarChar, policy.PolicyVersion, 128);
        IndexSql.Add(command, "@Lease", SqlDbType.UniqueIdentifier, leaseId);
        IndexWorkLease result;
        using (var cancel = RelationalSession.CancelCommand(command, token))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Work identity not returned.");
            result = new(reader.GetInt64(0), document.DocumentKey, publicationId, leaseId,
                IndexSql.Hex((byte[])reader[1]), IndexSql.Hex(fingerprint), policy);
        }
        await transaction.CommitAsync(token);
        return result;
    }

    // Positive identity is also recovery for a commit whose acknowledgement was lost.
    public async Task<long?> FindPublishedRevisionAsync(Guid publicationId, CancellationToken token = default)
    {
        await using var connection = await _session.OpenAsync(token);
        await using var command = IndexSql.Command(connection, null,
            "SELECT DocumentRevisionKey FROM surf.DocumentRevision WHERE PublicationId=@Publication;");
        IndexSql.Add(command, "@Publication", SqlDbType.UniqueIdentifier, publicationId);
        using var cancel = RelationalSession.CancelCommand(command, token);
        object? result = await command.ExecuteScalarAsync(token);
        return result is long key ? key : null;
    }

    public async Task<DocumentHandle> GetHandleAsync(long documentKey, CancellationToken token = default)
    {
        await using var connection = await _session.OpenAsync(token);
        await using var command = IndexSql.Command(connection, null,
            "SELECT Version FROM surf.Document WHERE DocumentKey=@Document;");
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        var version = await command.ExecuteScalarAsync(token) as byte[] ?? throw new KeyNotFoundException("Document not found.");
        return new(documentKey, IndexSql.Hex(version));
    }

    public async Task FailWorkAsync(IndexWorkLease lease, string errorCode, IndexFreshness freshness = IndexFreshness.Failed,
        CancellationToken token = default)
    {
        if (freshness is not (IndexFreshness.Failed or IndexFreshness.Stale or IndexFreshness.Missing or IndexFreshness.Inaccessible) ||
            errorCode.Length is < 1 or > 64 || errorCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("A redacted failure code and non-success freshness are required.");
        await using var connection = await _session.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await IndexSql.MutateHeadAsync(connection, transaction, token);
        await using var command = IndexSql.Command(connection, transaction, """
UPDATE surf.IndexWorkItem SET State=3,ErrorCode=@Error,FinishedAtUtc=SYSDATETIMEOFFSET(),LeaseUntilUtc=NULL
WHERE WorkItemKey=@Work AND DocumentKey=@Document AND PublicationId=@Publication AND LeaseId=@Lease AND State=1;
IF @@ROWCOUNT=1
 UPDATE surf.Document SET Freshness=@Freshness WHERE DocumentKey=@Document AND Version=@Version;
""");
        WorkParameters(command, lease);
        IndexSql.Add(command, "@Error", SqlDbType.VarChar, errorCode, 64);
        IndexSql.Add(command, "@Freshness", SqlDbType.Int, (int)freshness);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    // The publication is one source only. Callers schedule parsing; this store never parses a scope.
    public async Task<long> PublishAsync(IndexPublication publication, CancellationToken token = default)
    {
        IndexWorkLease lease = publication.Lease;
        IndexSql.Policy(lease.Policy);
        IndexSql.ShortValue(publication.Language, nameof(publication.Language));
        if (publication.Text.Length > RelationalContentStore.MaximumSingleContentBytes / 2 ||
            publication.Symbols.IsDefault || publication.Symbols.Length > MaximumSymbolsPerDocument)
            throw new IndexDocumentTooLargeException();
        foreach (SymbolInput symbol in publication.Symbols) ValidateSymbol(symbol);
        long? recovered;
        await using (var recoveryConnection = await _session.OpenAsync(token))
            recovered = await VerifyRecoveredPublicationAsync(recoveryConnection, null, publication, token);
        if (recovered.HasValue) return recovered.Value;

        string? path = await PhysicalPathAsync(lease.DocumentKey, token);
        await using IndexedFileRead? file = path == null ? null : await IndexedFileRead.OpenAsync(path,
            RelationalContentStore.MaximumSingleContentBytes / 2, token);
        if (file != null && (file.Fingerprint != publication.FileFingerprint ||
            file.Fingerprint.Sha256 != lease.SourceFingerprint || !string.Equals(file.Text, publication.Text, StringComparison.Ordinal)))
            throw new InvalidOperationException("Physical source changed before publication.");

        await using var connection = await _session.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await IndexSql.MutateHeadAsync(connection, transaction, token);
        recovered = await VerifyRecoveredPublicationAsync(connection, transaction, publication, token);
        if (recovered.HasValue)
        {
            await transaction.CommitAsync(token);
            return recovered.Value;
        }
        IndexedDocumentKind kind;
        long? fileKey, resourceKey, diagramKey;
        await using (var owner = IndexSql.Command(connection, transaction, """
SELECT d.Kind,d.FileSourceKey,d.SnapshotResourceKey,d.DiagramRevisionKey
FROM surf.Document d JOIN surf.IndexWorkItem w ON w.DocumentKey=d.DocumentKey
WHERE d.DocumentKey=@Document AND d.Version=@Version AND w.WorkItemKey=@Work
 AND w.PublicationId=@Publication AND w.LeaseId=@Lease AND w.State=1 AND w.LeaseUntilUtc>SYSDATETIMEOFFSET()
 AND w.SourceFingerprint=@Fingerprint AND w.ExpectedDocumentVersion=@Version
 AND CONVERT(varbinary(max),w.ParserVersion)=CONVERT(varbinary(max),@Parser)
 AND CONVERT(varbinary(max),w.RendererVersion)=CONVERT(varbinary(max),@Renderer)
 AND CONVERT(varbinary(max),w.PolicyVersion)=CONVERT(varbinary(max),@Policy);
"""))
        {
            WorkParameters(owner, lease);
            IndexSql.Add(owner, "@Fingerprint", SqlDbType.Binary, IndexSql.Bytes(lease.SourceFingerprint, 32), 32);
            IndexSql.Add(owner, "@Parser", SqlDbType.NVarChar, lease.Policy.ParserVersion, 128);
            IndexSql.Add(owner, "@Renderer", SqlDbType.NVarChar, lease.Policy.RendererVersion, 128);
            IndexSql.Add(owner, "@Policy", SqlDbType.NVarChar, lease.Policy.PolicyVersion, 128);
            using var cancel = RelationalSession.CancelCommand(owner, token);
            await using var reader = await owner.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Index work is obsolete, failed or expired.");
            kind = (IndexedDocumentKind)reader.GetInt32(0);
            fileKey = NullableKey(reader, 1); resourceKey = NullableKey(reader, 2); diagramKey = NullableKey(reader, 3);
        }
        await ValidateSourceAsync(connection, transaction, kind, resourceKey, diagramKey, publication, token);
        long contentKey = await _content.PutTextAsync(connection, transaction, publication.Text, token);
        long revisionKey;
        await using (var insert = IndexSql.Command(connection, transaction, """
INSERT surf.DocumentRevision(DocumentKey,PublicationId,ContentKey,FileSourceKey,SnapshotResourceKey,SourceRevisionKey,
 DiagramRevisionKey,SourceFingerprint,SourceByteCount,SourceWriteUtc,ParserVersion,RendererVersion,PolicyVersion,Language)
OUTPUT INSERTED.DocumentRevisionKey VALUES(@Document,@Publication,@Content,@File,@Resource,@SourceRevision,@Diagram,
 @Fingerprint,@Bytes,@Write,@Parser,@Renderer,@Policy,@Language);
"""))
        {
            IndexSql.Add(insert, "@Document", SqlDbType.BigInt, lease.DocumentKey);
            IndexSql.Add(insert, "@Publication", SqlDbType.UniqueIdentifier, lease.PublicationId);
            IndexSql.Add(insert, "@Content", SqlDbType.BigInt, contentKey);
            IndexSql.Add(insert, "@File", SqlDbType.BigInt, fileKey);
            IndexSql.Add(insert, "@Resource", SqlDbType.BigInt, resourceKey);
            IndexSql.Add(insert, "@SourceRevision", SqlDbType.BigInt, publication.SourceRevisionKey);
            IndexSql.Add(insert, "@Diagram", SqlDbType.BigInt, diagramKey);
            IndexSql.Add(insert, "@Fingerprint", SqlDbType.Binary, IndexSql.Bytes(lease.SourceFingerprint, 32), 32);
            IndexSql.Add(insert, "@Bytes", SqlDbType.BigInt, file?.Fingerprint.ByteCount);
            IndexSql.Add(insert, "@Write", SqlDbType.DateTimeOffset, file?.Fingerprint.LastWriteUtc);
            IndexSql.Add(insert, "@Parser", SqlDbType.NVarChar, lease.Policy.ParserVersion, 128);
            IndexSql.Add(insert, "@Renderer", SqlDbType.NVarChar, lease.Policy.RendererVersion, 128);
            IndexSql.Add(insert, "@Policy", SqlDbType.NVarChar, lease.Policy.PolicyVersion, 128);
            IndexSql.Add(insert, "@Language", SqlDbType.NVarChar, publication.Language, 128);
            using var cancel = RelationalSession.CancelCommand(insert, token);
            revisionKey = (long)(await insert.ExecuteScalarAsync(token))!;
        }
        await WriteSymbolsAsync(connection, transaction, lease.DocumentKey, revisionKey, publication.Symbols, token);
        await using var finish = IndexSql.Command(connection, transaction, """
IF @Kind<>2 INSERT surf.SearchProjection(DocumentKey,DocumentRevisionKey,ContentKey,ProjectionKind)
 VALUES(@Document,@Revision,@Content,CASE WHEN @Kind=3 THEN 2 ELSE @Kind END);
UPDATE surf.Document SET CurrentRevisionKey=@Revision,Freshness=1,Language=@Language WHERE DocumentKey=@Document;
UPDATE surf.IndexWorkItem SET State=2,FinishedAtUtc=SYSDATETIMEOFFSET(),PublishedRevisionKey=@Revision,LeaseUntilUtc=NULL
 WHERE WorkItemKey=@Work AND LeaseId=@Lease;
IF @File IS NOT NULL UPDATE surf.FileSource SET EncodingName=@Encoding,LastSuccessfulFingerprint=@Fingerprint,
 LastSuccessfulByteCount=@Bytes,LastSuccessfulWriteUtc=@Write,LastVerifiedAtUtc=SYSDATETIMEOFFSET() WHERE FileSourceKey=@File;
""");
        IndexSql.Add(finish, "@Kind", SqlDbType.Int, (int)kind);
        IndexSql.Add(finish, "@Document", SqlDbType.BigInt, lease.DocumentKey);
        IndexSql.Add(finish, "@Revision", SqlDbType.BigInt, revisionKey);
        IndexSql.Add(finish, "@Content", SqlDbType.BigInt, contentKey);
        IndexSql.Add(finish, "@Work", SqlDbType.BigInt, lease.WorkItemKey);
        IndexSql.Add(finish, "@Lease", SqlDbType.UniqueIdentifier, lease.LeaseId);
        IndexSql.Add(finish, "@Language", SqlDbType.NVarChar, publication.Language, 128);
        IndexSql.Add(finish, "@File", SqlDbType.BigInt, fileKey);
        IndexSql.Add(finish, "@Encoding", SqlDbType.NVarChar, file?.EncodingName, 128);
        IndexSql.Add(finish, "@Fingerprint", SqlDbType.Binary, IndexSql.Bytes(lease.SourceFingerprint, 32), 32);
        IndexSql.Add(finish, "@Bytes", SqlDbType.BigInt, file?.Fingerprint.ByteCount);
        IndexSql.Add(finish, "@Write", SqlDbType.DateTimeOffset, file?.Fingerprint.LastWriteUtc);
        using (var cancel = RelationalSession.CancelCommand(finish, token)) await finish.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return revisionKey;
    }

    private static void ValidateSymbol(SymbolInput s)
    {
        IndexSql.ShortValue(s.Language, nameof(s.Language));
        if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Locator) ||
            s.Name.Length > 65536 || s.QualifiedName.Length > 65536 || s.Locator.Length > 65536 || s.ContainerName.Length > 65536 ||
            (int)s.Kind is < 0 or > 12 || s.LineNumber < 1 || s.ColumnNumber < 1 || s.EndLineNumber < s.LineNumber ||
            s.EndColumnNumber < 1 || (s.EndLineNumber == s.LineNumber && s.EndColumnNumber < s.ColumnNumber) ||
            s.ParameterCount < 0 || s.MinimumArgumentCount < 0 || s.MaximumArgumentCount < 0 ||
            s.MinimumArgumentCount > s.MaximumArgumentCount) throw new ArgumentException("Invalid symbol metadata.");
    }
    private static void WorkParameters(SqlCommand command, IndexWorkLease lease)
    {
        IndexSql.Add(command, "@Work", SqlDbType.BigInt, lease.WorkItemKey);
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, lease.DocumentKey);
        IndexSql.Add(command, "@Publication", SqlDbType.UniqueIdentifier, lease.PublicationId);
        IndexSql.Add(command, "@Lease", SqlDbType.UniqueIdentifier, lease.LeaseId);
        IndexSql.Add(command, "@Version", SqlDbType.Binary, IndexSql.Bytes(lease.ExpectedDocumentVersion, 8), 8);
    }
    private static async Task<long?> VerifyRecoveredPublicationAsync(SqlConnection connection, SqlTransaction? transaction,
        IndexPublication publication, CancellationToken token)
    {
        await using var command = IndexSql.Command(connection, transaction, """
SELECT r.DocumentRevisionKey,r.DocumentKey,r.SourceFingerprint,r.ParserVersion,r.RendererVersion,r.PolicyVersion,r.Language,
 CONVERT(bit,CASE WHEN CONVERT(varbinary(max),c.Text)=CONVERT(varbinary(max),@Text) THEN 1 ELSE 0 END)
FROM surf.DocumentRevision r JOIN surf.TextContent c ON c.ContentKey=r.ContentKey WHERE r.PublicationId=@Publication;
""");
        IndexSql.Add(command, "@Text", SqlDbType.NVarChar, publication.Text, -1);
        IndexSql.Add(command, "@Publication", SqlDbType.UniqueIdentifier, publication.Lease.PublicationId);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        IndexWorkLease lease = publication.Lease;
        if (reader.GetInt64(1) != lease.DocumentKey || IndexSql.Hex((byte[])reader[2]) != lease.SourceFingerprint ||
            reader.GetString(3) != lease.Policy.ParserVersion || reader.GetString(4) != lease.Policy.RendererVersion ||
            reader.GetString(5) != lease.Policy.PolicyVersion || reader.GetString(6) != publication.Language || !reader.GetBoolean(7))
            throw new InvalidOperationException("Publication identity was reused for different source content or policy.");
        return reader.GetInt64(0);
    }
    private static long? NullableKey(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private async Task<string?> PhysicalPathAsync(long documentKey, CancellationToken token)
    {
        await using var connection = await _session.OpenAsync(token);
        await using var command = IndexSql.Command(connection, null, """
SELECT f.OriginalPath FROM surf.Document d JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey WHERE d.DocumentKey=@Document;
""");
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        return await command.ExecuteScalarAsync(token) as string;
    }
}

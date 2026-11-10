using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    public async Task<bool> RenewWorkAsync(IndexWorkLease lease, CancellationToken token = default)
    {
        await using var connection = await _session.OpenAsync(token);
        await using var command = IndexSql.Command(connection, null, """
UPDATE w SET LeaseUntilUtc=DATEADD(minute,10,SYSDATETIMEOFFSET()) FROM surf.IndexWorkItem w
JOIN surf.Document d ON d.DocumentKey=w.DocumentKey
WHERE w.WorkItemKey=@Work AND w.DocumentKey=@Document AND w.PublicationId=@Publication AND w.LeaseId=@Lease
 AND w.State=1 AND w.LeaseUntilUtc>SYSDATETIMEOFFSET() AND d.Version=@Version AND w.ExpectedDocumentVersion=@Version;
""");
        WorkParameters(command, lease);
        using var cancel = RelationalSession.CancelCommand(command, token);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<IndexWorkLease?> ReclaimExpiredWorkAsync(Guid publicationId, CancellationToken token = default)
    {
        Guid leaseId = Guid.NewGuid();
        await using var connection = await _session.OpenAsync(token);
        await using var command = IndexSql.Command(connection, null, """
UPDATE w SET LeaseId=@Lease,LeaseUntilUtc=DATEADD(minute,10,SYSDATETIMEOFFSET())
OUTPUT INSERTED.WorkItemKey,INSERTED.DocumentKey,INSERTED.ExpectedDocumentVersion,INSERTED.SourceFingerprint,
 INSERTED.ParserVersion,INSERTED.RendererVersion,INSERTED.PolicyVersion
FROM surf.IndexWorkItem w JOIN surf.Document d ON d.DocumentKey=w.DocumentKey
WHERE w.PublicationId=@Publication AND w.State=1 AND w.LeaseUntilUtc<=SYSDATETIMEOFFSET()
 AND d.Version=w.ExpectedDocumentVersion;
""");
        IndexSql.Add(command, "@Lease", SqlDbType.UniqueIdentifier, leaseId);
        IndexSql.Add(command, "@Publication", SqlDbType.UniqueIdentifier, publicationId);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetInt64(0), reader.GetInt64(1), publicationId, leaseId,
            IndexSql.Hex((byte[])reader[2]), IndexSql.Hex((byte[])reader[3]),
            new(reader.GetString(4), reader.GetString(5), reader.GetString(6)));
    }
}

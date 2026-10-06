using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Index;

internal static class IndexSql
{
    internal const int MaximumBatch = 512;
    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.Unicode.GetBytes(value));
    internal static string Hex(byte[] value) => Convert.ToHexString(value);
    internal static byte[] Bytes(string value, int size)
    {
        byte[] result = Convert.FromHexString(value);
        if (result.Length != size) throw new ArgumentException("Invalid index token length.");
        return result;
    }
    internal static void Add(SqlCommand command, string name, SqlDbType type, object? value, int size = 0) =>
        command.Parameters.Add(RelationalSession.Parameter(name, type, value, size));
    internal static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return command;
    }
    internal static void Keys(SqlCommand command, string name, IEnumerable<long> keys)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(long));
        foreach (long key in keys.Distinct())
        {
            if (key <= 0 || table.Rows.Count == MaximumBatch) throw new ArgumentException("Invalid or oversized key batch.");
            table.Rows.Add(key);
        }
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Structured) { TypeName = "surf.IndexKeyBatch", Value = table });
    }
    internal static async Task MutateHeadAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = Command(connection, transaction,
            "UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;");
        using var cancel = RelationalSession.CancelCommand(command, token);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Index schema is not installed.");
    }
    internal static void ShortValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Invalid index policy/language.", name);
    }
    internal static void Policy(IndexPolicy policy)
    {
        ShortValue(policy.ParserVersion, nameof(policy.ParserVersion));
        ShortValue(policy.RendererVersion, nameof(policy.RendererVersion));
        ShortValue(policy.PolicyVersion, nameof(policy.PolicyVersion));
    }
    internal static async Task CheckContextAsync(SqlConnection connection, SqlTransaction transaction,
        RelationalSession session, IndexRequestContext context, CancellationToken token)
    {
        if (context.Epoch != session.Epoch) throw new IndexGenerationChangedException();
        await using var command = Command(connection, transaction, """
SELECT h.Generation, s.Version, sh.RowVersion,COALESCE(dg.Version,CONVERT(binary(8),0)),
    CONVERT(bit, CASE WHEN i.ReconciledScopeVersion=s.Version AND i.ReconciledSnapshotVersion=sh.RowVersion
      AND i.ReconciledDiagramVersion=COALESCE(dg.Version,CONVERT(binary(8),0)) THEN 1 ELSE 0 END)
FROM surf.IndexCatalogueHead h CROSS JOIN surf.Scope s
JOIN surf.SnapshotCatalogueHead sh ON sh.UserKey=s.ProfileKey
LEFT JOIN surf.StateCatalogueGeneration dg ON dg.ProfileKey=s.ProfileKey AND dg.Kind=1
LEFT JOIN surf.ScopeIndexState i ON i.ScopeKey=s.ScopeKey
WHERE h.Singleton=1 AND s.ScopeKey=@Scope;
""");
        Add(command, "@Scope", SqlDbType.BigInt, context.ScopeKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetInt64(0) != context.CatalogueGeneration ||
            Hex((byte[])reader[1]) != context.ScopeVersion || Hex((byte[])reader[2]) != context.SnapshotCatalogueVersion ||
            Hex((byte[])reader[3]) != context.DiagramCatalogueVersion || reader.GetBoolean(4) != context.DiscoveryReconciled)
            throw new IndexGenerationChangedException();
    }
    internal static void ScopeParameters(SqlCommand command, IndexRequestContext context)
    {
        Add(command, "@Scope", SqlDbType.BigInt, context.ScopeKey);
        Add(command, "@Restricted", SqlDbType.Bit, context.RestrictDocumentKeys);
        Keys(command, "@Unloaded", context.UnloadedScopeResourceKeys);
        Keys(command, "@Documents", context.DocumentKeys);
    }
    // Domain pointer changes are detected independently of the derived catalogue head.
    internal const string FreshnessExpression = """
CASE WHEN d.Freshness<>1 THEN d.Freshness
 WHEN d.Kind IN (1,2) AND (r.SourceRevisionKey IS NULL OR r.SourceRevisionKey<>source.CurrentRevisionKey OR source.CurrentRevisionKey IS NULL) THEN 2
 WHEN d.Kind=3 AND (diagram.DiagramKey IS NULL OR diagram.CurrentRevisionKey<>d.DiagramRevisionKey OR diagram.CurrentRevisionKey IS NULL) THEN 2
 ELSE d.Freshness END
""";
    internal const string OwnerJoins = """
LEFT JOIN surf.SnapshotResource source ON source.ResourceKey=d.SnapshotResourceKey
LEFT JOIN surf.DiagramRevision ownerDiagram ON ownerDiagram.DiagramRevisionKey=d.DiagramRevisionKey
LEFT JOIN surf.Diagram diagram ON diagram.DiagramKey=ownerDiagram.DiagramKey
""";
    internal const string ScopePredicate = """
sr.ScopeKey=@Scope AND NOT EXISTS (SELECT 1 FROM @Unloaded u WHERE u.Id=sr.ScopeResourceKey)
AND (@Restricted=0 OR EXISTS (SELECT 1 FROM @Documents k WHERE k.Id=d.DocumentKey))
""";
}

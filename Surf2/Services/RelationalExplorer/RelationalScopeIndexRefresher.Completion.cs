using System.Data;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalScopeIndexRefresher
{
    private bool _completionInstalled;
    private readonly ConcurrentDictionary<long, byte> _verifiedPhysicalRoots = new();
    private const string CompletionPolicy = "reference-roots-v1:database-metadata-v1:definition-v1:explorer-v1";

    // This optional, derived extension does not change the v1 migration scripts or recovery checksum.
    private async Task EnsureCompletionStoreAsync(CancellationToken ct)
    {
        if (_completionInstalled) return;
        string resource = typeof(RelationalScopeIndexRefresher).Assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(".002.ReferenceCompletion.sql", StringComparison.Ordinal));
        using var stream = typeof(RelationalScopeIndexRefresher).Assembly.GetManifestResourceStream(resource)!;
        using var text = new StreamReader(stream, Encoding.UTF8);
        string script = await text.ReadToEndAsync(ct).ConfigureAwait(false);
        byte[] checksum = RelationalSchemaInstaller.ScriptChecksum([script]);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, transaction, """
DECLARE @Lock int;
EXEC @Lock=sys.sp_getapplock @Resource=N'Surf2.ReferenceCompletion.v1', @LockMode='Exclusive',
 @LockOwner='Transaction', @LockTimeout=10000;
IF @Lock<0 THROW 51110,'Reference completion installation lock unavailable.',1;
IF NOT EXISTS(SELECT 1 FROM surf.StorageFormatInfo WHERE Singleton=1 AND State='Ready')
 THROW 51111,'Reference completion requires a ready database.',1;
SELECT CASE WHEN OBJECT_ID(N'surf.ReferenceCompletionExtension',N'U') IS NULL THEN 0 ELSE 1 END;
""");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        bool installed = (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 1;
        if (!installed)
        {
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            command.CommandText = "INSERT surf.ReferenceCompletionExtension(Singleton,Version,Checksum) VALUES(1,1,@Checksum);";
            IndexSql.Add(command, "@Checksum", SqlDbType.Binary, checksum, 32);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            command.CommandText = "SELECT COUNT_BIG(*) FROM surf.ReferenceCompletionExtension WHERE Singleton=1 AND Version=1 AND Checksum=@Checksum;";
            IndexSql.Add(command, "@Checksum", SqlDbType.Binary, checksum, 32);
            if (!Equals(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), 1L))
                throw new InvalidOperationException("Unsupported reference completion extension.");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        _completionInstalled = true;
    }

    private static bool CapturedRoot(ExplorerResource resource) =>
        resource.Kind == ResourceKind.DatabaseSnapshot && resource.Snapshot != null ||
        resource.Kind == ResourceKind.Diagram && resource.DiagramRevisionKey.HasValue;

    private static bool PhysicalRoot(ExplorerResource resource) => resource.Kind is ResourceKind.File or ResourceKind.Folder;

    private static byte[] CompletionDependency(ExplorerScope scope, ExplorerResource resource, ExplorerIndexLanguagePolicy languagePolicy)
    {
        // Length-prefixed fields avoid collisions between hierarchy/alias names containing delimiters.
        var key = new StringBuilder();
        void Field(string value) => key.Append(value.Length).Append(':').Append(value);
        Field(CompletionPolicy); Field(RelationalIndexStore.TableRendererVersion); Field(RelationalIndexStore.DiagramRendererVersion);
        if (PhysicalRoot(resource))
            foreach (var extension in languagePolicy.Extensions) { Field(extension.Extension); Field(extension.Language); }
        var aliases = scope.Resources.Where(r => PhysicalRoot(resource) ? PhysicalRoot(r) : resource.Kind == ResourceKind.DatabaseSnapshot
            ? r.Snapshot?.SnapshotKey == resource.Snapshot!.SnapshotKey
            : r.DiagramRevisionKey == resource.DiagramRevisionKey).OrderBy(r => r.ScopeResourceKey);
        foreach (var alias in aliases)
        {
            Field(alias.ScopeResourceKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Field(alias.SortOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Field(alias.Path); Field(alias.Alias); Field(alias.DiagramName ?? "");
            Field(alias.IncludeChildren ? "1" : "0");
            Field(alias.DiagramRevisionKey?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
            if (alias.Snapshot is { } snapshot)
            {
                Field(snapshot.SnapshotKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Field(snapshot.SnapshotId); Field(snapshot.DisplayName); Field(Convert.ToHexString(snapshot.RowVersion));
            }
        }
        return IndexSql.Hash(key.ToString());
    }

    private async Task<Dictionary<long, long>> ReadCompletedRootsAsync(ExplorerScope scope, ExplorerIndexLanguagePolicy languagePolicy, CancellationToken ct)
    {
        var resources = scope.Resources.Where(r => r.IsLoaded && (CapturedRoot(r) || PhysicalRoot(r) && _verifiedPhysicalRoots.ContainsKey(r.ScopeResourceKey))).ToArray();
        if (resources.Length == 0) return [];
        var context = await _index.CaptureContextAsync(scope.Context.ScopeKey, scope.Context.UnloadedScopeResourceKeys, token: ct).ConfigureAwait(false);
        RequireSameDomain(scope.Context, context);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, transaction, """
SELECT c.ScopeResourceKey,c.DependencyHash,c.DocumentCount
FROM surf.ReferenceResourceCompletion c JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=c.ScopeResourceKey
JOIN @Roots wanted ON wanted.Id=c.ScopeResourceKey
LEFT JOIN surf.Diagram diagram ON diagram.DiagramKey=sr.DiagramKey
OUTER APPLY(SELECT TOP(1) RowVersion FROM surf.SnapshotResource source
 WHERE source.SnapshotKey=sr.SnapshotKey ORDER BY RowVersion DESC) latest
WHERE sr.ScopeKey=@Scope AND c.SourceVersion=COALESCE(latest.RowVersion,diagram.Version,CONVERT(binary(8),0));
""");
        IndexSql.Add(command, "@Scope", SqlDbType.BigInt, context.ScopeKey);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        var result = new Dictionary<long, long>();
        foreach (var batch in resources.Chunk(IndexSql.MaximumBatch))
        {
            if (command.Parameters.Contains("@Roots")) command.Parameters.RemoveAt("@Roots");
            IndexSql.Keys(command, "@Roots", batch.Select(r => r.ScopeResourceKey));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long key = reader.GetInt64(0);
                if (((byte[])reader[1]).AsSpan().SequenceEqual(CompletionDependency(scope, batch.Single(r => r.ScopeResourceKey == key), languagePolicy)))
                    result.Add(key, reader.GetInt64(2));
            }
        }
        return result;
    }

    private async Task CompleteRootsAsync(ExplorerScope scope, IEnumerable<long> roots,
        HashSet<(long Resource, long Document)> seen, ExplorerIndexLanguagePolicy languagePolicy, bool allowIncomplete, CancellationToken ct)
    {
        var resources = scope.Resources.Where(r => roots.Contains(r.ScopeResourceKey) && (CapturedRoot(r) || PhysicalRoot(r))).ToArray();
        if (resources.Length == 0) return;
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        // Writers take the catalogue lock before document/membership locks; completion uses the same order.
        await using (var head = IndexSql.Command(connection, transaction,
            "SELECT Generation FROM surf.IndexCatalogueHead WITH(UPDLOCK,HOLDLOCK) WHERE Singleton=1;"))
        {
            using var cancel = RelationalSession.CancelCommand(head, ct);
            await head.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        await FenceDomainAsync(connection, transaction, scope.Context, ct).ConfigureAwait(false);
        var verifiedPhysical = new List<long>();
        foreach (var resource in resources)
        {
            await using var command = IndexSql.Command(connection, transaction, $"""
IF EXISTS(SELECT 1 FROM surf.ResourceDocument m JOIN surf.Document d ON d.DocumentKey=m.DocumentKey
 LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
 {IndexSql.OwnerJoins}
 WHERE m.ScopeResourceKey=@Root AND ({IndexSql.FreshnessExpression})<>1)
BEGIN
 IF @AllowIncomplete=0 THROW 51112,'Cannot complete a resource with stale documents.',1;
 DELETE surf.ReferenceResourceCompletion WHERE ScopeResourceKey=@Root;
 SELECT CONVERT(bit,0);
END
ELSE
BEGIN
DELETE surf.ReferenceResourceCompletion WHERE ScopeResourceKey=@Root;
INSERT surf.ReferenceResourceCompletion(ScopeResourceKey,DependencyHash,SourceVersion,DocumentCount)
 SELECT @Root,@Hash,COALESCE(latest.RowVersion,diagram.Version,CONVERT(binary(8),0)),@Count
 FROM surf.ScopeResource sr LEFT JOIN surf.Diagram diagram ON diagram.DiagramKey=sr.DiagramKey
 OUTER APPLY(SELECT TOP(1) RowVersion FROM surf.SnapshotResource source
  WHERE source.SnapshotKey=sr.SnapshotKey ORDER BY RowVersion DESC) latest
 WHERE sr.ScopeResourceKey=@Root;
 SELECT CONVERT(bit,1);
END
""");
            IndexSql.Add(command, "@Root", SqlDbType.BigInt, resource.ScopeResourceKey);
            IndexSql.Add(command, "@Hash", SqlDbType.Binary, CompletionDependency(scope, resource, languagePolicy), 32);
            IndexSql.Add(command, "@Count", SqlDbType.BigInt, (long)seen.Count(m => m.Resource == resource.ScopeResourceKey));
            IndexSql.Add(command, "@AllowIncomplete", SqlDbType.Bit, allowIncomplete);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            if (Equals(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), true) && PhysicalRoot(resource))
                verifiedPhysical.Add(resource.ScopeResourceKey);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        foreach (long root in verifiedPhysical) _verifiedPhysicalRoots[root] = 0;
    }

    private async Task<long> CountReusedDocumentsAsync(IEnumerable<long> roots, CancellationToken ct)
    {
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, null, "CREATE TABLE #ReusedRoots(Id bigint NOT NULL PRIMARY KEY);");
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        foreach (var batch in roots.Chunk(IndexSql.MaximumBatch))
        {
            command.Parameters.Clear(); IndexSql.Keys(command, "@Roots", batch);
            command.CommandText = "INSERT #ReusedRoots(Id) SELECT Id FROM @Roots;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT_BIG(DISTINCT m.DocumentKey) FROM surf.ResourceDocument m JOIN #ReusedRoots wanted ON wanted.Id=m.ScopeResourceKey; DROP TABLE #ReusedRoots;";
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    // Foreground navigation may rebase an unchanged verified view while unrelated file writes
    // advance the global generation. This reads only root proofs, never waits for the worker gate.
    internal async Task<ExplorerIndexRefreshProgress> VerifyCompletedViewAsync(ExplorerScope view,
        ExplorerIndexLanguagePolicy languagePolicy, CancellationToken ct)
    {
        var context = await _index.CaptureContextAsync(view.Context.ScopeKey, view.Context.UnloadedScopeResourceKeys, token: ct).ConfigureAwait(false);
        RequireSameDomain(view.Context, context);
        var current = view with { Context = context };
        var ready = await ReadCompletedRootsAsync(current, languagePolicy, ct).ConfigureAwait(false);
        if (current.Resources.Any(r => r.IsLoaded && !ready.ContainsKey(r.ScopeResourceKey)) ||
            !await _index.IsContextCurrentAsync(context, ct).ConfigureAwait(false)) throw new IndexGenerationChangedException();
        return new(0, 0, 0, 0, context.UnloadedScopeResourceKeys.Length, true, true, context,
            Phase: "Finished", ReusedResources: ready.Count);
    }
}

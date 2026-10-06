using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    private sealed record SavedBinding(long? Snapshot, long? Resource, SavedDocumentTargetState State);

    // Migration-only resolution. Existing typed targets are retained, including across renames.
    internal async Task ResolveSavedDocumentBindingsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        _session.RejectValidationWrite();
        long after = 0;
        while (true)
        {
            long key; long? scope; string path;
            await using (var command = Command(connection, transaction, """
SELECT TOP(1) w.DocumentWindowKey,b.ResolvedScopeKey,CONVERT(bigint,DATALENGTH(w.FilePath)),w.FilePath
FROM surf.DocumentWindowState w LEFT JOIN surf.Workbench b ON b.WorkbenchKey=w.WorkbenchKey
WHERE w.DocumentWindowKey>@After AND w.BoundResourceKey IS NULL ORDER BY w.DocumentWindowKey;
""", Key(after, "@After")))
            {
                using var cancellation = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                if (!await reader.ReadAsync(ct)) break;
                key = reader.GetInt64(0); scope = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                path = (await StringAsync(reader, 2, new(_limits), ct))!;
            }
            var binding = await ResolveSavedPathAsync(connection, transaction, path, scope, ct);
            await ExecuteAsync(connection, transaction, """
UPDATE surf.DocumentWindowState SET BoundSnapshotKey=@Snapshot,BoundResourceKey=@Resource,TargetState=@State
WHERE DocumentWindowKey=@Key;
""", ct, Key(key), RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, binding.Snapshot),
                RelationalSession.Parameter("@Resource", SqlDbType.BigInt, binding.Resource),
                RelationalSession.Parameter("@State", SqlDbType.Int, (int)binding.State));
            after = key;
        }
        after = 0;
        while (true)
        {
            long key; long? scope; string source, target;
            await using (var command = Command(connection, transaction, """
SELECT TOP(1) r.ReferenceConnectionLineKey,b.ResolvedScopeKey,
CONVERT(bigint,DATALENGTH(r.SourceFilePath)),r.SourceFilePath,
CONVERT(bigint,DATALENGTH(r.TargetFilePath)),r.TargetFilePath
FROM surf.ReferenceConnectionLine r JOIN surf.Workbench b ON b.WorkbenchKey=r.WorkbenchKey
WHERE r.ReferenceConnectionLineKey>@After ORDER BY r.ReferenceConnectionLineKey;
""", Key(after, "@After")))
            {
                using var cancellation = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                if (!await reader.ReadAsync(ct)) break;
                key = reader.GetInt64(0); scope = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                var budget = new StateBudget(_limits);
                source = (await StringAsync(reader, 2, budget, ct))!;
                target = (await StringAsync(reader, 4, budget, ct))!;
            }
            var a = await ResolveSavedPathAsync(connection, transaction, source, scope, ct);
            var b = await ResolveSavedPathAsync(connection, transaction, target, scope, ct);
            await ExecuteAsync(connection, transaction, """
UPDATE surf.ReferenceConnectionLine SET
SourceBoundSnapshotKey=@SS,SourceBoundResourceKey=@SR,SourceTargetState=@SA,
TargetBoundSnapshotKey=@TS,TargetBoundResourceKey=@TR,TargetTargetState=@TA
WHERE ReferenceConnectionLineKey=@Key AND SourceBoundResourceKey IS NULL AND TargetBoundResourceKey IS NULL;
""", ct, Key(key), RelationalSession.Parameter("@SS", SqlDbType.BigInt, a.Snapshot),
                RelationalSession.Parameter("@SR", SqlDbType.BigInt, a.Resource), RelationalSession.Parameter("@SA", SqlDbType.Int, (int)a.State),
                RelationalSession.Parameter("@TS", SqlDbType.BigInt, b.Snapshot),
                RelationalSession.Parameter("@TR", SqlDbType.BigInt, b.Resource), RelationalSession.Parameter("@TA", SqlDbType.Int, (int)b.State));
            after = key;
        }
    }

    private async Task<SavedBinding> ResolveSavedPathAsync(SqlConnection connection, SqlTransaction transaction,
        string path, long? scope, CancellationToken ct)
    {
        if (!DatabaseDocumentService.IsDatabaseDocumentPath(path)) return new(null, null, SavedDocumentTargetState.External);
        if (!DatabaseDocumentService.TryParseDocumentPath(path, out var reference)) return new(null, null, SavedDocumentTargetState.Missing);
        var candidates = new List<(long Key, int Rank)>();
        var budget = new StateBudget(_limits);
        await using (var command = Command(connection, transaction, """
SELECT TOP (@Limit) s.SnapshotKey,CONVERT(bigint,DATALENGTH(s.OriginalSnapshotId)),s.OriginalSnapshotId,
CONVERT(bigint,DATALENGTH(s.DisplayName)),s.DisplayName,CONVERT(bigint,DATALENGTH(s.DatabaseName)),s.DatabaseName
FROM surf.DatabaseSnapshot s WHERE s.IsPublished=1
AND (@Scope IS NULL OR EXISTS(SELECT 1 FROM surf.ScopeResource sr WHERE sr.ScopeKey=@Scope AND sr.SnapshotKey=s.SnapshotKey))
ORDER BY s.SortOrdinal,s.SnapshotKey;
""", Key((long)_limits.MaximumRows + 1, "@Limit"), RelationalSession.Parameter("@Scope", SqlDbType.BigInt, scope)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Row(); long key = reader.GetInt64(0);
                var header = new DatabaseMetadataSnapshot
                {
                    SnapshotId = (await StringAsync(reader, 1, budget, ct))!,
                    DisplayName = (await StringAsync(reader, 3, budget, ct))!,
                    DatabaseName = (await StringAsync(reader, 5, budget, ct))!
                };
                string canonical = DatabaseDocumentService.CreateCanonicalSnapshotDocumentPath(header)[5..];
                string readable = DatabaseDocumentService.CreateSnapshotDocumentPath(header)[5..];
                int rank = Equal(reference.SnapshotId, header.SnapshotId) || Equal(reference.SnapshotId, canonical) ? 0 :
                    Equal(reference.SnapshotId, readable) ? 1 : -1;
                if (rank >= 0) candidates.Add((key, rank));
            }
        }
        if (candidates.Count == 0) return new(null, null, SavedDocumentTargetState.Missing);
        int best = candidates.Min(x => x.Rank);
        var matches = candidates.Where(x => x.Rank == best).ToArray();
        if (matches.Length != 1) return new(null, null, SavedDocumentTargetState.Ambiguous);
        long snapshot = matches[0].Key;
        DatabaseVersionedResourceKind? kind = reference.DocumentType.ToLowerInvariant() switch
        {
            "table" => DatabaseVersionedResourceKind.TableMetadata,
            "table-data" => DatabaseVersionedResourceKind.TableData,
            "object" when reference.ObjectKind.HasValue => SnapshotIdentity.ResourceKind(reference.ObjectKind.Value),
            _ => null
        };
        if (kind == null && reference.ObjectKind != SqlDatabaseObjectKind.Unknown)
            return new(null, null, SavedDocumentTargetState.Missing);
        long? resource = null;
        budget = new(_limits);
        await using (var command = Command(connection, transaction, """
SELECT TOP (@Limit) ResourceKey,CONVERT(bigint,DATALENGTH(SchemaName)),SchemaName,
CONVERT(bigint,DATALENGTH(ObjectName)),ObjectName FROM surf.SnapshotResource
WHERE SnapshotKey=@Snapshot AND (Kind=@Kind OR Kind IS NULL AND @Kind IS NULL) AND CurrentRevisionKey IS NOT NULL
ORDER BY CurrentSortOrdinal,ResourceKey;
""", Key(snapshot, "@Snapshot"), Key((long)_limits.MaximumRows + 1, "@Limit"),
            RelationalSession.Parameter("@Kind", SqlDbType.Int, kind.HasValue ? (int)kind.Value : null)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                budget.Row(); long key = reader.GetInt64(0);
                string schema = (await StringAsync(reader, 1, budget, ct))!;
                string name = (await StringAsync(reader, 3, budget, ct))!;
                if (!Equal(reference.FullName, SqlName.FormatPlainMultipartName(schema, name))) continue;
                if (resource.HasValue) return new(null, null, SavedDocumentTargetState.Ambiguous);
                resource = key;
            }
        }
        return resource.HasValue ? new(snapshot, resource, SavedDocumentTargetState.Resolved) : new(null, null, SavedDocumentTargetState.Missing);
    }

    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

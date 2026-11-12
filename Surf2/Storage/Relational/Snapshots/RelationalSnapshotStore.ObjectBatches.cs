using System.Data;
using System.Text.Json;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed record SnapshotObjectTextIdentity(long RevisionKey, long ContentKey, long ByteCount, byte[] ContentHash);

public sealed partial class RelationalSnapshotStore
{
    public const int MaximumObjectBatchSize = 128;
    public const long MaximumDefinitionBatchBytes = 32L * 1024 * 1024;

    /// <summary>Reads immutable text identities, without fetching definition bodies.</summary>
    public async Task<IReadOnlyDictionary<long, SnapshotObjectTextIdentity>> ReadObjectTextIdentitiesAsync(
        IReadOnlyList<long> revisionKeys, CancellationToken ct = default)
    {
        long[] requested = ObjectBatchKeys(revisionKeys, ct);
        var result = new Dictionary<long, SnapshotObjectTextIdentity>();
        if (requested.Length == 0) return result;
        var memories = session.SnapshotMemory.All();
        foreach (long revision in requested)
            foreach (var memory in memories)
                if (memory.ObjectIdentities.TryGetValue(revision, out var identity))
                { result.Add(revision, identity with { ContentHash = identity.ContentHash.ToArray() }); break; }
        if (result.Count == requested.Length) return result;
        result.Clear();
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT o.RevisionKey,o.DefinitionContentKey,c.ByteCount,c.ContentHash
            FROM OPENJSON(@Keys) WITH (RevisionKey bigint '$') requested
            JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=requested.RevisionKey
            JOIN surf.TextContent c ON c.ContentKey=o.DefinitionContentKey
            JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=o.RevisionKey
            JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
            JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
            WHERE rr.IsSealed=1 AND s.IsPublished=1;
            """, Text("@Keys", JsonSerializer.Serialize(requested)));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long revision = reader.GetInt64(0), bytes = reader.GetInt64(2);
            SnapshotReadGuard.RequireBytes(bytes, SnapshotReadLimits.MaximumDefinitionBytes, command);
            result.Add(revision, new(revision, reader.GetInt64(1), bytes, (byte[])reader.GetValue(3)));
        }
        if (result.Count != requested.Length) throw new KeyNotFoundException("A requested published object revision no longer exists.");
        return result;
    }

    /// <summary>One bounded read for selected immutable definitions and their scalar metadata.</summary>
    public async Task<IReadOnlyDictionary<long, SqlDatabaseObject>> ReadObjectsAsync(
        IReadOnlyList<long> revisionKeys, CancellationToken ct = default)
    {
        long[] requested = ObjectBatchKeys(revisionKeys, ct);
        var result = new Dictionary<long, SqlDatabaseObject>();
        if (requested.Length == 0) return result;
        var memories = session.SnapshotMemory.All(); long cachedBytes = 0;
        foreach (long revision in requested)
            foreach (var memory in memories)
                if (memory.Objects.TryGetValue(revision, out var value))
                {
                    long bytes = value.Definition.Length * 2L;
                    SnapshotReadGuard.RequireBytes(bytes, _readLimits.SelectedDefinitionBytes);
                    SnapshotReadGuard.RequireBytes(bytes, MaximumDefinitionBatchBytes - cachedBytes);
                    cachedBytes += bytes; result.Add(revision, SnapshotMemoryCache.Clone(value)); break;
                }
        if (result.Count == requested.Length) return result;
        result.Clear();
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT o.RevisionKey,
                CONVERT(bigint,256)+DATALENGTH(o.SchemaName)+DATALENGTH(o.ObjectName)+DATALENGTH(o.TypeDescription)
                    +DATALENGTH(o.ParentSchemaName)+DATALENGTH(o.ParentObjectName),DATALENGTH(c.Text),
                o.SchemaName,o.ObjectName,o.ObjectKind,o.TypeDescription,o.ParentSchemaName,o.ParentObjectName,
                CONVERT(varbinary(max),c.Text)
            FROM OPENJSON(@Keys) WITH (RevisionKey bigint '$') requested
            JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=requested.RevisionKey
            JOIN surf.TextContent c ON c.ContentKey=o.DefinitionContentKey
            JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=o.RevisionKey
            JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
            JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
            WHERE rr.IsSealed=1 AND s.IsPublished=1;
            """, Text("@Keys", JsonSerializer.Serialize(requested)));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var metadata = new SnapshotMetadataBudget(); long total = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long revision = reader.GetInt64(0); metadata.Add(reader.GetInt64(1), command);
            long length = reader.GetInt64(2);
            SnapshotReadGuard.RequireBytes(length, _readLimits.SelectedDefinitionBytes, command);
            SnapshotReadGuard.RequireBytes(length, MaximumDefinitionBatchBytes - total, command);
            total += length;
            var value = new SqlDatabaseObject { SchemaName = reader.GetString(3), ObjectName = reader.GetString(4),
                Kind = (SqlDatabaseObjectKind)reader.GetInt32(5), TypeDescription = reader.GetString(6),
                ParentSchemaName = reader.GetString(7), ParentObjectName = reader.GetString(8) };
            await using var bytes = reader.GetStream(9);
            value.Definition = await SnapshotReadGuard.ReadUtf16Async(bytes, length, _readLimits.SelectedDefinitionBytes, ct, command).ConfigureAwait(false);
            result.Add(revision, value);
        }
        if (result.Count != requested.Length) throw new KeyNotFoundException("A requested published object revision no longer exists.");
        return result;
    }

    public async Task<IReadOnlyDictionary<long, string>> ReadObjectDefinitionsAsync(
        IReadOnlyList<long> revisionKeys, CancellationToken ct = default) =>
        (await ReadObjectsAsync(revisionKeys, ct).ConfigureAwait(false)).ToDictionary(pair => pair.Key, pair => pair.Value.Definition);

    private static long[] ObjectBatchKeys(IReadOnlyList<long> revisions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revisions); ct.ThrowIfCancellationRequested();
        if (revisions.Count > MaximumObjectBatchSize || revisions.Any(key => key <= 0))
            throw new ArgumentException("Select at most 128 positive immutable object revision keys.", nameof(revisions));
        return revisions.Distinct().ToArray();
    }
}

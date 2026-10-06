using System.Data;
using System.IO;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Storage.Relational.Migration;

public sealed partial class LegacySnapshotImporter
{
    private async Task ImportHistoriesAsync(CancellationToken ct)
    {
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, 0, "Histories", async (ordinal, cursor, token) =>
        {
            long offset = cursor.TokenOffset;
            var header = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotHistory>(FilePath, offset, ["Versions"], token);
            var matches = _snapshots.Where(value => string.Equals(value.Id, header.SnapshotId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("History has an absent or ambiguous snapshot owner: " + header.SnapshotId);
            long snapshot = matches[0].Key;
            string path = $"histories/{ordinal}";
            long history = await Write(path, "History", ordinal, (writer, inner) =>
                writer.InsertHistoryAsync(snapshot, header.SnapshotId, header.NextVersionNumber, ordinal, inner), token);
            var resources = await ReadResourcesAsync(snapshot, token);
            long resourceBytes = resources.Sum(value => 256L + (value.Schema.Length + (long)value.Name.Length + value.OriginalKey.Length) * 2);
            await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "Versions", async (versionOrdinal, versionCursor, versionToken) =>
            {
                long versionOffset = versionCursor.TokenOffset;
                var version = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotVersion>(FilePath, versionOffset, ["Changes"], versionToken);
                string versionPath = path + $"/versions/{versionOrdinal}";
                long versionKey = await Write(versionPath, "Version", ordinal, (writer, inner) => writer.InsertVersionAsync(history,
                    new(version.VersionId, version.VersionName, version.VersionNumber, version.CreatedAtUtc, version.IsInitial, versionOrdinal), inner), versionToken);
                await LegacyProjectionReader.ArrayPropertyAsync(FilePath, versionOffset, "Changes", async (changeOrdinal, changeCursor, changeToken) =>
                {
                    long changeOffset = changeCursor.TokenOffset;
                    string changePath = versionPath + $"/changes/{changeOrdinal}";
                    var change = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotResourceChange>(FilePath, changeOffset, ["PreviousPayload"], changeToken);
                    DatabaseSnapshotResourcePayload? payload = null;
                    long? payloadOffset = null;
                    await LegacyProjectionReader.ValuePropertyAsync(FilePath, changeOffset, "PreviousPayload", async (value, inner) =>
                    {
                        if (value.TokenType != JsonTokenType.Null)
                        {
                            payloadOffset = value.TokenOffset;
                            payload = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotResourcePayload>(FilePath, value.TokenOffset, ["TableDataSet"], inner);
                        }
                        await value.SkipValueAsync(inner);
                    }, changeToken);
                    if (payload != null && (payload.Kind != change.Kind || change.ChangeKind == DatabaseSnapshotResourceChangeKind.Added))
                        throw new InvalidDataException("A noncanonical historical payload requires review before conversion: " + change.ResourceKey);

                    ResourceBinding? resource = resources.FirstOrDefault(value => value.Kind == change.Kind &&
                        string.Equals(value.OriginalKey, change.ResourceKey, StringComparison.OrdinalIgnoreCase));
                    if (resource == null)
                    {
                        (string schema, string name) = ParseIdentity(change, payload);
                        long resourceKey = await Write(changePath + "/resource", "HistoricalResource", ordinal, (writer, inner) =>
                            writer.CreateResourceAsync(snapshot, change.Kind, schema, name, change.ResourceKey, changeOrdinal, inner), changeToken);
                        resource = new(resourceKey, change.Kind, schema, name, change.ResourceKey);
                        ReserveMetadata(ref resourceBytes, schema, name, change.ResourceKey);
                        if (resources.Count >= MetadataLimit) throw new InvalidDataException("Historical resource metadata exceeds the migration budget.");
                        resources.Add(resource);
                    }
                    long? previous = payload == null ? null : await ImportPayloadAsync(snapshot, resource.Key,
                        payload, payloadOffset!.Value, changePath + "/previous", ordinal, changeToken);
                    await Write(changePath, "Change", ordinal, (writer, inner) => writer.InsertChangeAsync(versionKey, resource.Key,
                        new(change.Kind, change.ChangeKind, change.ResourceKey, change.DisplayName, change.RelativePath, changeOrdinal, previous), inner), changeToken);
                    await changeCursor.SkipValueAsync(changeToken);
                }, versionToken);
                await versionCursor.SkipValueAsync(versionToken);
            }, token);
            await cursor.SkipValueAsync(token);
        }, ct);
    }

    private async Task<long?> ImportPayloadAsync(long snapshot, long resource, DatabaseSnapshotResourcePayload payload,
        long offset, string path, long ordinal, CancellationToken ct)
    {
        switch (payload.Kind)
        {
            case DatabaseVersionedResourceKind.StoredProcedure:
            case DatabaseVersionedResourceKind.View:
            case DatabaseVersionedResourceKind.Function:
            case DatabaseVersionedResourceKind.Trigger:
                if (payload.DatabaseObject == null || payload.Table != null || payload.Columns.Count != 0 || payload.PrimaryKeys.Count != 0)
                    throw new InvalidDataException("An historical code payload has missing or unexpected fields.");
                await RequireNoDataSetAsync(offset, ct);
                return await Write(path, "PreviousObjectRevision", ordinal, async (writer, token) =>
                {
                    long revision = await writer.InsertObjectRevisionAsync(resource, payload.DatabaseObject, token);
                    await writer.SealRevisionAsync(revision, token);
                    return revision;
                }, ct);
            case DatabaseVersionedResourceKind.TableMetadata:
                if (payload.Table == null || payload.DatabaseObject != null) throw new InvalidDataException("An historical table payload has missing or unexpected fields.");
                await RequireNoDataSetAsync(offset, ct);
                return await Write(path, "PreviousTableRevision", ordinal, async (writer, token) =>
                {
                    long revision = await writer.InsertTableRevisionAsync(resource, payload.Table, token);
                    for (int i = 0; i < payload.Columns.Count; i++) await writer.InsertColumnAsync(snapshot, revision, payload.Columns[i], i, ct: token);
                    for (int i = 0; i < payload.PrimaryKeys.Count; i++) await writer.InsertPrimaryKeyAsync(snapshot, revision, payload.PrimaryKeys[i], i, ct: token);
                    await writer.SealRevisionAsync(revision, token);
                    return revision;
                }, ct);
            case DatabaseVersionedResourceKind.TableData:
                if (payload.DatabaseObject != null || payload.Columns.Count != 0 || payload.PrimaryKeys.Count != 0)
                    throw new InvalidDataException("An historical dataset payload has unexpected fields.");
                long? result = null;
                await LegacyProjectionReader.ValuePropertyAsync(FilePath, offset, "TableDataSet", async (cursor, token) =>
                {
                    if (cursor.TokenType == JsonTokenType.Null) throw new InvalidDataException("An historical dataset is missing.");
                    long dataOffset = cursor.TokenOffset;
                    var header = await LegacyProjectionReader.HeaderAsync<SqlTableDataSet>(FilePath, dataOffset, ["Rows"], token);
                    long revision = await Write(path, "PreviousDataRevision", ordinal, (writer, inner) =>
                        writer.InsertTableDataRevisionAsync(resource, header.SchemaName, header.TableName, payload.Table, inner), token);
                    await new LegacyCaptureImporter(session) { Progress = Progress }.ImportAsync(revision, FilePath, dataOffset,
                        Array.Empty<CaptureColumnDefinition>(), token);
                    await Write(path + "/seal", "SealedPreviousData", ordinal, async (writer, inner) =>
                    { await writer.SealRevisionAsync(revision, inner); return revision; }, token);
                    result = revision;
                    await cursor.SkipValueAsync(token);
                }, ct);
                return result ?? throw new InvalidDataException("An historical dataset payload is absent.");
            default:
                throw new InvalidDataException("An historical resource kind is unsupported.");
        }
    }

    private Task RequireNoDataSetAsync(long offset, CancellationToken ct) =>
        LegacyProjectionReader.ValuePropertyAsync(FilePath, offset, "TableDataSet", (cursor, _) =>
        {
            if (cursor.TokenType != JsonTokenType.Null) throw new InvalidDataException("An unexpected historical dataset cannot be discarded.");
            return Task.CompletedTask;
        }, ct);

    private async Task<List<ResourceBinding>> ReadResourcesAsync(long snapshot, CancellationToken ct)
    {
        var result = new List<ResourceBinding>();
        long bytes = 0;
        await using var connection = await session.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ResourceKey,Kind,SchemaName,ObjectName,OriginalResourceKey FROM surf.SnapshotResource WHERE SnapshotKey=@Key ORDER BY CurrentSortOrdinal,ResourceKey;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, snapshot));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (result.Count >= MetadataLimit) throw new InvalidDataException("Resource metadata exceeds the migration budget.");
            string schema = reader.GetString(2), name = reader.GetString(3), original = reader.GetString(4);
            ReserveMetadata(ref bytes, schema, name, original);
            result.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(1), schema, name, original));
        }
        return result;
    }

    private static (string Schema, string Name) ParseIdentity(DatabaseSnapshotResourceChange change, DatabaseSnapshotResourcePayload? payload)
    {
        int separator = change.ResourceKey.IndexOf('|');
        string fullName = separator < 0 ? string.Empty : change.ResourceKey[(separator + 1)..];
        int dot = fullName.IndexOf('.');
        if (dot > 0 && dot < fullName.Length - 1) return (fullName[..dot], fullName[(dot + 1)..]);
        if (payload?.DatabaseObject is { } code) return (code.SchemaName, code.ObjectName);
        if (payload?.Table is { } table) return (table.SchemaName, table.TableName);
        // Keep the original malformed locator. The historical resolver must preserve its no-op removal semantics.
        return (string.Empty, fullName);
    }
}

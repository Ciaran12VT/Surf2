using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Storage.Relational.Migration;

public sealed partial class LegacySnapshotImporter(RelationalSession session, MigrationJournal journal,
    LegacySourceStage source)
{
    private const string Document = "database-snapshots";
    private const int MetadataLimit = 100_000;
    private readonly RelationalContentStore _content = new();
    private readonly List<Binding> _snapshots = [];
    private long _catalogueBytes;
    private string FilePath => source.StagedDocuments[Document].FilePath;
    private sealed record Binding(long Key, string Id, long Ordinal);
    private sealed record TableBinding(SqlTable Table, long Revision);
    private sealed record ResourceBinding(long Key, DatabaseVersionedResourceKind? Kind, string Schema,
        string Name, string OriginalKey);

    public async Task ImportAsync(CancellationToken ct = default)
    {
        await Write("header", "SnapshotLibrary", 0, (_, _) => Task.FromResult(1L), ct);
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, 0, "Snapshots", async (ordinal, value, token) =>
        {
            await ImportSnapshotAsync(ordinal, value.TokenOffset, token);
            await value.SkipValueAsync(token);
        }, ct);
        await ImportHistoriesAsync(ct);
        foreach (var snapshot in _snapshots)
        {
            await Write($"snapshots/{snapshot.Ordinal}/publish", "SnapshotPublication", snapshot.Ordinal,
                async (writer, token) =>
                {
                    await using var command = writer.Connection.CreateCommand();
                    command.Transaction = writer.Transaction;
                    command.CommandText = "SELECT TOP(1) VersionKey FROM surf.SnapshotVersion WHERE HistoryKey=(SELECT TOP(1) HistoryKey FROM surf.SnapshotHistory WHERE SnapshotKey=@Key ORDER BY SortOrdinal,HistoryKey) ORDER BY VersionNumber DESC, SortOrdinal, VersionKey;";
                    command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, snapshot.Key));
                    object? version = await command.ExecuteScalarAsync(token);
                    await writer.PublishSnapshotAsync(snapshot.Key, version is long key ? key : null, token);
                    return snapshot.Key;
                }, ct);
        }
    }

    private async Task ImportSnapshotAsync(long ordinal, long offset, CancellationToken ct)
    {
        if (_snapshots.Count >= MetadataLimit) throw new InvalidDataException("The snapshot catalogue exceeds the migration metadata budget.");
        var header = await LegacyProjectionReader.HeaderAsync<DatabaseMetadataSnapshot>(FilePath, offset,
            ["Objects", "Tables", "Columns", "PrimaryKeys", "TableDataSets", "FullDataTableNames"], ct);
        ReserveMetadata(ref _catalogueBytes, header.SnapshotId);
        long key = await Write($"snapshots/{ordinal}", "Snapshot", ordinal, (writer, token) =>
            writer.CreateSnapshotAsync(new(header.SnapshotId, header.DisplayName, header.DatabaseName, header.ImportedAtUtc, ordinal), token), ct);
        _snapshots.Add(new(key, header.SnapshotId, ordinal));
        var tables = new List<TableBinding>();
        long tableBytes = 0;
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "Objects", async (index, cursor, token) =>
        {
            var value = LegacySchemaInspector.Deserialize<SqlDatabaseObject>(await cursor.ReadValueAsync(token));
            await Write($"snapshots/{ordinal}/objects/{index}", "ObjectRevision", ordinal, async (writer, inner) =>
                (await writer.InsertCurrentObjectAsync(key, value, index, ct: inner)).RevisionKey, token);
        }, ct);
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "Tables", async (index, cursor, token) =>
        {
            if (tables.Count >= MetadataLimit) throw new InvalidDataException("Table metadata exceeds the migration budget.");
            var value = LegacySchemaInspector.Deserialize<SqlTable>(await cursor.ReadValueAsync(token));
            ReserveMetadata(ref tableBytes, value.SchemaName, value.TableName);
            // Ambiguous duplicate ownership cannot be assigned to a typed revision silently.
            if (tables.Any(table => NamesEqual(table.Table.SchemaName, table.Table.TableName, value.SchemaName, value.TableName)))
                throw new InvalidDataException("Duplicate table identities require repair before relational migration.");
            long revision = await Write($"snapshots/{ordinal}/tables/{index}", "TableRevision", ordinal, async (writer, inner) =>
                (await writer.InsertCurrentTableAsync(key, value, index, ct: inner)).RevisionKey, token);
            tables.Add(new(value, revision));
        }, ct);
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "Columns", async (index, cursor, token) =>
        {
            var value = LegacySchemaInspector.Deserialize<SqlColumn>(await cursor.ReadValueAsync(token));
            long? revision = tables.FirstOrDefault(table => NamesEqual(table.Table.SchemaName, table.Table.TableName, value.SchemaName, value.TableName))?.Revision;
            await Write($"snapshots/{ordinal}/columns/{index}", "Column", ordinal, (writer, inner) =>
                writer.InsertColumnAsync(key, revision, value, index, index, inner), token);
        }, ct);
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "PrimaryKeys", async (index, cursor, token) =>
        {
            var value = LegacySchemaInspector.Deserialize<SqlPrimaryKeyColumn>(await cursor.ReadValueAsync(token));
            long? revision = tables.FirstOrDefault(table => NamesEqual(table.Table.SchemaName, table.Table.TableName, value.SchemaName, value.TableName))?.Revision;
            await Write($"snapshots/{ordinal}/keys/{index}", "PrimaryKey", ordinal, (writer, inner) =>
                writer.InsertPrimaryKeyAsync(key, revision, value, index, index, inner), token);
        }, ct);
        for (int i = 0; i < tables.Count; i++)
        {
            long revision = tables[i].Revision;
            await Write($"snapshots/{ordinal}/tables/{i}/seal", "SealedRevision", ordinal, async (writer, token) =>
            { await writer.SealRevisionAsync(revision, token); return revision; }, ct);
        }
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "FullDataTableNames", async (index, cursor, token) =>
        {
            string value = (await cursor.ReadValueAsync(token)).GetString() ?? throw new InvalidDataException("A table selection is null.");
            await Write($"snapshots/{ordinal}/selections/{index}", "FullDataSelection", ordinal,
                (writer, inner) => writer.InsertFullDataSelectionAsync(key, value, index, inner), token);
        }, ct);
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath, offset, "TableDataSets", async (index, cursor, token) =>
        {
            long dataOffset = cursor.TokenOffset;
            var data = await LegacyProjectionReader.HeaderAsync<SqlTableDataSet>(FilePath, dataOffset, ["Rows"], token);
            string path = $"snapshots/{ordinal}/datasets/{index}";
            long revision = await Write(path, "DataRevision", ordinal, async (writer, inner) =>
            {
                long resource = await writer.CreateResourceAsync(key, DatabaseVersionedResourceKind.TableData,
                    data.SchemaName, data.TableName, SnapshotIdentity.LegacyResourceKey(DatabaseVersionedResourceKind.TableData, data.SchemaName, data.TableName), index, inner);
                return await writer.InsertTableDataRevisionAsync(resource, data.SchemaName, data.TableName, ct: inner);
            }, token);
            await new LegacyCaptureImporter(session).ImportAsync(revision, FilePath, dataOffset,
                await CaptureColumnsAsync(key, data.SchemaName, data.TableName, token), token);
            await Write(path + "/seal", "CurrentDataRevision", ordinal, async (writer, inner) =>
            {
                await writer.SealRevisionAsync(revision, inner);
                await writer.SetCurrentRevisionAsync(await ResourceForRevisionAsync(writer, revision, inner), revision, index, ct: inner);
                return revision;
            }, token);
            await cursor.SkipValueAsync(token);
        }, ct);
    }

    private Task<long> Write(string path, string kind, long ordinal,
        Func<RelationalSnapshotWriter, CancellationToken, Task<long>> write, CancellationToken ct) =>
        journal.UnitAsync(Document, path, kind, ordinal, (connection, transaction, token) =>
            write(new(connection, transaction, _content), token), ct);

    private static bool NamesEqual(string aSchema, string aName, string bSchema, string bName) =>
        string.Equals(aSchema, bSchema, StringComparison.OrdinalIgnoreCase) && string.Equals(aName, bName, StringComparison.OrdinalIgnoreCase);

    private static void ReserveMetadata(ref long bytes, params string[] values)
    {
        bytes = checked(bytes + 256 + values.Sum(value => (long)value.Length * 2));
        if (bytes > 64L * 1024 * 1024) throw new InvalidDataException("Retained migration metadata exceeds its 64 MiB budget.");
    }

    private static async Task<long> ResourceForRevisionAsync(RelationalSnapshotWriter writer, long revision, CancellationToken ct)
    {
        await using var command = writer.Connection.CreateCommand();
        command.Transaction = writer.Transaction;
        command.CommandText = "SELECT ResourceKey FROM surf.SnapshotResourceRevision WHERE RevisionKey=@Key;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, revision));
        return (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidDataException("Missing migrated revision."));
    }

    private async Task<IReadOnlyList<CaptureColumnDefinition>> CaptureColumnsAsync(long snapshotKey, string schema, string table, CancellationToken ct)
    {
        var result = new List<CaptureColumnDefinition>();
        await using var connection = await session.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.SchemaName,c.TableName,c.ColumnName,c.DataType,c.MaxLength,c.NumericPrecision,c.NumericScale,c.IsNullable,c.SourceOrdinal,c.IsIdentity FROM surf.SnapshotCurrentColumn e JOIN surf.TableColumnRevision c ON c.ColumnRevisionKey=e.ColumnRevisionKey WHERE e.SnapshotKey=@Key ORDER BY e.SortOrdinal,e.EntryKey;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, snapshotKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!NamesEqual(reader.GetString(0), reader.GetString(1), schema, table)) continue;
            result.Add(new(reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetByte(5),
                reader.GetInt32(6), reader.GetBoolean(7), reader.GetInt32(8), reader.GetBoolean(9)));
            if (result.Count > 128) throw new InvalidDataException("Captured column metadata exceeds the supported layout budget.");
        }
        return result;
    }
}

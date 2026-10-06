using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

public enum HistoricalCollection { Objects, Tables, Columns, PrimaryKeys, TableDataSets, FullDataTableNames }
public enum TableRevisionSource { Metadata, DataCompanion }

public sealed record HistoricalSnapshotEntry(long EntryKey, HistoricalCollection Collection, long SortOrdinal,
    long? ResourceKey, long? RevisionKey, long? ChildKey, string SchemaName, string Name,
    SqlDatabaseObjectKind? ObjectKind = null, SqlTable? Table = null,
    TableRevisionSource TableSource = TableRevisionSource.Metadata);
public sealed record HistoricalPayloadHeader(long ResourceKey, long RevisionKey, DatabaseVersionedResourceKind Kind,
    string SchemaName, string Name, SqlDatabaseObjectKind? ObjectKind = null, SqlTable? Table = null,
    long ReportedRowCount = 0, long ActualRowCount = 0, DateTimeOffset DataImportedAtUtc = default);
public sealed record HistoricalRollbackEvent(DatabaseVersionedResourceKind Kind,
    DatabaseSnapshotResourceChangeKind ChangeKind, string OriginalResourceKey, HistoricalPayloadHeader? Previous);

// Both the SQL-backed bounded projection and small regression fixtures execute
// this same replay program. No definition, dataset row or library is accepted.
public interface IHistoricalSnapshotState
{
    Task RemoveAsync(HistoricalCollection collection, string schema, string name, SqlDatabaseObjectKind? objectKind, CancellationToken ct);
    Task<HistoricalSnapshotEntry> AppendAsync(HistoricalSnapshotEntry entry, CancellationToken ct);
    Task AppendChildrenAsync(long revisionKey, HistoricalCollection collection, CancellationToken ct);
    Task<HistoricalSnapshotEntry?> FirstTableAsync(string schema, string name, CancellationToken ct);
    Task UpdateTableAsync(long entryKey, bool hasData, long count, DateTimeOffset? importedAt, CancellationToken ct);
    Task EnsureSelectionAsync(string name, CancellationToken ct);
}

public static class HistoricalSnapshotReplay
{
    public static async Task ApplyAsync(IHistoricalSnapshotState state, HistoricalRollbackEvent change, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (change.ChangeKind == DatabaseSnapshotResourceChangeKind.Added)
        {
            if (!TryParse(change.OriginalResourceKey, out string schema, out string name)) return;
            switch (change.Kind)
            {
                case DatabaseVersionedResourceKind.TableMetadata:
                    await RemoveMetadata(state, schema, name, ct).ConfigureAwait(false);
                    break;
                case DatabaseVersionedResourceKind.TableData:
                    await state.RemoveAsync(HistoricalCollection.TableDataSets, schema, name, null, ct).ConfigureAwait(false);
                    var table = await state.FirstTableAsync(schema, name, ct).ConfigureAwait(false);
                    if (table != null) await state.UpdateTableAsync(table.EntryKey, false, 0, null, ct).ConfigureAwait(false);
                    await state.RemoveAsync(HistoricalCollection.FullDataTableNames, "", SqlName.FormatPlainMultipartName(schema, name), null, ct).ConfigureAwait(false);
                    break;
                default:
                    await state.RemoveAsync(HistoricalCollection.Objects, schema, name, ObjectKind(change.Kind), ct).ConfigureAwait(false);
                    break;
            }
            return;
        }
        var payload = change.Previous;
        if (payload == null) return;
        switch (payload.Kind)
        {
            case DatabaseVersionedResourceKind.TableMetadata:
                if (payload.Table == null) return;
                await RemoveMetadata(state, payload.Table.SchemaName, payload.Table.TableName, ct).ConfigureAwait(false);
                await state.AppendAsync(new(0, HistoricalCollection.Tables, 0, payload.ResourceKey, payload.RevisionKey, null,
                    payload.Table.SchemaName, payload.Table.TableName, Table: payload.Table), ct).ConfigureAwait(false);
                await state.AppendChildrenAsync(payload.RevisionKey, HistoricalCollection.Columns, ct).ConfigureAwait(false);
                await state.AppendChildrenAsync(payload.RevisionKey, HistoricalCollection.PrimaryKeys, ct).ConfigureAwait(false);
                break;
            case DatabaseVersionedResourceKind.TableData:
                await state.RemoveAsync(HistoricalCollection.TableDataSets, payload.SchemaName, payload.Name, null, ct).ConfigureAwait(false);
                await state.AppendAsync(new(0, HistoricalCollection.TableDataSets, 0, payload.ResourceKey, payload.RevisionKey, null,
                    payload.SchemaName, payload.Name), ct).ConfigureAwait(false);
                var table = await state.FirstTableAsync(payload.SchemaName, payload.Name, ct).ConfigureAwait(false);
                if (table == null && payload.Table != null)
                    table = await state.AppendAsync(new(0, HistoricalCollection.Tables, 0, payload.ResourceKey, payload.RevisionKey, null,
                        payload.Table.SchemaName, payload.Table.TableName, Table: payload.Table, TableSource: TableRevisionSource.DataCompanion), ct).ConfigureAwait(false);
                if (table != null)
                    await state.UpdateTableAsync(table.EntryKey, true, Math.Max(payload.ReportedRowCount, payload.ActualRowCount), payload.DataImportedAtUtc, ct).ConfigureAwait(false);
                await state.EnsureSelectionAsync(SqlName.FormatPlainMultipartName(payload.SchemaName, payload.Name), ct).ConfigureAwait(false);
                break;
            default:
                if (!payload.ObjectKind.HasValue) return;
                await state.RemoveAsync(HistoricalCollection.Objects, payload.SchemaName, payload.Name, payload.ObjectKind, ct).ConfigureAwait(false);
                await state.AppendAsync(new(0, HistoricalCollection.Objects, 0, payload.ResourceKey, payload.RevisionKey, null,
                    payload.SchemaName, payload.Name, payload.ObjectKind), ct).ConfigureAwait(false);
                break;
        }
    }

    public static bool Matches(HistoricalSnapshotEntry entry, HistoricalCollection collection, string schema, string name, SqlDatabaseObjectKind? objectKind = null) =>
        entry.Collection == collection && (collection == HistoricalCollection.FullDataTableNames || string.Equals(entry.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) && (!objectKind.HasValue || entry.ObjectKind == objectKind);

    private static async Task RemoveMetadata(IHistoricalSnapshotState state, string schema, string name, CancellationToken ct)
    {
        foreach (var collection in new[] { HistoricalCollection.Tables, HistoricalCollection.Columns, HistoricalCollection.PrimaryKeys, HistoricalCollection.TableDataSets })
            await state.RemoveAsync(collection, schema, name, null, ct).ConfigureAwait(false);
        await state.RemoveAsync(HistoricalCollection.FullDataTableNames, "", SqlName.FormatPlainMultipartName(schema, name), null, ct).ConfigureAwait(false);
    }
    private static SqlDatabaseObjectKind ObjectKind(DatabaseVersionedResourceKind kind) => kind switch
    {
        DatabaseVersionedResourceKind.StoredProcedure => SqlDatabaseObjectKind.StoredProcedure,
        DatabaseVersionedResourceKind.View => SqlDatabaseObjectKind.View,
        DatabaseVersionedResourceKind.Function => SqlDatabaseObjectKind.Function,
        DatabaseVersionedResourceKind.Trigger => SqlDatabaseObjectKind.Trigger,
        _ => SqlDatabaseObjectKind.Unknown
    };
    private static bool TryParse(string key, out string schema, out string name)
    {
        schema = name = "";
        string[] parts = key.Split('|', 2);
        if (parts.Length != 2 || !Enum.TryParse<DatabaseVersionedResourceKind>(parts[0], out _)) return false;
        string[] names = parts[1].Split('.', 2);
        if (names.Length != 2) return false;
        schema = names[0]; name = names[1];
        return true;
    }
}

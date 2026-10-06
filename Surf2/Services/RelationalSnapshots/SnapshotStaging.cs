using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalSnapshots;

internal static class SnapshotStaging
{
    public static async Task ReadBaselineAsync(RelationalRuntime runtime, RuntimeSnapshotPlan plan,
        SnapshotOperationBudget budget, CancellationToken ct)
    {
        if (plan.IsNew) return;
        var identities = new HashSet<(DatabaseVersionedResourceKind, string, string)>(RuntimeResourceComparer.Instance);
        await foreach (var resource in Pages(cursor => runtime.Snapshots.ListResourcesAsync(plan.SnapshotKey, pageSize: 128, cursor: cursor, ct: ct), ct))
        {
            if (!resource.Kind.HasValue) throw Unsupported("unknown object kinds");
            if (!identities.Add((resource.Kind.Value, resource.SchemaName, resource.ObjectName))) throw Unsupported("ambiguous current identities");
            budget.Add(resource.SchemaName, resource.ObjectName);
            plan.Previous.Add(new(resource.ResourceKey, resource.RevisionKey, resource.Kind.Value, resource.SchemaName, resource.ObjectName, resource.SortOrdinal));
        }
        var globalColumns = new List<long>(); var globalKeys = new List<long>();
        await foreach (var column in Pages(cursor => runtime.Snapshots.ListCurrentColumnsAsync(plan.SnapshotKey, 128, cursor, ct), ct))
        { budget.Add(64); globalColumns.Add(column.ColumnRevisionKey); }
        await foreach (var key in Pages(cursor => runtime.Snapshots.ListCurrentPrimaryKeysAsync(plan.SnapshotKey, 128, cursor, ct), ct))
        { budget.Add(64); globalKeys.Add(key.KeyColumnKey); }
        var groupedColumns = new List<long>(); var groupedKeys = new List<long>();
        var tables = new Dictionary<(string, string), SqlTable>(RuntimeTableComparer.Instance);
        foreach (var value in plan.Previous.Where(x => x.Kind == DatabaseVersionedResourceKind.TableMetadata).OrderBy(x => x.SortOrdinal))
        {
            var details = await runtime.Snapshots.ReadTableMetadataAsync(value.RevisionKey, ct).ConfigureAwait(false);
            tables.Add((value.SchemaName, value.Name), details.Table);
            groupedColumns.AddRange(details.Columns.Select(x => x.ColumnRevisionKey));
            groupedKeys.AddRange(details.PrimaryKeys.Select(x => x.KeyColumnKey));
        }
        if (!globalColumns.SequenceEqual(groupedColumns) || !globalKeys.SequenceEqual(groupedKeys))
            throw Unsupported("orphan or interleaved column/key collections");
        var dataNames = new List<string>();
        foreach (var value in plan.Previous.Where(x => x.Kind == DatabaseVersionedResourceKind.TableData).OrderBy(x => x.SortOrdinal))
        {
            var data = await runtime.CapturedData.GetForRevisionAsync(value.RevisionKey, ct).ConfigureAwait(false) ?? throw Unsupported("incomplete captured data");
            if (!tables.TryGetValue((value.SchemaName, value.Name), out var table) || !table.HasFullData ||
                table.FullDataRowCount != Math.Max(data.Summary.ReportedRowCount, data.Summary.ActualRowCount) ||
                table.FullDataImportedAtUtc is not { } time || !time.EqualsExact(data.Summary.ImportedAtUtc))
                throw Unsupported("inconsistent table/data companion fields");
            dataNames.Add(SqlName.FormatPlainMultipartName(value.SchemaName, value.Name));
        }
        var selections = new List<string>();
        await foreach (var selected in Pages(cursor => runtime.Snapshots.ListFullDataSelectionsAsync(plan.SnapshotKey, 128, cursor, ct), ct))
        { budget.Add(selected.TableName); selections.Add(selected.TableName); }
        if (!selections.SequenceEqual(dataNames, StringComparer.Ordinal)) throw Unsupported("noncanonical full-data selection order/identities");
    }

    private static InvalidOperationException Unsupported(string detail) => new(
        "This snapshot cannot be replaced losslessly by the current reverse-history format: " + detail + ". Current data has not been changed.");

    internal static async IAsyncEnumerable<T> Pages<T>(Func<SnapshotCursor?, Task<SnapshotPage<T>>> read,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        SnapshotCursor? cursor = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await read(cursor).ConfigureAwait(false);
            foreach (var item in page.Items) { ct.ThrowIfCancellationRequested(); yield return item; }
            cursor = page.Next;
        } while (cursor != null);
    }

    internal static async Task<(RuntimeSnapshotResource Resource, List<long> Columns, List<long> Keys)> TableAsync(
        RelationalRuntime runtime, RuntimeSnapshotPlan plan, SqlTable table, IReadOnlyList<SqlColumn> columns,
        IReadOnlyList<SqlPrimaryKeyColumn> keys, long ordinal, CancellationToken ct, long? existingResource = null)
    {
        return await runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
        {
            long resource = existingResource ?? await runtime.Snapshots.RuntimeResourceAsync(writer, plan, DatabaseVersionedResourceKind.TableMetadata,
                table.SchemaName, table.TableName, ordinal, token).ConfigureAwait(false);
            long revision = await writer.InsertTableRevisionAsync(resource, table, token).ConfigureAwait(false);
            var columnKeys = new List<long>(); var keyKeys = new List<long>();
            for (int i = 0; i < columns.Count; i++) columnKeys.Add(await writer.InsertColumnAsync(plan.SnapshotKey, revision, columns[i], i, ct: token).ConfigureAwait(false));
            for (int i = 0; i < keys.Count; i++) keyKeys.Add(await writer.InsertPrimaryKeyAsync(plan.SnapshotKey, revision, keys[i], i, ct: token).ConfigureAwait(false));
            await writer.SealRevisionAsync(revision, token).ConfigureAwait(false);
            return (new RuntimeSnapshotResource(resource, revision, DatabaseVersionedResourceKind.TableMetadata, table.SchemaName, table.TableName, ordinal), columnKeys, keyKeys);
        }, ct).ConfigureAwait(false);
    }
}

internal sealed class RuntimeResourceComparer : IEqualityComparer<(DatabaseVersionedResourceKind Kind, string Schema, string Name)>
{
    public static RuntimeResourceComparer Instance { get; } = new();
    public bool Equals((DatabaseVersionedResourceKind Kind, string Schema, string Name) a, (DatabaseVersionedResourceKind Kind, string Schema, string Name) b) =>
        a.Kind == b.Kind && StringComparer.OrdinalIgnoreCase.Equals(a.Schema, b.Schema) && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);
    public int GetHashCode((DatabaseVersionedResourceKind Kind, string Schema, string Name) value) =>
        HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
}
internal sealed class RuntimeTableComparer : IEqualityComparer<(string Schema, string Name)>
{
    public static RuntimeTableComparer Instance { get; } = new();
    public bool Equals((string Schema, string Name) a, (string Schema, string Name) b) =>
        StringComparer.OrdinalIgnoreCase.Equals(a.Schema, b.Schema) && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);
    public int GetHashCode((string Schema, string Name) value) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
}

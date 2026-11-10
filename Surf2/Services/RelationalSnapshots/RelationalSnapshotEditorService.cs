using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Services.RelationalSnapshots;

/// <summary>Materializes only an explicitly edited snapshot; startup and browsing never use this adapter.</summary>
public sealed class RelationalSnapshotEditorService(RelationalRuntime runtime)
{
    public async Task<DatabaseMetadataSnapshot> ReadAsync(SnapshotSummary snapshot, CancellationToken ct = default)
    {
        await RequireHeadAsync(snapshot, ct).ConfigureAwait(false);
        var result = new DatabaseMetadataSnapshot { SnapshotId = snapshot.SnapshotId, DisplayName = snapshot.DisplayName,
            DatabaseName = snapshot.DatabaseName, ImportedAtUtc = snapshot.ImportedAtUtc };
        await foreach (var resource in SnapshotStaging.Pages(cursor => runtime.Snapshots.ListResourcesAsync(snapshot.SnapshotKey,
            pageSize: 128, cursor: cursor, ct: ct), ct))
        {
            if (resource.Kind == DatabaseVersionedResourceKind.TableMetadata)
                result.Tables.Add((await runtime.Snapshots.ReadTableMetadataAsync(resource.RevisionKey, ct).ConfigureAwait(false)).Table);
            else if (resource.Kind == DatabaseVersionedResourceKind.TableData)
            {
                var data = await runtime.CapturedData.GetForRevisionAsync(resource.RevisionKey, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The selected snapshot has incomplete captured data.");
                var set = new SqlTableDataSet { SchemaName = resource.SchemaName, TableName = resource.ObjectName,
                    RowCount = data.Summary.ReportedRowCount, ImportedAtUtc = data.Summary.ImportedAtUtc };
                await foreach (var row in runtime.CapturedData.StreamRowsAsync(data.Summary.DataSetKey, cancellationToken: ct).ConfigureAwait(false))
                    set.Rows.Add(row.Value.Clone());
                result.TableDataSets.Add(set);
            }
            else result.Objects.Add(await runtime.Snapshots.ReadObjectAsync(resource.RevisionKey, ct).ConfigureAwait(false));
        }
        await foreach (var column in SnapshotStaging.Pages(cursor => runtime.Snapshots.ListCurrentColumnsAsync(snapshot.SnapshotKey, 128, cursor, ct), ct))
            result.Columns.Add(column.Column);
        await foreach (var key in SnapshotStaging.Pages(cursor => runtime.Snapshots.ListCurrentPrimaryKeysAsync(snapshot.SnapshotKey, 128, cursor, ct), ct))
            result.PrimaryKeys.Add(key.Column);
        await foreach (var selection in SnapshotStaging.Pages(cursor => runtime.Snapshots.ListFullDataSelectionsAsync(snapshot.SnapshotKey, 128, cursor, ct), ct))
            result.FullDataTableNames.Add(selection.TableName);
        await RequireHeadAsync(snapshot, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<RelationalCaptureResult> SaveAsync(DatabaseMetadataSnapshot edited, Scope selectedScope,
        StateToken expectedScope, SnapshotRuntimeToken? replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var scope = await runtime.StateStore.ReadScopeAsync(expectedScope.Key, ct).ConfigureAwait(false)
            ?? throw new StateConflictException("snapshot editor scope");
        if (scope.Token.Epoch != expectedScope.Epoch || scope.Token.Key != expectedScope.Key ||
            scope.Token.PublicationId != expectedScope.PublicationId || !scope.Token.Version.AsSpan().SequenceEqual(expectedScope.Version) ||
            !StringComparer.Ordinal.Equals(scope.Value.ScopeId, selectedScope.ScopeId)) throw new StateConflictException("snapshot editor scope");
        var plan = await runtime.Snapshots.BeginRuntimeStageAsync(replacement, edited.DisplayName, edited.DatabaseName,
            edited.ImportedAtUtc, ct).ConfigureAwait(false);
        var budget = new SnapshotOperationBudget(new());
        await SnapshotStaging.ReadBaselineAsync(runtime, plan, budget, ct).ConfigureAwait(false);
        long ordinal = 0;
        foreach (var value in edited.Objects)
        {
            budget.Add(value.SchemaName, value.ObjectName);
            var kind = SnapshotIdentity.ResourceKind(value.Kind)
                ?? throw new InvalidOperationException("An object with unknown kind cannot be published by the snapshot editor.");
            var resource = await runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
            {
                long key = await runtime.Snapshots.RuntimeResourceAsync(writer, plan, kind, value.SchemaName, value.ObjectName, ordinal, token).ConfigureAwait(false);
                long revision = await writer.InsertObjectRevisionAsync(key, value, token).ConfigureAwait(false);
                await writer.SealRevisionAsync(revision, token).ConfigureAwait(false);
                return new RuntimeSnapshotResource(key, revision, kind, value.SchemaName, value.ObjectName, ordinal);
            }, ct).ConfigureAwait(false);
            plan.Resources.Add(resource); ordinal++;
        }
        // Map child identities back to the original global collections, which can be interleaved or contain orphan children.
        var columnIds = new Dictionary<SqlColumn, long>(); var keyIds = new Dictionary<SqlPrimaryKeyColumn, long>();
        ordinal = 0;
        foreach (var table in edited.Tables)
        {
            budget.Add(table.SchemaName, table.TableName);
            var columns = edited.Columns.Where(x => SameTable(x.SchemaName, x.TableName, table.SchemaName, table.TableName)).ToArray();
            var keys = edited.PrimaryKeys.Where(x => SameTable(x.SchemaName, x.TableName, table.SchemaName, table.TableName)).ToArray();
            var staged = await SnapshotStaging.TableAsync(runtime, plan, table, columns, keys, ordinal++, ct).ConfigureAwait(false);
            plan.Resources.Add(staged.Resource);
            for (int i = 0; i < columns.Length; i++) columnIds.Add(columns[i], staged.Columns[i]);
            for (int i = 0; i < keys.Length; i++) keyIds.Add(keys[i], staged.Keys[i]);
        }
        for (int i = 0; i < edited.Columns.Count; i++)
        {
            var column = edited.Columns[i]; budget.Add(column.ColumnName, column.DataType);
            if (!columnIds.TryGetValue(column, out long id)) id = await runtime.Snapshots.StageRuntimeUnitAsync(
                (writer, token) => writer.InsertColumnAsync(plan.SnapshotKey, null, column, i, ct: token), ct).ConfigureAwait(false);
            plan.Columns.Add(id);
        }
        for (int i = 0; i < edited.PrimaryKeys.Count; i++)
        {
            var key = edited.PrimaryKeys[i]; budget.Add(key.ConstraintName, key.ColumnName);
            if (!keyIds.TryGetValue(key, out long id)) id = await runtime.Snapshots.StageRuntimeUnitAsync(
                (writer, token) => writer.InsertPrimaryKeyAsync(plan.SnapshotKey, null, key, i, ct: token), ct).ConfigureAwait(false);
            plan.PrimaryKeys.Add(id);
        }
        ordinal = 0; long rows = 0;
        foreach (var data in edited.TableDataSets)
        {
            budget.Add(data.SchemaName, data.TableName);
            var staged = await runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
            {
                long key = await runtime.Snapshots.RuntimeResourceAsync(writer, plan, DatabaseVersionedResourceKind.TableData,
                    data.SchemaName, data.TableName, ordinal, token).ConfigureAwait(false);
                var table = edited.Tables.FirstOrDefault(x => SameTable(x.SchemaName, x.TableName, data.SchemaName, data.TableName));
                long revision = await writer.InsertTableDataRevisionAsync(key, data.SchemaName, data.TableName, table, token).ConfigureAwait(false);
                return new RuntimeSnapshotResource(key, revision, DatabaseVersionedResourceKind.TableData, data.SchemaName, data.TableName, ordinal);
            }, ct).ConfigureAwait(false);
            // Infer from the actual first row, as the master paste/import workflow permits incomplete table metadata.
            var layout = data.Rows.Count == 0 ? Array.Empty<CaptureColumnDefinition>() : CaptureColumns.FromFirstRow(data.Rows[0]);
            var write = await runtime.CapturedData.CreateDataSetAsync(new(staged.RevisionKey, layout, data.RowCount,
                data.ImportedAtUtc, data.Rows.Count), ct).ConfigureAwait(false);
            await runtime.CapturedData.AppendRowsAsync(write, RowsAsync(data.Rows, ct), 0, ct).ConfigureAwait(false);
            await runtime.CapturedData.CompleteDataSetAsync(write, data.Rows.Count, ct).ConfigureAwait(false);
            await runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
            { await writer.SealRevisionAsync(staged.RevisionKey, token).ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);
            plan.Resources.Add(staged); ordinal++; rows = checked(rows + data.Rows.Count);
        }
        foreach (string selection in edited.FullDataTableNames) { budget.Add(selection); plan.FullDataSelections.Add(selection); }
        if (plan.IsNew) scope.Value.Resources.Add(new() { ResourceId = Guid.NewGuid().ToString("N"), Kind = ResourceKind.DatabaseSnapshot,
            Path = plan.Header.SnapshotId, AddedAtUtc = plan.Header.ImportedAtUtc, IncludeChildren = true });
        var published = await runtime.Snapshots.PublishRuntimeStageAsync(plan, scope.Value, expectedScope, Guid.NewGuid(), null, ct).ConfigureAwait(false);
        return new(published.Snapshot, published.ScopeToken!, scope.Value, rows, published.VersionKey);
    }

    private async Task RequireHeadAsync(SnapshotSummary expected, CancellationToken ct)
    {
        var current = await runtime.Snapshots.GetSnapshotAsync(expected.SnapshotKey, ct).ConfigureAwait(false);
        if (current == null || !current.RowVersion.AsSpan().SequenceEqual(expected.RowVersion)) throw new SnapshotConcurrencyException();
    }
    private static bool SameTable(string schema, string table, string otherSchema, string otherTable) =>
        StringComparer.OrdinalIgnoreCase.Equals(schema, otherSchema) && StringComparer.OrdinalIgnoreCase.Equals(table, otherTable);
    private static async IAsyncEnumerable<JsonElement> RowsAsync(IEnumerable<JsonElement> rows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var row in rows) { ct.ThrowIfCancellationRequested(); yield return row; }
        await Task.CompletedTask;
    }
}

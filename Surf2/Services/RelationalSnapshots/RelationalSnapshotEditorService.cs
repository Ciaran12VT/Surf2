using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Services.RelationalSnapshots;

/// <summary>Materializes only an explicitly edited snapshot; startup and browsing never use this adapter.</summary>
public sealed class RelationalSnapshotEditorService(RelationalRuntime runtime)
{
    private readonly Dictionary<long, SqlTableDataSet> _captureBaselines = [];
    public async Task<DatabaseMetadataSnapshot> ReadAsync(SnapshotSummary snapshot, CancellationToken ct = default)
    {
        await RequireHeadAsync(snapshot, ct).ConfigureAwait(false);
        _captureBaselines.Clear();
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
                RememberCapture(resource.RevisionKey, set);
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
        foreach (var batch in edited.Objects.Chunk(128))
        {
            var unchangedObjects = await ReadUnchangedObjectRevisionsAsync(plan, batch, ct).ConfigureAwait(false);
            foreach (var value in batch)
            {
                budget.Add(value.SchemaName, value.ObjectName);
                var kind = SnapshotIdentity.ResourceKind(value.Kind)
                    ?? throw new InvalidOperationException("An object with unknown kind cannot be published by the snapshot editor.");
                var previous = plan.FindPrevious(kind, value.SchemaName, value.ObjectName);
                if (previous != null && unchangedObjects.Contains(previous.RevisionKey))
                { plan.Resources.Add(previous with { SortOrdinal = ordinal++ }); continue; }
                var resource = await runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
                {
                    long key = await runtime.Snapshots.RuntimeResourceAsync(writer, plan, kind, value.SchemaName, value.ObjectName, ordinal, token).ConfigureAwait(false);
                    long revision = await writer.InsertObjectRevisionAsync(key, value, token).ConfigureAwait(false);
                    await writer.SealRevisionAsync(revision, token).ConfigureAwait(false);
                    return new RuntimeSnapshotResource(key, revision, kind, value.SchemaName, value.ObjectName, ordinal);
                }, ct).ConfigureAwait(false);
                plan.Resources.Add(resource); ordinal++;
            }
        }
        // Map child identities back to the original global collections, which can be interleaved or contain orphan children.
        var columnIds = new long?[edited.Columns.Count]; var keyIds = new long?[edited.PrimaryKeys.Count];
        var reusedColumns = new HashSet<int>(); var reusedKeys = new HashSet<int>();
        var previousTableScalars = new Dictionary<long, SqlTable>();
        ordinal = 0;
        foreach (var table in edited.Tables)
        {
            budget.Add(table.SchemaName, table.TableName);
            var columnPositions = edited.Columns.Select((value, index) => (value, index))
                .Where(x => SameTable(x.value.SchemaName, x.value.TableName, table.SchemaName, table.TableName)).ToArray();
            var keyPositions = edited.PrimaryKeys.Select((value, index) => (value, index))
                .Where(x => SameTable(x.value.SchemaName, x.value.TableName, table.SchemaName, table.TableName)).ToArray();
            var columns = columnPositions.Select(x => x.value).ToArray(); var keys = keyPositions.Select(x => x.value).ToArray();
            var previous = plan.FindPrevious(DatabaseVersionedResourceKind.TableMetadata, table.SchemaName, table.TableName);
            if (previous != null)
            {
                var original = await runtime.Snapshots.ReadTableMetadataAsync(previous.RevisionKey, ct).ConfigureAwait(false);
                previousTableScalars[previous.RevisionKey] = original.Table;
                var oldColumns = plan.PreviousColumns.Select((value, index) => (value, index))
                    .Where(x => SameTable(x.value.Column.SchemaName, x.value.Column.TableName, table.SchemaName, table.TableName)).ToArray();
                var oldKeys = plan.PreviousPrimaryKeys.Select((value, index) => (value, index))
                    .Where(x => SameTable(x.value.Column.SchemaName, x.value.Column.TableName, table.SchemaName, table.TableName)).ToArray();
                if (SnapshotValueEquality.Table(table, original.Table) &&
                    SnapshotValueEquality.Sequence(columns, oldColumns.Select(x => x.value.Column).ToArray(), SnapshotValueEquality.Column) &&
                    SnapshotValueEquality.Sequence(keys, oldKeys.Select(x => x.value.Column).ToArray(), SnapshotValueEquality.Key))
                {
                    plan.Resources.Add(previous with { SortOrdinal = ordinal++ });
                    for (int i = 0; i < columns.Length; i++) { columnIds[columnPositions[i].index] = oldColumns[i].value.ColumnRevisionKey; reusedColumns.Add(oldColumns[i].index); }
                    for (int i = 0; i < keys.Length; i++) { keyIds[keyPositions[i].index] = oldKeys[i].value.KeyColumnKey; reusedKeys.Add(oldKeys[i].index); }
                    continue;
                }
            }
            var staged = await SnapshotStaging.TableAsync(runtime, plan, table, columns, keys, ordinal++, ct).ConfigureAwait(false);
            plan.Resources.Add(staged.Resource);
            for (int i = 0; i < columns.Length; i++) columnIds[columnPositions[i].index] = staged.Columns[i];
            for (int i = 0; i < keys.Length; i++) keyIds[keyPositions[i].index] = staged.Keys[i];
        }
        for (int i = 0; i < edited.Columns.Count; i++)
        {
            var column = edited.Columns[i]; budget.Add(column.ColumnName, column.DataType);
            long? id = columnIds[i];
            if (!id.HasValue)
                for (int p = 0; p < plan.PreviousColumns.Count; p++)
                    if (!reusedColumns.Contains(p) && SnapshotValueEquality.Column(column, plan.PreviousColumns[p].Column))
                    { id = plan.PreviousColumns[p].ColumnRevisionKey; reusedColumns.Add(p); break; }
            id ??= await runtime.Snapshots.StageRuntimeUnitAsync(
                (writer, token) => writer.InsertColumnAsync(plan.SnapshotKey, null, column, i, ct: token), ct).ConfigureAwait(false);
            plan.Columns.Add(id.Value);
        }
        for (int i = 0; i < edited.PrimaryKeys.Count; i++)
        {
            var key = edited.PrimaryKeys[i]; budget.Add(key.ConstraintName, key.ColumnName);
            long? id = keyIds[i];
            if (!id.HasValue)
                for (int p = 0; p < plan.PreviousPrimaryKeys.Count; p++)
                    if (!reusedKeys.Contains(p) && SnapshotValueEquality.Key(key, plan.PreviousPrimaryKeys[p].Column))
                    { id = plan.PreviousPrimaryKeys[p].KeyColumnKey; reusedKeys.Add(p); break; }
            id ??= await runtime.Snapshots.StageRuntimeUnitAsync(
                (writer, token) => writer.InsertPrimaryKeyAsync(plan.SnapshotKey, null, key, i, ct: token), ct).ConfigureAwait(false);
            plan.PrimaryKeys.Add(id.Value);
        }
        ordinal = 0; long rows = 0;
        foreach (var data in edited.TableDataSets)
        {
            budget.Add(data.SchemaName, data.TableName);
            var previous = plan.FindPrevious(DatabaseVersionedResourceKind.TableData, data.SchemaName, data.TableName);
            var table = edited.Tables.FirstOrDefault(x => SameTable(x.SchemaName, x.TableName, data.SchemaName, data.TableName));
            if (previous != null && await IsUnchangedDataAsync(previous, data, ct).ConfigureAwait(false) &&
                await CanReuseDataCompanionAsync(plan, previous, table, previousTableScalars, ct).ConfigureAwait(false))
            {
                plan.Resources.Add(previous with { SortOrdinal = ordinal++ }); rows = checked(rows + data.Rows.Count); continue;
            }
            var staged = await runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
            {
                long key = await runtime.Snapshots.RuntimeResourceAsync(writer, plan, DatabaseVersionedResourceKind.TableData,
                    data.SchemaName, data.TableName, ordinal, token).ConfigureAwait(false);
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
        PreserveUnchangedResourceOrder(plan);
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
    private static void PreserveUnchangedResourceOrder(RuntimeSnapshotPlan plan)
    {
        static int Collection(RuntimeSnapshotResource value) => value.Kind switch
        { DatabaseVersionedResourceKind.TableMetadata => 1, DatabaseVersionedResourceKind.TableData => 2, _ => 0 };
        foreach (var group in plan.Resources.GroupBy(Collection))
        {
            var before = plan.Previous.Where(value => Collection(value) == group.Key)
                .OrderBy(value => value.SortOrdinal).ThenBy(value => value.ResourceKey).ToArray();
            if (!group.Select(value => value.ResourceKey).SequenceEqual(before.Select(value => value.ResourceKey))) continue;
            var ordinals = before.ToDictionary(value => value.ResourceKey, value => value.SortOrdinal);
            for (int i = 0; i < plan.Resources.Count; i++)
                if (ordinals.TryGetValue(plan.Resources[i].ResourceKey, out long ordinal))
                    plan.Resources[i] = plan.Resources[i] with { SortOrdinal = ordinal };
        }
    }
    private async Task<HashSet<long>> ReadUnchangedObjectRevisionsAsync(RuntimeSnapshotPlan plan,
        IReadOnlyList<SqlDatabaseObject> values, CancellationToken ct)
    {
        var candidates = values.Select(value => (Value: value, Revision: SnapshotIdentity.ResourceKind(value.Kind) is { } kind
                ? plan.FindPrevious(kind, value.SchemaName, value.ObjectName)?.RevisionKey : null))
            .Where(candidate => candidate.Revision.HasValue).ToArray();
        var revisions = candidates.Select(candidate => candidate.Revision!.Value).Distinct().ToArray();
        var result = new HashSet<long>();
        if (revisions.Length == 0) return result;
        var identities = await runtime.Snapshots.ReadObjectTextIdentitiesAsync(revisions, ct).ConfigureAwait(false);
        var batch = new List<long>(); long bytes = 0;
        foreach (long revision in revisions)
        {
            long size = identities[revision].ByteCount;
            if (batch.Count != 0 && size > RelationalSnapshotStore.MaximumDefinitionBatchBytes - bytes) await FlushAsync().ConfigureAwait(false);
            batch.Add(revision); bytes += size;
        }
        if (batch.Count != 0) await FlushAsync().ConfigureAwait(false);
        return result;
        async Task FlushAsync()
        {
            var originals = await runtime.Snapshots.ReadObjectsAsync(batch, ct).ConfigureAwait(false);
            foreach (var candidate in candidates)
                if (originals.TryGetValue(candidate.Revision!.Value, out var original) && SnapshotValueEquality.Object(candidate.Value, original))
                    result.Add(candidate.Revision.Value);
            batch.Clear(); bytes = 0;
        }
    }
    private async Task<bool> IsUnchangedDataAsync(RuntimeSnapshotResource previous, SqlTableDataSet data, CancellationToken ct)
    {
        if (_captureBaselines.TryGetValue(previous.RevisionKey, out var known))
        {
            if (known.SchemaName != data.SchemaName || known.TableName != data.TableName || known.RowCount != data.RowCount ||
                !known.ImportedAtUtc.EqualsExact(data.ImportedAtUtc) || known.Rows.Count != data.Rows.Count) return false;
            for (int i = 0; i < data.Rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!SnapshotValueEquality.Row(known.Rows[i], data.Rows[i])) return false;
            }
            return true;
        }
        var descriptor = await runtime.CapturedData.GetForRevisionAsync(previous.RevisionKey, ct).ConfigureAwait(false);
        if (descriptor == null || previous.SchemaName != data.SchemaName || previous.Name != data.TableName ||
            descriptor.Summary.ReportedRowCount != data.RowCount || descriptor.Summary.ActualRowCount != data.Rows.Count ||
            !descriptor.Summary.ImportedAtUtc.EqualsExact(data.ImportedAtUtc)) return false;
        int index = 0;
        await foreach (var row in runtime.CapturedData.StreamRowsAsync(descriptor.Summary.DataSetKey, cancellationToken: ct).ConfigureAwait(false))
            if (index == data.Rows.Count || !SnapshotValueEquality.Row(row.Value, data.Rows[index++])) return false;
        if (index != data.Rows.Count) return false;
        RememberCapture(previous.RevisionKey, data); return true;
    }
    private async Task<bool> CanReuseDataCompanionAsync(RuntimeSnapshotPlan plan, RuntimeSnapshotResource data,
        SqlTable? desiredTable, Dictionary<long, SqlTable> previousTables, CancellationToken ct)
    {
        SqlTable? previousTable = null;
        var table = plan.FindPrevious(DatabaseVersionedResourceKind.TableMetadata, data.SchemaName, data.Name);
        if (table != null && !previousTables.TryGetValue(table.RevisionKey, out previousTable))
        {
            previousTable = (await runtime.Snapshots.ReadTableMetadataAsync(table.RevisionKey, ct).ConfigureAwait(false)).Table;
            previousTables[table.RevisionKey] = previousTable;
        }
        // Preserve hidden legacy companion discrepancies on an otherwise unchanged
        // edit. An explicit table scalar edit still updates the capture's companion,
        // which can later recreate that table when restoring captured data alone.
        if (SnapshotValueEquality.Table(desiredTable, previousTable)) return true;
        var companion = await runtime.Snapshots.ReadTableDataMetadataAsync(data.RevisionKey, ct).ConfigureAwait(false);
        return SnapshotValueEquality.Table(desiredTable, companion.Table);
    }
    private void RememberCapture(long revision, SqlTableDataSet data) => _captureBaselines[revision] = new()
    {
        SchemaName = data.SchemaName, TableName = data.TableName, RowCount = data.RowCount, ImportedAtUtc = data.ImportedAtUtc,
        // JsonElement is immutable. Retain its existing backing data and copy only
        // the row handles, so replacing rows in the editable list cannot alter the baseline.
        Rows = data.Rows.ToList()
    };
    private static bool SameTable(string schema, string table, string otherSchema, string otherTable) =>
        StringComparer.OrdinalIgnoreCase.Equals(schema, otherSchema) && StringComparer.OrdinalIgnoreCase.Equals(table, otherTable);
    private static async IAsyncEnumerable<JsonElement> RowsAsync(IEnumerable<JsonElement> rows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var row in rows) { ct.ThrowIfCancellationRequested(); yield return row; }
        await Task.CompletedTask;
    }
}

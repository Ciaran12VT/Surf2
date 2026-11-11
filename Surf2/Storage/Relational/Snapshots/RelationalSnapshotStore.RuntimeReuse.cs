using System.Data;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class RelationalSnapshotStore
{
    private sealed record RuntimeObjectValue(string Schema, string Name, int Kind, string Description,
        string ParentSchema, string ParentName, long ContentKey);

    /// <summary>All inputs are sealed immutable revisions; publication still fences the mutable head afterwards.</summary>
    private async Task NormalizeRuntimeResourcesAsync(RuntimeSnapshotPlan plan, CancellationToken ct)
    {
        if (plan.IsNew) return;
        var previous = plan.Previous.ToDictionary(resource => resource.ResourceKey);
        var objects = plan.Resources.Where(value => value.Kind is not (DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData) &&
            previous.TryGetValue(value.ResourceKey, out var old) && value.RevisionKey != old.RevisionKey).ToArray();
        var values = new Dictionary<long, RuntimeObjectValue>();
        foreach (var batch in objects.SelectMany(value => new[] { value.RevisionKey, previous[value.ResourceKey].RevisionKey }).Distinct().Chunk(128))
            foreach (var pair in await ReadRuntimeObjectValuesAsync(batch, ct).ConfigureAwait(false)) values.Add(pair.Key, pair.Value);
        var columns = new Dictionary<long,long>(); var keys = new Dictionary<long,long>();
        for (int i = 0; i < plan.Resources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var value = plan.Resources[i];
            if (!previous.TryGetValue(value.ResourceKey, out var old) || value.RevisionKey == old.RevisionKey) continue;
            bool same;
            if (value.Kind == DatabaseVersionedResourceKind.TableMetadata)
            {
                var before = await ReadTableMetadataAsync(old.RevisionKey, ct).ConfigureAwait(false);
                var after = await ReadTableMetadataAsync(value.RevisionKey, ct).ConfigureAwait(false);
                same = SnapshotValueEquality.Table(before.Table, after.Table) &&
                    SnapshotValueEquality.Sequence(before.Columns, after.Columns, (a,b) => SnapshotValueEquality.Column(a.Column, b.Column)) &&
                    SnapshotValueEquality.Sequence(before.PrimaryKeys, after.PrimaryKeys, (a,b) => SnapshotValueEquality.Key(a.Column, b.Column));
                if (same)
                {
                    for (int c = 0; c < after.Columns.Count; c++) columns[after.Columns[c].ColumnRevisionKey] = before.Columns[c].ColumnRevisionKey;
                    for (int k = 0; k < after.PrimaryKeys.Count; k++) keys[after.PrimaryKeys[k].KeyColumnKey] = before.PrimaryKeys[k].KeyColumnKey;
                }
            }
            else if (value.Kind == DatabaseVersionedResourceKind.TableData)
                same = await RuntimeDataEqualAsync(old.RevisionKey, value.RevisionKey, ct).ConfigureAwait(false);
            else same = values[old.RevisionKey] == values[value.RevisionKey];
            if (same) plan.Resources[i] = value with { RevisionKey = old.RevisionKey };
        }
        for (int i = 0; i < plan.Columns.Count; i++) if (columns.TryGetValue(plan.Columns[i], out long id)) plan.Columns[i] = id;
        for (int i = 0; i < plan.PrimaryKeys.Count; i++) if (keys.TryGetValue(plan.PrimaryKeys[i], out long id)) plan.PrimaryKeys[i] = id;
    }

    private async Task<Dictionary<long, RuntimeObjectValue>> ReadRuntimeObjectValuesAsync(long[] revisions, CancellationToken ct)
    {
        var result = new Dictionary<long, RuntimeObjectValue>();
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(o.SchemaName)+DATALENGTH(o.ObjectName)+DATALENGTH(o.TypeDescription)
                +DATALENGTH(o.ParentSchemaName)+DATALENGTH(o.ParentObjectName),o.RevisionKey,o.SchemaName,o.ObjectName,
                o.ObjectKind,o.TypeDescription,o.ParentSchemaName,o.ParentObjectName,o.DefinitionContentKey
            FROM OPENJSON(@Keys) WITH(RevisionKey bigint '$') k
            JOIN surf.DatabaseObjectRevision o ON o.RevisionKey=k.RevisionKey
            JOIN surf.SnapshotResourceRevision r ON r.RevisionKey=o.RevisionKey
            WHERE r.IsSealed=1;
            """, Text("@Keys", JsonSerializer.Serialize(revisions)));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var budget = new SnapshotMetadataBudget();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            budget.Add(reader.GetInt64(0), command);
            result.Add(reader.GetInt64(1), new(reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8)));
        }
        if (result.Count != revisions.Length) throw new InvalidOperationException("A staged object revision is not sealed.");
        return result;
    }

    private async Task<bool> RuntimeDataEqualAsync(long beforeRevision, long afterRevision, CancellationToken ct)
    {
        var capture = new RelationalCaptureStore(session);
        var before = await capture.GetForRevisionAsync(beforeRevision, ct).ConfigureAwait(false);
        var after = await capture.GetForRevisionAsync(afterRevision, ct).ConfigureAwait(false);
        if (before == null || after == null) throw new InvalidOperationException("A staged dataset is not ready.");
        if (before.Summary.ReportedRowCount != after.Summary.ReportedRowCount ||
            before.Summary.ActualRowCount != after.Summary.ActualRowCount ||
            !before.Summary.ImportedAtUtc.EqualsExact(after.Summary.ImportedAtUtc) ||
            !before.Columns.SequenceEqual(after.Columns)) return false;
        var oldMetadata = await ReadTableDataMetadataAsync(beforeRevision, ct).ConfigureAwait(false);
        var newMetadata = await ReadTableDataMetadataAsync(afterRevision, ct).ConfigureAwait(false);
        if (oldMetadata.SchemaName != newMetadata.SchemaName || oldMetadata.TableName != newMetadata.TableName ||
            !SnapshotValueEquality.Table(oldMetadata.Table, newMetadata.Table)) return false;
        await using var oldRows = capture.StreamRowsAsync(before.Summary.DataSetKey, cancellationToken: ct).GetAsyncEnumerator(ct);
        await using var newRows = capture.StreamRowsAsync(after.Summary.DataSetKey, cancellationToken: ct).GetAsyncEnumerator(ct);
        while (await oldRows.MoveNextAsync().ConfigureAwait(false))
            if (!await newRows.MoveNextAsync().ConfigureAwait(false) || oldRows.Current.RowOrdinal != newRows.Current.RowOrdinal ||
                !SnapshotValueEquality.Row(oldRows.Current.Value, newRows.Current.Value)) return false;
        return !await newRows.MoveNextAsync().ConfigureAwait(false);
    }

    private static bool RuntimeStateUnchanged(RuntimeSnapshotPlan plan)
    {
        if (plan.IsNew || plan.Previous.Count != plan.Resources.Count ||
            !plan.Columns.SequenceEqual(plan.PreviousColumns.Select(value => value.ColumnRevisionKey)) ||
            !plan.PrimaryKeys.SequenceEqual(plan.PreviousPrimaryKeys.Select(value => value.KeyColumnKey)) ||
            !plan.FullDataSelections.SequenceEqual(plan.PreviousFullDataSelections, StringComparer.Ordinal)) return false;
        var previous = plan.Previous.ToDictionary(value => value.ResourceKey);
        return plan.Resources.All(value => previous.TryGetValue(value.ResourceKey, out var old) && value == old);
    }
}

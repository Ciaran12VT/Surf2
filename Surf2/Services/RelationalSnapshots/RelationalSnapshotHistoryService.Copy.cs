using System.Runtime.CompilerServices;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Services.RelationalSnapshots;

public sealed partial class RelationalSnapshotHistoryService
{
    public async Task<RelationalCaptureResult> RestoreVersionAsCopyAsync(long sourceSnapshotKey, long sourceVersionKey,
        Scope selectedScope, StateToken expectedScope, string displayName, IProgress<RelationalCaptureProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (expectedScope.Epoch != _runtime.Session.Epoch || string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Select a scope and copy name.");
        var scope = await _runtime.StateStore.ReadScopeAsync(expectedScope.Key, ct).ConfigureAwait(false) ?? throw new StateConflictException("copy scope");
        if (scope.Value.ScopeId != selectedScope.ScopeId || scope.Token.PublicationId != expectedScope.PublicationId ||
            !scope.Token.Version.AsSpan().SequenceEqual(expectedScope.Version)) throw new StateConflictException("copy scope");
        await using var source = await OpenVersionAsync(sourceSnapshotKey, sourceVersionKey, ct).ConfigureAwait(false);
        var plan = await _runtime.Snapshots.BeginRuntimeStageAsync(null, displayName, source.Header.DatabaseName, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        var budget = new SnapshotOperationBudget(_limits); var columns = new Dictionary<long, long>(); var keys = new Dictionary<long, long>();
        long rows = 0;
        await foreach (var entry in source.StreamAsync(HistoricalCollection.Objects, ct).ConfigureAwait(false))
        {
            budget.Add(entry.SchemaName, entry.Name);
            var value = await _selectedReads.ReadObjectAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false);
            plan.Resources.Add(await CopyObjectAsync(plan, value, entry.SortOrdinal, ct).ConfigureAwait(false));
        }
        await foreach (var entry in source.StreamAsync(HistoricalCollection.Tables, ct).ConfigureAwait(false))
        {
            var table = await StageHistoricalTableAsync(plan, source, entry, entry.SortOrdinal, budget, ct).ConfigureAwait(false);
            plan.Resources.Add(table.Resource);
            for (int i = 0; i < table.Details.Columns.Count; i++) columns.TryAdd(table.Details.Columns[i].ColumnRevisionKey, table.Columns[i]);
            for (int i = 0; i < table.Details.PrimaryKeys.Count; i++) keys.TryAdd(table.Details.PrimaryKeys[i].KeyColumnKey, table.Keys[i]);
            progress?.Report(new("Copying table metadata", budget.Units, rows));
        }
        await foreach (var entry in source.StreamAsync(HistoricalCollection.Columns, ct).ConfigureAwait(false))
        {
            budget.Add(64); long old = entry.ChildKey!.Value;
            if (!columns.TryGetValue(old, out long child))
            {
                var value = await _runtime.Snapshots.ReadRuntimeColumnAsync(sourceSnapshotKey, old, ct).ConfigureAwait(false);
                budget.Add(value.SchemaName, value.TableName, value.ColumnName, value.DataType);
                child = await _runtime.Snapshots.StageRuntimeUnitAsync((w, token) => w.InsertColumnAsync(plan.SnapshotKey, null, value, entry.SortOrdinal, ct: token), ct).ConfigureAwait(false);
                columns.Add(old, child);
            }
            plan.Columns.Add(child);
        }
        await foreach (var entry in source.StreamAsync(HistoricalCollection.PrimaryKeys, ct).ConfigureAwait(false))
        {
            budget.Add(64); long old = entry.ChildKey!.Value;
            if (!keys.TryGetValue(old, out long child))
            {
                var value = await _runtime.Snapshots.ReadRuntimeKeyAsync(sourceSnapshotKey, old, ct).ConfigureAwait(false);
                budget.Add(value.SchemaName, value.TableName, value.ConstraintName, value.ColumnName);
                child = await _runtime.Snapshots.StageRuntimeUnitAsync((w, token) => w.InsertPrimaryKeyAsync(plan.SnapshotKey, null, value, entry.SortOrdinal, ct: token), ct).ConfigureAwait(false);
                keys.Add(old, child);
            }
            plan.PrimaryKeys.Add(child);
        }
        await foreach (var entry in source.StreamAsync(HistoricalCollection.TableDataSets, ct).ConfigureAwait(false))
        {
            budget.Add(entry.SchemaName, entry.Name);
            var data = await CopyDataAsync(plan, entry, entry.SchemaName, entry.Name, entry.SortOrdinal, ct).ConfigureAwait(false);
            plan.Resources.Add(data.Resource); rows = checked(rows + data.Rows);
            progress?.Report(new("Copying captured rows", budget.Units, rows));
        }
        await foreach (var entry in source.StreamAsync(HistoricalCollection.FullDataTableNames, ct).ConfigureAwait(false))
        { budget.Add(entry.Name); plan.FullDataSelections.Add(entry.Name); }
        scope.Value.Resources.Add(new() { ResourceId=Guid.NewGuid().ToString("N"), Kind=ResourceKind.DatabaseSnapshot,
            Path=plan.Header.SnapshotId, DisplayNameOverride=displayName, IncludeChildren=true, AddedAtUtc=plan.Header.ImportedAtUtc });
        progress?.Report(new("Publishing", budget.Units, rows));
        var result = await _runtime.Snapshots.PublishRuntimeStageAsync(plan, scope.Value, expectedScope, Guid.NewGuid(), "Copied version", ct).ConfigureAwait(false);
        return new(result.Snapshot, result.ScopeToken!, scope.Value, rows, result.VersionKey);
    }

    public async Task<RelationalHistoryRestoreResult> RestoreResourceAsCopyAsync(SnapshotRuntimeToken expectedTarget,
        long sourceSnapshotKey, long sourceVersionKey, long sourceResourceKey, string schema, string name,
        string? editedText = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || schema.Length > 128 || name.Length > 128)
            throw new ArgumentException("Copy requires SQL schema/name fields of at most 128 characters.");
        var (plan, budget) = await BeginRestoreAsync(expectedTarget, ct).ConfigureAwait(false);
        plan.Resources.AddRange(plan.Previous);
        await foreach (var column in SnapshotStaging.Pages(c => _runtime.Snapshots.ListCurrentColumnsAsync(plan.SnapshotKey, 128, c, ct), ct)) plan.Columns.Add(column.ColumnRevisionKey);
        await foreach (var key in SnapshotStaging.Pages(c => _runtime.Snapshots.ListCurrentPrimaryKeysAsync(plan.SnapshotKey, 128, c, ct), ct)) plan.PrimaryKeys.Add(key.KeyColumnKey);
        await foreach (var selection in SnapshotStaging.Pages(c => _runtime.Snapshots.ListFullDataSelectionsAsync(plan.SnapshotKey, 128, c, ct), ct)) plan.FullDataSelections.Add(selection.TableName);
        await using var source = await OpenVersionAsync(sourceSnapshotKey, sourceVersionKey, ct).ConfigureAwait(false);
        var entry = await source.FindResource(sourceResourceKey, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Selected copy resource is absent.");
        var kind = EntryKind(entry); budget.Add(schema, name);
        bool Same(RuntimeSnapshotResource r) => RuntimeTableComparer.Instance.Equals((r.SchemaName, r.Name), (schema, name));
        if (kind == DatabaseVersionedResourceKind.TableMetadata)
        {
            var target = plan.Resources.FirstOrDefault(r => r.Kind == kind && Same(r));
            if (target != null)
            {
                var old = await _runtime.Snapshots.ReadTableMetadataAsync(target.RevisionKey, ct).ConfigureAwait(false);
                var oldColumns=old.Columns.Select(c=>c.ColumnRevisionKey).ToHashSet(); var oldKeys=old.PrimaryKeys.Select(k=>k.KeyColumnKey).ToHashSet();
                plan.Columns.RemoveAll(oldColumns.Contains); plan.PrimaryKeys.RemoveAll(oldKeys.Contains);
            }
            plan.Resources.RemoveAll(r => Same(r) && r.Kind is DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData);
            RemoveSelection(plan,schema,name);
            var details = await source.ReadTableMetadataAsync(entry,ct).ConfigureAwait(false);
            if (details.Columns.Count > _limits.MaximumTableColumns || details.PrimaryKeys.Count > _limits.MaximumTableColumns)
                throw new InvalidOperationException("Selected copy table exceeds its child budget.");
            details.Table.SchemaName=schema; details.Table.TableName=name;
            foreach(var column in details.Columns) { budget.Add(column.Column.ColumnName,column.Column.DataType); column.Column.SchemaName=schema; column.Column.TableName=name; }
            foreach(var key in details.PrimaryKeys) { budget.Add(key.Column.ConstraintName,key.Column.ColumnName); key.Column.SchemaName=schema; key.Column.TableName=name; }
            var staged=await SnapshotStaging.TableAsync(_runtime,plan,details.Table,details.Columns.Select(c=>c.Column).ToArray(),
                details.PrimaryKeys.Select(k=>k.Column).ToArray(),NextOrdinal(plan,kind),ct,target?.ResourceKey).ConfigureAwait(false);
            plan.Resources.Add(staged.Resource); plan.Columns.AddRange(staged.Columns); plan.PrimaryKeys.AddRange(staged.Keys);
        }
        else if(kind==DatabaseVersionedResourceKind.TableData)
        {
            var copied=await CopyDataAsync(plan,entry,schema,name,NextOrdinal(plan,kind),ct).ConfigureAwait(false);
            plan.Resources.RemoveAll(r=>r.Kind==kind && Same(r)); plan.Resources.Add(copied.Resource);
            var target=plan.Resources.FirstOrDefault(r=>r.Kind==DatabaseVersionedResourceKind.TableMetadata && Same(r));
            var targetDetails=target==null?null:await _runtime.Snapshots.ReadTableMetadataAsync(target.RevisionKey,ct).ConfigureAwait(false);
            var scalar=targetDetails?.Table ?? await SourceTableScalarAsync(source,entry,ct).ConfigureAwait(false);
            var descriptor=await _capture.GetForRevisionAsync(copied.Resource.RevisionKey,ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Copied dataset is not Ready.");
            if(scalar!=null)
            {
                scalar.SchemaName=schema; scalar.TableName=name; scalar.HasFullData=true;
                scalar.FullDataRowCount=Math.Max(descriptor.Summary.ReportedRowCount,descriptor.Summary.ActualRowCount); scalar.FullDataImportedAtUtc=descriptor.Summary.ImportedAtUtc;
                var table=await SnapshotStaging.TableAsync(_runtime,plan,scalar,targetDetails?.Columns.Select(c=>c.Column).ToArray()??[],
                    targetDetails?.PrimaryKeys.Select(k=>k.Column).ToArray()??[],target?.SortOrdinal??NextOrdinal(plan,DatabaseVersionedResourceKind.TableMetadata),ct,target?.ResourceKey).ConfigureAwait(false);
                if(target!=null) plan.Resources.Remove(target); plan.Resources.Add(table.Resource);
                if(targetDetails!=null)
                {
                    for(int i=0;i<targetDetails.Columns.Count;i++) ReplaceId(plan.Columns,targetDetails.Columns[i].ColumnRevisionKey,table.Columns[i]);
                    for(int i=0;i<targetDetails.PrimaryKeys.Count;i++) ReplaceId(plan.PrimaryKeys,targetDetails.PrimaryKeys[i].KeyColumnKey,table.Keys[i]);
                }
            }
            string fullName=SqlName.FormatPlainMultipartName(schema,name);
            if(!plan.FullDataSelections.Contains(fullName,StringComparer.OrdinalIgnoreCase)) plan.FullDataSelections.Add(fullName);
        }
        else
        {
            var value=await _selectedReads.ReadObjectAsync(entry.RevisionKey!.Value,ct).ConfigureAwait(false);
            value.SchemaName=schema; value.ObjectName=name;
            if(editedText!=null) { if(editedText.Length*2L>_limits.MaximumDefinitionBytes) throw new InvalidOperationException("Edited copy definition exceeds its byte budget."); value.Definition=editedText; }
            plan.Resources.RemoveAll(r=>r.Kind==kind && Same(r));
            plan.Resources.Add(await CopyObjectAsync(plan,value,NextObjectOrdinal(plan),ct).ConfigureAwait(false));
        }
        var result=await _runtime.Snapshots.PublishRuntimeStageAsync(plan,null,null,Guid.NewGuid(),"Copied resource",ct).ConfigureAwait(false);
        return new(result.Snapshot,result.VersionKey);
    }
    private Task<RuntimeSnapshotResource> CopyObjectAsync(RuntimeSnapshotPlan plan,SqlDatabaseObject value,long ordinal,CancellationToken ct) =>
        _runtime.Snapshots.StageRuntimeUnitAsync(async(w,token)=>
        {
            var kind=SnapshotIdentity.ResourceKind(value.Kind) ?? throw new InvalidOperationException("Unknown selected copy object kind.");
            long resource=await _runtime.Snapshots.RuntimeResourceAsync(w,plan,kind,value.SchemaName,value.ObjectName,ordinal,token).ConfigureAwait(false);
            long revision=await w.InsertObjectRevisionAsync(resource,value,token).ConfigureAwait(false); await w.SealRevisionAsync(revision,token).ConfigureAwait(false);
            return new RuntimeSnapshotResource(resource,revision,kind,value.SchemaName,value.ObjectName,ordinal);
        },ct);
    private async Task<(RuntimeSnapshotResource Resource,long Rows)> CopyDataAsync(RuntimeSnapshotPlan plan,HistoricalSnapshotEntry entry,
        string schema,string name,long ordinal,CancellationToken ct)
    {
        var source=await _capture.GetForRevisionAsync(entry.RevisionKey!.Value,ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Selected source dataset is not Ready.");
        var resource=await _runtime.Snapshots.StageRuntimeUnitAsync(async(w,token)=>
        {
            long key=await _runtime.Snapshots.RuntimeResourceAsync(w,plan,DatabaseVersionedResourceKind.TableData,schema,name,ordinal,token).ConfigureAwait(false);
            long revision=await w.InsertTableDataRevisionAsync(key,schema,name,ct:token).ConfigureAwait(false);
            return new RuntimeSnapshotResource(key,revision,DatabaseVersionedResourceKind.TableData,schema,name,ordinal);
        },ct).ConfigureAwait(false);
        var handle=await _capture.CreateDataSetAsync(new(resource.RevisionKey,source.Columns,source.Summary.ReportedRowCount,
            source.Summary.ImportedAtUtc,source.Summary.ActualRowCount),ct).ConfigureAwait(false);
        long rows=await _capture.AppendRowsAsync(handle,CopyRowsAsync(source.Summary.DataSetKey,ct),0,ct).ConfigureAwait(false);
        await _capture.CompleteDataSetAsync(handle,source.Summary.ActualRowCount,ct).ConfigureAwait(false);
        await _runtime.Snapshots.StageRuntimeUnitAsync(async(w,token)=> { await w.SealRevisionAsync(resource.RevisionKey,token).ConfigureAwait(false); return 0; },ct).ConfigureAwait(false);
        return(resource,rows);
    }
    private async IAsyncEnumerable<JsonElement> CopyRowsAsync(long dataSet,[EnumeratorCancellation] CancellationToken ct)
    { await foreach(var row in _capture.StreamRowsAsync(dataSet,cancellationToken:ct).ConfigureAwait(false)) yield return row.Value; }
    private async Task<SqlTable?> SourceTableScalarAsync(HistoricalSnapshotContext source,HistoricalSnapshotEntry data,CancellationToken ct)
    {
        var budget = new SnapshotOperationBudget(_limits);
        await foreach(var entry in source.StreamAsync(HistoricalCollection.Tables,ct).ConfigureAwait(false))
        {
            budget.Add(entry.SchemaName, entry.Name);
            if(RuntimeTableComparer.Instance.Equals((entry.SchemaName,entry.Name),(data.SchemaName,data.Name))) return entry.Table;
        }
        return null;
    }
}

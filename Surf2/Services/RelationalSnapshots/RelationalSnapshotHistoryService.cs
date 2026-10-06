using Surf2.Models;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalDocuments;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalSnapshots;

public sealed record RelationalHistoryPreview(string? Text, CaptureDataSetDescriptor? CapturedData);
public sealed record RelationalHistoryRestoreResult(SnapshotSummary Snapshot, long VersionKey);

public sealed partial class RelationalSnapshotHistoryService
{
    private readonly RelationalRuntime _runtime;
    private readonly RelationalSnapshotOperationLimits _limits;
    private readonly RelationalCaptureStore _capture;
    private readonly RelationalSnapshotStore _selectedReads;
    public RelationalSnapshotHistoryService(RelationalRuntime runtime, RelationalSnapshotOperationLimits? limits = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); _limits = limits ?? new(); _limits.Validate();
        _capture = new(runtime.Session, _limits.Capture);
        _selectedReads = new(runtime.Session, new RelationalContentStore(), new() { SelectedDefinitionBytes = _limits.MaximumDefinitionBytes });
    }
    public Task<SnapshotPage<SnapshotHistorySummary>> ListHistoriesAsync(long snapshotKey, int size = 50,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => _runtime.Snapshots.ListHistoriesAsync(snapshotKey, size, cursor, ct);
    public Task<SnapshotPage<SnapshotVersionSummary>> ListVersionsAsync(long snapshotKey, long historyKey, int size = 50,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => _runtime.Snapshots.ListHistoryVersionsAsync(snapshotKey, historyKey, size, cursor, ct);
    public Task<SnapshotPage<SnapshotChangeSummary>> ListChangesAsync(long snapshotKey, long versionKey, int size = 50,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => _runtime.Snapshots.ListChangesAsync(snapshotKey, versionKey, size, cursor, ct);
    public Task<HistoricalSnapshotContext> OpenVersionAsync(long snapshotKey, long versionKey, CancellationToken ct = default) =>
        _runtime.Snapshots.OpenHistoricalSnapshotAsync(snapshotKey, versionKey, ct);

    public async Task<RelationalComparisonTarget> ResourceTargetAsync(HistoricalSnapshotContext version,
        HistoricalSnapshotEntry entry, CancellationToken ct = default)
    {
        RequireContext(version);
        entry = await SelectedEntryAsync(version, entry, ct).ConfigureAwait(false);
        return RelationalComparisonService.FromResource(await version.ResourceSummary(entry, ct).ConfigureAwait(false),
            _runtime.Session.Epoch, version.VersionKey, entry.EntryKey);
    }
    public RelationalComparisonTarget VersionTarget(SnapshotSummary snapshot, long versionKey, string versionName) =>
        new(new(snapshot.DisplayName + " / " + versionName, "Database version", "db://" + snapshot.SnapshotId,
            ComparisonResourceKind.DatabaseSnapshot, "DatabaseSnapshot", true, false, false,
            $"snapshot:{snapshot.SnapshotKey}:version:{versionKey}", SnapshotId: snapshot.SnapshotId),
            _runtime.Session.Epoch, snapshot.SnapshotKey, VersionKey: versionKey);

    public async Task<RelationalHistoryPreview> ReadPreviewAsync(HistoricalSnapshotContext version,
        HistoricalSnapshotEntry entry, CancellationToken ct = default)
    {
        RequireContext(version);
        entry = await SelectedEntryAsync(version, entry, ct).ConfigureAwait(false);
        if (entry.Collection == HistoricalCollection.Tables)
            return new(RelationalDocumentService.RenderTable(await version.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false),
                _limits.MaximumDefinitionBytes), null);
        if (entry.Collection == HistoricalCollection.TableDataSets)
            return new(null, await _capture.GetForRevisionAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Selected historical dataset is not Ready."));
        if (entry.Collection != HistoricalCollection.Objects) throw new ArgumentException("Select a historical resource.");
        var value = await _selectedReads.ReadObjectAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false);
        if (value.Definition.Length * 2L > _limits.MaximumDefinitionBytes) throw new InvalidOperationException("Selected definition exceeds its preview budget.");
        return new(value.Definition, null);
    }
    public async Task<RelationalHistoryPreview> ReadPreviousPayloadAsync(long snapshotKey, SnapshotChangeSummary change,
        CancellationToken ct = default)
    {
        SnapshotChangeSummary? stored = null;
        await foreach (var item in SnapshotStaging.Pages(c => ListChangesAsync(snapshotKey, change.VersionKey, 128, c, ct), ct))
            if (item.ChangeKey == change.ChangeKey) { stored = item; break; }
        if (stored == null || stored.VersionKey != change.VersionKey) throw new ArgumentException("Change belongs to another selected snapshot/version.");
        if (stored.PreviousRevisionKey is not { } revision) return new(null, null);
        if (stored.Kind == DatabaseVersionedResourceKind.TableData)
            return new(null, await _capture.GetForRevisionAsync(revision, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Selected previous dataset is not Ready."));
        if (stored.Kind == DatabaseVersionedResourceKind.TableMetadata)
            return new(RelationalDocumentService.RenderTable(await _runtime.Snapshots.ReadTableMetadataAsync(revision, ct).ConfigureAwait(false),
                _limits.MaximumDefinitionBytes), null);
        var value = await _selectedReads.ReadObjectAsync(revision, ct).ConfigureAwait(false);
        if (value.Definition.Length * 2L > _limits.MaximumDefinitionBytes) throw new InvalidOperationException("Selected definition exceeds its preview budget.");
        return new(value.Definition, null);
    }

    public async Task<RelationalHistoryRestoreResult> RestoreVersionAsync(SnapshotRuntimeToken expected, long versionKey,
        string? versionName = null, IProgress<RelationalCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        var (plan, budget) = await BeginRestoreAsync(expected, ct).ConfigureAwait(false);
        await using (var source = await OpenVersionAsync(expected.SnapshotKey, versionKey, ct).ConfigureAwait(false))
        {
            var columns = new Dictionary<long, long>(); var keys = new Dictionary<long, long>();
            await foreach (var entry in source.StreamAsync(HistoricalCollection.Objects, ct).ConfigureAwait(false))
            {
                budget.Add(entry.SchemaName, entry.Name);
                var kind = SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown)
                    ?? throw new InvalidOperationException("Unknown historical object kind cannot be restored implicitly.");
                plan.Resources.Add(Handle(entry, kind));
            }
            await foreach (var entry in source.StreamAsync(HistoricalCollection.Tables, ct).ConfigureAwait(false))
            {
                var table = await StageHistoricalTableAsync(plan, source, entry, entry.SortOrdinal, budget, ct).ConfigureAwait(false);
                plan.Resources.Add(table.Resource);
                for (int i = 0; i < table.Details.Columns.Count; i++) columns.Add(table.Details.Columns[i].ColumnRevisionKey, table.Columns[i]);
                for (int i = 0; i < table.Details.PrimaryKeys.Count; i++) keys.Add(table.Details.PrimaryKeys[i].KeyColumnKey, table.Keys[i]);
                progress?.Report(new("Restoring table metadata", budget.Units, 0));
            }
            // Preserve the replayed root order, including orphan scalar children, without loading their values globally.
            await foreach (var entry in source.StreamAsync(HistoricalCollection.Columns, ct).ConfigureAwait(false))
            { budget.Add(64); plan.Columns.Add(columns.GetValueOrDefault(entry.ChildKey!.Value, entry.ChildKey.Value)); }
            await foreach (var entry in source.StreamAsync(HistoricalCollection.PrimaryKeys, ct).ConfigureAwait(false))
            { budget.Add(64); plan.PrimaryKeys.Add(keys.GetValueOrDefault(entry.ChildKey!.Value, entry.ChildKey.Value)); }
            await foreach (var entry in source.StreamAsync(HistoricalCollection.TableDataSets, ct).ConfigureAwait(false))
            {
                budget.Add(entry.SchemaName, entry.Name);
                if (await _capture.GetForRevisionAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false) == null)
                    throw new InvalidOperationException("Selected historical dataset is not Ready.");
                plan.Resources.Add(Handle(entry, DatabaseVersionedResourceKind.TableData));
            }
            await foreach (var entry in source.StreamAsync(HistoricalCollection.FullDataTableNames, ct).ConfigureAwait(false))
            { budget.Add(entry.Name); plan.FullDataSelections.Add(entry.Name); }
        }
        progress?.Report(new("Publishing", budget.Units, 0));
        var result = await _runtime.Snapshots.PublishRuntimeStageAsync(plan, null, null, Guid.NewGuid(),
            versionName ?? "Restored version", ct).ConfigureAwait(false);
        return new(result.Snapshot, result.VersionKey);
    }

    public async Task<RelationalHistoryRestoreResult> RestoreResourceAsync(SnapshotRuntimeToken expected, long versionKey,
        long resourceKey, string? versionName = null, CancellationToken ct = default)
    {
        var (plan, budget) = await BeginRestoreAsync(expected, ct).ConfigureAwait(false);
        plan.Resources.AddRange(plan.Previous);
        await foreach (var column in SnapshotStaging.Pages(c => _runtime.Snapshots.ListCurrentColumnsAsync(plan.SnapshotKey, 128, c, ct), ct))
            plan.Columns.Add(column.ColumnRevisionKey);
        await foreach (var key in SnapshotStaging.Pages(c => _runtime.Snapshots.ListCurrentPrimaryKeysAsync(plan.SnapshotKey, 128, c, ct), ct))
            plan.PrimaryKeys.Add(key.KeyColumnKey);
        await foreach (var selection in SnapshotStaging.Pages(c => _runtime.Snapshots.ListFullDataSelectionsAsync(plan.SnapshotKey, 128, c, ct), ct))
            plan.FullDataSelections.Add(selection.TableName);
        await using var source = await OpenVersionAsync(expected.SnapshotKey, versionKey, ct).ConfigureAwait(false);
        var entry = await source.FindResource(resourceKey, ct).ConfigureAwait(false);
        var current = plan.Previous.FirstOrDefault(r => r.ResourceKey == resourceKey);
        if (current == null && entry == null) throw new KeyNotFoundException("Resource is absent from the current and selected historical version.");
        var kind = entry == null ? current!.Kind : EntryKind(entry);
        string schema = entry?.SchemaName ?? current!.SchemaName, name = entry?.Name ?? current!.Name;
        bool Same(RuntimeSnapshotResource r) => RuntimeTableComparer.Instance.Equals((r.SchemaName, r.Name), (schema, name));
        var table = plan.Resources.FirstOrDefault(r => r.Kind == DatabaseVersionedResourceKind.TableMetadata && Same(r));
        TableMetadataDetails? currentTable = table == null ? null : await _runtime.Snapshots.ReadTableMetadataAsync(table.RevisionKey, ct).ConfigureAwait(false);
        if (kind == DatabaseVersionedResourceKind.TableMetadata)
        {
            plan.Resources.RemoveAll(r => Same(r) && r.Kind is DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData);
            if (currentTable != null)
            {
                var removedColumns = currentTable.Columns.Select(c => c.ColumnRevisionKey).ToHashSet();
                var removedKeys = currentTable.PrimaryKeys.Select(k => k.KeyColumnKey).ToHashSet();
                plan.Columns.RemoveAll(removedColumns.Contains); plan.PrimaryKeys.RemoveAll(removedKeys.Contains);
            }
            RemoveSelection(plan, schema, name);
            if (entry != null)
            {
                var staged = await StageHistoricalTableAsync(plan, source, entry, NextOrdinal(plan, kind), budget, ct).ConfigureAwait(false);
                plan.Resources.Add(staged.Resource); plan.Columns.AddRange(staged.Columns); plan.PrimaryKeys.AddRange(staged.Keys);
            }
        }
        else if (kind == DatabaseVersionedResourceKind.TableData)
        {
            plan.Resources.RemoveAll(r => r.Kind == kind && Same(r));
            var descriptor = entry == null ? null : await _capture.GetForRevisionAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Selected historical dataset is not Ready.");
            if (entry != null) plan.Resources.Add(Handle(entry, kind) with { SortOrdinal = NextOrdinal(plan, kind) });
            SqlTable? scalar = currentTable?.Table;
            if (scalar == null && entry != null) scalar = await SourceTableScalarAsync(source, entry, ct).ConfigureAwait(false);
            if (scalar != null)
            {
                scalar.HasFullData = descriptor != null;
                scalar.FullDataRowCount = descriptor == null ? 0 : Math.Max(descriptor.Summary.ReportedRowCount, descriptor.Summary.ActualRowCount);
                scalar.FullDataImportedAtUtc = descriptor?.Summary.ImportedAtUtc;
                var columns = currentTable?.Columns.Select(c => c.Column).ToArray() ?? [];
                var keys = currentTable?.PrimaryKeys.Select(k => k.Column).ToArray() ?? [];
                budget.Add(schema, name);
                var staged = await SnapshotStaging.TableAsync(_runtime, plan, scalar, columns, keys,
                    table?.SortOrdinal ?? NextOrdinal(plan, DatabaseVersionedResourceKind.TableMetadata), ct, table?.ResourceKey).ConfigureAwait(false);
                if (table != null) plan.Resources.Remove(table);
                plan.Resources.Add(staged.Resource);
                if (currentTable != null)
                {
                    for (int i = 0; i < currentTable.Columns.Count; i++) ReplaceId(plan.Columns, currentTable.Columns[i].ColumnRevisionKey, staged.Columns[i]);
                    for (int i = 0; i < currentTable.PrimaryKeys.Count; i++) ReplaceId(plan.PrimaryKeys, currentTable.PrimaryKeys[i].KeyColumnKey, staged.Keys[i]);
                }
            }
            if (entry == null) RemoveSelection(plan, schema, name);
            else if (!plan.FullDataSelections.Contains(SqlName.FormatPlainMultipartName(schema, name), StringComparer.OrdinalIgnoreCase))
                plan.FullDataSelections.Add(SqlName.FormatPlainMultipartName(schema, name));
        }
        else
        {
            plan.Resources.RemoveAll(r => r.Kind == kind && Same(r));
            if (entry != null) plan.Resources.Add(Handle(entry, kind) with { SortOrdinal = NextObjectOrdinal(plan) });
        }
        var result = await _runtime.Snapshots.PublishRuntimeStageAsync(plan, null, null, Guid.NewGuid(),
            versionName ?? "Restored resource", ct).ConfigureAwait(false);
        return new(result.Snapshot, result.VersionKey);
    }

    private async Task<(RuntimeSnapshotPlan Plan, SnapshotOperationBudget Budget)> BeginRestoreAsync(SnapshotRuntimeToken expected, CancellationToken ct)
    {
        if (expected.Epoch != _runtime.Session.Epoch) throw new ArgumentException("Restore token belongs to another epoch.");
        var snapshot = await _runtime.Snapshots.GetSnapshotAsync(expected.SnapshotKey, ct).ConfigureAwait(false)
            ?? throw new SnapshotConcurrencyException();
        var plan = await _runtime.Snapshots.BeginRuntimeStageAsync(expected, snapshot.DisplayName, snapshot.DatabaseName, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        var budget = new SnapshotOperationBudget(_limits);
        await SnapshotStaging.ReadBaselineAsync(_runtime, plan, budget, ct).ConfigureAwait(false);
        return (plan, budget);
    }
    private async Task<(RuntimeSnapshotResource Resource, TableMetadataDetails Details, List<long> Columns, List<long> Keys)>
        StageHistoricalTableAsync(RuntimeSnapshotPlan plan, HistoricalSnapshotContext source, HistoricalSnapshotEntry entry,
            long ordinal, SnapshotOperationBudget budget, CancellationToken ct)
    {
        var details = await source.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false);
        budget.Add(entry.SchemaName, entry.Name);
        if (details.Columns.Count > _limits.MaximumTableColumns || details.PrimaryKeys.Count > _limits.MaximumTableColumns)
            throw new InvalidOperationException("Selected historical table exceeds its scalar child budget.");
        foreach (var column in details.Columns) budget.Add(column.Column.ColumnName, column.Column.DataType);
        foreach (var key in details.PrimaryKeys) budget.Add(key.Column.ConstraintName, key.Column.ColumnName);
        var table = await SnapshotStaging.TableAsync(_runtime, plan, details.Table,
            details.Columns.Select(c => c.Column).ToArray(), details.PrimaryKeys.Select(k => k.Column).ToArray(), ordinal, ct,
            entry.TableSource == TableRevisionSource.Metadata && source.Header.SnapshotKey == plan.SnapshotKey ? entry.ResourceKey : null).ConfigureAwait(false);
        return (table.Resource, details, table.Columns, table.Keys);
    }
    private void RequireContext(HistoricalSnapshotContext context)
    { if (context.Epoch != _runtime.Session.Epoch) throw new ArgumentException("History selection belongs to another epoch."); }
    private static async Task<HistoricalSnapshotEntry> SelectedEntryAsync(HistoricalSnapshotContext context,
        HistoricalSnapshotEntry supplied, CancellationToken ct)
    {
        await foreach (var entry in context.StreamAsync(supplied.Collection, ct).ConfigureAwait(false))
            if (entry.EntryKey == supplied.EntryKey && entry.ResourceKey == supplied.ResourceKey && entry.RevisionKey == supplied.RevisionKey &&
                entry.TableSource == supplied.TableSource && StringComparer.Ordinal.Equals(entry.SchemaName, supplied.SchemaName) &&
                StringComparer.Ordinal.Equals(entry.Name, supplied.Name)) return entry;
        throw new ArgumentException("Resource is not in the selected historical projection.");
    }
    private static RuntimeSnapshotResource Handle(HistoricalSnapshotEntry entry, DatabaseVersionedResourceKind kind) =>
        new(entry.ResourceKey!.Value, entry.RevisionKey!.Value, kind, entry.SchemaName, entry.Name, entry.SortOrdinal);
    private static DatabaseVersionedResourceKind EntryKind(HistoricalSnapshotEntry entry) => entry.Collection switch
    {
        HistoricalCollection.Tables => DatabaseVersionedResourceKind.TableMetadata,
        HistoricalCollection.TableDataSets => DatabaseVersionedResourceKind.TableData,
        _ => SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown) ?? throw new InvalidOperationException("Unknown historical object kind.")
    };
    private static void RemoveSelection(RuntimeSnapshotPlan plan, string schema, string name) =>
        plan.FullDataSelections.RemoveAll(n => StringComparer.OrdinalIgnoreCase.Equals(n, SqlName.FormatPlainMultipartName(schema, name)));
    private static long NextOrdinal(RuntimeSnapshotPlan plan, DatabaseVersionedResourceKind kind) =>
        checked(plan.Resources.Where(r => r.Kind == kind).Select(r => r.SortOrdinal).DefaultIfEmpty(-1).Max() + 1);
    private static long NextObjectOrdinal(RuntimeSnapshotPlan plan) => checked(plan.Resources.Where(r =>
        r.Kind is not DatabaseVersionedResourceKind.TableMetadata and not DatabaseVersionedResourceKind.TableData)
        .Select(r => r.SortOrdinal).DefaultIfEmpty(-1).Max() + 1);
    private static void ReplaceId(List<long> ids, long old, long replacement)
    { for (int i = 0; i < ids.Count; i++) if (ids[i] == old) ids[i] = replacement; }
}

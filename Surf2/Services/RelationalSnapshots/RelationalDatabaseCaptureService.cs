using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Services.RelationalSnapshots;

public sealed class RelationalDatabaseCaptureService
{
    private readonly RelationalRuntime _runtime;
    private readonly RelationalSnapshotOperationLimits _limits;
    public RelationalDatabaseCaptureService(RelationalRuntime runtime, RelationalSnapshotOperationLimits? limits = null)
    { _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); _limits = limits ?? new(); _limits.Validate(); }

    public async Task<RelationalSourceTablePage> ListSourceTablesAsync(string sourceConnectionString, int pageSize = 50,
        int? afterObjectId = null, CancellationToken ct = default)
    {
        RequireDifferentSource(sourceConnectionString);
        var destination = await DestinationIdentityAsync(ct).ConfigureAwait(false);
        await using var source = await SqlSnapshotSource.OpenAsync(sourceConnectionString, _limits, destination, ct, metadataOnly: true).ConfigureAwait(false);
        var page = await source.TablesAsync(pageSize, afterObjectId ?? 0, ct).ConfigureAwait(false);
        await source.CompleteAsync(ct).ConfigureAwait(false); return page;
    }

    public async Task<RelationalCaptureResult> CaptureAsync(RelationalDatabaseCaptureRequest request,
        IProgress<RelationalCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDifferentSource(request.SourceConnectionString);
        if (request.ExpectedScope.Epoch != _runtime.Session.Epoch || string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ArgumentException("Capture requires an active scope token and nonempty display name.");
        if (request.FullDataTables.Count > _limits.MaximumMetadataUnits) throw new InvalidOperationException("Table selection exceeds its metadata budget.");
        var selected = new Dictionary<(string, string), RelationalSourceTable>(RuntimeTableComparer.Instance);
        foreach (var table in request.FullDataTables)
        {
            if (table.ObjectId <= 0 || table.SchemaName.Length is < 1 or > 128 || table.TableName.Length is < 1 or > 128 ||
                !selected.TryAdd((table.SchemaName, table.TableName), table)) throw new ArgumentException("Invalid or duplicate selected source table.");
        }
        var scope = await _runtime.StateStore.ReadScopeAsync(request.ExpectedScope.Key, ct).ConfigureAwait(false) ?? throw new StateConflictException("capture scope");
        if (!SameToken(scope.Token, request.ExpectedScope) || !StringComparer.Ordinal.Equals(scope.Value.ScopeId, request.SelectedScope.ScopeId))
            throw new StateConflictException("capture scope");
        var destination = await DestinationIdentityAsync(ct).ConfigureAwait(false);
        await using var source = await SqlSnapshotSource.OpenAsync(request.SourceConnectionString, _limits, destination, ct,
            request.AllowSerializableFallback).ConfigureAwait(false);
        var sourceIsolation = source.Isolation;
        progress?.Report(new("Source consistency: " + sourceIsolation, 0, 0));
        string database = await source.DatabaseNameAsync(ct).ConfigureAwait(false);
        var budget = new SnapshotOperationBudget(_limits); long capturedRows = 0;
        var plan = await _runtime.Snapshots.BeginRuntimeStageAsync(request.ReplaceSnapshot, request.DisplayName, database, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await SnapshotStaging.ReadBaselineAsync(_runtime, plan, budget, ct).ConfigureAwait(false);
        int afterObject = 0; long objectOrdinal = 0;
        while (await source.NextObjectAsync(afterObject, ct).ConfigureAwait(false) is { } item)
        {
            afterObject = item.Id; var value = item.Value; budget.Add(value.SchemaName, value.ObjectName);
            var resource = await _runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
            {
                var kind = SnapshotIdentity.ResourceKind(value.Kind)!.Value;
                long key = await _runtime.Snapshots.RuntimeResourceAsync(writer, plan, kind, value.SchemaName, value.ObjectName, objectOrdinal, token).ConfigureAwait(false);
                long revision = await writer.InsertObjectRevisionAsync(key, value, token).ConfigureAwait(false);
                await writer.SealRevisionAsync(revision, token).ConfigureAwait(false);
                return new RuntimeSnapshotResource(key, revision, kind, value.SchemaName, value.ObjectName, objectOrdinal);
            }, ct).ConfigureAwait(false);
            plan.Resources.Add(resource); objectOrdinal++;
            progress?.Report(new("Definitions", budget.Units, capturedRows));
        }
        int afterTable = 0; long tableOrdinal = 0, dataOrdinal = 0;
        var foundSelections = new HashSet<(string, string)>(RuntimeTableComparer.Instance);
        var capture = new RelationalCaptureStore(_runtime.Session, _limits.Capture);
        while (true)
        {
            var page = await source.TablesAsync(1, afterTable, ct).ConfigureAwait(false);
            if (page.Items.Count == 0) break;
            var table = page.Items[0]; afterTable = table.ObjectId;
            var metadata = await source.MetadataAsync(table, ct).ConfigureAwait(false);
            budget.Add(table.SchemaName, table.TableName);
            foreach (var column in metadata.Columns) budget.Add(column.ColumnName, column.DataType);
            foreach (var key in metadata.Keys) budget.Add(key.ConstraintName, key.ColumnName);
            bool full = selected.TryGetValue((table.SchemaName, table.TableName), out var expectedTable);
            if (full && expectedTable!.ObjectId != table.ObjectId) throw new InvalidOperationException("A selected source table was replaced after selection.");
            long count = 0;
            if (full)
            {
                foundSelections.Add((table.SchemaName, table.TableName));
                if (metadata.Columns.Count > CaptureLimits.MaximumColumns) throw new CaptureLimitException("Selected full-data table exceeds the 128-column capture layout limit.");
                count = await source.RowCountAsync(table, ct).ConfigureAwait(false);
                var staged = await _runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
                {
                    long key = await _runtime.Snapshots.RuntimeResourceAsync(writer, plan, DatabaseVersionedResourceKind.TableData,
                        table.SchemaName, table.TableName, dataOrdinal, token).ConfigureAwait(false);
                    long revision = await writer.InsertTableDataRevisionAsync(key, table.SchemaName, table.TableName, ct: token).ConfigureAwait(false);
                    return new RuntimeSnapshotResource(key, revision, DatabaseVersionedResourceKind.TableData, table.SchemaName, table.TableName, dataOrdinal);
                }, ct).ConfigureAwait(false);
                var layout = metadata.Columns.Select(c => new CaptureColumnDefinition(c.ColumnName, c.DataType, c.MaxLength,
                    c.NumericPrecision, c.NumericScale, c.IsNullable, c.Ordinal, c.IsIdentity)).ToArray();
                var write = await capture.CreateDataSetAsync(new(staged.RevisionKey, layout, count, plan.Header.ImportedAtUtc, count), ct).ConfigureAwait(false);
                long appended = await capture.AppendRowsAsync(write, source.RowsAsync(table, ct), 0, ct).ConfigureAwait(false);
                if (appended != count) throw new InvalidOperationException("Source row count changed during capture.");
                await capture.CompleteDataSetAsync(write, count, ct).ConfigureAwait(false);
                await _runtime.Snapshots.StageRuntimeUnitAsync(async (writer, token) =>
                { await writer.SealRevisionAsync(staged.RevisionKey, token).ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);
                budget.Add(64); plan.Resources.Add(staged); dataOrdinal++;
                plan.FullDataSelections.Add(table.DisplayName); capturedRows = checked(capturedRows + count);
                progress?.Report(new("Captured " + table.DisplayName, budget.Units, capturedRows));
            }
            var header = new SqlTable { SchemaName = table.SchemaName, TableName = table.TableName, HasFullData = full,
                FullDataRowCount = count, FullDataImportedAtUtc = full ? plan.Header.ImportedAtUtc : null };
            var stagedTable = await SnapshotStaging.TableAsync(_runtime, plan, header, metadata.Columns, metadata.Keys, tableOrdinal++, ct).ConfigureAwait(false);
            plan.Resources.Add(stagedTable.Resource); plan.Columns.AddRange(stagedTable.Columns); plan.PrimaryKeys.AddRange(stagedTable.Keys);
            progress?.Report(new("Table metadata", budget.Units, capturedRows));
        }
        if (foundSelections.Count != selected.Count) throw new InvalidOperationException("A selected source table disappeared or is not visible.");
        await source.CompleteAsync(ct).ConfigureAwait(false);
        if (plan.IsNew) scope.Value.Resources.Add(new() { ResourceId = Guid.NewGuid().ToString("N"), Kind = ResourceKind.DatabaseSnapshot,
            Path = plan.Header.SnapshotId, AddedAtUtc = plan.Header.ImportedAtUtc, IncludeChildren = true });
        progress?.Report(new("Publishing", budget.Units, capturedRows));
        var result = await _runtime.Snapshots.PublishRuntimeStageAsync(plan, scope.Value, request.ExpectedScope, Guid.NewGuid(), request.VersionName, ct).ConfigureAwait(false);
        return new(result.Snapshot, result.ScopeToken!, scope.Value, capturedRows, result.VersionKey) { SourceIsolation = sourceIsolation };
    }

    private void RequireDifferentSource(string connectionString)
    {
        var source = new SqlConnectionStringBuilder(connectionString);
        var destination = new SqlConnectionStringBuilder(_runtime.Options.ConnectionString);
        if (StringComparer.OrdinalIgnoreCase.Equals(source.DataSource, destination.DataSource) &&
            StringComparer.OrdinalIgnoreCase.Equals(source.InitialCatalog, destination.InitialCatalog))
            throw new ArgumentException("The source must be a different database from the relational destination.");
    }
    private async Task<SqlCaptureDatabaseIdentity> DestinationIdentityAsync(CancellationToken ct)
    {
        await _runtime.Session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
        return await SqlCaptureDatabaseIdentity.ReadAsync(connection, _limits.SourceCommandTimeoutSeconds, ct).ConfigureAwait(false);
    }
    private static bool SameToken(StateToken a, StateToken b) => a.Key == b.Key && a.Epoch == b.Epoch &&
        a.PublicationId == b.PublicationId && a.Version.AsSpan().SequenceEqual(b.Version);
}

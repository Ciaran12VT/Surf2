using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalGrid;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private sealed record SnapshotWorkflow(RelationalRuntime Runtime, StateEditSession<Scope>? Scope,
        long Generation, QueryLifetime Owner, QueryRequest Request, Window? Source)
    { public CancellationToken Token => Request.CancellationToken; }
    private readonly Dictionary<Task, SnapshotWorkflow> _relationalSnapshotOperations = [];
    private readonly Dictionary<Window, Func<Task>> _relationalSnapshotWindows = [];
    private readonly HashSet<Task> _relationalSnapshotRetirements = [];
    private long _relationalSnapshotGeneration;
    private bool _relationalSnapshotsStopping;
    private int _relationalSnapshotCleanupFailures;

    public Func<ExplorerNodeSummary, CancellationToken, Task>? RelationalDatabaseCaptureHandler { get; private set; }

    private void InitializeRelationalSnapshotWorkflows()
    {
        Dispatcher.VerifyAccess();
        if (_relationalSnapshotOperations.Count != 0 || _relationalSnapshotWindows.Count != 0 || _relationalSnapshotRetirements.Count != 0)
            throw new InvalidOperationException("Drain the previous snapshot workflow context before initializing a runtime.");
        if (_relationalSnapshotCleanupFailures != 0) throw new IOException("The previous snapshot workflow context did not retire cleanly.");
        _relationalSnapshotsStopping = false; ++_relationalSnapshotGeneration;
        RelationalDatabaseHistoryHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => OpenRelationalHistoryAsync(work, node), ct);
        RelationalDatabaseExportHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => ExportRelationalSnapshotAsync(work, node), ct);
        RelationalDatabaseCaptureHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => CaptureRelationalSnapshotAsync(work, node), ct);
    }

    private Task RunRelationalSnapshotWorkflowAsync(Func<SnapshotWorkflow, Task> action,
        CancellationToken parentToken = default, Window? source = null)
    {
        Dispatcher.VerifyAccess();
        if (_relationalSnapshotsStopping) return Task.CompletedTask;
        var runtime = _relational ?? throw new InvalidOperationException("Relational storage is not selected.");
        if (_relationalSnapshotOperations.Count >= 64) throw new InvalidOperationException("Too many snapshot operations are still draining.");
        var owner = new QueryLifetime(runtime.Session.Epoch, _relationalScopeEdit?.SubjectKey);
        var work = new SnapshotWorkflow(runtime, _relationalScopeEdit, _relationalSnapshotGeneration, owner, owner.BeginRequest(), source);
        Task operation = CoreAsync(); _relationalSnapshotOperations.Add(operation, work);
        TrackRelationalSnapshotRetirement(RetireAsync()); return operation;
        async Task CoreAsync()
        {
            await Task.Yield();
            using var registration = parentToken.Register(owner.Dispose);
            try
            {
                RequireRelationalSnapshotContext(work);
                await work.Runtime.Session.RequireReadyAsync(work.Token);
                await action(work);
            }
            catch (OperationCanceledException) { }
            catch (SnapshotPublicationOutcomeUnknownException error)
            {
                if (IsRelationalSnapshotContext(work)) StatusText = "Snapshot publication outcome is unknown. Reload the selected scope before retrying: " + error.Message;
                if (IsRelationalSnapshotContext(work)) await LoadRelationalLastActiveScopeAsync();
            }
            catch (Exception error) { if (IsRelationalSnapshotContext(work)) StatusText = "Snapshot operation failed: " + error.Message; }
            finally { await work.Request.DisposeAsync(); await owner.DisposeAsync(); }
        }
        async Task RetireAsync()
        { try { await operation; } finally { _relationalSnapshotOperations.Remove(operation); } }
    }
    private bool IsRelationalSnapshotContext(SnapshotWorkflow work) => !_relationalSnapshotsStopping &&
        work.Generation == _relationalSnapshotGeneration && ReferenceEquals(work.Runtime, _relational) &&
        ReferenceEquals(work.Scope, _relationalScopeEdit) && work.Owner.IsCurrent(work.Request.Stamp) &&
        (work.Source == null || _relationalSnapshotWindows.ContainsKey(work.Source));
    private void RequireRelationalSnapshotContext(SnapshotWorkflow work)
    { work.Token.ThrowIfCancellationRequested(); if (!IsRelationalSnapshotContext(work)) throw new OperationCanceledException("The selected snapshot context changed."); }

    private async Task<SnapshotSummary> RequireRelationalSnapshotNodeAsync(SnapshotWorkflow work, ExplorerNodeSummary node)
    {
        if (node.SnapshotKey is not { } key || !node.IsScopeResourceRoot || node.ResourceKind != ResourceKind.DatabaseSnapshot || work.Scope == null)
            throw new InvalidOperationException("Select a database snapshot root in the active scope.");
        var snapshot = await work.Runtime.Snapshots.GetSnapshotAsync(key, work.Token)
            ?? throw new KeyNotFoundException("The selected database snapshot no longer exists.");
        RequireRelationalSnapshotContext(work);
        if (!work.Scope.Snapshot().Resources.Any(resource => resource.Kind == ResourceKind.DatabaseSnapshot &&
            resource.ResourceId == node.ScopeResourceId && StringComparer.OrdinalIgnoreCase.Equals(resource.Path, snapshot.SnapshotId)))
            throw new OperationCanceledException("The selected snapshot is no longer a member of the active scope.");
        return snapshot;
    }

    private async Task OpenRelationalHistoryAsync(SnapshotWorkflow work, ExplorerNodeSummary node)
    {
        var snapshot = await RequireRelationalSnapshotNodeAsync(work, node);
        var window = new RelationalSnapshotHistoryWindow(work.Runtime, snapshot) { Owner = this };
        window.OpenRequested += (_, e) => QueueRelationalSnapshotWorkflow(w => OpenRelationalHistoricalResourceAsync(w, e.Target), window);
        window.CompareRequested += (_, e) => QueueRelationalSnapshotWorkflow(async w =>
        { RequireRelationalSnapshotContext(w); await OpenRelationalComparisonAsync(e.Left, e.Right); }, window);
        window.CopyRequested += (_, e) => QueueRelationalSnapshotWorkflow(w => CopyRelationalHistoricalResourceAsync(w, e.Target), window);
        window.SnapshotChanged += _ => QueueRelationalSnapshotWorkflow(RefreshRelationalSnapshotRootsAsync);
        OwnRelationalSnapshotWindow(work, window, window.CloseAndDrainAsync);
        ShowRelationalSnapshotWindow(window); StatusText = window.Title + ".";
    }

    private async Task ExportRelationalSnapshotAsync(SnapshotWorkflow work, ExplorerNodeSummary node)
    {
        var snapshot = await RequireRelationalSnapshotNodeAsync(work, node);
        var dialog = new SaveFileDialog { AddExtension = true, DefaultExt = ".zip", Filter = "Zip archive (*.zip)|*.zip",
            FileName = SafeRelationalSnapshotExportName(snapshot.DisplayName), OverwritePrompt = true, Title = "Export DB Data" };
        if (dialog.ShowDialog(this) != true) return;
        RequireRelationalSnapshotContext(work); StatusText = "Exporting " + snapshot.DisplayName + "...";
        var service = new RelationalSnapshotHistoryService(work.Runtime);
        await Task.Run(() => service.ExportCurrentAsync(new(work.Runtime.Session.Epoch, snapshot.SnapshotKey, snapshot.RowVersion),
            dialog.FileName, ct: work.Token), work.Token);
        RequireRelationalSnapshotContext(work); StatusText = "Exported " + snapshot.DisplayName + " to " + dialog.FileName + ".";
    }
    private static string SafeRelationalSnapshotExportName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim('.', ' '); return (name.Length == 0 ? "Database Export" : name) + ".zip";
    }

    private async Task CaptureRelationalSnapshotAsync(SnapshotWorkflow work, ExplorerNodeSummary node)
    {
        RequireRelationalWrite();
        var selected = await RequireRelationalSnapshotNodeAsync(work, node);
        await SaveRelationalScopeAsync(work.Token); RequireRelationalSnapshotContext(work);
        var window = new RelationalDatabaseCaptureWindow(work.Runtime, work.Scope!.Snapshot(), work.Scope.ExpectedToken, selected) { Owner = this };
        OwnRelationalSnapshotWindow(work, window, window.CloseAndDrainAsync);
        try { window.ShowDialog(); } finally { await window.CloseAndDrainAsync(); }
        RequireRelationalSnapshotContext(work);
        if (window.RequiresReload) { await LoadRelationalLastActiveScopeAsync(); return; }
        if (window.Result is not { } result) return;
        await AdoptRelationalSnapshotScopeAsync(work, result);
        StatusText = "Captured " + result.CapturedRows.ToString("N0", CultureInfo.CurrentCulture) + " rows into " + result.Snapshot.DisplayName + ".";
    }

    private async Task OpenRelationalHistoricalResourceAsync(SnapshotWorkflow work, RelationalComparisonTarget target)
    {
        if (target.Resource.IsCollection) throw new InvalidOperationException("Select one historical resource to open.");
        await using var version = await OpenRelationalHistoricalTargetAsync(work, target);
        var entry = await FindRelationalHistoricalEntryAsync(version, target, work.Token);
        var service = new RelationalSnapshotHistoryService(work.Runtime);
        var preview = await Task.Run(() => service.ReadPreviewAsync(version, entry, work.Token), work.Token);
        RequireRelationalSnapshotContext(work);
        string title = target.Resource.DisplayName + " (historical version " + target.VersionKey + ")";
        if (preview.CapturedData is { } captured)
        {
            IReadOnlyList<GridColumn>? columns = null;
            int units = 0; long bytes = 0;
            await foreach (var tableEntry in version.StreamAsync(HistoricalCollection.Tables, work.Token))
            {
                RequireRelationalHistoricalMetadataBudget(ref units, ref bytes, tableEntry);
                if (!StringComparer.OrdinalIgnoreCase.Equals(tableEntry.SchemaName, entry.SchemaName) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(tableEntry.Name, entry.Name)) continue;
                var table = await version.ReadTableMetadataAsync(tableEntry, work.Token);
                if (table.Columns.Count != 0) columns = table.Columns.OrderBy(c => c.Column.Ordinal)
                    .Select((c, i) => new GridColumn(i, c.Column.ColumnName, c.Column.ColumnName)).ToArray();
                break;
            }
            IDataGridSource? source = await CapturedGridSource.OpenAsync(work.Runtime.CapturedData, captured.Summary.DataSetKey,
                cancellationToken: work.Token, displayColumns: columns);
            try
            {
                RequireRelationalSnapshotContext(work);
                var grid = new HistoricalGridWindow(title, source) { Owner = this }; source = null;
                OwnRelationalSnapshotWindow(work, grid, grid.CloseAndDrainAsync); ShowRelationalSnapshotWindow(grid);
            }
            finally { if (source != null) await source.DisposeAsync(); }
        }
        else
        {
            var window = new Window { Owner = this, Title = title, Width = 1000, Height = 650, MinWidth = 640, MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = preview.Text ?? "", FontFamily = new("Consolas"), FontSize = 13, IsReadOnly = true,
                    AcceptsReturn = true, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } };
            OwnRelationalSnapshotWindow(work, window, () => { window.Close(); return Task.CompletedTask; }); ShowRelationalSnapshotWindow(window);
        }
        StatusText = title + ".";
    }
    private async Task<HistoricalSnapshotContext> OpenRelationalHistoricalTargetAsync(SnapshotWorkflow work, RelationalComparisonTarget target)
    {
        if (target.Epoch != work.Runtime.Session.Epoch || target.SnapshotKey is not { } snapshot || target.VersionKey is not { } version)
            throw new InvalidOperationException("A typed historical snapshot/version locator is required.");
        return await work.Runtime.Snapshots.OpenHistoricalSnapshotAsync(snapshot, version, work.Token);
    }
    private static async Task<HistoricalSnapshotEntry> FindRelationalHistoricalEntryAsync(HistoricalSnapshotContext context,
        RelationalComparisonTarget target, CancellationToken ct)
    {
        var collection = target.Resource.IsTableData ? HistoricalCollection.TableDataSets :
            target.Resource.Kind == ComparisonResourceKind.TableMetadata ? HistoricalCollection.Tables : HistoricalCollection.Objects;
        int units = 0; long bytes = 0;
        await foreach (var entry in context.StreamAsync(collection, ct))
        {
            RequireRelationalHistoricalMetadataBudget(ref units, ref bytes, entry);
            if (entry.ResourceKey == target.ResourceKey && entry.RevisionKey == target.RevisionKey &&
                entry.EntryKey == target.HistoricalEntryKey) return entry;
        }
        throw new SnapshotConcurrencyException();
    }
    private static void RequireRelationalHistoricalMetadataBudget(ref int units, ref long bytes, HistoricalSnapshotEntry entry)
    {
        bytes = checked(bytes + 256 + 2L * (entry.SchemaName.Length + entry.Name.Length));
        if (++units > 100_000 || bytes > 32L * 1024 * 1024) throw new InvalidDataException("Historical selection exceeds its metadata budget.");
    }

    private async Task CopyRelationalHistoricalResourceAsync(SnapshotWorkflow work, RelationalComparisonTarget target)
    {
        RequireRelationalWrite();
        if (work.Scope == null) throw new InvalidOperationException("Select a loaded scope before copying a database version.");
        await using var source = await OpenRelationalHistoricalTargetAsync(work, target);
        var service = new RelationalSnapshotHistoryService(work.Runtime);
        if (target.Resource.IsCollection)
        {
            var prompt = new RenameResourceWindow(target.Resource.DisplayName) { Owner = this, Title = "Copy Database Version" };
            OwnRelationalSnapshotWindow(work, prompt, () => { prompt.Close(); return Task.CompletedTask; });
            if (prompt.ShowDialog() != true) return;
            await SaveRelationalScopeAsync(work.Token); RequireRelationalSnapshotContext(work);
            var result = await Task.Run(() => service.RestoreVersionAsCopyAsync(target.SnapshotKey!.Value, target.VersionKey!.Value,
                work.Scope.Snapshot(), work.Scope.ExpectedToken, prompt.ResourceName, ct: work.Token), work.Token);
            RequireRelationalSnapshotContext(work); await AdoptRelationalSnapshotScopeAsync(work, result);
            StatusText = "Copied historical version to " + result.Snapshot.DisplayName + "."; return;
        }
        var entry = await FindRelationalHistoricalEntryAsync(source, target, work.Token);
        string? definition = null;
        if (entry.Collection == HistoricalCollection.Objects)
            definition = (await Task.Run(() => service.ReadPreviewAsync(source, entry, work.Token), work.Token)).Text;
        RequireRelationalSnapshotContext(work);
        var picker = new SnapshotCopyTargetWindow(work.Runtime, entry.SchemaName, entry.Name, definition) { Owner = this };
        OwnRelationalSnapshotWindow(work, picker, picker.CloseAndDrainAsync);
        bool? selected; try { selected = picker.ShowDialog(); } finally { await picker.CloseAndDrainAsync(); }
        if (selected != true || picker.SelectedSnapshot is not { } destination) return;
        RequireRelationalSnapshotContext(work);
        var resultResource = await Task.Run(() => service.RestoreResourceAsCopyAsync(
            new(work.Runtime.Session.Epoch, destination.SnapshotKey, destination.RowVersion), target.SnapshotKey!.Value,
            target.VersionKey!.Value, target.ResourceKey!.Value, picker.SchemaName, picker.ResourceName, picker.EditedText, work.Token), work.Token);
        RequireRelationalSnapshotContext(work); await RefreshRelationalSnapshotRootsAsync(work);
        StatusText = "Copied historical resource into " + resultResource.Snapshot.DisplayName + ".";
    }

    private async Task AdoptRelationalSnapshotScopeAsync(SnapshotWorkflow work, RelationalCaptureResult result)
    {
        await _relationalOwnerCommands.WaitAsync(work.Token);
        try
        {
            RequireRelationalSnapshotContext(work);
            var previous = work.Scope!;
            var next = NewRelationalScopeEdit(result.UpdatedScope, result.UpdatedScopeToken);
            _relationalScopeEdit = next; SetActiveScope(result.UpdatedScope);
            await previous.DisposeAsync();
        }
        finally { _relationalOwnerCommands.Release(); }
        // The publication replaced the scope token, so refresh uses a new, independently fenced owner.
        QueueRelationalSnapshotWorkflow(RefreshRelationalSnapshotRootsAsync);
    }
    private async Task RefreshRelationalSnapshotRootsAsync(SnapshotWorkflow work)
    {
        if (work.Scope == null) return;
        var explorer = await work.Runtime.Explorer.OpenScopeAsync(work.Scope.SubjectKey, GetUnloadedResourceIds(), CultureInfo.CurrentCulture.Name, work.Token);
        var roots = await work.Runtime.Explorer.GetRootsAsync(explorer, work.Token);
        var references = await work.Runtime.References.LoadPaintAsync(explorer, work.Token);
        RequireRelationalSnapshotContext(work);
        _relationalExplorerScope = explorer; _relationalReferenceCatalogue = references; _referenceIndex = ScopeReferenceIndex.Empty;
        RootNodes.Clear(); _relationalExplorerNodes.Clear(); _expandedObjectExplorerNodeKeys.Clear();
        foreach (var root in roots) RootNodes.Add(CreateRelationalExplorerNode(root));
        BeginRelationalExplorerContext(); InvalidateRelationalDocumentContext();
    }

    private void QueueRelationalSnapshotWorkflow(Func<SnapshotWorkflow, Task> action, Window? source = null)
    {
        try { _ = RunRelationalSnapshotWorkflowAsync(action, source: source); }
        catch (Exception error) { if (!_relationalSnapshotsStopping) StatusText = "Snapshot operation failed: " + error.Message; }
    }
    private void OwnRelationalSnapshotWindow(SnapshotWorkflow work, Window window, Func<Task> close)
    {
        try
        {
            RequireRelationalSnapshotContext(work);
            if (_relationalSnapshotWindows.Count >= 8) throw new InvalidOperationException("Close an existing snapshot window before opening another.");
        }
        catch { TrackRelationalSnapshotRetirement(close()); throw; }
        _relationalSnapshotWindows.Add(window, close);
        window.Closed += (_, _) =>
        {
            _relationalSnapshotWindows.Remove(window);
            foreach (var operation in _relationalSnapshotOperations.Where(pair => ReferenceEquals(pair.Value.Source, window)).ToArray())
                operation.Value.Owner.Dispose();
            TrackRelationalSnapshotRetirement(window switch
            {
                RelationalSnapshotHistoryWindow history => history.CloseCompletion,
                RelationalDatabaseCaptureWindow capture => capture.CloseCompletion,
                HistoricalGridWindow grid => grid.CloseCompletion,
                SnapshotCopyTargetWindow picker => picker.CloseCompletion,
                _ => Task.CompletedTask
            });
        };
    }
    private void ShowRelationalSnapshotWindow(Window window)
    {
        try { window.Show(); }
        catch
        {
            if (_relationalSnapshotWindows.TryGetValue(window, out var close)) TrackRelationalSnapshotRetirement(close());
            throw;
        }
    }
    private void TrackRelationalSnapshotRetirement(Task task)
    {
        if (!_relationalSnapshotRetirements.Add(task)) return;
        _ = RetireAsync();
        async Task RetireAsync()
        {
            try { await task; }
            catch (Exception error) { ++_relationalSnapshotCleanupFailures; StatusText = "Snapshot cleanup failed: " + error.Message; }
            finally { _relationalSnapshotRetirements.Remove(task); }
        }
    }
    private void InvalidateRelationalSnapshotWorkflows()
    {
        Dispatcher.VerifyAccess(); ++_relationalSnapshotGeneration;
        foreach (var operation in _relationalSnapshotOperations.Values.ToArray()) operation.Owner.Dispose();
        var windows = _relationalSnapshotWindows.ToArray(); _relationalSnapshotWindows.Clear();
        foreach (var window in windows) TrackRelationalSnapshotRetirement(window.Value());
    }
    private async Task StopRelationalSnapshotWorkflowsAsync()
    {
        Dispatcher.VerifyAccess(); _relationalSnapshotsStopping = true;
        RelationalDatabaseHistoryHandler = RelationalDatabaseExportHandler = RelationalDatabaseCaptureHandler = null;
        InvalidateRelationalSnapshotWorkflows();
        List<Exception> failures = [];
        while (_relationalSnapshotOperations.Count != 0 || _relationalSnapshotRetirements.Count != 0)
        {
            try { await Task.WhenAll(_relationalSnapshotOperations.Keys.Concat(_relationalSnapshotRetirements).ToArray()); }
            catch (Exception error) { failures.Add(error); }
            foreach (var operation in _relationalSnapshotOperations.Keys.Where(task => task.IsCompleted).ToArray())
                _relationalSnapshotOperations.Remove(operation);
            _relationalSnapshotRetirements.RemoveWhere(task => task.IsCompleted);
        }
        if (_relationalSnapshotCleanupFailures != 0 || failures.Count != 0)
            throw new IOException("One or more snapshot workflows did not retire cleanly.",
                failures.Count == 0 ? null : new AggregateException(failures));
    }

    private Task RunRelationalSnapshotNodeHandlerAsync(FileSystemNode node, Func<ExplorerNodeSummary, CancellationToken, Task>? handler)
    {
        if (handler == null || !_relationalExplorerNodes.TryGetValue(node, out var summary))
        { StatusText = "This snapshot workflow is unavailable for the selected Object Explorer item."; return Task.CompletedTask; }
        return handler(summary, CancellationToken.None);
    }
}

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
    public Func<ExplorerNodeSummary, CancellationToken, Task>? RelationalDatabaseEditHandler { get; private set; }

    private void InitializeRelationalSnapshotWorkflows()
    {
        Dispatcher.VerifyAccess();
        if (_relationalSnapshotOperations.Count != 0 || _relationalSnapshotWindows.Count != 0 || _relationalSnapshotRetirements.Count != 0)
            throw new InvalidOperationException("Drain the previous snapshot workflow context before initializing a runtime.");
        if (_relationalSnapshotCleanupFailures != 0) throw new IOException("The previous snapshot workflow context did not retire cleanly.");
        _relationalSnapshotsStopping = false; ++_relationalSnapshotGeneration;
        RelationalDatabaseHistoryHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => OpenRelationalHistoryAsync(work, node), ct, readFromMemory: true);
        RelationalDatabaseExportHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => ExportRelationalSnapshotAsync(work, node), ct);
        RelationalDatabaseCaptureHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => CaptureRelationalSnapshotAsync(work, node), ct);
        RelationalDatabaseEditHandler = (node, ct) => RunRelationalSnapshotWorkflowAsync(work => EditRelationalSnapshotAsync(work, node), ct);
    }

    private Task RunRelationalSnapshotWorkflowAsync(Func<SnapshotWorkflow, Task> action,
        CancellationToken parentToken = default, Window? source = null, bool readFromMemory = false)
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
                if (readFromMemory) await EnsureRelationalSnapshotMemoryReadyAsync(work.Token);
                else await work.Runtime.Session.RequireReadyAsync(work.Token);
                RequireRelationalSnapshotContext(work);
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
        var window = new RelationalSnapshotHistoryWindow(work.Runtime, snapshot) { Owner = this, VerifyWriteAccess = RequireRelationalWrite };
        TrackResourceWindow(window, snapshot.SnapshotKey);
        window.OpenRequested += (_, e) => QueueRelationalSnapshotWorkflow(w => OpenRelationalHistoricalResourceAsync(w, e.Target), window, readFromMemory: true);
        window.CompareWithRequested += (_, e) => QueueRelationalSnapshotWorkflow(w => CompareRelationalHistoryWithAsync(w, e.Target), window, readFromMemory: true);
        window.CopyRequested += (_, e) => QueueRelationalSnapshotWorkflow(w => CopyRelationalHistoricalResourceAsync(w, e.Target), window);
        window.RefreshSnapshotMemoryAsync = async ct =>
        {
            await RunRelationalSnapshotWorkflowAsync(RefreshRelationalSnapshotRootsAsync, ct, window);
            await EnsureRelationalSnapshotMemoryReadyAsync(ct);
        };
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
        await ShowRelationalSnapshotCaptureAsync(work, selected);
    }

    private async Task ShowRelationalSnapshotCaptureAsync(SnapshotWorkflow work, SnapshotSummary selected)
    {
        await SaveRelationalScopeAsync(work.Token); RequireRelationalSnapshotContext(work);
        var window = new RelationalDatabaseCaptureWindow(work.Runtime, work.Scope!.Snapshot(), work.Scope.ExpectedToken, selected) { Owner = this };
        OwnRelationalSnapshotWindow(work, window, window.CloseAndDrainAsync);
        try { window.ShowDialog(); } finally { await window.CloseAndDrainAsync(); }
        RequireRelationalSnapshotContext(work);
        if (window.RequiresReload) { await LoadRelationalLastActiveScopeAsync(); return; }
        if (window.Result is not { } result) return;
        await AdoptRelationalSnapshotScopeAsync(work, result);
        StatusText = "Captured " + result.CapturedRows.ToString("N0", CultureInfo.CurrentCulture) + " rows into " + result.Snapshot.DisplayName + "." + (result.SkippedTables.Count == 0 ? "" : " Skipped missing tables: " + string.Join(", ", result.SkippedTables));
    }

    private async Task EditRelationalSnapshotAsync(SnapshotWorkflow work, ExplorerNodeSummary node)
    {
        RequireRelationalWrite();
        var selected = await RequireRelationalSnapshotNodeAsync(work, node);
        var chooser = new DatabaseResourceWorkflowWindow(selected.DisplayName) { Owner = this };
        OwnRelationalSnapshotWindow(work, chooser, () => { chooser.Close(); return Task.CompletedTask; });
        if (chooser.ShowDialog() != true) return;
        RequireRelationalSnapshotContext(work);
        if (chooser.SelectedWorkflow == DatabaseResourceWorkflow.DirectConnection)
        {
            await ShowRelationalSnapshotCaptureAsync(work, selected);
            return;
        }
        await SaveRelationalScopeAsync(work.Token);
        RequireRelationalSnapshotContext(work);
        var service = new RelationalSnapshotEditorService(work.Runtime);
        DatabaseMetadataSnapshot model;
        if (chooser.SelectedWorkflow == DatabaseResourceWorkflow.ExportFolder)
        {
            StatusText = "Importing the Surf2 export folder…";
            var imported = await Task.Run(() => new DatabaseExportFolderImportService()
                .ImportAsync(chooser.ExportFolderPath!, selected.DisplayName, work.Token), work.Token);
            RequireRelationalSnapshotContext(work);
            model = imported.Snapshot;
            model.SnapshotId = selected.SnapshotId;
            if (string.IsNullOrWhiteSpace(model.DatabaseName)) model.DatabaseName = selected.DatabaseName;
            if (imported.Warnings.Count != 0 && MessageBox.Show(this,
                "Review the following export limitations before saving the imported database:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, imported.Warnings.Select(warning => "• " + warning)),
                "Surf2 export folder", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        }
        else
        {
            StatusText = "Loading " + selected.DisplayName + " for editing…";
            model = await Task.Run(() => service.ReadAsync(selected, work.Token), work.Token);
        }
        RequireRelationalSnapshotContext(work);
        var window = new DatabaseResourceWindow(model) { Owner = this };
        OwnRelationalSnapshotWindow(work, window, () => { window.Close(); return Task.CompletedTask; });
        if (window.ShowDialog() != true || window.Snapshot is not { } edited) return;
        RequireRelationalSnapshotContext(work);
        if (edited.Origin?.IsComplete != true)
        {
            var origin = new SnapshotOriginWindow(edited.Origin, edited.DatabaseName) { Owner = this };
            if (origin.ShowDialog() != true) return;
            edited.Origin = origin.Origin;
        }
        if ((await SnapshotImportTargetWindow.ChooseAsync(this, work.Runtime, edited.Origin, selected, work.Token)).Cancelled) return;
        RequireRelationalSnapshotContext(work);
        StatusText = "Saving " + edited.DisplayName + "…";
        var result = await Task.Run(() => service.SaveAsync(edited, work.Scope!.Snapshot(), work.Scope.ExpectedToken,
            new SnapshotRuntimeToken(work.Runtime.Session.Epoch, selected.SnapshotKey, selected.RowVersion), work.Token), work.Token);
        RequireRelationalSnapshotContext(work);
        if (await Services.SourceResources.SourceSnapshotLinker.LinkMatchingAsync(work.Runtime, result.UpdatedScope, ct: work.Token) != 0)
            result = result with { UpdatedScopeToken = await work.Runtime.StateStore.SaveScopeAsync(result.UpdatedScope, result.UpdatedScopeToken, Guid.NewGuid(), work.Token) };
        await AdoptRelationalSnapshotScopeAsync(work, result);
        StatusText = "Saved " + result.Snapshot.DisplayName + ".";
    }

    private async Task OpenRelationalHistoricalResourceAsync(SnapshotWorkflow work, RelationalComparisonTarget target)
    {
        if (target.Resource.IsCollection) throw new InvalidOperationException("Select one historical resource to open.");
        await using var version = await OpenRelationalHistoricalTargetAsync(work, target);
        var entry = await FindRelationalHistoricalEntryAsync(version, target, work.Token);
        var service = new RelationalSnapshotHistoryService(work.Runtime);
        string content = await Task.Run(() => service.ReadFileTextAsync(version, entry, work.Token), work.Token);
        RequireRelationalSnapshotContext(work);
        ShowRelationalHistoryFileDiff(work, target.Resource.DisplayName, target.Resource.DisplayName, content, content, viewOnly: true);
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
        if (target.HistoricalEntryKey is not { } key) throw new SnapshotConcurrencyException();
        var entry = await context.FindEntryAsync(key, ct);
        if (entry != null && entry.Collection == collection && entry.ResourceKey == target.ResourceKey && entry.RevisionKey == target.RevisionKey)
            return entry;
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
            var prompt = new RenameResourceWindow(target.Resource.DisplayName) { Owner = this, Title = "Restore Database Version To" };
            OwnRelationalSnapshotWindow(work, prompt, () => { prompt.Close(); return Task.CompletedTask; });
            if (prompt.ShowDialog() != true) return;
            await SaveRelationalScopeAsync(work.Token); RequireRelationalSnapshotContext(work);
            var result = await Task.Run(() => service.RestoreVersionAsCopyAsync(target.SnapshotKey!.Value, target.VersionKey!.Value,
                work.Scope.Snapshot(), work.Scope.ExpectedToken, prompt.ResourceName, ct: work.Token), work.Token);
            RequireRelationalSnapshotContext(work); await AdoptRelationalSnapshotScopeAsync(work, result);
            StatusText = "Restored historical version as '" + result.Snapshot.DisplayName + "'."; return;
        }
        var entry = await FindRelationalHistoricalEntryAsync(source, target, work.Token);
        string content = await Task.Run(() => service.ReadFileTextAsync(source, entry, work.Token), work.Token);
        var snapshots = await ReadRelationalHistoryScopeSnapshotsAsync(work, useAliases: false);
        var targets = snapshots.Select(snapshot => new DatabaseRestoreTargetOption(snapshot.DisplayName, snapshot.SnapshotId))
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        RequireRelationalSnapshotContext(work);
        if (targets.Length == 0) { StatusText = "No target databases are available."; return; }
        var picker = new DatabaseHistoryRestoreToWindow(targets,
            targets.FirstOrDefault(option => option.SnapshotId == source.Header.SnapshotId),
            CreateRestoredResourceName(SqlName.FormatPlainMultipartName(entry.SchemaName, entry.Name)),
            content, entry.Collection == HistoricalCollection.Objects) { Owner = this, Title = "Restore File To" };
        OwnRelationalSnapshotWindow(work, picker, () => { picker.Close(); return Task.CompletedTask; });
        if (picker.ShowDialog() != true || picker.SelectedTarget is not { } selected) return;
        var destination = snapshots.First(snapshot => snapshot.SnapshotId == selected.SnapshotId);
        (string schemaName, string objectName) = SplitDatabaseObjectName(picker.ResourceName);
        RequireRelationalSnapshotContext(work);
        var resultResource = await Task.Run(() => service.RestoreResourceAsCopyAsync(
            new(work.Runtime.Session.Epoch, destination.SnapshotKey, destination.RowVersion), target.SnapshotKey!.Value,
            target.VersionKey!.Value, target.ResourceKey!.Value, schemaName, objectName,
            entry.Collection == HistoricalCollection.Objects ? picker.EditedContent : null, work.Token), work.Token);
        RequireRelationalSnapshotContext(work); await RefreshRelationalSnapshotRootsAsync(work);
        StatusText = "Restored historical resource to " + resultResource.Snapshot.DisplayName + " as " + schemaName + "." + objectName + ".";
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
        RetireRelationalExplorerContext();
        await _relationalExplorerRetirement;
        RequireRelationalSnapshotContext(work);
        var names = await ReadRelationalNameCatalogueAsync(work.Runtime, work.Scope.SubjectKey,
            GetUnloadedResourceIds(), CultureInfo.CurrentCulture.Name, work.Token);
        RequireRelationalSnapshotContext(work);
        SetRelationalNameCatalogue(work.Runtime, names);
        _relationalReferenceCatalogue = null; _referenceIndex = ScopeReferenceIndex.Empty;
        RootNodes.Clear(); _relationalExplorerNodes.Clear(); _expandedObjectExplorerNodeKeys.Clear();
        foreach (var root in names.Roots) RootNodes.Add(CreateRelationalExplorerNode(root));
        BeginRelationalExplorerContext(); InvalidateRelationalDocumentContext();
    }

    private void QueueRelationalSnapshotWorkflow(Func<SnapshotWorkflow, Task> action, Window? source = null, bool readFromMemory = false)
    {
        try { _ = RunRelationalSnapshotWorkflowAsync(action, source: source, readFromMemory: readFromMemory); }
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
        if (work.Source != null)
            foreach (var owner in _sourceWindows.Where(pair => pair.Value.Contains(work.Source)).Select(pair => pair.Key).ToArray())
                if (Services.SourceResources.ResourceWorkspace.Find(owner) is { } member) TrackSourceWindow(member, window);
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
                RelationalHistoryFilePickerWindow picker => picker.CloseCompletion,
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
        RelationalDatabaseHistoryHandler = RelationalDatabaseExportHandler = RelationalDatabaseCaptureHandler = RelationalDatabaseEditHandler = null;
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

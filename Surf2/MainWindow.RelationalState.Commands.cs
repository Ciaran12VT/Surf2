using System.IO;
using System.Windows;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private DiagramDocument? _relationalSavedDiagram;
    private string? _relationalDefaultWorkbenchComparison;
    private long? _relationalDefaultWorkbenchScope;
    private Task _relationalWorkbenchRefreshTask = Task.CompletedTask;
    private bool _relationalStateCommandOutcomeUnknown;

    private async Task<bool> SaveRelationalDiagramFromUiAsync(string id, string name)
    {
        try
        {
            RequireRelationalWrite();
            using var operation = BeginRelationalStateOperation();
            if (_relationalDiagramEdit != null && string.Equals(id, _relationalDiagramEdit.Snapshot().Document.DiagramId, StringComparison.Ordinal))
            {
                await SaveRelationalDiagramAsync(operation.Token, name);
            }
            else
            {
                // An embedded workbench graph is not the current diagram owner, even if its original ID matches.
                string newId = _relationalDiagramEdit == null ? Guid.NewGuid().ToString("N") : id;
                await CreateRelationalDiagramAsync(newId, name, operation.Token);
            }
            if (!_relationalStateClosing) StatusText = "Saved diagram '" + name + "'.";
            return true;
        }
        catch (Exception error) { ReportRelationalStateFailure(error, "save diagram"); return false; }
    }

    private async Task OpenRelationalDiagramFromUiAsync(string path)
    {
        try { using var operation = BeginRelationalStateOperation(); await LoadRelationalDiagramAsync(path); if (_relationalStateClosing) return; EnsureDiagramViewVisible(); SetActiveWorkspaceView(WorkspaceViewKind.Diagram); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "open diagram"); }
    }

    private async Task DeleteRelationalDiagramFromUiAsync()
    {
        try
        {
            RequireRelationalWrite();
            using var operation = BeginRelationalStateOperation();
            var edit = _relationalDiagramEdit;
            long generation = _relationalSelectionGeneration;
            if (edit == null) { StatusText = "Open the current saved diagram before deleting it. A saved Workbench graph is independent."; return; }
            string name = CurrentDiagramName;
            if (MessageBox.Show(this, "Delete diagram '" + name + "' and its resolved scope references? Saved Workbench copies will remain.",
                "Delete Diagram", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _relationalOwnerCommands.WaitAsync(operation.Token);
            try
            {
                RequireRelationalSelection(generation, operation.Token);
                if (!ReferenceEquals(edit, _relationalDiagramEdit)) throw new OperationCanceledException("The selected diagram changed.");
                await SaveRelationalScopeAsync(operation.Token);
                RequireRelationalSelection(generation, operation.Token);
                try { await RelationalStateRuntime.StateStore.DeleteDiagramExpectedAsync(edit.ExpectedToken, Guid.NewGuid(), operation.Token); }
                catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
                if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(edit, _relationalDiagramEdit)) return;
                await edit.DisposeAsync(); _relationalDiagramEdit = null; _relationalSavedDiagram = null;
                ClearLoadedDiagram(); _relationalDiagramHeader = null; _relationalDiagramDraft = false; _relationalActiveDiagramRevisionKey = null;
                if (_relationalScopeEdit != null)
                {
                    var old = _relationalScopeEdit;
                    var loaded = RequireRelationalLoad(await RelationalStateRuntime.State.LoadScopeAsync(old.SubjectKey, operation.Token), "scope after diagram deletion");
                    if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(old, _relationalScopeEdit)) { await loaded.DisposeAsync(); return; }
                    _relationalScopeEdit = loaded; SetActiveScope(loaded.Snapshot()); await old.DisposeAsync();
                    await RefreshRelationalExplorerRootsAsync();
                }
                await RefreshRelationalWorkbenchCatalogueAsync(operation.Token);
                if (!_relationalStateClosing) StatusText = "Deleted diagram '" + name + "'. Saved Workbench copies were retained.";
            }
            finally { _relationalOwnerCommands.Release(); }
        }
        catch (Exception error) { ReportRelationalStateFailure(error, "delete diagram"); }
    }

    private void CreateRelationalBlankDiagramFromUi()
    {
        if (_relationalStateClosing || !_isPersistenceHydrated) return;
        _relationalSelectionGeneration++;
        if (_relationalSelectionLifetime != null) RetireRelationalSelection(_relationalSelectionLifetime);
        _relationalSelectionLifetime = null;
        // Keep the prior selected owner/token until a deliberate save-copy or another successful load.
        ClearLoadedDiagram(); _relationalDiagramHeader = null; _relationalSavedDiagram = null; _relationalDiagramDraft = true;
        _relationalActiveDiagramRevisionKey = null;
        _relationalControlImages = new(); _relationalControlOccurrences = new(); _relationalFilenameImages.Clear();
        ClearSelectedWorkbench(); EnsureDiagramViewVisible();
        StatusText = "Created a new blank diagram. Use Save to name and persist it.";
    }

    private bool HasRelationalUnsavedDiagramChanges()
    {
        if (_relationalLayoutRestoring || !_isPersistenceHydrated) return false;
        bool present = _relationalDiagramHeader != null || DiagramCanvas.Children.OfType<FrameworkElement>().Any(IsDiagramObject) || _currentDiagramWorkflows.Count != 0;
        if (!present) return false;
        var baseline = _relationalDiagramDraft ? null : _relationalSavedDiagram ?? _relationalWorkbenchEdit?.Snapshot().Workbench.ActiveDiagramSnapshot;
        return CreateDiagramComparisonKey(CaptureRelationalDiagram().Document) != CreateDiagramComparisonKey(baseline);
    }

    private bool HasRelationalUnsavedWorkbenchChanges()
    {
        if (_relationalLayoutRestoring || !_isPersistenceHydrated) return false;
        var captured = CaptureRelationalWorkbench(_relationalWorkbenchEdit?.Snapshot().Workbench, defaultForScope: true).Workbench;
        if (_relationalDefaultWorkbenchScope == _relationalScopeEdit?.SubjectKey && _relationalDefaultWorkbenchComparison != null)
            return CreateRelationalWorkbenchComparisonKey(captured) != _relationalDefaultWorkbenchComparison;
        // Unknown is not clean: exit's Save action performs a fresh selected default-owner lookup.
        return HasMeaningfulWorkbenchState(captured);
    }

    private async Task<bool> SaveRelationalDefaultWorkbenchFromUiAsync()
    {
        try { using var operation = BeginRelationalStateOperation(); await SaveRelationalWorkbenchAsync(_relationalStateLifetime.Token); if (!_relationalStateClosing) StatusText = "Saved default Workbench."; return true; }
        catch (Exception error) { ReportRelationalStateFailure(error, "save default Workbench"); return false; }
    }

    private async Task SaveRelationalWorkbenchCopyFromUiAsync()
    {
        try
        {
            RequireRelationalWrite();
            using var operation = BeginRelationalStateOperation();
            if (_relationalScopeEdit == null) { StatusText = "Open a scope before saving a Workbench."; return; }
            string? name = PromptForWorkbenchName((_activeScope?.Name ?? "Workbench") + " Copy");
            if (string.IsNullOrWhiteSpace(name)) return;
            await SaveRelationalWorkbenchCopyAsync(name, operation.Token); if (!_relationalStateClosing) StatusText = "Saved Workbench '" + name + "'.";
        }
        catch (Exception error) { ReportRelationalStateFailure(error, "save Workbench copy"); }
    }

    private async Task DeleteRelationalWorkbenchFromUiAsync()
    {
        try
        {
            RequireRelationalWrite();
            using var operation = BeginRelationalStateOperation();
            if (WorkbenchSelector.SelectedItem is not WorkbenchSummary summary) return;
            long generation = _relationalSelectionGeneration;
            if (MessageBox.Show(this, "Delete saved Workbench '" + summary.Name + "'?", "Delete Workbench", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _relationalOwnerCommands.WaitAsync(operation.Token);
            try
            {
                RequireRelationalSelection(generation, operation.Token);
                try { await RelationalStateRuntime.StateStore.DeleteWorkbenchExpectedAsync(summary.Token, Guid.NewGuid(), operation.Token); }
                catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
                if (_relationalStateClosing || generation != _relationalSelectionGeneration) return;
                if (_relationalWorkbenchEdit?.SubjectKey == summary.Token.Key)
                {
                    await _relationalWorkbenchEdit.DisposeAsync(); _relationalWorkbenchEdit = null;
                    // Its already open embedded diagram stays a draft with its bytes; never redirect to the current library.
                    if (_relationalDiagramEdit == null) _relationalDiagramDraft = true;
                }
                _relationalDefaultWorkbenchComparison = null;
                await RefreshRelationalWorkbenchCatalogueAsync(operation.Token); if (!_relationalStateClosing) StatusText = "Deleted Workbench '" + summary.Name + "'.";
            }
            finally { _relationalOwnerCommands.Release(); }
        }
        catch (Exception error) { ReportRelationalStateFailure(error, "delete Workbench"); }
    }

    private async Task LoadRelationalWorkbenchFromUiAsync(WorkbenchSummary summary)
    {
        try { using var operation = BeginRelationalStateOperation(); await LoadRelationalWorkbenchAsync(summary); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "load Workbench"); }
    }

    private void QueueRelationalWorkbenchRefresh()
    {
        if (!_relationalWorkbenchRefreshTask.IsCompleted) return;
        _relationalWorkbenchRefreshTask = RefreshRelationalWorkbenchFromUiAsync();
    }
    private async Task RefreshRelationalWorkbenchFromUiAsync()
    {
        try { await RefreshRelationalWorkbenchCatalogueAsync(_relationalStateLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportRelationalStateFailure(error, "refresh Workbench catalogue"); }
    }

    private async Task RefreshRelationalExplorerRootsAsync()
    {
        using var operation = BeginRelationalStateOperation();
        if (_relationalScopeEdit == null || _activeScope == null) return;
        var old = _relationalScopeEdit; long generation = _relationalSelectionGeneration;
        var prepared = await PrepareRelationalScopeAsync(new(old.ExpectedToken, 0, _activeScope.ScopeId, _activeScope.Name), operation.Token);
        if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(old, _relationalScopeEdit)) { await prepared.Edit.DisposeAsync(); return; }
        ApplyRelationalScope(prepared); await old.DisposeAsync();
    }

    private void ReportRelationalStateFailure(Exception error, string operation)
    {
        if (_relationalStateClosing) return;
        InternalLogService.Error(error, "Selected state command failed.");
        StatusText = _relationalStateCommandOutcomeUnknown
            ? "The publication outcome is unknown. Restart and reload before further writes. Current controls and image bytes were retained."
            : error is StateConflictException ? "The selected owner changed. Reload before saving; current edits were retained."
            : "Could not " + operation + ". Current state was retained; no replacement defaults were saved.";
    }

    private async Task ShowRelationalSettingsAsync()
    {
        try
        {
            RequireRelationalWrite();
            using var operation = BeginRelationalStateOperation();
            var preferenceEdit = _relationalPreferenceEdit ?? throw new InvalidOperationException("Preferences were not successfully loaded.");
            var scalarSettings = new AppSettings(); preferenceEdit.Snapshot().ApplyToRuntime(scalarSettings);
            var window = new SettingsWindow(scalarSettings) { Owner = this };
            window.InitializeRelationalPersistence(RelationalStateRuntime.Options);
            bool? accepted;
            try
            {
                await window.InitializeRelationalPreferencesAsync(RelationalStateRuntime, preferenceEdit.ExpectedToken, operation.Token);
                if (_relationalStateClosing) return;
                accepted = window.ShowDialog();
            }
            finally { await window.DrainRelationalPreferenceQueriesAsync(); }
            operation.Token.ThrowIfCancellationRequested();
            if (accepted != true) return;
            // Child edits have already been published atomically with the head. Never save this partial AppSettings as a root library.
            var loaded = RequireRelationalLoad(await RelationalStateRuntime.Preferences.LoadStartupPreferencesAsync(operation.Token), "saved preferences");
            if (_relationalStateClosing) { await loaded.DisposeAsync(); return; }
            _relationalPreferenceEdit = loaded; await preferenceEdit.DisposeAsync();
            loaded.Snapshot().ApplyToRuntime(_appSettings);
            await LoadRelationalAppearanceRulesAsync(loaded.ExpectedToken, _relationalStateLifetime.Token);
            AppThemeService.Apply(_appSettings.Appearance.Theme); ApplyThemeToRuntimeSurfaces(); ApplyInternalLoggingSetting("selected preferences saved");
            ApplySettingsToOpenWindows(); ApplySettingsToPreview(); BeginRelationalExplorerContext();
            RefreshDiagramImageToolMenu();
            StatusText = window.ConnectionSettingsWereChanged ? "Settings saved. Connection changes apply after restart." : "Settings saved.";
        }
        catch (Exception error) { ReportRelationalStateFailure(error, "edit settings"); }
    }

    private async Task BindPublishedRelationalDiagramRevisionAsync(StateToken token, DiagramState captured)
    {
        using var operation = BeginRelationalStateOperation();
        long generation = _relationalSelectionGeneration;
        var edit = _relationalDiagramEdit;
        var revision = await RelationalStateRuntime.StateStore.ReadCurrentDiagramRevisionKeyAsync(token, operation.Token);
        if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(edit, _relationalDiagramEdit)) return;
        _relationalActiveDiagramRevisionKey = revision; BindPublishedRelationalOccurrences(captured.Document);
    }
    private void BindPublishedRelationalOccurrences(DiagramDocument captured)
    {
        foreach (var control in DiagramCanvas.Children.OfType<FrameworkElement>())
            if (_relationalControlOccurrences.TryGetValue(control, out var occurrence)) occurrence.SourceOrdinal = null;
        for (int i = 0; i < captured.Objects.Count; i++)
            if (_relationalSnapshotOccurrences.TryGetValue(captured.Objects[i], out var occurrence) && occurrence.Control.TryGetTarget(out var control) && DiagramCanvas.Children.Contains(control))
                occurrence.SourceOrdinal = i;
    }

    private StateEditSession<WorkbenchAggregate> NewRelationalWorkbenchEdit(WorkbenchAggregate value, StateToken token) => new("Workbench", token.Key, new(value, token),
        v => new(StateCopies.Workbench(v.Workbench, new(RelationalRuntimeStateLimits)), v.PastedImages.ToDictionary(p => p.Key, p => p.Value.ToArray()),
            v.PastedImageFallbacks?.ToDictionary(p => p.Key, p => new PastedImageFallback(p.Value.Resolution, p.Value.Bytes.ToArray()))),
        RelationalStateRuntime.StateStore.SaveWorkbenchAsync, ct => RelationalStateRuntime.StateStore.ReadWorkbenchTokenAsync(token.Key, ct));
}

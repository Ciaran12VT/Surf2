using System.Windows;
using Surf2.Models;

namespace Surf2;

public partial class MainWindow
{
    private string? _relationalScopeSwitchWorkbenchComparison;
    private long? _relationalScopeSwitchScopeKey;

    internal Func<MessageBoxResult>? RelationalStateSwitchConfirmation { get; set; }

    private void RememberRelationalScopeSwitchWorkbenchBaseline(WorkbenchState? published = null)
    {
        Dispatcher.VerifyAccess();
        // A loaded workspace is a baseline even when it has no saved default Workbench.
        var baseline = published ?? CaptureRelationalWorkbench(_relationalWorkbenchEdit?.Snapshot().Workbench, defaultForScope: true).Workbench;
        _relationalScopeSwitchWorkbenchComparison = CreateRelationalWorkbenchComparisonKey(baseline);
        _relationalScopeSwitchScopeKey = _relationalScopeEdit?.SubjectKey;
    }

    private bool HasRelationalScopeSwitchWorkbenchChanges()
    {
        if (_relationalLayoutRestoring || !_isPersistenceHydrated) return false;
        if (_relationalScopeSwitchWorkbenchComparison == null || _relationalScopeSwitchScopeKey != _relationalScopeEdit?.SubjectKey)
            return HasRelationalUnsavedWorkbenchChanges();
        var captured = CaptureRelationalWorkbench(_relationalWorkbenchEdit?.Snapshot().Workbench, defaultForScope: true).Workbench;
        return CreateRelationalWorkbenchComparisonKey(captured) != _relationalScopeSwitchWorkbenchComparison;
    }

    private bool HasRelationalScopeSwitchScopeChanges()
    {
        if (_relationalLayoutRestoring || !_isPersistenceHydrated || _activeScope == null) return false;
        return _relationalScopeEdit == null || CreateRelationalScopeSwitchComparisonKey(_activeScope) !=
            CreateRelationalScopeSwitchComparisonKey(_relationalScopeEdit.Snapshot());
    }

    private static string CreateRelationalScopeSwitchComparisonKey(Scope scope) => System.Text.Json.JsonSerializer.Serialize(new
    {
        scope.ScopeId, scope.Name, scope.Description,
        Resources = scope.Resources.Select(r => new
        {
            r.ResourceId, r.Kind, r.Path, r.DisplayNameOverride, r.DetailsOverride, r.IncludeChildren
        }).ToArray(),
        VirtualFolders = scope.VirtualFolders.Select(f => new
        {
            f.VirtualFolderId, f.Name, f.ParentNodeKey, Children = f.ChildNodeKeys.ToArray()
        }).ToArray()
    });

    private async Task<bool> ConfirmRelationalGridEditsBeforeStateSwitchAsync(CancellationToken ct)
    {
        foreach (var grid in _openSpreadsheetWindows.Values.Distinct().ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!grid.UsesGridProvider || grid.IsGridDisposed) continue;
            // Commit the active cell before deciding whether the session has edits; Workbench saves do not save those cells.
            if (!grid.TryCaptureGridReopenView(out var view) || view == null) return false;
            if (view.OverlayBytes == 0) continue;
            bool confirmed = RelationalGridDiscardEditsConfirmation != null
                ? await RelationalGridDiscardEditsConfirmation(grid, ct)
                : MessageBox.Show(this, "Changing workspace will discard this table's unsaved session cell edits. Continue?",
                    "Unsaved Table Edits", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            ct.ThrowIfCancellationRequested();
            if (!confirmed || grid.IsGridDisposed || grid.PreparedGridSource.OverlayGeneration != view.OverlayGeneration) return false;
        }
        return true;
    }
}

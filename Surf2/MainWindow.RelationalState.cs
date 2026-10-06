using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private StateEditSession<StartupPreferences>? _relationalPreferenceEdit;
    private StateEditSession<WorkspaceState>? _relationalWorkspaceEdit;
    private StateEditSession<ScopeSelection>? _relationalSelectionEdit;
    private StateEditSession<Scope>? _relationalScopeEdit;
    private StateEditSession<DiagramState>? _relationalDiagramEdit;
    private StateEditSession<WorkbenchAggregate>? _relationalWorkbenchEdit;
    private ExplorerScope? _relationalExplorerScope;
    private ReferenceCatalogue? _relationalReferenceCatalogue;
    private readonly Dictionary<FileSystemNode, ExplorerNodeSummary> _relationalExplorerNodes = [];
    private RuntimeWorkbenchCursor? _relationalWorkbenchNext;
    private sealed record RelationalWorkbenchCatalogueAction(string Name);
    private RelationalSelectionRequest? _relationalSelectionLifetime;
    private readonly List<Task> _relationalSelectionRetirements = [];
    private long _relationalSelectionGeneration;
    private readonly SemaphoreSlim _relationalOwnerCommands = new(1, 1);
    private bool _relationalLayoutRestoring;
    private readonly List<RelationalWindowOccurrence> _relationalRetainedWindows = [];
    private readonly List<ReferenceConnectionLineState> _relationalRetainedConnections = [];
    private readonly ConditionalWeakTable<ReferenceConnectionLineState, ReferenceConnectionLineState> _relationalConnectionOccurrences = new();
    private readonly ConditionalWeakTable<OpenDocumentState, RelationalWindowOccurrence> _relationalWindowOccurrences = new();
    private ConditionalWeakTable<FrameworkElement, RelationalImagePayload> _relationalControlImages = new();
    private ConditionalWeakTable<DiagramObjectSnapshot, RelationalImagePayload> _relationalSnapshotImages = new();
    private readonly ConditionalWeakTable<DiagramObjectSnapshot, RelationalDiagramOccurrence> _relationalSnapshotOccurrences = new();
    private ConditionalWeakTable<FrameworkElement, RelationalDiagramOccurrence> _relationalControlOccurrences = new();
    private readonly Dictionary<string, List<WeakReference<RelationalImagePayload>>> _relationalFilenameImages = new(StringComparer.Ordinal);
    private int _relationalFilenameRegistrations;
    private RelationalDiagramHeader? _relationalDiagramHeader;
    private bool _relationalDiagramDraft;
    private long? _relationalActiveDiagramRevisionKey;

    // The document/grid owner installs this after converting OpenFileAsync. A missing opener retains descriptors.
    private Func<OpenDocumentState, CancellationToken, Task<bool>>? _relationalDocumentOpener;

    private sealed record RelationalDiagramHeader(string Id, string Name, DateTimeOffset Created);
    private sealed class RelationalWindowOccurrence(OpenDocumentState state)
    {
        public Guid Identity { get; } = Guid.NewGuid();
        public OpenDocumentState Original { get; } = state;
        public bool Closed { get; set; }
    }
    private sealed record RelationalImagePayload(string Filename, byte[] DisplayBytes, byte[]? PastedBytes,
        PastedImageFallback? Fallback);
    private sealed class RelationalDiagramOccurrence(DiagramObjectSnapshot original, DiagramObjectSnapshot rendered, FrameworkElement control)
    {
        public DiagramObjectSnapshot Original { get; } = original;
        public DiagramObjectSnapshot Rendered { get; } = rendered;
        public WeakReference<FrameworkElement> Control { get; } = new(control);
        public int? SourceOrdinal { get; set; }
    }
    private sealed record PreparedRelationalScope(StateEditSession<Scope> Edit, Scope Model, ExplorerScope Explorer,
        IReadOnlyList<ExplorerNodeSummary> Roots, ReferenceCatalogue References);
    private sealed record PreparedRelationalDiagram(DiagramState State, IReadOnlyList<RelationalImagePayload?> Images);

    private RelationalRuntime RelationalStateRuntime => _relational ?? throw new InvalidOperationException("No relational runtime is selected.");
    private static readonly StateLimits RelationalRuntimeStateLimits = new();

    private async Task LoadRelationalStartupAsync()
    {
        Dispatcher.VerifyAccess(); _isPersistenceHydrated = false;
        try
        {
            using var operation = BeginRelationalStateOperation();
            var ct = operation.Token;
            var runtime = RelationalStateRuntime;
            await runtime.Session.RequireReadyAsync(ct);
            RequireRelationalStartup(ct);
            if (_relationalDocumentOpener == null) InitializeRelationalDocumentAccess();
            var preferences = RequireRelationalLoad(await runtime.Preferences.LoadStartupPreferencesAsync(ct), "preferences");
            StateEditSession<WorkspaceState>? workspace = null;
            StateEditSession<ScopeSelection>? selection = null;
            try
            {
                workspace = RequireRelationalLoad(await runtime.State.LoadWorkspaceAsync(ct), "workspace");
                selection = RequireRelationalLoad(await runtime.State.LoadScopeSelectionAsync(ct), "scope selection");
                _ = await runtime.State.ListScopesAsync(100, ct: ct); // A summary page, never editable scope stubs.
                var workbenches = await runtime.StateStore.ListRecentWorkbenchesAsync(100, ct: ct);
                RequireRelationalStartup(ct);
                _relationalPreferenceEdit = preferences; _relationalWorkspaceEdit = workspace; _relationalSelectionEdit = selection;
                preferences.Snapshot().ApplyToRuntime(_appSettings);
                await LoadRelationalAppearanceRulesAsync(preferences.ExpectedToken, ct);
                RequireRelationalStartup(ct);
                AppThemeService.Apply(_appSettings.Appearance.Theme); ApplyThemeToRuntimeSurfaces();
                ApplyInternalLoggingSetting("relational startup preferences loaded");
                _workspaceState = workspace.Snapshot();
                BindRelationalWorkbenchPage(workbenches);
                var newest = preferences.Snapshot().LoadMostRecentWorkbenchOnStartup
                    ? await runtime.Preferences.ReadMostRecentWorkbenchSummaryAsync(ct) : null;
                RequireRelationalStartup(ct);
                if (newest?.Status == StateLoadStatus.Failed) throw newest.Error!;
                if (newest?.Status == StateLoadStatus.Cancelled) throw new OperationCanceledException(ct);
                if (newest?.IsReady == true) await LoadRelationalWorkbenchAsync(newest.Value!, ct);
                else
                {
                    await LoadRelationalLastActiveScopeAsync(ct);
                    RequireRelationalStartup(ct);
                    BeginRelationalLayout(_workspaceState.OpenDocuments);
                    _canvasZoom = NormalizeCanvasZoom(_workspaceState.CanvasZoom); ApplyCanvasZoom();
                    await RestoreRelationalDocumentsAsync(ct);
                    RequireRelationalStartup(ct);
                    await Dispatcher.InvokeAsync(new Action(() =>
                    {
                        RequireRelationalStartup(ct);
                        RestoreViewport(); WorkspaceScrollViewer.UpdateLayout();
                    }), DispatcherPriority.ContextIdle);
                }
                RequireRelationalStartup(ct);
                RememberRelationalScopeSwitchWorkbenchBaseline();
                _persistenceLoadFailureMessage = null; _isPersistenceHydrated = true;
                RefreshDiagramImageToolMenu();
            }
            catch
            {
                await preferences.DisposeAsync(); if (workspace != null) await workspace.DisposeAsync();
                if (selection != null) await selection.DisposeAsync();
                _relationalPreferenceEdit = null; _relationalWorkspaceEdit = null; _relationalSelectionEdit = null;
                throw;
            }
        }
        catch (Exception e)
        {
            _isPersistenceHydrated = false;
            _persistenceLoadFailureMessage = "Relational startup did not complete.";
            if (!_relationalStateClosing) StatusText = "Could not restore relational state. Saving is disabled; no replacement defaults were published.";
            InternalLogService.Error(e, "Relational state startup failed.");
            throw;
        }
    }

    private void RequireRelationalStartup(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_relationalStateClosing) throw new OperationCanceledException("The window is closing.", ct);
    }

    private static T RequireRelationalLoad<T>(StateLoad<T> load, string kind) where T : class => load.Value ??
        throw load.Error ?? (load.Status == StateLoadStatus.Cancelled ? new OperationCanceledException() :
            new InvalidOperationException("The selected " + kind + " is missing. No replacement will be created."));

    private void RequireRelationalWrite()
    {
        Dispatcher.VerifyAccess();
        if (_relationalStateClosing) throw new OperationCanceledException("The window is closing.");
        if (!_isPersistenceHydrated || _relationalLayoutRestoring)
            throw new InvalidOperationException("Relational state is not successfully loaded or is being restored. Saving is disabled.");
        if (_relationalStateCommandOutcomeUnknown)
            throw new InvalidOperationException("A selected create/delete publication has an unknown outcome. Reload before further writes.");
        RelationalStateRuntime.Session.RejectValidationWrite();
    }

    private sealed class RelationalSelectionRequest : IDisposable
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _cancellationGate = new();
        private readonly long _generation;
        private readonly CancellationTokenRegistration _callerCancellation, _ownerCancellation;
        private Task? _firstCancellation;
        public CancellationTokenSource Source { get; } = new();
        public RelationalSelectionRequest(long generation, CancellationToken caller, CancellationToken owner)
        {
            _generation = generation;
            _callerCancellation = caller.Register(static state => _ = ((RelationalSelectionRequest)state!).CancelAsync(), this);
            _ownerCancellation = owner.Register(static state => _ = ((RelationalSelectionRequest)state!).CancelAsync(), this);
        }
        public Task? Retirement { get; set; }
        public Task Completed => _completed.Task;
        public Task CancelAsync() { lock (_cancellationGate) return _firstCancellation ??= Source.CancelAsync(); }
        public void Deconstruct(out long value, out CancellationToken token) { value = _generation; token = Source.Token; }
        public void Dispose() => _completed.TrySetResult();
        public void DisposeSource() { _callerCancellation.Dispose(); _ownerCancellation.Dispose(); Source.Dispose(); }
    }
    private RelationalSelectionRequest BeginRelationalSelection(CancellationToken ct)
    {
        if (_relationalStateClosing) throw new OperationCanceledException("The window is closing.");
        if (_relationalSelectionLifetime != null) RetireRelationalSelection(_relationalSelectionLifetime);
        _relationalSelectionLifetime = new(++_relationalSelectionGeneration, ct, _relationalStateLifetime.Token);
        return _relationalSelectionLifetime;
    }
    private void RetireRelationalSelection(RelationalSelectionRequest request)
    {
        if (request.Retirement != null) return;
        var firstCancellation = request.CancelAsync();
        request.Retirement = FinishRelationalSelectionAsync(request, firstCancellation);
        _relationalSelectionRetirements.RemoveAll(t => t.IsCompletedSuccessfully);
        _relationalSelectionRetirements.Add(request.Retirement);
    }
    private static async Task FinishRelationalSelectionAsync(RelationalSelectionRequest request, Task firstCancellation)
    {
        try { await Task.WhenAll(firstCancellation, request.Completed); }
        finally { request.DisposeSource(); }
    }
    private void RequireRelationalSelection(long generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (generation != _relationalSelectionGeneration) throw new OperationCanceledException("A newer selection superseded this read.");
    }

    private async Task WaitRelationalSelectedOwnerIdleAsync(CancellationToken ct)
    {
        await _relationalOwnerCommands.WaitAsync(ct);
        _relationalOwnerCommands.Release();
    }

    private async Task<ScopeSummary?> FindRelationalScopeAsync(string id, CancellationToken ct)
    {
        StateCatalogueCursor<ScopeSummary>? cursor = null; ScopeSummary? found = null; int count = 0;
        do
        {
            var page = await RelationalStateRuntime.State.ListScopesAsync(100, cursor, ct);
            foreach (var item in page.Items)
            {
                if (++count > RelationalRuntimeStateLimits.MaximumRows) throw new InvalidDataException("The scope identity lookup exceeds its metadata budget.");
                if (!string.Equals(item.ScopeId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) throw new InvalidDataException("The original scope ID is ambiguous. Select a scope by its catalogue entry.");
                found = item;
            }
            cursor = page.Next;
        } while (cursor != null);
        return found;
    }

    private async Task<DiagramSummary?> FindRelationalDiagramAsync(string id, CancellationToken ct)
    {
        StateCatalogueCursor<DiagramSummary>? cursor = null; DiagramSummary? found = null; int count = 0;
        do
        {
            var page = await RelationalStateRuntime.State.ListDiagramsAsync(100, cursor, ct);
            foreach (var item in page.Items)
            {
                if (++count > RelationalRuntimeStateLimits.MaximumRows) throw new InvalidDataException("The diagram identity lookup exceeds its metadata budget.");
                if (!string.Equals(item.DiagramId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) throw new InvalidDataException("The original diagram ID is ambiguous. Open its typed scope occurrence instead.");
                found = item;
            }
            cursor = page.Next;
        } while (cursor != null);
        return found;
    }

    private async Task<PreparedRelationalScope> PrepareRelationalScopeAsync(ScopeSummary chosen, CancellationToken ct,
        IReadOnlySet<string>? unloaded = null)
    {
        var edit = RequireRelationalLoad(await RelationalStateRuntime.State.LoadScopeAsync(chosen, ct), "scope");
        try
        {
            var explorer = await RelationalStateRuntime.Explorer.OpenScopeAsync(edit.SubjectKey, unloaded ?? GetUnloadedResourceIds(), CultureInfo.CurrentCulture.Name, ct);
            var roots = await RelationalStateRuntime.Explorer.GetRootsAsync(explorer, ct);
            var references = await RelationalStateRuntime.References.LoadPaintAsync(explorer, ct);
            return new(edit, edit.Snapshot(), explorer, roots, references);
        }
        catch { await edit.DisposeAsync(); throw; }
    }

    private void ApplyRelationalScope(PreparedRelationalScope scope)
    {
        SetActiveScope(scope.Model); _relationalScopeEdit = scope.Edit;
        _relationalExplorerScope = scope.Explorer; _relationalReferenceCatalogue = scope.References;
        _referenceIndex = ScopeReferenceIndex.Empty; // The reference owner consumes the shared relational paint lookup.
        RootNodes.Clear(); _relationalExplorerNodes.Clear(); _expandedObjectExplorerNodeKeys.Clear();
        foreach (var root in scope.Roots) RootNodes.Add(CreateRelationalExplorerNode(root));
        CurrentFolderDisplay = "Scope: " + scope.Model.Name;
        BeginRelationalExplorerContext();
        InitializeRelationalScopeResourceCommands();
        InvalidateRelationalDocumentContext();
    }

    private FileSystemNode CreateRelationalExplorerNode(ExplorerNodeSummary summary)
    {
        var node = new FileSystemNode(summary.FullPath, summary.IsDirectory, summary.Exists, summary.Name, summary.IconKind,
            summary.ParentNodeKey, summary.NodeKey, summary.IsVirtualFolder, summary.VirtualFolderId, summary.ScopeResourceId,
            summary.ResourceKind, summary.IsScopeResourceRoot, summary.IsScopeResourceLoaded, summary.ToolTip)
        {
            NaturalParentKey = summary.NaturalParentKey, IsVirtualDocument = summary.IsVirtualDocument,
            HasUnresolvedQueries = summary.HasUnresolvedQueries, IsLoaded = summary.ChildrenState == ExplorerChildrenState.Loaded
        };
        _relationalExplorerNodes.Add(node, summary); if (!node.IsLoaded) node.AddLoadingPlaceholder(); return node;
    }

    private async Task LoadRelationalLastActiveScopeAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        using var operation = BeginRelationalStateOperation(cancellationToken);
        var selection = _relationalSelectionEdit ?? throw new InvalidOperationException("Scope selection is not loaded.");
        using var request = BeginRelationalSelection(operation.Token);
        var (generation, ct) = request;
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
        RequireRelationalSelection(generation, ct);
        string? id = selection.Snapshot().LastActiveScopeId;
        ScopeSummary? chosen = string.IsNullOrWhiteSpace(id) ? null : await FindRelationalScopeAsync(id, ct);
        if (chosen == null && string.IsNullOrWhiteSpace(id))
        {
            var page = await RelationalStateRuntime.State.ListScopesAsync(2, ct: ct);
            if (page.Items.Count == 1 && page.Next == null) chosen = page.Items[0];
        }
        if (chosen == null)
        {
            RequireRelationalSelection(generation, ct); SetActiveScope(null); RootNodes.Clear();
            _relationalExplorerScope = null; _relationalReferenceCatalogue = null; CurrentFolderDisplay = "No scope selected";
            if (_relationalScopeEdit != null) await _relationalScopeEdit.DisposeAsync(); _relationalScopeEdit = null;
            BeginRelationalExplorerContext(); InvalidateRelationalDocumentContext();
            InitializeRelationalScopeResourceCommands();
            return; // Missing raw IDs remain preserved, not rewritten to a default.
        }
        var prepared = await PrepareRelationalScopeAsync(chosen, ct);
        try
        {
            RequireRelationalSelection(generation, ct); var previous = _relationalScopeEdit; ApplyRelationalScope(prepared);
            if (previous != null) await previous.DisposeAsync();
        }
        catch { if (!ReferenceEquals(_relationalScopeEdit, prepared.Edit)) await prepared.Edit.DisposeAsync(); throw; }
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task ShowRelationalScopesAsync()
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation();
        await SaveRelationalScopeAsync();
        var picker = new RelationalScopePickerWindow(RelationalStateRuntime) { Owner = this };
        picker.LinkOwnerLifetime(operation.Token);
        bool? result;
        try { result = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
        operation.Token.ThrowIfCancellationRequested();
        if (result == true && picker.SelectedSummary != null)
        {
            using var request = BeginRelationalSelection(CancellationToken.None);
            var (generation, ct) = request;
            await WaitRelationalSelectedOwnerIdleAsync(ct);
            RequireRelationalSelection(generation, ct);
            if (!await ConfirmRelationalStateSwitchAsync(ct)) return;
            RequireRelationalSelection(generation, ct);
            await _relationalOwnerCommands.WaitAsync(ct);
            try
            {
            RequireRelationalSelection(generation, ct);
            var prepared = await PrepareRelationalScopeAsync(picker.SelectedSummary, ct);
            try
            {
                RequireRelationalSelection(generation, ct);
                var selection = _relationalSelectionEdit ?? throw new InvalidOperationException("Scope selection is unavailable.");
                selection.Replace(selection.Snapshot() with { LastActiveScopeId = prepared.Model.ScopeId }); await selection.SaveAsync(ct);
                RequireRelationalSelection(generation, ct);
                var previous = _relationalScopeEdit; CloseAllOpenWindows(); ClearSelectedWorkbench();
                _relationalRetainedWindows.Clear(); _relationalRetainedConnections.Clear(); ApplyRelationalScope(prepared);
                RememberRelationalScopeSwitchWorkbenchBaseline();
                if (previous != null) await previous.DisposeAsync();
            }
            catch { if (!ReferenceEquals(_relationalScopeEdit, prepared.Edit)) await prepared.Edit.DisposeAsync(); throw; }
            }
            finally { _relationalOwnerCommands.Release(); }
        }
        else if (_relationalScopeEdit != null && (picker.ChangedScopeKeys.Contains(_relationalScopeEdit.SubjectKey) ||
            picker.DeletedScopeKeys.Contains(_relationalScopeEdit.SubjectKey))) await LoadRelationalLastActiveScopeAsync();
    }

    private async Task SaveRelationalScopeAsync(CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        if (_activeScope == null) return;
        var edit = _relationalScopeEdit ?? throw new InvalidOperationException("This scope was not successfully loaded. Saving is disabled.");
        if (!string.Equals(_activeScope.ScopeId, edit.Snapshot().ScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("The active scope is not the loaded owner.");
        var copy = StateCopies.Scope(_activeScope, new(RelationalRuntimeStateLimits));
        if (SameRelationalScope(edit.Snapshot(), copy)) return;
        edit.Replace(copy); await edit.SaveAsync(ct);
    }

    private static bool SameRelationalScope(Scope a, Scope b)
    {
        if (a.ScopeId != b.ScopeId || a.Name != b.Name || a.Description != b.Description || a.Resources.Count != b.Resources.Count || a.VirtualFolders.Count != b.VirtualFolders.Count) return false;
        for (int i = 0; i < a.Resources.Count; i++)
        {
            var x = a.Resources[i]; var y = b.Resources[i];
            if (x.ResourceId != y.ResourceId || x.Kind != y.Kind || x.Path != y.Path || x.DisplayNameOverride != y.DisplayNameOverride ||
                x.DetailsOverride != y.DetailsOverride || x.AddedAtUtc != y.AddedAtUtc || x.IncludeChildren != y.IncludeChildren) return false;
        }
        for (int i = 0; i < a.VirtualFolders.Count; i++)
        {
            var x = a.VirtualFolders[i]; var y = b.VirtualFolders[i];
            if (x.VirtualFolderId != y.VirtualFolderId || x.Name != y.Name || x.ParentNodeKey != y.ParentNodeKey || !x.ChildNodeKeys.SequenceEqual(y.ChildNodeKeys)) return false;
        }
        return true;
    }

    private async Task SaveRelationalSettingsAsync(CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        var edit = _relationalPreferenceEdit ?? throw new InvalidOperationException("Preferences were not loaded.");
        var captured = new StartupPreferences(_appSettings.Appearance.Theme, _appSettings.LoadMostRecentWorkbenchOnStartup,
            _appSettings.ResourceComparison.IgnoreWhitespaceByDefault, _appSettings.ResourceComparison.IgnoreCaseByDefault,
            _appSettings.Diagnostics.EnableInternalLogging, _appSettings.CodeWindows.DefaultBackcolor,
            StateInputPreferences.From(_appSettings.KeyboardShortcuts));
        if (captured == edit.Snapshot()) return;
        edit.Replace(captured); await edit.SaveAsync(ct); // Scalar head only; unqueried image/style children are untouched.
    }

    private void BeginRelationalLayout(IEnumerable<OpenDocumentState> documents)
    {
        _relationalRetainedWindows.Clear();
        foreach (var document in documents)
        {
            var occurrence = new RelationalWindowOccurrence(StateCopies.Window(document, new(RelationalRuntimeStateLimits)));
            _relationalRetainedWindows.Add(occurrence); _relationalWindowOccurrences.Remove(document); _relationalWindowOccurrences.Add(document, occurrence);
        }
    }

    private async Task RestoreRelationalDocumentsAsync(CancellationToken ct)
    {
        if (_relationalDocumentOpener == null) return;
        foreach (var document in _workspaceState.OpenDocuments.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (document.TargetState is SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) continue;
            try { await _relationalDocumentOpener(document, ct); RequireRelationalStartup(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { RequireRelationalStartup(ct); StatusText = "A saved document could not be opened; its layout and raw address are retained."; }
        }
    }

    private void ForgetRelationalDocumentState(OpenDocumentState document)
    {
        if (!_relationalLayoutRestoring && _relationalWindowOccurrences.TryGetValue(document, out var occurrence)) occurrence.Closed = true;
    }

    private List<OpenDocumentState> CaptureRelationalWindows()
    {
        SyncOpenDocumentStatesFromWindows();
        var live = OpenTabs.Select(t => t.State).ToArray(); var remaining = live.ToHashSet(ReferenceEqualityComparer.Instance);
        var result = new List<OpenDocumentState>();
        foreach (var occurrence in _relationalRetainedWindows)
        {
            if (occurrence.Closed) continue;
            var window = live.FirstOrDefault(w => _relationalWindowOccurrences.TryGetValue(w, out var id) && ReferenceEquals(id, occurrence));
            if (window != null) remaining.Remove(window);
            var copy = StateCopies.Window(window ?? occurrence.Original, new(RelationalRuntimeStateLimits));
            copy.FilePath = occurrence.Original.FilePath; // A readable/canonical display address must not replace the original locator.
            result.Add(copy);
        }
        foreach (var window in live.Where(remaining.Contains)) result.Add(StateCopies.Window(window, new(RelationalRuntimeStateLimits)));
        return result;
    }

    private async Task SaveRelationalWorkspaceAsync(CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        var edit = _relationalWorkspaceEdit ?? throw new InvalidOperationException("Workspace was not loaded.");
        CaptureViewportState();
        var captured = StateCopies.Workspace(_workspaceState, new(RelationalRuntimeStateLimits));
        captured.OpenDocuments = new ObservableCollection<OpenDocumentState>(CaptureRelationalWindows());
        if (SameRelationalWorkspace(edit.Snapshot(), captured)) return;
        edit.Replace(captured); await edit.SaveAsync(ct);
    }
    private static bool SameRelationalWorkspace(WorkspaceState a, WorkspaceState b) =>
        a.LastFolderPath == b.LastFolderPath && a.CanvasZoom == b.CanvasZoom && a.ViewportHorizontalOffset == b.ViewportHorizontalOffset &&
        a.ViewportVerticalOffset == b.ViewportVerticalOffset && a.UnloadedResourceIds.SequenceEqual(b.UnloadedResourceIds) &&
        a.OpenDocuments.Count == b.OpenDocuments.Count && a.OpenDocuments.Zip(b.OpenDocuments).All(p =>
            StateMaps.Window.Fields.All(f => Equals(f.Get(p.First), f.Get(p.Second))) && p.First.SpreadsheetFilters.SequenceEqual(p.Second.SpreadsheetFilters));

    private async Task<PreparedRelationalDiagram> PrepareRelationalDiagramAsync(DiagramState state, CancellationToken ct)
    {
        var images = new List<RelationalImagePayload?>(); var budget = new StateBudget(RelationalRuntimeStateLimits);
        foreach (var pair in state.Document.Objects.Select((value, ordinal) => (value, ordinal)))
        {
            ct.ThrowIfCancellationRequested(); budget.Row(); var snapshot = pair.value;
            if (snapshot.ObjectType != DiagramObjectType.Image) { images.Add(null); continue; }
            byte[]? pasted = state.PastedImages.TryGetValue(pair.ordinal, out var p) ? p : null;
            PastedImageFallback? fallback = state.PastedImageFallbacks != null && state.PastedImageFallbacks.TryGetValue(pair.ordinal, out var f) ? f : null;
            byte[]? display = pasted ?? fallback?.Bytes ?? StateImages.Decode(snapshot.ImageDataBase64, RelationalRuntimeStateLimits, budget, ct);
            display ??= await ReadRelationalDefinitionBytesAsync(snapshot.ImageDefinitionId, ct);
            if (display == null) throw new InvalidDataException("Diagram image at ordinal " + pair.ordinal + " has no usable selected asset. Its original object was retained; loading was not applied.");
            budget.Asset(display.LongLength); StateImages.Validate(display, RelationalRuntimeStateLimits, cancellationToken: ct);
            _ = DecodeRelationalImage(display); // Decode every chosen image before clearing the current canvas.
            images.Add(new(snapshot.PastedImageFileName, display, pasted, fallback));
        }
        return new(state, images);
    }

    private async Task<byte[]?> ReadRelationalDefinitionBytesAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        PreferenceCursor<ImageDefinitionSummary>? cursor = null; ImageDefinitionSummary? found = null; StateToken? owner = null; int count = 0;
        do
        {
            var page = RequireRelationalLoad(await RelationalStateRuntime.Preferences.ListImagesAsync(100, cursor, ct), "image catalogue");
            owner = page.Owner;
            foreach (var image in page.Items)
            {
                if (++count > RelationalRuntimeStateLimits.MaximumRows) throw new InvalidDataException("Image identity lookup exceeds its metadata budget.");
                if (!string.Equals(image.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) throw new InvalidDataException("The original image definition ID is ambiguous. Select a definition explicitly.");
                found = image;
            }
            cursor = page.Next;
        } while (cursor != null);
        if (found == null || !found.HasAsset) return null;
        return RequireRelationalLoad(await RelationalStateRuntime.Preferences.ReadImageAssetAsync(found.Key, owner!, ct), "image asset").CopyBytes();
    }

    private static BitmapImage DecodeRelationalImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    // Parent hook: if (_relational != null) return CreateRelationalDiagramImageFromSnapshot(snapshot);
    private DiagramImageControl CreateRelationalDiagramImageFromSnapshot(DiagramObjectSnapshot snapshot)
    {
        var payload = FindRelationalImagePayload(snapshot) ?? throw new InvalidDataException("This image has no selected byte payload; local filenames are not read in relational mode.");
        var image = new DiagramImageControl(snapshot.ImageDefinitionId, snapshot.ImageName, DecodeRelationalImage(payload.DisplayBytes),
            snapshot.ImageDataBase64, snapshot.PastedImageFileName, snapshot.Id);
        _relationalControlImages.Add(image, payload); AttachDiagramImageHandlers(image); return image;
    }

    private void PasteRelationalClipboardImage(BitmapSource source)
    {
        RequireRelationalWrite();
        if (TryBlockDiagramObjectEditWhenLocked("paste images onto the diagram")) return;
        if (checked((long)source.PixelWidth * source.PixelHeight) > RelationalRuntimeStateLimits.MaximumImagePixels)
            throw new InvalidDataException("The clipboard image exceeds its decoded pixel budget.");
        using var bytes = new RelationalPngBuffer(RelationalRuntimeStateLimits.MaximumAssetBytes);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(bytes);
        byte[] png = bytes.ToArray(); StateImages.Validate(png, RelationalRuntimeStateLimits, true);
        var display = DecodeRelationalImage(png); string filename = Guid.NewGuid().ToString("N") + ".png";
        var payload = new RelationalImagePayload(filename, png, png, null);
        var image = new DiagramImageControl(string.Empty, "Pasted Image", display, string.Empty, filename);
        _relationalControlImages.Add(image, payload); RegisterRelationalImageFilename(payload); AttachDiagramImageHandlers(image);
        var size = GetPastedImageDisplaySize(display); var center = GetDiagramPasteCenterPoint();
        image.SetCanvasBounds(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        ApplyDefaultDiagramZIndex(image); DiagramCanvas.Children.Add(image); SelectDiagramObject(image);
        var snapshot = CreateDiagramObjectSnapshot(image)!; BindRelationalObjectOccurrence(image, snapshot);
        PushDiagramUndo(DiagramUndoActionKind.Added, null, snapshot); StatusText = "Pasted image onto diagram.";
    }

    private sealed class RelationalPngBuffer(int maximum) : MemoryStream
    {
        private void RequireLength(long length)
        { if (length < 0 || length > maximum) throw new InvalidDataException("The encoded clipboard PNG exceeds its byte budget."); }
        public override void Write(byte[] buffer, int offset, int count) { RequireLength(checked(Position + count)); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { RequireLength(checked(Position + buffer.Length)); base.Write(buffer); }
        public override void WriteByte(byte value) { RequireLength(checked(Position + 1)); base.WriteByte(value); }
        public override void SetLength(long value) { RequireLength(value); base.SetLength(value); }
    }

    private RelationalImagePayload? FindRelationalImagePayload(DiagramObjectSnapshot snapshot)
    {
        if (snapshot.ObjectType != DiagramObjectType.Image) return null;
        if (_relationalSnapshotImages.TryGetValue(snapshot, out var exact)) return exact;
        if (!string.IsNullOrWhiteSpace(snapshot.PastedImageFileName) && _relationalFilenameImages.TryGetValue(snapshot.PastedImageFileName, out var candidates))
        {
            RelationalImagePayload? first = null;
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                if (!candidates[i].TryGetTarget(out var value)) { candidates.RemoveAt(i); continue; }
                if (first != null && !SameRelationalImagePayload(first, value))
                    throw new InvalidDataException("Repeated image filenames have different assets; the undo occurrence binding is required.");
                first ??= value;
            }
            if (candidates.Count == 0) _relationalFilenameImages.Remove(snapshot.PastedImageFileName);
            if (first != null) return first;
        }
        byte[]? inline = StateImages.Decode(snapshot.ImageDataBase64, RelationalRuntimeStateLimits, new(RelationalRuntimeStateLimits));
        return inline == null ? null : new(snapshot.PastedImageFileName, inline, null, null);
    }

    private void RegisterRelationalImageFilename(RelationalImagePayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Filename)) return;
        // Controls, clipboard snapshots and undo entries own bytes. This fallback
        // name lookup must not keep deleted images alive after those owners retire.
        if ((unchecked(++_relationalFilenameRegistrations) & 31) == 0)
            foreach (string key in _relationalFilenameImages.Keys.ToArray())
            {
                var entries = _relationalFilenameImages[key];
                entries.RemoveAll(reference => !reference.TryGetTarget(out _));
                if (entries.Count == 0) _relationalFilenameImages.Remove(key);
            }
        if (!_relationalFilenameImages.TryGetValue(payload.Filename, out var list))
            _relationalFilenameImages.Add(payload.Filename, list = []);
        list.Add(new(payload));
    }
    private static bool SameRelationalImagePayload(RelationalImagePayload a, RelationalImagePayload b) =>
        a.DisplayBytes.AsSpan().SequenceEqual(b.DisplayBytes) && (a.PastedBytes == null) == (b.PastedBytes == null) &&
        a.Fallback?.Resolution == b.Fallback?.Resolution;

    private void AttachRelationalSnapshotImage(FrameworkElement control, DiagramObjectSnapshot snapshot)
    {
        if (_relationalControlImages.TryGetValue(control, out var payload))
        { _relationalSnapshotImages.Remove(snapshot); _relationalSnapshotImages.Add(snapshot, payload); }
        if (_relationalControlOccurrences.TryGetValue(control, out var occurrence))
        { _relationalSnapshotOccurrences.Remove(snapshot); _relationalSnapshotOccurrences.Add(snapshot, occurrence); }
    }
    // Parent uses this instead of snapshot.Clone() in undo and clipboard paths, preserving duplicate-ID occurrences.
    private DiagramObjectSnapshot CloneRelationalDiagramSnapshot(DiagramObjectSnapshot snapshot)
    {
        var copy = CopyRelationalObject(snapshot); var payload = FindRelationalImagePayload(snapshot);
        if (payload != null) _relationalSnapshotImages.Add(copy, payload);
        if (_relationalSnapshotOccurrences.TryGetValue(snapshot, out var occurrence)) _relationalSnapshotOccurrences.Add(copy, occurrence);
        return copy;
    }

    private static DiagramObjectSnapshot CopyRelationalObject(DiagramObjectSnapshot snapshot)
    {
        var budget = new StateBudget(RelationalRuntimeStateLimits); var copy = StateMaps.Object.Copy(snapshot, budget);
        copy.Metadata.Queries = snapshot.Metadata.Queries.Select(q => StateMaps.Query.Copy(q, budget)).ToList(); return copy;
    }

    // Parent calls after CreateDiagramObjectFromSnapshot has applied geometry, including undo recreation.
    private void BindRelationalObjectOccurrence(FrameworkElement control, DiagramObjectSnapshot snapshot)
    {
        if (!_relationalSnapshotOccurrences.TryGetValue(snapshot, out var occurrence) || occurrence.Original.Id != snapshot.Id)
            occurrence = new(CopyRelationalObject(snapshot), CopyRelationalObject(CreateDiagramObjectSnapshot(control)!), control);
        else occurrence.Control.SetTarget(control);
        _relationalControlOccurrences.Remove(control); _relationalControlOccurrences.Add(control, occurrence);
        AttachRelationalSnapshotImage(control, snapshot);
    }

    // Parent applies this to the switch result in CreateDiagramObjectSnapshot before returning it.
    private DiagramObjectSnapshot PreserveRelationalObjectSnapshot(FrameworkElement control, DiagramObjectSnapshot captured)
    {
        if (!_relationalControlOccurrences.TryGetValue(control, out var occurrence))
        {
            _relationalControlOccurrences.Add(control, new(CopyRelationalObject(captured), CopyRelationalObject(captured), control));
            AttachRelationalSnapshotImage(control, captured); return captured;
        }
        var copy = CopyRelationalObject(occurrence.Original);
        foreach (var field in StateMaps.Object.Fields)
            if (!Equals(field.Get(captured), field.Get(occurrence.Rendered))) field.Set(copy, field.Get(captured));
        if (!SameRelationalQueries(captured.Metadata.Queries, occurrence.Rendered.Metadata.Queries))
            copy.Metadata.Queries = captured.Metadata.Queries.Select(q => StateMaps.Query.Copy(q, new(RelationalRuntimeStateLimits))).ToList();
        AttachRelationalSnapshotImage(control, copy); return copy;
    }
    private static bool SameRelationalQueries(IReadOnlyList<QueryItem> a, IReadOnlyList<QueryItem> b) =>
        a.Count == b.Count && a.Zip(b).All(p => StateMaps.Query.Fields.All(f => Equals(f.Get(p.First), f.Get(p.Second))));

    private FrameworkElement? FindRelationalDiagramObjectBySnapshot(DiagramObjectSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        if (!_relationalSnapshotOccurrences.TryGetValue(snapshot, out var occurrence))
            throw new InvalidOperationException("This undo record has no typed object occurrence; raw duplicate IDs cannot select an object safely.");
        return occurrence.Control.TryGetTarget(out var control) && DiagramCanvas.Children.Contains(control) ? control : null;
    }

    private void ApplyRelationalDiagram(PreparedRelationalDiagram prepared)
    {
        var diagram = prepared.State.Document;
        var oldWorkflows = _currentDiagramWorkflows;
        var controls = new List<FrameworkElement>();
        var newImages = new ConditionalWeakTable<FrameworkElement, RelationalImagePayload>();
        _currentDiagramWorkflows = StateCopies.Diagram(diagram, new(RelationalRuntimeStateLimits)).Workflows;
        try
        {
            for (int i = 0; i < diagram.Objects.Count; i++)
            {
                var snapshot = diagram.Objects[i]; FrameworkElement control;
                if (prepared.Images[i] is { } payload)
                {
                    var image = new DiagramImageControl(snapshot.ImageDefinitionId, snapshot.ImageName, DecodeRelationalImage(payload.DisplayBytes),
                        snapshot.ImageDataBase64, snapshot.PastedImageFileName, snapshot.Id);
                    AttachDiagramImageHandlers(image); ApplyDiagramObjectSnapshot(image, snapshot); newImages.Add(image, payload); control = image;
                }
                else control = CreateDiagramObjectFromSnapshot(snapshot) ?? throw new InvalidDataException("The selected diagram contains an unsupported object; no object was omitted.");
                controls.Add(control);
            }
        }
        catch { _currentDiagramWorkflows = oldWorkflows; throw; }
        ClearDiagramObjects(); _relationalControlImages = newImages; _relationalControlOccurrences = new(); _relationalFilenameImages.Clear();
        for (int i = 0; i < controls.Count; i++)
        {
            var control = controls[i]; BindRelationalObjectOccurrence(control, diagram.Objects[i]);
            if (_relationalControlOccurrences.TryGetValue(control, out var occurrence)) occurrence.SourceOrdinal = i;
            DiagramCanvas.Children.Add(control);
            if (_relationalControlImages.TryGetValue(control, out var payload) && !string.IsNullOrWhiteSpace(payload.Filename))
            {
                RegisterRelationalImageFilename(payload);
            }
        }
        ResetWorkflowEditor(); RefreshWorkflowList(); foreach (var workflow in _currentDiagramWorkflows) ApplyWorkflowMarkerVisibility(workflow.WorkflowId);
        _diagramUndoStack.Clear(); SelectDiagramObject(null); SetCurrentDiagramIdentity(diagram.DiagramId, diagram.Name);
        _relationalDiagramHeader = new(diagram.DiagramId, diagram.Name, diagram.CreatedAtUtc); _relationalDiagramDraft = false;
        _diagramCanvasZoom = NormalizeCanvasZoom(diagram.CanvasZoom); ApplyDiagramCanvasZoom();
        long generation = _relationalSelectionGeneration;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_relationalStateClosing || generation != _relationalSelectionGeneration) return;
            DiagramScrollViewer.UpdateLayout(); DiagramScrollViewer.ScrollToHorizontalOffset(diagram.ViewportHorizontalOffset);
            DiagramScrollViewer.ScrollToVerticalOffset(diagram.ViewportVerticalOffset);
        }), DispatcherPriority.ContextIdle);
    }

    private async Task LoadRelationalDiagramAsync(string id)
    {
        using var operation = BeginRelationalStateOperation();
        using var request = BeginRelationalSelection(operation.Token);
        var (generation, ct) = request;
        string raw = DiagramDocumentService.IsDiagramDocumentPath(id) ? DiagramDocumentService.GetDiagramId(id) : id;
        var chosen = await FindRelationalDiagramAsync(raw, ct) ?? throw new KeyNotFoundException("The selected diagram is missing; the canvas was not replaced.");
        await LoadRelationalDiagramCoreAsync(chosen, generation, ct);
    }
    private async Task LoadRelationalDiagramAsync(DiagramSummary chosen, CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        using var operation = BeginRelationalStateOperation(cancellationToken);
        using var request = BeginRelationalSelection(cancellationToken);
        var (generation, ct) = request;
        await LoadRelationalDiagramCoreAsync(chosen, generation, ct);
    }
    private async Task LoadRelationalDiagramCoreAsync(DiagramSummary chosen, long generation, CancellationToken ct)
    {
        await WaitRelationalSelectedOwnerIdleAsync(ct);
        RequireRelationalSelection(generation, ct);
        if (_isPersistenceHydrated && !await ConfirmRelationalStateSwitchAsync(ct)) return;
        RequireRelationalSelection(generation, ct);
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
        RequireRelationalSelection(generation, ct);
        var edit = RequireRelationalLoad(await RelationalStateRuntime.State.LoadDiagramAsync(chosen, ct), "diagram");
        try
        {
            var prepared = await PrepareRelationalDiagramAsync(edit.Snapshot(), ct); RequireRelationalSelection(generation, ct);
            ApplyRelationalDiagram(prepared); var previous = _relationalDiagramEdit; _relationalDiagramEdit = edit;
            _relationalSavedDiagram = StateCopies.Diagram(prepared.State.Document, new(RelationalRuntimeStateLimits));
            _relationalActiveDiagramRevisionKey = chosen.RevisionKey;
            if (previous != null) await previous.DisposeAsync(); StatusText = "Loaded diagram '" + chosen.Name + "'.";
        }
        catch { if (!ReferenceEquals(_relationalDiagramEdit, edit)) await edit.DisposeAsync(); throw; }
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private DiagramState CaptureRelationalDiagram(string? id = null, string? name = null)
    {
        Dispatcher.VerifyAccess(); CommitDiagramObjectTextEdits(); CommitMetadataEditorChanges();
        var document = new DiagramDocument
        {
            DiagramId = id ?? _activeDiagramId ?? _relationalDiagramHeader?.Id ?? string.Empty,
            Name = name ?? CurrentDiagramName, CreatedAtUtc = id != null && id != _relationalDiagramHeader?.Id ? DateTimeOffset.UtcNow : _relationalDiagramHeader?.Created ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow, CanvasZoom = _diagramCanvasZoom,
            ViewportHorizontalOffset = DiagramScrollViewer.HorizontalOffset, ViewportVerticalOffset = DiagramScrollViewer.VerticalOffset,
            Workflows = _currentDiagramWorkflows
        };
        var pasted = new Dictionary<int, byte[]>(); var fallback = new Dictionary<int, PastedImageFallback>();
        foreach (var control in DiagramCanvas.Children.OfType<FrameworkElement>().Where(IsDiagramObject)
            .OrderBy(Panel.GetZIndex).ThenBy(c => DiagramCanvas.Children.IndexOf(c)))
        {
            var rawSnapshot = CreateDiagramObjectSnapshot(control) ?? throw new InvalidDataException("A diagram object could not be captured; saving was cancelled.");
            var snapshot = PreserveRelationalObjectSnapshot(control, rawSnapshot);
            AttachRelationalSnapshotImage(control, snapshot); int ordinal = document.Objects.Count; document.Objects.Add(snapshot);
            if (snapshot.ObjectType != DiagramObjectType.Image || string.IsNullOrWhiteSpace(snapshot.PastedImageFileName)) continue;
            var payload = FindRelationalImagePayload(snapshot) ?? throw new InvalidDataException("A pasted image has no selected payload. Saving was cancelled rather than reading its filename.");
            if (payload.PastedBytes != null) pasted.Add(ordinal, payload.PastedBytes);
            else if (payload.Fallback != null) fallback.Add(ordinal, payload.Fallback);
            else throw new InvalidDataException("A pasted image has no explicit preserved resolution.");
        }
        var copy = StateCopies.Diagram(document, new(RelationalRuntimeStateLimits));
        for (int i = 0; i < document.Objects.Count; i++)
        {
            if (_relationalSnapshotOccurrences.TryGetValue(document.Objects[i], out var occurrence)) _relationalSnapshotOccurrences.Add(copy.Objects[i], occurrence);
            if (_relationalSnapshotImages.TryGetValue(document.Objects[i], out var image)) _relationalSnapshotImages.Add(copy.Objects[i], image);
        }
        return new(copy, pasted, fallback);
    }

    private async Task SaveRelationalDiagramAsync(CancellationToken ct = default, string? name = null)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        long generation = _relationalSelectionGeneration;
        var edit = _relationalDiagramEdit ?? throw new InvalidOperationException("No current diagram owner was successfully loaded. Use an explicit create/save-copy command for a draft or saved workbench diagram.");
        var scopeEdit = _relationalScopeEdit ?? throw new InvalidOperationException("Select a successfully loaded scope before saving a diagram.");
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
            RequireRelationalSelection(generation, ct);
            if (!ReferenceEquals(edit, _relationalDiagramEdit) || !ReferenceEquals(scopeEdit, _relationalScopeEdit))
                throw new OperationCanceledException("The selected save owner changed.");
            var captured = CaptureRelationalDiagram(name: name);
            if (_relationalDiagramDraft || !string.Equals(captured.Document.DiagramId, edit.Snapshot().Document.DiagramId, StringComparison.Ordinal))
                throw new InvalidOperationException("This diagram is not the loaded owner. Save-copy must explicitly create a new identity.");
            if (edit.Status is StateEditStatus.Conflict or StateEditStatus.OutcomeUnknown)
                throw new InvalidOperationException("Recover or reload the selected diagram before saving again.");
            var scope = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits));
            var expectedScope = scopeEdit.ExpectedToken;
            var resolved = await RelationalStateRuntime.StateStore.ReadRuntimeScopeTargetsAsync(expectedScope, ct);
            RequireRelationalSelection(generation, ct);
            var matches = resolved.Where(r => r.DiagramKey == edit.SubjectKey && r.ResourceOrdinal < scope.Resources.Count &&
                scope.Resources[r.ResourceOrdinal].Kind == ResourceKind.Diagram &&
                string.Equals(scope.Resources[r.ResourceOrdinal].Path, captured.Document.DiagramId, StringComparison.OrdinalIgnoreCase)).ToArray();
            int ordinal;
            if (matches.Length == 0)
            {
                ordinal = scope.Resources.Count;
                scope.Resources.Add(new() { Kind = ResourceKind.Diagram, Path = captured.Document.DiagramId, DisplayNameOverride = captured.Document.Name });
            }
            else ordinal = matches[0].ResourceOrdinal;
            RuntimeDiagramPublication? publication = null;
            var targeted = new StateEditSession<DiagramState>("Diagram", edit.SubjectKey, new(edit.Snapshot(), edit.ExpectedToken),
                v => new(StateCopies.Diagram(v.Document, new(RelationalRuntimeStateLimits)),
                    v.PastedImages.ToDictionary(p => p.Key, p => p.Value.ToArray()), v.PastedImageFallbacks),
                async (v, expected, operation, token) =>
                {
                    publication = await RelationalStateRuntime.StateStore.SaveDiagramInScopeAsync(v, scope, expected, expectedScope, ordinal, operation, token);
                    return publication.Diagram;
                }, token => RelationalStateRuntime.StateStore.ReadDiagramTokenAsync(edit.SubjectKey, token));
            _relationalDiagramEdit = targeted; await edit.DisposeAsync();
            targeted.Replace(captured);
            try { await targeted.SaveAsync(ct); }
            catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
            if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(targeted, _relationalDiagramEdit)) return;
            _relationalSavedDiagram = StateCopies.Diagram(captured.Document, new(RelationalRuntimeStateLimits));
            _relationalDiagramHeader = new(captured.Document.DiagramId, captured.Document.Name, captured.Document.CreatedAtUtc);
            SetCurrentDiagramIdentity(captured.Document.DiagramId, captured.Document.Name);
            await BindPublishedRelationalDiagramRevisionAsync(targeted.ExpectedToken, captured);
            if (ReferenceEquals(scopeEdit, _relationalScopeEdit))
            {
                var latestScope = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits));
                if (matches.Length == 0) latestScope.Resources.Add(scope.Resources[ordinal]);
                _relationalScopeEdit = NewRelationalScopeEdit(scope, publication!.Scope);
                SetActiveScope(latestScope);
                if (!SameRelationalScope(scope, latestScope)) _relationalScopeEdit.Replace(latestScope);
                await scopeEdit.DisposeAsync();
            }
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task CreateRelationalDiagramAsync(string id, string name, CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        long generation = _relationalSelectionGeneration;
        var scopeEdit = _relationalScopeEdit ?? throw new InvalidOperationException("Select a successfully loaded scope before creating a diagram.");
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
            RequireRelationalSelection(generation, ct);
            if (!ReferenceEquals(scopeEdit, _relationalScopeEdit)) throw new OperationCanceledException("The selected scope changed.");
            var captured = CaptureRelationalDiagram(id, name); var scope = StateCopies.Scope(_activeScope!, new(RelationalRuntimeStateLimits));
            scope.Resources.Add(new() { Kind = ResourceKind.Diagram, Path = id, DisplayNameOverride = name });
            var catalogue = await RelationalStateRuntime.StateStore.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Diagrams, ct);
            RequireRelationalSelection(generation, ct);
            RuntimeDiagramPublication published;
            try { published = await RelationalStateRuntime.StateStore.CreateDiagramInScopeAsync(captured, scope, scopeEdit.ExpectedToken, catalogue, Guid.NewGuid(), ct); }
            catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
            if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(scopeEdit, _relationalScopeEdit)) return;
            var newScope = NewRelationalScopeEdit(scope, published.Scope);
            // Do not replace mutable controls with the captured graph after an asynchronous publication.
            _activeScope!.Resources.Add(scope.Resources[^1]);
            if (!SameRelationalScope(scope, _activeScope)) newScope.Replace(_activeScope);
            _relationalScopeEdit = newScope; await scopeEdit.DisposeAsync();
            var old = _relationalDiagramEdit; _relationalDiagramEdit = NewRelationalDiagramEdit(captured, published.Diagram);
            SetCurrentDiagramIdentity(id, name); _relationalDiagramHeader = new(id, name, captured.Document.CreatedAtUtc); _relationalDiagramDraft = false;
            _relationalSavedDiagram = StateCopies.Diagram(captured.Document, new(RelationalRuntimeStateLimits));
            await BindPublishedRelationalDiagramRevisionAsync(published.Diagram, captured);
            if (old != null) await old.DisposeAsync();
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private StateEditSession<Scope> NewRelationalScopeEdit(Scope value, StateToken token) => new("Scope", token.Key, new(value, token),
        v => StateCopies.Scope(v, new(RelationalRuntimeStateLimits)), RelationalStateRuntime.StateStore.SaveScopeAsync,
        ct => RelationalStateRuntime.StateStore.ReadScopeTokenAsync(token.Key, ct));
    private StateEditSession<DiagramState> NewRelationalDiagramEdit(DiagramState value, StateToken token) => new("Diagram", token.Key, new(value, token),
        v => new(StateCopies.Diagram(v.Document, new(RelationalRuntimeStateLimits)),
            v.PastedImages.ToDictionary(p => p.Key, p => p.Value.ToArray()), v.PastedImageFallbacks?.ToDictionary(p => p.Key, p => new PastedImageFallback(p.Value.Resolution, p.Value.Bytes.ToArray()))),
        RelationalStateRuntime.StateStore.SaveDiagramAsync, ct => RelationalStateRuntime.StateStore.ReadDiagramTokenAsync(token.Key, ct));

    private WorkbenchAggregate CaptureRelationalWorkbench(WorkbenchState? baseline, bool defaultForScope)
    {
        Dispatcher.VerifyAccess(); CaptureViewportState();
        bool hasDiagram = !string.IsNullOrWhiteSpace(_activeDiagramId) || DiagramCanvas.Children.OfType<FrameworkElement>().Any(IsDiagramObject) || _currentDiagramWorkflows.Count > 0;
        var diagram = hasDiagram ? CaptureRelationalDiagram() : null;
        var model = new WorkbenchState
        {
            WorkbenchId = baseline?.WorkbenchId ?? Guid.NewGuid().ToString("N"), Name = baseline?.Name ?? _activeScope?.Name ?? "Workbench",
            IsDefaultForScope = defaultForScope, CreatedAtUtc = baseline?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow, SavedAtUtc = DateTimeOffset.UtcNow,
            ScopeId = _activeScope?.ScopeId ?? baseline?.ScopeId ?? string.Empty, ScopeName = _activeScope?.Name ?? baseline?.ScopeName ?? "No scope",
            IsCodeViewVisible = CodeViewToggle.IsChecked == true, IsDiagramViewVisible = DiagramViewToggle.IsChecked == true,
            ActiveWorkspaceView = _activeWorkspaceView.ToString(), WorkspaceSplitOrientation = _workspaceSplitOrientation.ToString(),
            CodeViewMode = _codeViewMode.ToString(), PinnedExplorerDetailTab = GetPinnedExplorerDetailTabKey(),
            ReferenceConnectionLinesEnabled = _referenceConnectionLinesEnabled, CodeCanvasZoom = _canvasZoom,
            CodeViewportHorizontalOffset = WorkspaceScrollViewer.HorizontalOffset, CodeViewportVerticalOffset = WorkspaceScrollViewer.VerticalOffset,
            UnloadedResourceIds = GetUnloadedResourceIds().ToList(), OpenDocuments = CaptureRelationalWindows(),
            ReferenceConnectionLines = CaptureRelationalConnections(), ActiveDocumentPath = GetActiveDocumentPath() ?? baseline?.ActiveDocumentPath ?? string.Empty,
            ActiveDiagramId = _activeDiagramId ?? string.Empty, ActiveDiagramName = CurrentDiagramName, ActiveDiagramSnapshot = diagram?.Document,
            IsDiagramLocked = _isDiagramLocked, DiagramCanvasZoom = _diagramCanvasZoom,
            DiagramViewportHorizontalOffset = DiagramScrollViewer.HorizontalOffset, DiagramViewportVerticalOffset = DiagramScrollViewer.VerticalOffset
        };
        return new(model, diagram?.PastedImages ?? new Dictionary<int, byte[]>(), diagram?.PastedImageFallbacks);
    }

    private List<ReferenceConnectionLineState> CaptureRelationalConnections()
    {
        var current = _referenceConnectionLines.Select(c => c.State).ToList();
        // Preserve unavailable endpoints. Explicit connection removal must call ForgetRelationalConnectionState.
        var result = _relationalRetainedConnections.Select(c => StateMaps.Connection.Copy(c, new(RelationalRuntimeStateLimits))).ToList();
        foreach (var line in current)
        {
            int index = _relationalConnectionOccurrences.TryGetValue(line, out var original) ? _relationalRetainedConnections.FindIndex(r => ReferenceEquals(r, original)) : -1;
            var copy = StateMaps.Connection.Copy(line, new(RelationalRuntimeStateLimits));
            if (original != null) { copy.SourceFilePath = original.SourceFilePath; copy.TargetFilePath = original.TargetFilePath; }
            if (index < 0) result.Add(copy); else result[index] = copy;
        }
        return result;
    }
    private void ForgetRelationalConnectionState(ReferenceConnectionLineState line)
    {
        var original = _relationalConnectionOccurrences.TryGetValue(line, out var found) ? found : line;
        _relationalRetainedConnections.RemoveAll(r => ReferenceEquals(r, original));
    }

    private void RestoreRelationalReferenceLines(IEnumerable<ReferenceConnectionLineState> states)
    {
        ClearReferenceConnectionLines();
        if (!_referenceConnectionLinesEnabled) return;
        FloatingCodeWindow? Endpoint(string raw, long? snapshot, long? resource, SavedDocumentTargetState state)
        {
            if (state is SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous) return null;
            var candidates = _openWindows.Values.Distinct().Where(w => state == SavedDocumentTargetState.Resolved
                ? snapshot.HasValue && resource.HasValue && w.State.BoundSnapshotKey == snapshot && w.State.BoundResourceKey == resource
                : string.Equals(w.State.FilePath, raw, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        foreach (var state in states)
        {
            var source = Endpoint(state.SourceFilePath, state.SourceBoundSnapshotKey, state.SourceBoundResourceKey, state.SourceTargetState);
            var target = Endpoint(state.TargetFilePath, state.TargetBoundSnapshotKey, state.TargetBoundResourceKey, state.TargetTargetState);
            if (source == null || target == null || ReferenceEquals(source, target)) continue;
            var display = StateMaps.Connection.Copy(state, new(RelationalRuntimeStateLimits));
            display.SourceFilePath = source.State.FilePath; display.TargetFilePath = target.State.FilePath;
            AddReferenceConnectionLine(display);
            _relationalConnectionOccurrences.Add(_referenceConnectionLines[^1].State, state);
        }
        RefreshReferenceConnectionLines();
    }

    private async Task SaveRelationalWorkbenchAsync(CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        long generation = _relationalSelectionGeneration;
        var scope = _relationalScopeEdit ?? throw new InvalidOperationException("Select a successfully loaded scope before saving a default workbench.");
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
            RequireRelationalSelection(generation, ct);
            if (!ReferenceEquals(scope, _relationalScopeEdit)) throw new OperationCanceledException("The selected scope changed.");
            var target = await RelationalStateRuntime.StateStore.ReadDefaultWorkbenchTargetAsync(scope.SubjectKey, _activeScope!.ScopeId, ct);
            StateEditSession<WorkbenchAggregate>? edit = target.Summary == null ? null :
                RequireRelationalLoad(await RelationalStateRuntime.State.LoadWorkbenchAsync(target.Summary, ct), "default workbench");
            try
            {
                RequireRelationalSelection(generation, ct);
                if (!ReferenceEquals(scope, _relationalScopeEdit)) throw new OperationCanceledException("The selected scope changed.");
                var captured = CaptureRelationalWorkbench(edit?.Snapshot().Workbench, defaultForScope: true);
                captured.Workbench.Name = GetDefaultWorkbenchName(captured.Workbench);
                StateToken saved;
                if (edit == null)
                {
                    try { saved = await RelationalStateRuntime.StateStore.CreateWorkbenchExpectedAsync(captured, target.Catalogue, scope.ExpectedToken, Guid.NewGuid(), ct); }
                    catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
                }
                else
                {
                    edit.Replace(captured);
                    try { saved = (await edit.SaveAsync(ct)).Token; }
                    catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
                }
                if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(scope, _relationalScopeEdit)) return;
                var old = _relationalWorkbenchEdit; _relationalWorkbenchEdit = NewRelationalWorkbenchEdit(captured, saved);
                if (old != null) await old.DisposeAsync();
                _relationalDefaultWorkbenchScope = scope.SubjectKey;
                _relationalDefaultWorkbenchComparison = CreateRelationalWorkbenchComparisonKey(captured.Workbench);
                RememberRelationalScopeSwitchWorkbenchBaseline(captured.Workbench);
                await RefreshRelationalWorkbenchCatalogueAsync(ct);
                if (!_relationalStateClosing && generation == _relationalSelectionGeneration && _relationalDiagramEdit == null)
                {
                    _relationalActiveDiagramRevisionKey = (WorkbenchSelector.SelectedItem as WorkbenchSummary)?.EmbeddedDiagramRevisionKey;
                    if (captured.Workbench.ActiveDiagramSnapshot != null) BindPublishedRelationalOccurrences(captured.Workbench.ActiveDiagramSnapshot);
                }
            }
            finally { if (edit != null) await edit.DisposeAsync(); }
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task SaveRelationalWorkbenchCopyAsync(string name, CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        long generation = _relationalSelectionGeneration;
        var scope = _relationalScopeEdit ?? throw new InvalidOperationException("Select a successfully loaded scope before creating a workbench.");
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
        RequireRelationalSelection(generation, ct);
        if (!ReferenceEquals(scope, _relationalScopeEdit)) throw new OperationCanceledException("The selected scope changed.");
        var captured = CaptureRelationalWorkbench(null, defaultForScope: false); captured.Workbench.Name = name;
        var catalogue = await RelationalStateRuntime.StateStore.ReadRuntimeCatalogueTokenAsync(RuntimeStateCatalogue.Workbenches, ct);
        RequireRelationalSelection(generation, ct);
        StateToken saved;
        try { saved = await RelationalStateRuntime.StateStore.CreateWorkbenchExpectedAsync(captured, catalogue, scope.ExpectedToken, Guid.NewGuid(), ct); }
        catch (Exception error) when (error is not StateConflictException) { _relationalStateCommandOutcomeUnknown = true; throw; }
        if (_relationalStateClosing || generation != _relationalSelectionGeneration || !ReferenceEquals(scope, _relationalScopeEdit)) return;
        var old = _relationalWorkbenchEdit; _relationalWorkbenchEdit = NewRelationalWorkbenchEdit(captured, saved); if (old != null) await old.DisposeAsync();
        RememberRelationalScopeSwitchWorkbenchBaseline(captured.Workbench);
        await RefreshRelationalWorkbenchCatalogueAsync(ct);
        if (!_relationalStateClosing && generation == _relationalSelectionGeneration && _relationalDiagramEdit == null)
        {
            _relationalActiveDiagramRevisionKey = (WorkbenchSelector.SelectedItem as WorkbenchSummary)?.EmbeddedDiagramRevisionKey;
            if (captured.Workbench.ActiveDiagramSnapshot != null) BindPublishedRelationalOccurrences(captured.Workbench.ActiveDiagramSnapshot);
        }
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private async Task LoadRelationalWorkbenchAsync(WorkbenchSummary summary, CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        using var operation = BeginRelationalStateOperation(cancellationToken);
        using var request = BeginRelationalSelection(operation.Token);
        var (generation, ct) = request;
        await WaitRelationalSelectedOwnerIdleAsync(ct);
        RequireRelationalSelection(generation, ct);
        if (_isPersistenceHydrated && !await ConfirmRelationalStateSwitchAsync(ct)) return;
        RequireRelationalSelection(generation, ct);
        await _relationalOwnerCommands.WaitAsync(ct);
        try
        {
        RequireRelationalSelection(generation, ct);
        var edit = RequireRelationalLoad(await RelationalStateRuntime.State.LoadWorkbenchAsync(summary, ct), "workbench");
        PreparedRelationalScope? preparedScope = null;
        try
        {
            var aggregate = edit.Snapshot(); var model = aggregate.Workbench;
            var scopeTarget = await RelationalStateRuntime.StateStore.ReadWorkbenchScopeTargetAsync(summary.Token.Key, ct);
            if (scopeTarget == null || !SameRelationalOwner(scopeTarget.Token, edit.ExpectedToken)) throw new StateConflictException("workbench scope target");
            if (scopeTarget.Resolution == StateLinkResolution.Ambiguous) throw new InvalidDataException("The saved workbench's scope ID is ambiguous. No workspace was replaced.");
            if (scopeTarget.ScopeKey.HasValue)
            {
                var scopeLoad = RequireRelationalLoad(await RelationalStateRuntime.State.LoadScopeAsync(scopeTarget.ScopeKey.Value, ct), "workbench scope");
                await using (scopeLoad)
                {
                    var value = scopeLoad.Snapshot(); preparedScope = await PrepareRelationalScopeAsync(new(scopeLoad.ExpectedToken, 0, value.ScopeId, value.Name), ct,
                        model.UnloadedResourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
                }
            }
            else if (!string.IsNullOrWhiteSpace(model.ScopeId) && scopeTarget.Resolution == StateLinkResolution.None)
            {
                var chosen = await FindRelationalScopeAsync(model.ScopeId, ct); if (chosen != null) preparedScope = await PrepareRelationalScopeAsync(chosen, ct,
                    model.UnloadedResourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }
            PreparedRelationalDiagram? diagram = model.ActiveDiagramSnapshot == null ? null :
                await PrepareRelationalDiagramAsync(new(model.ActiveDiagramSnapshot, aggregate.PastedImages, aggregate.PastedImageFallbacks), ct);
            RequireRelationalSelection(generation, ct);
            _relationalLayoutRestoring = true;
            try
            {
                CloseAllOpenWindows(); ClearLoadedDiagram(); var previousScope = _relationalScopeEdit;
                if (preparedScope != null) ApplyRelationalScope(preparedScope);
                else
                {
                    SetActiveScope(null); RootNodes.Clear(); _relationalScopeEdit = null; _relationalExplorerScope = null; _relationalReferenceCatalogue = null;
                    BeginRelationalExplorerContext(); InvalidateRelationalDocumentContext();
                    InitializeRelationalScopeResourceCommands();
                }
                if (previousScope != null) await previousScope.DisposeAsync();
                RequireRelationalSelection(generation, ct);
                _workspaceState = CreateWorkspaceState(model); BeginRelationalLayout(_workspaceState.OpenDocuments);
                _relationalRetainedConnections.Clear(); _relationalRetainedConnections.AddRange(model.ReferenceConnectionLines);
                _canvasZoom = NormalizeCanvasZoom(model.CodeCanvasZoom); ApplyCanvasZoom(); RestorePinnedExplorerDetailTab(model.PinnedExplorerDetailTab);
                _workspaceSplitOrientation = ParseWorkspaceSplitOrientation(model.WorkspaceSplitOrientation); SetCodeViewMode(ParseCodeViewMode(model.CodeViewMode));
                SetReferenceConnectionLinesEnabled(model.ReferenceConnectionLinesEnabled, clearWhenDisabled: false); SetDiagramLockState(model.IsDiagramLocked, updateToggle: true);
                _relationalSavedDiagram = null;
                _relationalActiveDiagramRevisionKey = summary.EmbeddedDiagramRevisionKey;
                if (diagram != null) ApplyRelationalDiagram(diagram);
                else { _relationalDiagramHeader = null; _relationalDiagramDraft = false; _relationalControlImages = new(); _relationalControlOccurrences = new(); _relationalFilenameImages.Clear(); }
                _diagramCanvasZoom = NormalizeCanvasZoom(model.DiagramCanvasZoom); ApplyDiagramCanvasZoom();
                if (_relationalDiagramEdit != null) await _relationalDiagramEdit.DisposeAsync(); _relationalDiagramEdit = null;
                RequireRelationalSelection(generation, ct);
                SetWorkspaceViewVisibility(model.IsCodeViewVisible, model.IsDiagramViewVisible, ParseWorkspaceViewKind(model.ActiveWorkspaceView));
                var previous = _relationalWorkbenchEdit; _relationalWorkbenchEdit = edit; if (previous != null) await previous.DisposeAsync();
                RequireRelationalSelection(generation, ct);
                await RestoreRelationalDocumentsAsync(ct);
                RequireRelationalSelection(generation, ct);
                if (!string.IsNullOrWhiteSpace(model.ActiveDocumentPath)) SelectOpenDocument(model.ActiveDocumentPath);
                RestoreRelationalReferenceLines(model.ReferenceConnectionLines); UpdateEmptyWorkspaceHint();
                await Dispatcher.InvokeAsync(new Action(() =>
                {
                    RequireRelationalSelection(generation, ct);
                    RestoreViewport(); DiagramScrollViewer.ScrollToHorizontalOffset(model.DiagramViewportHorizontalOffset);
                    DiagramScrollViewer.ScrollToVerticalOffset(model.DiagramViewportVerticalOffset);
                    WorkspaceScrollViewer.UpdateLayout(); DiagramScrollViewer.UpdateLayout();
                }), DispatcherPriority.ContextIdle);
                RequireRelationalSelection(generation, ct);
                RememberRelationalScopeSwitchWorkbenchBaseline();
                if (model.IsDefaultForScope)
                {
                    _relationalDefaultWorkbenchScope = _relationalScopeEdit?.SubjectKey;
                    _relationalDefaultWorkbenchComparison = CreateRelationalWorkbenchComparisonKey(model);
                }
                StatusText = "Loaded Workbench '" + model.Name + "'.";
            }
            finally { _relationalLayoutRestoring = false; }
        }
        catch
        {
            if (!ReferenceEquals(_relationalWorkbenchEdit, edit)) await edit.DisposeAsync();
            if (preparedScope != null && !ReferenceEquals(_relationalScopeEdit, preparedScope.Edit)) await preparedScope.Edit.DisposeAsync();
            throw;
        }
        }
        finally { _relationalOwnerCommands.Release(); }
    }

    private void BindRelationalWorkbenchPage(RuntimeWorkbenchPage page)
    {
        _isUpdatingWorkbenchSelection = true;
        try
        {
            long? selected = (WorkbenchSelector.SelectedItem as WorkbenchSummary)?.Token.Key ?? _relationalWorkbenchEdit?.SubjectKey;
            WorkbenchSelector.ItemsSource = page.Items.Cast<object>().Append(new RelationalWorkbenchCatalogueAction("Browse all Workbenches...")).ToArray();
            WorkbenchSelector.DisplayMemberPath = nameof(WorkbenchSummary.Name); _relationalWorkbenchNext = page.Next;
            WorkbenchSelector.SelectedItem = page.Items.FirstOrDefault(w => w.Token.Key == selected);
        }
        finally { _isUpdatingWorkbenchSelection = false; UpdateWorkbenchCommandState(); }
    }
    private async Task RefreshRelationalWorkbenchCatalogueAsync(CancellationToken ct = default)
    {
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        var page = await RelationalStateRuntime.StateStore.ListRecentWorkbenchesAsync(100, ct: ct);
        ct.ThrowIfCancellationRequested(); if (!_relationalStateClosing) BindRelationalWorkbenchPage(page);
    }
    private async Task ShowRelationalWorkbenchesAsync()
    {
        using var operation = BeginRelationalStateOperation();
        var picker = new RelationalWorkbenchPickerWindow(RelationalStateRuntime.StateStore) { Owner = this };
        picker.LinkOwnerLifetime(operation.Token);
        bool? result;
        try { result = picker.ShowDialog(); } finally { await picker.DrainQueriesAsync(); }
        operation.Token.ThrowIfCancellationRequested();
        if (result == true && picker.SelectedSummary != null) await LoadRelationalWorkbenchAsync(picker.SelectedSummary);
    }

    private async Task<bool> ConfirmRelationalStateSwitchAsync(CancellationToken ct = default)
    {
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        if (!_isPersistenceHydrated) return true;
        if (!await ConfirmRelationalGridEditsBeforeStateSwitchAsync(ct)) return false;
        bool diagramDirty = HasRelationalUnsavedDiagramChanges();
        bool workbenchDirty = HasRelationalScopeSwitchWorkbenchChanges();
        bool scopeDirty = HasRelationalScopeSwitchScopeChanges();
        ct.ThrowIfCancellationRequested();
        if (!diagramDirty && !workbenchDirty && !scopeDirty) return true;
        var choice = RelationalStateSwitchConfirmation?.Invoke() ?? MessageBox.Show(this, "Save changes before replacing the current workspace?", "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.No) return true;
        if (scopeDirty) await SaveRelationalScopeAsync(ct);
        if (diagramDirty && !await SaveDiagramFromUiAsync()) return false;
        if (workbenchDirty) await SaveRelationalWorkbenchAsync(ct); return true;
    }

    private async Task SaveRelationalApplicationStateAsync(CancellationToken ct = default)
    {
        RequireRelationalWrite();
        using var operation = BeginRelationalStateOperation(ct); ct = operation.Token;
        await SaveRelationalScopeAsync(ct); await SaveRelationalSettingsAsync(ct); await SaveRelationalWorkspaceAsync(ct);
        // Do not recapture unsaved diagrams/workbenches here: shutdown's No choice must remain effective.
        if (_relationalSelectionEdit?.IsDirty == true) await _relationalSelectionEdit.SaveAsync(ct);
    }

    private static string CreateRelationalWorkbenchComparisonKey(WorkbenchState workbench) =>
        CreateWorkbenchComparisonKey(workbench) + "|" + System.Text.Json.JsonSerializer.Serialize(new
        {
            Windows = workbench.OpenDocuments.Select(w => new { w.BoundSnapshotKey, w.BoundResourceKey, w.TargetState }).ToArray(),
            Connections = workbench.ReferenceConnectionLines.Select(c => new
            {
                c.SourceBoundSnapshotKey, c.SourceBoundResourceKey, c.SourceTargetState,
                c.TargetBoundSnapshotKey, c.TargetBoundResourceKey, c.TargetTargetState
            }).ToArray()
        });

    private static bool SameRelationalOwner(StateToken a, StateToken b) => a.Key == b.Key && a.Epoch == b.Epoch &&
        a.PublicationId == b.PublicationId && a.Version.AsSpan().SequenceEqual(b.Version);
}

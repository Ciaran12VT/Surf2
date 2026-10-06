using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalGrid;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public partial class MainWindow
{
    private RelationalDocumentService? _relationalDocuments;
    private SingleFlight<RelationalDocumentIdentity, bool>? _relationalDocumentFlights;
    private QueryLifetime? _relationalPreviewLifetime;
    private QueryLifetime? _relationalNavigationLifetime;
    private long _relationalDocumentContextGeneration;
    private readonly Dictionary<RelationalDocumentIdentity, QueryLifetime> _relationalDocumentLifetimes = [];
    private readonly Dictionary<RelationalDocumentIdentity, FloatingCodeWindow> _relationalDocumentWindows = [];
    private readonly Dictionary<RelationalDocumentIdentity, DocumentTextKey> _relationalOpenTextKeys = [];
    private readonly Dictionary<OpenDocumentState, RelationalDocumentIdentity> _relationalGridStates = [];
    private readonly Dictionary<FloatingCodeWindow, QueryLifetime> _relationalMenuLifetimes = [];

    // On successful return the presenter owns the source until LOGICAL close, not WPF Unloaded.
    // Parent supplies the paged spreadsheet adapter; absence is an explicit error, never eager CSV fallback.
    public Action<OpenDocumentState, IDataGridSource, FloatingCodeWindow?>? RelationalGridDocumentPresenter { get; set; }

    private void InitializeRelationalDocumentAccess()
    {
        if (_relational == null) throw new InvalidOperationException("Select a ready relational runtime first.");
        if (_relationalDocuments != null) throw new InvalidOperationException("Dispose the previous document access before reconnecting.");
        InitializeRelationalComparisons();
        _relationalDocuments = new(_relational);
        _relationalDocumentFlights = new(8, 64);
        _relationalPreviewLifetime = new(_relational.Session.Epoch, _relationalExplorerScope?.Context.ScopeKey);
        _relationalNavigationLifetime = new(_relational.Session.Epoch, _relationalExplorerScope?.Context.ScopeKey);
        _relationalDocumentOpener = (state, ct) => TryOpenRelationalFileCoreAsync(state.FilePath, state, null, null, null, null, true, ct);
    }

    // Parent calls immediately on scope/membership/unloaded-state changes and before layout replacement.
    private void InvalidateRelationalDocumentContext()
    {
        InvalidateRelationalComparisons();
        InvalidateRelationalAuxiliaryQueries();
        InvalidateRelationalSnapshotWorkflows();
        if (_relational == null) return;
        long? scope = _relationalExplorerScope?.Context.ScopeKey;
        _relationalDocumentContextGeneration++;
        _relationalPreviewLifetime?.ChangeContext(_relational.Session.Epoch, scope);
        _relationalNavigationLifetime?.ChangeContext(_relational.Session.Epoch, scope);
        foreach (var owner in _relationalDocumentLifetimes.Values) owner.ChangeContext(_relational.Session.Epoch, scope);
        foreach (var owner in _relationalMenuLifetimes.Values) owner.ChangeContext(_relational.Session.Epoch, scope);
    }

    // Parent calls from the logical close paths. Reparenting does not release document ownership.
    private void ReleaseRelationalDocumentAccess(OpenDocumentState state)
    {
        _relationalPreviewLifetime?.InvalidateContext();
        _relationalNavigationLifetime?.InvalidateContext();
        if (_relationalGridStates.Remove(state, out var gridIdentity) && _relationalDocumentLifetimes.Remove(gridIdentity, out var gridOwner)) gridOwner.Dispose();
        foreach (var pair in _relationalDocumentWindows.Where(p => ReferenceEquals(p.Value.State, state)).ToArray())
        {
            _relationalDocumentWindows.Remove(pair.Key);
            _relationalOpenTextKeys.Remove(pair.Key);
            if (_relationalDocumentLifetimes.Remove(pair.Key, out var owner)) owner.Dispose();
            if (_relationalMenuLifetimes.Remove(pair.Value, out var menu)) menu.Dispose();
        }
        if (_relational != null && state.BoundSnapshotKey is > 0 && state.BoundResourceKey is > 0)
        {
            bool data = DatabaseDocumentService.TryParseDocumentPath(state.FilePath, out var reference) && reference.DocumentType == "table-data";
            var key = new SnapshotDocumentIdentity(_relational.Session.Epoch, state.BoundSnapshotKey.Value, state.BoundResourceKey.Value, data);
            if (_relationalDocumentLifetimes.Remove(key, out var owner)) owner.Dispose();
        }
    }

    private async Task DisposeRelationalDocumentAccessAsync()
    {
        _relationalDocumentContextGeneration++;
        _relationalDocumentOpener = null;
        var preview = _relationalPreviewLifetime; _relationalPreviewLifetime = null;
        var navigation = _relationalNavigationLifetime; _relationalNavigationLifetime = null;
        var flights = _relationalDocumentFlights; _relationalDocumentFlights = null;
        var documents = _relationalDocuments; _relationalDocuments = null;
        var owners = _relationalDocumentLifetimes.Values.Concat(_relationalMenuLifetimes.Values).ToArray();
        _relationalDocumentLifetimes.Clear(); _relationalMenuLifetimes.Clear();
        _relationalDocumentWindows.Clear(); _relationalOpenTextKeys.Clear(); _relationalGridStates.Clear();
        preview?.Dispose(); navigation?.Dispose(); foreach (var owner in owners) owner.Dispose();
        if (flights != null) await flights.DisposeAsync();
        if (preview != null) await preview.DisposeAsync();
        if (navigation != null) await navigation.DisposeAsync();
        foreach (var owner in owners) await owner.DisposeAsync();
        if (documents != null) await documents.DisposeAsync();
    }

    private Task<bool> TryOpenRelationalFileAsync(string filePath, OpenDocumentState? existingState = null,
        int? targetLine = null, int? targetColumn = null, ReferenceEntity? targetReference = null,
        FloatingCodeWindow? sourceWindow = null, bool suppressHistory = false, CancellationToken ct = default,
        RelationalDocumentAddress? preferredResolvedAddress = null) =>
        TryOpenRelationalFileCoreAsync(filePath, existingState, targetLine, targetColumn, targetReference, sourceWindow, suppressHistory,
            ct, preferredResolvedAddress: preferredResolvedAddress);

    private async Task<RelationalDocumentAddress?> TryOpenRelationalExplorerNodeAsync(ExplorerNodeSummary selected,
        CancellationToken ct = default)
    {
        if (_relational == null || _relationalExplorerScope == null) return null;
        var runtime = _relational; var scope = _relationalExplorerScope;
        var documents = _relationalDocuments ?? throw new InvalidOperationException("Relational document access was not initialized.");
        long generation = _relationalDocumentContextGeneration;
        try
        {
            var address = await documents.ResolveExplorerNodeAsync(scope, selected, ct);
            if (!ReferenceEquals(_relational, runtime) || !ReferenceEquals(_relationalExplorerScope, scope) ||
                generation != _relationalDocumentContextGeneration) return null;
            await TryOpenRelationalFileAsync(address.DocumentPath, ct: ct, preferredResolvedAddress: address);
            return ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope) &&
                generation == _relationalDocumentContextGeneration &&
                (_openWindows.ContainsKey(address.DocumentPath) || _openSpreadsheetWindows.ContainsKey(address.DocumentPath)) ? address : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope) &&
                generation == _relationalDocumentContextGeneration)
                StatusText = "Could not open the selected explorer document: " + error.Message;
            return null;
        }
    }

    // Explorer activation supplies the selected keys, rather than guessing from a duplicate alias/name.
    private async Task<bool> TryOpenRelationalResourceAsync(long snapshotKey, long resourceKey, FloatingCodeWindow? sourceWindow = null,
        int? targetLine = null, int? targetColumn = null, CancellationToken ct = default)
    {
        if (_relational == null) return false;
        var documents = _relationalDocuments ?? throw new InvalidOperationException("Relational document access was not initialized.");
        var scope = _relationalExplorerScope;
        var address = await documents.ResolveResourceAsync(snapshotKey, resourceKey, scope, ct);
        if (!ReferenceEquals(_relationalExplorerScope, scope)) return true;
        return await TryOpenRelationalFileCoreAsync(address.DocumentPath, null, targetLine, targetColumn, null, sourceWindow, false, ct,
            preferredResolvedAddress: address);
    }

    private async Task<bool> TryOpenRelationalFileCoreAsync(string filePath, OpenDocumentState? existingState,
        int? targetLine, int? targetColumn, ReferenceEntity? targetReference, FloatingCodeWindow? sourceWindow,
        bool suppressHistory, CancellationToken ct, DocumentTextKey? expectedTextKey = null, IndexRequestContext? expectedIndex = null,
        RelationalDocumentAddress? preferredResolvedAddress = null)
    {
        if (_relational == null) return false;
        var runtime = _relational;
        var documents = _relationalDocuments ?? throw new InvalidOperationException("Relational document access was not initialized.");
        var flights = _relationalDocumentFlights ?? throw new InvalidOperationException("Relational document access is closing.");
        var scope = _relationalExplorerScope;
        long contextGeneration = _relationalDocumentContextGeneration;
        try
        {
            RelationalDocumentService.RejectUnresolvedSavedTarget(existingState);
            var address = preferredResolvedAddress ?? await documents.ResolveAsync(filePath, scope, existingState, ct);
            if (preferredResolvedAddress != null)
            {
                if (existingState?.TargetState == SavedDocumentTargetState.Resolved &&
                    (existingState.BoundSnapshotKey != address.Resource?.SnapshotKey || existingState.BoundResourceKey != address.Resource?.ResourceKey))
                    throw new DocumentTargetException(SavedDocumentTargetState.Ambiguous, "The prepared address does not match the saved document binding.");
                await documents.ValidateAddressAsync(address, scope, ct);
            }
            if (!ReferenceEquals(_relational, runtime) || !ReferenceEquals(_relationalExplorerScope, scope) || contextGeneration != _relationalDocumentContextGeneration) return true;
            if (!_relationalDocumentLifetimes.TryGetValue(address.Identity, out var owner))
                _relationalDocumentLifetimes.Add(address.Identity, owner = new(runtime.Session.Epoch, scope?.Context.ScopeKey));
            await flights.RunAsync(address.Identity, async flightToken =>
            {
                using var request = owner.BeginRequest();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, flightToken);
                var token = linked.Token;
                if (_relationalDocumentWindows.TryGetValue(address.Identity, out var opened))
                {
                    if (targetReference != null)
                    {
                        var current = await documents.DescribeTextAsync(address, token);
                        if (expectedTextKey != null && expectedTextKey != current.Key) throw new DocumentReferenceChangedException();
                        if (!_relationalOpenTextKeys.TryGetValue(address.Identity, out var resident) || resident != current.Key)
                            throw new DocumentReferenceChangedException();
                        await documents.ValidateTextPlanAsync(current, scope, token);
                        if (expectedIndex != null && !await runtime.Index.IsContextCurrentAsync(expectedIndex, token)) throw new IndexGenerationChangedException();
                    }
                    if (CanPublishRelationalDocument(runtime, scope, owner, request))
                        RevealRelationalDocument(opened, targetLine, targetColumn, targetReference, suppressHistory);
                    return true;
                }
                if (address.Identity is SnapshotDocumentIdentity { IsTableData: true } || IsSpreadsheetDocument(address.DocumentPath))
                {
                    var presenter = RelationalGridDocumentPresenter ?? throw new InvalidOperationException("The paged spreadsheet presenter is not connected.");
                    if (_openSpreadsheetWindows.TryGetValue(address.DocumentPath, out var grid))
                    {
                        if (CanPublishRelationalDocument(runtime, scope, owner, request))
                        { SetActiveCodeWindow(null, syncOpenTabsSelection: false); BringToFront(grid); RevealWindow(grid); }
                        return true;
                    }
                    IDataGridSource? source = await documents.OpenGridAsync(address, token);
                    try
                    {
                        await documents.ValidateAddressAsync(address, scope, token);
                        if (!CanPublishRelationalDocument(runtime, scope, owner, request)) return true;
                        bool restored = existingState != null;
                        var state = existingState ?? CreateNewDocumentState(address.DocumentPath, sourceWindow);
                        BindRelationalState(state, address);
                        presenter(state, source, restored ? null : sourceWindow);
                        source = null;
                        _relationalGridStates[state] = address.Identity;
                        // The presenter integrates the window/tab/handlers; new state is retained here.
                        if (!restored && !_workspaceState.OpenDocuments.Contains(state)) _workspaceState.OpenDocuments.Add(state);
                        return true;
                    }
                    finally { if (source != null) await source.DisposeAsync(); }
                }
                var plan = await documents.DescribeTextAsync(address, token);
                if (expectedTextKey != null && expectedTextKey != plan.Key) throw new DocumentReferenceChangedException();
                if (!CanPublishRelationalDocument(runtime, scope, owner, request)) return true;
                if (plan.EstimatedTextBytes >= documents.Limits.WarningBytes && MessageBox.Show(this,
                    $"This document may require at least {plan.EstimatedTextBytes / (1024 * 1024.0):N1} MiB of resident text, plus editor and syntax memory. Open it?",
                    "Large Document", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return true;
                token.ThrowIfCancellationRequested();
                var content = await documents.ReadTextAsync(plan, token);
                await documents.ValidateTextPlanAsync(plan, scope, token);
                if (expectedIndex != null && !await runtime.Index.IsContextCurrentAsync(expectedIndex, token)) throw new IndexGenerationChangedException();
                if (!CanPublishRelationalDocument(runtime, scope, owner, request)) return true;
                if (sourceWindow != null && !_openWindows.Values.Contains(sourceWindow)) return true;
                if (existingState != null) BindRelationalState(existingState, address);
                // This existing helper performs synchronous dispatcher publication and preserves every window handler.
                await OpenDocumentContentAsync(address.DocumentPath, content.Text, address.SyntaxPath, address.DisplayName,
                    existingState, targetLine, targetColumn, targetReference, sourceWindow, suppressHistory);
                if (_openWindows.TryGetValue(address.DocumentPath, out var window))
                {
                    BindRelationalState(window.State, address);
                    SetRelationalDocumentFilenameSeed(window, address, scope, contextGeneration);
                    _relationalDocumentWindows[address.Identity] = window;
                    _relationalOpenTextKeys[address.Identity] = plan.Key;
                }
                return true;
            }, ct);
            // A joined open still honors this caller's requested line without creating another window.
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope) &&
                contextGeneration == _relationalDocumentContextGeneration &&
                _relationalDocumentLifetimes.TryGetValue(address.Identity, out var activeOwner) && ReferenceEquals(activeOwner, owner) &&
                _relationalDocumentWindows.TryGetValue(address.Identity, out var active))
            {
                if (expectedTextKey != null && (!_relationalOpenTextKeys.TryGetValue(address.Identity, out var resident) || resident != expectedTextKey))
                    throw new DocumentReferenceChangedException();
                // Reopening the same identity through another alias reuses the window
                // and text, but its clipboard ancestry follows the clicked occurrence.
                SetRelationalDocumentFilenameSeed(active, address, scope, contextGeneration);
                RevealRelationalDocument(active, targetLine, targetColumn, targetReference, suppressHistory);
            }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope) && contextGeneration == _relationalDocumentContextGeneration)
                StatusText = "Could not open the selected document: " + ex.Message;
            // Handled failures MUST NOT fall through to eager legacy loading or alter a missing saved target.
            return true;
        }
    }

    private void SetRelationalDocumentFilenameSeed(FloatingCodeWindow window, RelationalDocumentAddress address,
        ExplorerScope? scope, long contextGeneration) =>
        window.ClipboardTextFileNameSeedProvider = () => ReferenceEquals(_relationalExplorerScope, scope) &&
            contextGeneration == _relationalDocumentContextGeneration ? address.TextFileNameSeed : null;

    private bool CanPublishRelationalDocument(Services.RelationalRuntime runtime, ExplorerScope? scope, QueryLifetime owner, QueryRequest request) =>
        ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope) && owner.IsCurrent(request.Stamp);

    private static void BindRelationalState(OpenDocumentState state, RelationalDocumentAddress address)
    {
        state.FilePath = address.DocumentPath; state.DisplayName = address.DisplayName;
        state.BoundSnapshotKey = address.Resource?.SnapshotKey; state.BoundResourceKey = address.Resource?.ResourceKey;
        state.TargetState = address.Resource == null ? SavedDocumentTargetState.External : SavedDocumentTargetState.Resolved;
    }

    private void RevealRelationalDocument(FloatingCodeWindow window, int? line, int? column, ReferenceEntity? reference, bool suppressHistory)
    {
        ActivateCodeWindow(window); RevealWindow(window);
        int? target = reference?.LineNumber ?? line;
        if (target.HasValue) ScrollWindowToPositionAndReveal(window, target.Value, reference?.ColumnNumber ?? column ?? 1,
            selectLine: reference != null, suppressCursorPositionChanged: suppressHistory);
    }

    private async Task<ImmutableArray<SymbolSummary>> ResolveRelationalReferencesAsync(ExplorerScope scope,
        ReferenceNavigationRequestedEventArgs request, string sourceFilePath, CancellationToken ct)
    {
        var runtime = _relational ?? throw new InvalidOperationException("The relational runtime is closed.");
        var targets = await runtime.References.ResolveAsync(scope.Context, [new(request.Token, request.ArgumentCount)], ct);
        var candidates = targets[0].Candidates;
        _openWindows.TryGetValue(sourceFilePath, out var source);
        return await (_relationalDocuments ?? throw new InvalidOperationException("The document access is closed."))
            .PrioritizeReferencesAsync(candidates, source?.State, sourceFilePath, ct);
    }

    private async Task PreviewRelationalReferenceAsync(ReferenceNavigationRequestedEventArgs request, string sourceFilePath)
    {
        if (_relational == null || _relationalExplorerScope == null || _relationalDocuments == null || _relationalPreviewLifetime == null) return;
        var runtime = _relational; var scope = _relationalExplorerScope; var documents = _relationalDocuments;
        var lifetime = _relationalPreviewLifetime;
        using var pending = lifetime.BeginRequest();
        try
        {
            await Task.Delay(100, pending.CancellationToken);
            var candidates = await ResolveRelationalReferencesAsync(scope, request, sourceFilePath, pending.CancellationToken);
            if (candidates.IsEmpty) return;
            var indexed = await documents.ReferenceDocumentAsync(scope, candidates[0], pending.CancellationToken);
            var address = await documents.ResolveReferenceAddressAsync(scope, indexed, pending.CancellationToken);
            var plan = await documents.DescribeTextAsync(address, pending.CancellationToken);
            RelationalDocumentService.ValidateReferenceSource(indexed, address, plan);
            if (plan.EstimatedTextBytes >= documents.Limits.WarningBytes)
                throw new DocumentTextLimitException(documents.Limits.WarningBytes);
            var content = await documents.ReadTextAsync(plan, pending.CancellationToken);
            await documents.ValidateTextPlanAsync(plan, scope, pending.CancellationToken);
            if (!await runtime.Index.IsContextCurrentAsync(scope.Context, pending.CancellationToken)) throw new IndexGenerationChangedException();
            if (!ReferenceEquals(_relational, runtime) || !ReferenceEquals(_relationalExplorerScope, scope)) return;
            lifetime.TryPublish(pending.Stamp, () => ShowReferencePreview(RelationalDocumentService.Reference(candidates[0], address.DocumentPath),
                content.Text, candidates.Length, address.SyntaxPath, address.DisplayName));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope))
                lifetime.TryPublish(pending.Stamp, () => { ClearReferencePreview("The selected reference could not be previewed."); StatusText = ex.Message; });
        }
    }

    private async Task NavigateRelationalReferenceAsync(ReferenceNavigationRequestedEventArgs request, string sourceFilePath, FloatingCodeWindow? sourceWindow)
    {
        if (_relational == null || _relationalExplorerScope == null || _relationalDocuments == null || _relationalNavigationLifetime == null) return;
        var runtime = _relational; var scope = _relationalExplorerScope; var documents = _relationalDocuments;
        var lifetime = _relationalNavigationLifetime;
        using var pending = lifetime.BeginRequest();
        try
        {
            var candidates = await ResolveRelationalReferencesAsync(scope, request, sourceFilePath, pending.CancellationToken);
            if (candidates.IsEmpty) { StatusText = "No published reference target was found."; return; }
            var choices = candidates.Select(s => RelationalDocumentService.Reference(s, s.Definition.Locator)).ToArray();
            var picked = choices.Length == 1 ? choices[0] : PickReference(request.Token, choices, sourceFilePath);
            if (picked == null) return;
            var symbol = candidates[Array.IndexOf(choices, picked)];
            var indexed = await documents.ReferenceDocumentAsync(scope, symbol, pending.CancellationToken);
            var address = await documents.ResolveReferenceAddressAsync(scope, indexed, pending.CancellationToken);
            var plan = await documents.DescribeTextAsync(address, pending.CancellationToken);
            RelationalDocumentService.ValidateReferenceSource(indexed, address, plan);
            if (!ReferenceEquals(_relational, runtime) || !ReferenceEquals(_relationalExplorerScope, scope) ||
                sourceWindow != null && !_openWindows.Values.Contains(sourceWindow)) return;
            var target = RelationalDocumentService.Reference(symbol, address.DocumentPath);
            // A new navigation uses the normal placement path, not an existing-state restore.
            await TryOpenRelationalFileCoreAsync(address.DocumentPath, null, null, null, target, sourceWindow, false,
                pending.CancellationToken, plan.Key, scope.Context, preferredResolvedAddress: address);
            if (_openWindows.TryGetValue(address.DocumentPath, out var opened) && ReferenceEquals(_relationalExplorerScope, scope) && lifetime.IsCurrent(pending.Stamp) &&
                _relationalOpenTextKeys.TryGetValue(address.Identity, out var resident) && resident == plan.Key)
            {
                if (sourceWindow != null && !ReferenceEquals(sourceWindow, opened))
                    AddRelationalReferenceLine(sourceWindow, request, target, opened);
                if (symbol.Definition.Kind == ReferenceEntityKind.Table)
                {
                    var data = await documents.RelatedTableDataAsync(address, scope, pending.CancellationToken);
                    if (data != null && ReferenceEquals(_relationalExplorerScope, scope))
                        await TryOpenRelationalFileAsync(data.DocumentPath, sourceWindow: sourceWindow,
                            ct: pending.CancellationToken, preferredResolvedAddress: data);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope))
                lifetime.TryPublish(pending.Stamp, () => StatusText = ex.Message);
        }
    }

    private void AddRelationalReferenceLine(FloatingCodeWindow source, ReferenceNavigationRequestedEventArgs request,
        ReferenceEntity target, FloatingCodeWindow destination)
    {
        if (!_referenceConnectionLinesEnabled || _codeViewMode != CodeViewMode.Canvas) return;
        var columns = ResolveReferenceEntityColumns(destination, target);
        AddReferenceConnectionLine(new ReferenceConnectionLineState
        {
            SourceFilePath = source.State.FilePath, SourceLineNumber = request.LineNumber,
            SourceStartColumnNumber = Math.Max(1, request.TokenStartColumnNumber), SourceEndColumnNumber = Math.Max(request.TokenStartColumnNumber + 1, request.TokenEndColumnNumber),
            TargetFilePath = destination.State.FilePath, TargetLineNumber = target.LineNumber,
            TargetStartColumnNumber = columns.StartColumn, TargetEndColumnNumber = columns.EndColumn,
            SourceBoundSnapshotKey = source.State.BoundSnapshotKey, SourceBoundResourceKey = source.State.BoundResourceKey, SourceTargetState = source.State.TargetState,
            TargetBoundSnapshotKey = destination.State.BoundSnapshotKey, TargetBoundResourceKey = destination.State.BoundResourceKey, TargetTargetState = destination.State.TargetState
        });
        RefreshReferenceConnectionLines();
    }

    private sealed record RelationalMenuToken(ReferenceQuery Query, int Line, int Column, SqlReferenceToken? SqlToken = null);

    private async Task ShowRelationalReferenceMenuAsync(FloatingCodeWindow window, CodeWindowContextMenuOpeningEventArgs e)
    {
        RemoveDynamicReferencesMenuItems(e.ContextMenu);
        var menu = new MenuItem { Header = "References", Tag = DynamicReferencesContextMenuTag };
        AddDisabledMenuItem(menu, "Loading reference metadata");
        bool sql = IsSqlDocument(window.State.FilePath), vb = IsVisualBasicDocument(window.State.FilePath);
        if (sql) InsertDynamicReferencesMenu(e.ContextMenu, CreateSqlFindContextMenu(window), CreateSqlTraceContextMenu(window), menu);
        else InsertDynamicReferencesMenu(e.ContextMenu, menu);
        if (_relational == null || _relationalExplorerScope == null || _relationalDocuments == null) return;
        var runtime = _relational; var scope = _relationalExplorerScope; var documents = _relationalDocuments;
        if (!_relationalMenuLifetimes.TryGetValue(window, out var lifetime))
        {
            if (_relationalMenuLifetimes.Count >= 64) { menu.Items.Clear(); AddDisabledMenuItem(menu, "Reference request capacity exceeded"); return; }
            _relationalMenuLifetimes.Add(window, lifetime = new(runtime.Session.Epoch, scope.Context.ScopeKey));
        }
        using var pending = lifetime.BeginRequest();
        RoutedEventHandler closed = (_, _) => { try { lifetime.InvalidateContext(); } catch (ObjectDisposedException) { } };
        e.ContextMenu.Closed += closed;
        string text = window.Text;
        try
        {
            var tokens = await Task.Run(() => ReadRelationalMenuTokens(text, sql, vb, documents.Limits.MaximumMenuOccurrences, pending.CancellationToken), pending.CancellationToken);
            var queries = tokens.Select(t => t.Query).Distinct().ToArray();
            var targets = new Dictionary<ReferenceQuery, ImmutableArray<SymbolSummary>>();
            long characters = 0;
            foreach (var batch in queries.Chunk(64))
            {
                var resolved = await runtime.References.ResolveAsync(scope.Context, batch, pending.CancellationToken);
                foreach (var target in resolved)
                {
                    foreach (var candidate in target.Candidates)
                    {
                        var d = candidate.Definition;
                        characters = checked(characters + (long)d.Name.Length + d.QualifiedName.Length + d.Locator.Length + d.ContainerName.Length + d.Language.Length + 128);
                        if (characters > 4 * 1024 * 1024) throw new ExplorerLimitException("The complete reference menu exceeds its metadata budget.");
                    }
                    targets.Add(target.Query, target.Candidates);
                }
            }
            var keys = targets.Values.SelectMany(v => v).Select(s => s.DocumentKey).Distinct().ToArray();
            var memberships = new Dictionary<long, List<IndexedDocumentSummary>>();
            int membershipCount = 0;
            foreach (var batch in keys.Chunk(512))
            {
                var context = scope.Context with { DocumentKeys = batch.ToImmutableArray(), RestrictDocumentKeys = true };
                SearchCursor? cursor = null;
                bool exhausted;
                do
                {
                    var page = await runtime.Index.ReadDocumentsPageAsync(context, cursor, token: pending.CancellationToken);
                    foreach (var document in page.Items)
                    {
                        characters = checked(characters + (long)document.DisplayName.Length + document.Locator.Length + document.NodeKey.Length + document.ParentNodeKey.Length + 128);
                        if (++membershipCount > 4096 || characters > 4 * 1024 * 1024) throw new ExplorerLimitException("The complete reference menu exceeds its metadata budget.");
                        if (!memberships.TryGetValue(document.DocumentKey, out var owners)) memberships.Add(document.DocumentKey, owners = []);
                        owners.Add(document);
                    }
                    cursor = page.Next; exhausted = page.Exhausted;
                } while (!exhausted);
            }
            var sourceKeys = RelationalSourceMemberships(scope, window.State).Select(r => r.ScopeResourceKey).ToHashSet();
            var occurrences = new List<CodeReferenceOccurrence>();
            foreach (var token in tokens)
            {
                foreach (var target in targets[token.Query])
                {
                    if (sql && (!SqlContextMenuReferenceKinds.Contains(target.Definition.Kind) ||
                        token.SqlToken != null && IsSqlDefinitionToken(text, token.SqlToken, target.Definition.Kind))) continue;
                    if (!memberships.TryGetValue(target.DocumentKey, out var owners)) throw new IndexGenerationChangedException();
                    var member = owners.FirstOrDefault(m => sourceKeys.Contains(m.ScopeResourceKey)) ?? owners[0];
                    var resource = scope.Resources.First(r => r.ScopeResourceKey == member.ScopeResourceKey);
                    var context = new ReferenceResourceContext(resource.ScopeResourceKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ExplorerCompatibility.ResourceDisplayName(resource), resource.Kind, resource.Path);
                    occurrences.Add(new(token.Query.Token, token.Line, token.Column,
                        RelationalDocumentService.Reference(target, target.Definition.Locator), context,
                        sourceKeys.Contains(resource.ScopeResourceKey) || IsSameFile(target.Definition.Locator, window.State.FilePath)));
                    if (occurrences.Count > documents.Limits.MaximumMenuOccurrences) throw new ExplorerLimitException("The complete reference menu exceeds its occurrence budget.");
                }
            }
            if (!await runtime.Index.IsContextCurrentAsync(scope.Context, pending.CancellationToken)) throw new IndexGenerationChangedException();
            if (!ReferenceEquals(_relational, runtime) || !ReferenceEquals(_relationalExplorerScope, scope) ||
                !_openWindows.Values.Contains(window) || !e.ContextMenu.IsOpen || window.Text != text) return;
            lifetime.TryPublish(pending.Stamp, () =>
            {
                menu.Items.Clear();
                if (sql)
                    foreach (var kind in SqlContextMenuReferenceKinds) AddSqlReferenceKindMenu(menu, window, kind, occurrences.Where(o => o.Target.Kind == kind));
                else if (occurrences.Count == 0) AddDisabledMenuItem(menu, "No references found in this file");
                else
                {
                    string language = vb ? CodeWindowSettings.VisualBasicLanguage : string.Empty;
                    AddInternalReferencesMenu(menu, window, occurrences.Where(o => o.IsInternal), language);
                    AddExternalReferencesMenu(menu, window, occurrences.Where(o => !o.IsInternal), language);
                }
                if (window.TryCreateCurrentReferenceDiagramObjectInfo(out var info) && targets.Any(t =>
                    ReferenceMetadata.Normalize(t.Key.Token).Equals(ReferenceMetadata.Normalize(info.Token), StringComparison.OrdinalIgnoreCase) && !t.Value.IsEmpty))
                    InsertCopyDiagramObjectMenuItem(e.ContextMenu, info);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope))
                lifetime.TryPublish(pending.Stamp, () => { menu.Items.Clear(); AddDisabledMenuItem(menu, "Reference metadata unavailable"); StatusText = ex.Message; });
        }
        finally { e.ContextMenu.Closed -= closed; }
    }

    private static IEnumerable<ExplorerResource> RelationalSourceMemberships(ExplorerScope scope, OpenDocumentState state) =>
        scope.Resources.Where(r => r.IsLoaded && (state.BoundSnapshotKey.HasValue ? r.Snapshot?.SnapshotKey == state.BoundSnapshotKey :
            r.Kind == ResourceKind.File && IsSameFile(r.Path, state.FilePath) || r.Kind == ResourceKind.Folder && ExplorerCompatibility.IsWithinPhysicalRoot(state.FilePath, r.Path)));

    private static ImmutableArray<RelationalMenuToken> ReadRelationalMenuTokens(string text, bool sql, bool vb, int maximum, CancellationToken ct)
    {
        var result = ImmutableArray.CreateBuilder<RelationalMenuToken>();
        if (sql)
        {
            foreach (var token in EnumerateSqlReferenceTokens(text))
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new(new(token.Token), token.LineNumber, token.ColumnNumber, token));
                if (result.Count > maximum) throw new ExplorerLimitException("The complete reference menu exceeds its occurrence budget.");
            }
        }
        else
        {
            SyntaxNode root = vb ? VisualBasicSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct) :
                CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
            {
                ct.ThrowIfCancellationRequested();
                if (vb ? !IsVisualBasicReferenceIdentifierToken(token) : !IsCSharpReferenceIdentifierToken(token)) continue;
                var location = token.GetLocation().GetLineSpan();
                result.Add(new(new(token.ValueText, vb ? TryGetVisualBasicInvocationArgumentCount(token) : TryGetCSharpInvocationArgumentCount(token)),
                    location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1));
                if (result.Count > maximum) throw new ExplorerLimitException("The complete reference menu exceeds its occurrence budget.");
            }
        }
        return result.ToImmutable();
    }

    private async Task ApplyRelationalSqlFindAsync(FloatingCodeWindow window, SqlFindOperation operation)
    {
        if (_relational == null || _relationalExplorerScope == null || _relationalNavigationLifetime == null)
        { StatusText = "Open a scope before using SQL Find."; return; }
        var runtime = _relational; var scope = _relationalExplorerScope; var lifetime = _relationalNavigationLifetime;
        using var pending = lifetime.BeginRequest();
        if (!window.TryGetSelectedTextOrReferenceToken(out string target, out int offset) || string.IsNullOrWhiteSpace(target))
        { StatusText = "Select a SQL table or field, or place the cursor on one, before using Find."; return; }
        try
        {
            string normalized = NormalizeSqlReferenceToken(target), simple = GetSqlSimpleName(normalized);
            if (string.IsNullOrWhiteSpace(simple)) { StatusText = "The selected SQL name is empty."; return; }
            var resolved = await runtime.References.ResolveAsync(scope.Context, [new(normalized), new(simple)], pending.CancellationToken);
            var references = resolved.SelectMany(r => r.Candidates).Select(s => RelationalDocumentService.Reference(s, s.Definition.Locator)).ToArray();
            string description = "'" + simple + "'";
            string pattern;
            if (references.Any(r => r.Kind == ReferenceEntityKind.Table)) pattern = CreateSqlTableUsageRegex(simple, operation);
            else
            {
                var fields = references.Where(r => r.Kind == ReferenceEntityKind.Field)
                    .DistinctBy(r => r.FilePath + "|" + r.QualifiedName + "|" + r.ContainerName).ToArray();
                var field = fields.Select(r => r.ContainerName).Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 1 ? fields.FirstOrDefault() : null;
                if (TryInferSqlFieldTable(window.Text, offset, normalized, out string table, field))
                {
                    string tableName = GetSqlSimpleName(table);
                    description = "field '" + simple + "' on table '" + tableName + "'";
                    pattern = CreateSqlFieldUsageRegex(simple, tableName, operation);
                }
                else pattern = CreateSqlGenericUsageRegex(simple, operation);
            }
            if (!await runtime.Index.IsContextCurrentAsync(scope.Context, pending.CancellationToken)) throw new IndexGenerationChangedException();
            if (!ReferenceEquals(_relational, runtime) || !ReferenceEquals(_relationalExplorerScope, scope) || !_openWindows.Values.Contains(window)) return;
            if (!lifetime.TryPublish(pending.Stamp, () =>
            {
                ObjectExplorerRegexToggle.IsChecked = true;
                SetObjectExplorerSearchTarget(ObjectExplorerSearchTarget.Content);
                ObjectExplorerSearchTextBox.Text = pattern;
            })) return;
            await ApplyObjectExplorerSearchAsync();
            if (ReferenceEquals(_relationalExplorerScope, scope)) lifetime.TryPublish(pending.Stamp,
                () => StatusText = $"Filtered Object Explorer for {GetSqlFindOperationHeader(operation)} usage of {description}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_relational, runtime) && ReferenceEquals(_relationalExplorerScope, scope))
                lifetime.TryPublish(pending.Stamp, () => StatusText = ex.Message);
        }
    }
}

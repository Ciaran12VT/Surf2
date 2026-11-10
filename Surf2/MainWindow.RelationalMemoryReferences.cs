using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;

namespace Surf2;

public partial class MainWindow
{
    private MemoryReferenceProgress? _relationalMemoryProgress;
    private DispatcherTimer? _relationalMemoryReloadTimer;
    private long _relationalMemoryFileVersion;
    private ScopeReferenceIndex? _relationalAvailableNamesIndex;
    private IReadOnlySet<string>? _relationalAvailableNames;
    private bool UsesRelationalMemoryReferences => _relationalExplorerScope?.DatabaseCatalogue != null;
    private IReadOnlySet<string>? RelationalAvailableReferenceNames()
    {
        if (!UsesRelationalMemoryReferences || _relationalFileReferencesReady)
        {
            _relationalAvailableNamesIndex = null;
            _relationalAvailableNames = null;
            return null;
        }
        if (!ReferenceEquals(_relationalAvailableNamesIndex, _referenceIndex))
        {
            _relationalAvailableNames = _referenceIndex.Entities
                .Where(entity => entity.Kind == ReferenceEntityKind.File || DatabaseDocumentService.IsDatabaseDocumentPath(entity.FilePath))
                .Select(entity => entity.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _relationalAvailableNamesIndex = _referenceIndex;
        }
        return _relationalAvailableNames;
    }

    private async Task BuildRelationalMemoryReferencesAsync(RelationalRuntime runtime, ExplorerScope scope,
        ExplorerIndexLanguagePolicy languages, Guid owner, Task retirement, CancellationToken ct)
    {
        try
        {
            await retirement.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var fields = await MemoryReferenceIndex.LoadTableFieldsAsync(runtime.Session, scope, ct).ConfigureAwait(false);
            var namedIndex = MemoryReferenceIndex.CreateNamedIndex(scope);
            foreach (var field in fields) namedIndex.Add(field);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                _referenceIndex = namedIndex;
                ApplyReferenceHighlightsToOpenWindows(); ApplyReferenceHighlightsToPreview();
            });
            var watch = new PhysicalReferenceWatch(scope, notification => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AcceptsRelationalExplorer(owner, runtime) &&
                    (notification.Reason & (PhysicalReferenceInvalidationReason.Changed | PhysicalReferenceInvalidationReason.WatcherError)) != 0)
                    QueueRelationalMemoryRefresh();
            })));
            long fileVersion = 0;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) { watch.Dispose(); return; }
                _relationalPhysicalWatch = watch;
                fileVersion = _relationalMemoryFileVersion;
                _relationalPhysicalReconciliationTimer = new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background,
                    (_, _) => { if (AcceptsRelationalExplorer(owner, runtime)) QueueRelationalMemoryRefresh(); }, Dispatcher);
            });
            DateTime lastPaint = DateTime.MinValue;
            MemoryReferenceProgress latest = new(0, 0, 0, true);
            var index = await MemoryReferenceIndex.BuildAsync(scope, languages, async progress =>
            {
                latest = progress;
                if (DateTime.UtcNow - lastPaint < TimeSpan.FromMilliseconds(150) && progress.Completed != progress.Discovered) return;
                lastPaint = DateTime.UtcNow;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!AcceptsRelationalExplorer(owner, runtime)) return;
                    _relationalMemoryProgress = progress;
                    _relationalIndexProgress = new(progress.Completed, progress.Completed, 0, progress.Failed,
                        scope.Resources.Count(r => !r.IsLoaded), false, false,
                        Phase: progress.Discovering ? "Discovering files" : "Loading definitions", Document: progress.File);
                    UpdateRelationalIndexUi();
                });
            }, ct, fields, async names =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!AcceptsRelationalExplorer(owner, runtime)) return;
                    _referenceIndex = names;
                    ApplyReferenceHighlightsToOpenWindows(); ApplyReferenceHighlightsToPreview();
                });
            }).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                if (_relationalMemoryFileVersion != fileVersion) { QueueRelationalMemoryRefresh(); return; }
                _referenceIndex = index;
                _relationalMemoryProgress = latest;
                _relationalIndexProgress = new(latest.Completed, latest.Completed, 0, latest.Failed,
                    scope.Resources.Count(r => !r.IsLoaded), true, latest.Failed == 0, scope.Context,
                    Phase: latest.Failed == 0 ? "Ready" : "Some files unavailable");
                _relationalIndexWorkerFinished = true;
                _relationalIndexStatusTimer?.Stop();
                _relationalIndexElapsed?.Stop();
                UpdateRelationalIndexUi();
                ApplyReferenceHighlightsToOpenWindows(); ApplyReferenceHighlightsToPreview(); ApplyReferenceHighlightsToPreviewPopout();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "The scope's in-memory definition index could not be loaded.");
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                _relationalIndexWorkerFinished = true;
                _relationalIndexProgress = new(0, 0, 0, 1, 0, true, false, Phase: "Loading incomplete");
                _relationalIndexStatusTimer?.Stop(); _relationalIndexElapsed?.Stop();
                UpdateRelationalIndexUi();
            });
        }
    }

    private void QueueRelationalMemoryRefresh()
    {
        _relationalMemoryFileVersion++;
        _relationalFileReferencesReady = false;
        ApplyReferenceHighlightsToOpenWindows(); ApplyReferenceHighlightsToPreview();
        _relationalMemoryReloadTimer?.Stop();
        var owner = _relationalExplorerOwner;
        _relationalMemoryReloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background,
            (_, _) =>
            {
                _relationalMemoryReloadTimer?.Stop();
                if (_relational != null && AcceptsRelationalExplorer(owner, _relational)) BeginRelationalExplorerContext();
            }, Dispatcher);
    }

    private IReadOnlyList<ReferenceEntity> ResolveRelationalMemoryReferences(string token, int? arguments, string sourcePath)
    {
        IEnumerable<ReferenceEntity> candidates = _referenceIndex.Resolve(token, arguments);
        if (!_relationalFileReferencesReady)
            candidates = candidates.Where(r => r.Kind == ReferenceEntityKind.File || DatabaseDocumentService.IsDatabaseDocumentPath(r.FilePath));
        return SortReferencesForSource(candidates.ToArray(), sourcePath);
    }

    private async Task NavigateRelationalMemoryReferenceAsync(ReferenceNavigationRequestedEventArgs request,
        string sourcePath, FloatingCodeWindow? sourceWindow)
    {
        if (_relational == null || _relationalDocuments == null || _relationalExplorerScope == null || _relationalNavigationLifetime == null) return;
        var runtime = _relational; var documents = _relationalDocuments; var scope = _relationalExplorerScope; var owner = _relationalExplorerOwner;
        using var pending = _relationalNavigationLifetime.BeginRequest();
        var references = ResolveRelationalMemoryReferences(request.Token, request.ArgumentCount, sourcePath);
        if (references.Count == 0)
        {
            StatusText = _relationalFileReferencesReady ? $"No definition found for '{request.Token}'." : "File definitions are still loading. Named database resources are available.";
            return;
        }
        var target = references.Count == 1 ? references[0] : PickReference(request.Token, references, sourcePath);
        if (target == null) return;
        try
        {
            var address = await documents.ResolveAsync(target.FilePath, scope, ct: pending.CancellationToken);
            if (!AcceptsRelationalExplorer(owner, runtime)) return;
            await TryOpenRelationalFileCoreAsync(address.DocumentPath, null, null, null, target, sourceWindow, false,
                pending.CancellationToken, preferredResolvedAddress: address);
            if (!AcceptsRelationalExplorer(owner, runtime)) return;
            if (sourceWindow != null && _openWindows.TryGetValue(address.DocumentPath, out var opened) && !ReferenceEquals(opened, sourceWindow))
                AddRelationalReferenceLine(sourceWindow, request, target, opened);
            if (target.Kind == ReferenceEntityKind.Table)
            {
                var data = await documents.RelatedTableDataAsync(address, scope, pending.CancellationToken);
                if (data != null && AcceptsRelationalExplorer(owner, runtime))
                    await TryOpenRelationalFileAsync(data.DocumentPath, sourceWindow: sourceWindow, ct: pending.CancellationToken, preferredResolvedAddress: data);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (AcceptsRelationalExplorer(owner, runtime)) StatusText = "The selected definition could not be opened. " + ex.Message;
        }
    }

    private async Task PreviewRelationalMemoryReferenceAsync(ReferenceNavigationRequestedEventArgs request, string sourcePath)
    {
        if (_relational == null || _relationalDocuments == null || _relationalExplorerScope == null || _relationalPreviewLifetime == null) return;
        var runtime = _relational; var documents = _relationalDocuments; var scope = _relationalExplorerScope; var owner = _relationalExplorerOwner;
        using var pending = _relationalPreviewLifetime.BeginRequest();
        var references = ResolveRelationalMemoryReferences(request.Token, request.ArgumentCount, sourcePath);
        if (references.Count == 0) return;
        var target = references[0];
        try
        {
            await Task.Delay(100, pending.CancellationToken);
            var address = await documents.ResolveAsync(target.FilePath, scope, ct: pending.CancellationToken);
            var plan = await documents.DescribeTextAsync(address, pending.CancellationToken);
            if (plan.EstimatedTextBytes >= documents.Limits.WarningBytes) return;
            var content = await documents.ReadTextAsync(plan, pending.CancellationToken);
            await documents.ValidateTextPlanAsync(plan, scope, pending.CancellationToken);
            if (AcceptsRelationalExplorer(owner, runtime))
                _relationalPreviewLifetime.TryPublish(pending.Stamp, () =>
                    ShowReferencePreview(target, content.Text, references.Count, address.SyntaxPath, address.DisplayName));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (AcceptsRelationalExplorer(owner, runtime)) StatusText = "The selected definition could not be previewed. " + ex.Message;
        }
    }

    private async Task ShowRelationalMemoryReferenceMenuAsync(FloatingCodeWindow window, CodeWindowContextMenuOpeningEventArgs e)
    {
        RemoveDynamicReferencesMenuItems(e.ContextMenu);
        var menu = new MenuItem { Header = "References", Tag = DynamicReferencesContextMenuTag };
        bool sql = IsSqlDocument(window.State.FilePath), vb = IsVisualBasicDocument(window.State.FilePath);
        if (sql) InsertDynamicReferencesMenu(e.ContextMenu, CreateSqlFindContextMenu(window), CreateSqlTraceContextMenu(window), menu);
        else InsertDynamicReferencesMenu(e.ContextMenu, menu);
        if (_relational == null || _relationalExplorerScope == null) return;
        var runtime = _relational; var scope = _relationalExplorerScope; var owner = _relationalExplorerOwner;
        var index = _referenceIndex; bool ready = _relationalFileReferencesReady;
        string text = window.Text, sourcePath = window.State.FilePath;
        var sourceKeys = RelationalSourceMemberships(scope, window.State).Select(resource => resource.ScopeResourceKey).ToHashSet();
        if (!_relationalMenuLifetimes.TryGetValue(window, out var lifetime))
        {
            if (_relationalMenuLifetimes.Count >= 64) { AddDisabledMenuItem(menu, "Reference request capacity exceeded"); return; }
            _relationalMenuLifetimes.Add(window, lifetime = new(runtime.Session.Epoch, scope.Context.ScopeKey));
        }
        using var pending = lifetime.BeginRequest();
        RoutedEventHandler closed = (_, _) => { try { lifetime.InvalidateContext(); } catch (ObjectDisposedException) { } };
        e.ContextMenu.Closed += closed;
        AddDisabledMenuItem(menu, "Reading references…");
        try
        {
            var occurrences = await Task.Run(() =>
            {
                var result = new List<CodeReferenceOccurrence>();
                foreach (var token in ReadRelationalMenuTokens(text, sql, vb, 10000, pending.CancellationToken))
                foreach (var target in index.Resolve(token.Query.Token, token.Query.ArgumentCount))
                {
                    pending.CancellationToken.ThrowIfCancellationRequested();
                    if (!ready && target.Kind != ReferenceEntityKind.File && !DatabaseDocumentService.IsDatabaseDocumentPath(target.FilePath)) continue;
                    if (sql && (!SqlContextMenuReferenceKinds.Contains(target.Kind) ||
                        token.SqlToken != null && IsSqlDefinitionToken(text, token.SqlToken, target.Kind))) continue;
                    long snapshot = DatabaseDocumentService.TryParseDocumentPath(target.FilePath, out var parsed) &&
                        ExplorerCompatibility.TryParseDatabaseIdentity(parsed, out _, out long key, out _) ? key : 0;
                    var resources = scope.Resources.Where(resource => resource.IsLoaded &&
                        (snapshot != 0 ? resource.Snapshot?.SnapshotKey == snapshot :
                            resource.Kind == ResourceKind.File && IsSameFile(resource.Path, target.FilePath) ||
                            resource.Kind == ResourceKind.Folder && ExplorerCompatibility.IsWithinPhysicalRoot(target.FilePath, resource.Path)));
                    var member = resources.FirstOrDefault(resource => sourceKeys.Contains(resource.ScopeResourceKey)) ?? resources.FirstOrDefault();
                    if (member == null) continue;
                    var context = new ReferenceResourceContext(member.ScopeResourceKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ExplorerCompatibility.ResourceDisplayName(member), member.Kind, member.Path);
                    result.Add(new(token.Query.Token, token.Line, token.Column, target, context,
                        sourceKeys.Contains(member.ScopeResourceKey) || IsSameFile(target.FilePath, sourcePath)));
                    if (result.Count > 10000) throw new ExplorerLimitException("The reference menu exceeds its occurrence budget.");
                }
                return result;
            }, pending.CancellationToken);
            if (!AcceptsRelationalExplorer(owner, runtime) || !_openWindows.Values.Contains(window) || window.Text != text || !e.ContextMenu.IsOpen) return;
            lifetime.TryPublish(pending.Stamp, () =>
            {
                menu.Items.Clear();
                if (!ready) AddDisabledMenuItem(menu, "File definitions are loading…");
                if (sql)
                    foreach (var kind in SqlContextMenuReferenceKinds) AddSqlReferenceKindMenu(menu, window, kind, occurrences.Where(o => o.Target.Kind == kind));
                else if (occurrences.Count == 0) AddDisabledMenuItem(menu, "No available references in this file");
                else
                {
                    string language = vb ? CodeWindowSettings.VisualBasicLanguage : string.Empty;
                    AddInternalReferencesMenu(menu, window, occurrences.Where(o => o.IsInternal), language);
                    AddExternalReferencesMenu(menu, window, occurrences.Where(o => !o.IsInternal), language);
                }
                if (window.TryCreateCurrentReferenceDiagramObjectInfo(out var info) &&
                    ResolveRelationalMemoryReferences(info.Token, null, sourcePath).Count != 0)
                    InsertCopyDiagramObjectMenuItem(e.ContextMenu, info);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (AcceptsRelationalExplorer(owner, runtime))
                lifetime.TryPublish(pending.Stamp, () => { menu.Items.Clear(); AddDisabledMenuItem(menu, "References unavailable"); StatusText = ex.Message; });
        }
        finally { e.ContextMenu.Closed -= closed; }
    }
}

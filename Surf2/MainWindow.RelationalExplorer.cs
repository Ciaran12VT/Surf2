using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Index;

namespace Surf2;

public partial class MainWindow
{
    // Scope/model/node fields and CreateRelationalExplorerNode are owned by the state partial.
    private ExplorerCancellationLifetime? _relationalExplorerCancellation;
    private ExplorerCancellationLifetime? _relationalSearchCancellation;
    private ExplorerCancellationLifetime? _relationalRootCancellation;
    private ExplorerChildrenLoader? _relationalChildrenLoader;
    private ExplorerRequestOwner? _relationalSearchOwner;
    private RelationalScopeIndexRefresher? _relationalIndexRefresher;
    private RelationalRuntime? _relationalIndexRuntime;
    private Task _relationalExplorerRetirement = Task.CompletedTask;
    private Task _relationalIndexRefreshTask = Task.CompletedTask;
    private Task _relationalExplorerSearchTask = Task.CompletedTask;
    private Task _relationalHighlightTask = Task.CompletedTask;
    private Task _relationalRootLoadTask = Task.CompletedTask;
    private Task _relationalReferencePreparationTask = Task.CompletedTask;
    private Guid _relationalReferencePreparationIdentity;
    private Task _relationalExplorerFocusTask = Task.CompletedTask;
    private long _relationalExplorerFocusGeneration;
    private long _relationalHighlightGeneration;
    private Guid _relationalExplorerSearchIdentity;
    private Guid _relationalExplorerOwner;
    private ExplorerIndexRefreshProgress? _relationalIndexProgress;
    private DispatcherTimer? _relationalIndexStatusTimer;
    private Stopwatch? _relationalIndexElapsed;
    private ExplorerIndexLanguagePolicy? _relationalIndexLanguages;
    private FrozenDictionary<string, FrozenDictionary<string, ReferenceHighlightStyleSetting>> _relationalHighlightStyles =
        new Dictionary<string, FrozenDictionary<string, ReferenceHighlightStyleSetting>>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenDictionary<string, ReferenceHighlightStyleSetting> EmptyRelationalHighlightStyles =
        new Dictionary<string, ReferenceHighlightStyleSetting>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _relationalTextFileNameSeeds = new(StringComparer.OrdinalIgnoreCase);
    private long _relationalTextSeedCharacters;
    private readonly Dictionary<FileSystemNode, ExplorerScope> _relationalExplorerNodeViews = [];
    private readonly HashSet<Task> _relationalExplorerPickerDrains = [];
    private const int RelationalExplorerResultLimit = 5000;
    public Func<ExplorerNodeSummary, CancellationToken, Task>? RelationalDatabaseHistoryHandler { get; set; }
    public Func<ExplorerNodeSummary, CancellationToken, Task>? RelationalDatabaseExportHandler { get; set; }
    public Func<ExplorerResource, CancellationToken, Task>? RelationalScopeResourceRemovalHandler { get; set; }
    public Func<ExplorerResourceCandidate, CancellationToken, Task>? RelationalScopeResourceAdditionHandler { get; set; }

    // State owner calls this immediately after applying a prepared scope, including startup/workbench restores.
    // It performs no source/SQL read on the caller. The refresh task is tracked and awaited on retirement.
    private void BeginRelationalExplorerContext()
    {
        Dispatcher.VerifyAccess();
        RetireRelationalExplorerContext();
        _relationalExplorerOwner = Guid.NewGuid();
        _relationalTextFileNameSeeds.Clear(); _relationalTextSeedCharacters = 0; _referenceIndex = ScopeReferenceIndex.Empty;
        _relationalExplorerNodeViews.Clear();
        _relationalHighlightStyles = new Dictionary<string, FrozenDictionary<string, ReferenceHighlightStyleSetting>>()
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _relationalIndexProgress = null;
        if (_relational == null || _relationalExplorerScope == null) return;
        var runtime = _relational; var scope = _relationalExplorerScope;
        _relationalExplorerCancellation = new();
        _relationalChildrenLoader = new(runtime.Explorer);
        _relationalSearchOwner = new();
        _relationalIndexLanguages = ExplorerIndexLanguagePolicy.Capture(_appSettings.CodeWindows);
        if (!ReferenceEquals(_relationalIndexRuntime, runtime))
        {
            _relationalIndexRuntime = runtime;
            _relationalIndexRefresher = new(runtime.Session, runtime.Index, runtime.Snapshots);
        }
        // An empty initial catalogue is allowed for browsing, never evidence that reference resolution is ready.
        StatusText = "Scope metadata loaded. Derived references are being verified and indexed.";
        ApplyReferenceHighlightsToOpenWindows(); ApplyReferenceHighlightsToPreview();
        if (_relationalReferenceCatalogue != null) RebuildRelationalReferenceHighlightStyles();
        var owner = _relationalExplorerOwner; var token = _relationalExplorerCancellation.Token;
        var retirement = _relationalExplorerRetirement; var languages = _relationalIndexLanguages;
        _relationalIndexElapsed = Stopwatch.StartNew();
        _relationalIndexStatusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (AcceptsRelationalExplorer(owner, runtime) && _relationalExplorerSearchTask.IsCompleted && _relationalIndexProgress != null)
                StatusText = RelationalIndexStatus(_relationalIndexProgress with { Elapsed = _relationalIndexElapsed?.Elapsed ?? _relationalIndexProgress.Elapsed });
        }, Dispatcher);
        // This bounded job includes per-document rendering/parsing and metadata assembly, not synchronous SQL wrappers.
        _relationalIndexRefreshTask = Task.Run(() => RefreshRelationalScopeIndexAsync(runtime, scope, languages, owner, retirement, token));
    }

    private void RetireRelationalExplorerContext()
    {
        _relationalIndexStatusTimer?.Stop(); _relationalIndexStatusTimer = null;
        _relationalIndexElapsed?.Stop(); _relationalIndexElapsed = null;
        _relationalExplorerOwner = Guid.Empty;
        var cancellation = _relationalExplorerCancellation; _relationalExplorerCancellation = null;
        Task cancellationCallbacks = cancellation?.Cancel() ?? Task.CompletedTask;
        var searchCancellation = _relationalSearchCancellation; _relationalSearchCancellation = null;
        Task searchCallbacks = searchCancellation?.Cancel() ?? Task.CompletedTask;
        _relationalExplorerSearchIdentity = Guid.Empty;
        var children = _relationalChildrenLoader; _relationalChildrenLoader = null;
        var searchOwner = _relationalSearchOwner; searchOwner?.Dispose(); _relationalSearchOwner = null;
        var previousRetirement = _relationalExplorerRetirement;
        var refresh = _relationalIndexRefreshTask; var search = _relationalExplorerSearchTask;
        var highlight = _relationalHighlightTask; _relationalHighlightGeneration++;
        var preparation = _relationalReferencePreparationTask;
        var focus = _relationalExplorerFocusTask; _relationalExplorerFocusGeneration++;
        var pickerDrains = Task.WhenAll(_relationalExplorerPickerDrains.ToArray());
        _relationalExplorerRetirement = RetireAsync();
        async Task RetireAsync()
        {
            try
            {
                await Task.WhenAll(previousRetirement, cancellationCallbacks, searchCallbacks,
                    searchOwner?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                    children?.DisposeAsync().AsTask() ?? Task.CompletedTask, refresh, search, highlight, preparation, focus, pickerDrains).ConfigureAwait(false);
            }
            catch (Exception ex) { InternalLogService.Error(ex, "Relational explorer request retirement failed."); }
            finally
            {
                await Task.WhenAll(cancellation?.RetireAsync() ?? Task.CompletedTask,
                    searchCancellation?.RetireAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }
    }

    private bool AcceptsRelationalExplorer(Guid owner, RelationalRuntime runtime) => owner != Guid.Empty &&
        owner == _relationalExplorerOwner && ReferenceEquals(runtime, _relational) &&
        _relationalExplorerCancellation is { IsCancellationRequested: false };

    private async Task RefreshRelationalScopeIndexAsync(RelationalRuntime runtime, ExplorerScope scope,
        ExplorerIndexLanguagePolicy languages, Guid owner, Task retirement, CancellationToken ct)
    {
        try
        {
            await retirement.ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            DateTime lastStatus = DateTime.MinValue;
            var refresher = _relationalIndexRefresher!;
            var result = await refresher.RefreshAsync(scope, languages, async progress =>
            {
                if (!progress.Completed && DateTime.UtcNow - lastStatus < TimeSpan.FromMilliseconds(250)) return;
                lastStatus = DateTime.UtcNow;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!AcceptsRelationalExplorer(owner, runtime)) return;
                    _relationalIndexProgress = progress.Completed ? progress with { Phase = "Preparing highlights" } : progress;
                    // A search owns the status while it is active; refresh still exposes its own coverage field.
                    if (_relationalExplorerSearchTask.IsCompleted) StatusText = RelationalIndexStatus(_relationalIndexProgress);
                });
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.FullyPublished) runtime.References.AcceptCompletedDiscovery(result);
            var currentScope = scope with { Context = result.Context ?? scope.Context };
            var catalogue = await runtime.References.LoadPaintAsync(currentScope, ct).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                _relationalExplorerScope = currentScope; _relationalReferenceCatalogue = catalogue; _relationalIndexProgress = result;
                RebuildRelationalReferenceHighlightStyles();
                if (_relationalExplorerSearchTask.IsCompleted) StatusText = RelationalIndexStatus(result);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Relational scope indexing did not complete.");
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                var prior = _relationalIndexProgress;
                _relationalIndexProgress = new(prior?.Considered ?? 0, prior?.Published ?? 0, prior?.Unchanged ?? 0,
                    Math.Max(1, prior?.Failed ?? 0), prior?.UnloadedResources ?? 0, true, false,
                    FailureCode: ex is Microsoft.Data.SqlClient.SqlException sql ? "SQL_" + sql.Number : ex.GetType().Name,
                    Resource: prior?.Resource, Document: prior?.Document, Phase: "Stopped", Elapsed: _relationalIndexElapsed?.Elapsed ?? default,
                    FailedResource: prior?.Resource, FailedDocument: prior?.Document);
                StatusText = RelationalIndexStatus(_relationalIndexProgress);
            });
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                _relationalIndexStatusTimer?.Stop(); _relationalIndexStatusTimer = null;
                _relationalIndexElapsed?.Stop();
            });
        }
    }

    internal static string RelationalIndexStatus(ExplorerIndexRefreshProgress progress)
    {
        string elapsed = progress.Elapsed.TotalDays >= 1 ? progress.Elapsed.ToString(@"d\.hh\:mm\:ss") : progress.Elapsed.ToString(@"hh\:mm\:ss");
        if (progress.Completed && progress.Phase != "Preparing highlights" && progress.FullyPublished)
            return $"References ready: {progress.Published:N0} indexed, {progress.Unchanged:N0} unchanged; " +
                $"{progress.UnloadedResources:N0} unloaded resources excluded; {elapsed}.";
        string state = progress.Phase == "Preparing highlights" ? "preparing highlights" : progress.Completed ? "finished (incomplete)" : progress.Phase.ToLowerInvariant();
        string current = progress.Resource == null ? "" : "; " + ShortName(progress.Resource);
        if (progress.Document != null) current += " / " + ShortName(progress.Document);
        string failure = progress.FailureCode == null ? "" : " (" + progress.FailureCode + ")";
        if (progress.Completed && progress.FailedResource != null)
            failure += " at " + ShortName(progress.FailedResource) + (progress.FailedDocument == null ? "" : " / " + ShortName(progress.FailedDocument));
        return $"Reference index {state}: {progress.Considered:N0} checked, {progress.Published:N0} indexed, {progress.Unchanged:N0} unchanged; " +
            $"{elapsed}; {progress.Failed:N0} failures{failure}, {progress.UnloadedResources:N0} unloaded resources{current}.";
        static string ShortName(string name) => name.Length <= 60 ? name : name[..57] + "...";
    }

    private void RequestRelationalReferenceRefresh()
    {
        Dispatcher.VerifyAccess();
        if (_relational == null || _activeScope == null || _relationalExplorerScope == null) return;
        if (_relationalExplorerCancellation == null) { BeginRelationalExplorerContext(); return; }
        var runtime = _relational; var owner = _relationalExplorerOwner;
        long key = _relationalScopeEdit!.ExpectedToken.Key;
        var unloaded = GetUnloadedResourceIds(); string culture = CultureInfo.CurrentCulture.Name;
        var token = _relationalExplorerCancellation.Token;
        var identity = Guid.NewGuid(); _relationalReferencePreparationIdentity = identity;
        var previous = _relationalReferencePreparationTask;
        _relationalIndexProgress = null;
        StatusText = "Derived reference refresh is pending; reference resolution is not ready.";
        RebuildRelationalReferenceHighlightStyles();
        _relationalReferencePreparationTask = PrepareAsync();
        async Task PrepareAsync()
        {
            try
            {
                await previous; token.ThrowIfCancellationRequested();
                if (_relationalReferencePreparationIdentity != identity || !AcceptsRelationalExplorer(owner, runtime)) return;
                var scope = await runtime.Explorer.OpenScopeAsync(key, unloaded, culture, token);
                if (_relationalReferencePreparationIdentity != identity || !AcceptsRelationalExplorer(owner, runtime)) return;
                _relationalExplorerScope = scope;
                BeginRelationalExplorerContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalLogService.Error(ex, "Relational reference refresh preparation failed.");
                if (AcceptsRelationalExplorer(owner, runtime) && _relationalReferencePreparationIdentity == identity)
                    StatusText = "Derived references could not be refreshed. Reference resolution remains unavailable.";
            }
        }
    }

    private Task LoadRelationalScopeAsync(Scope? scope)
    {
        Dispatcher.VerifyAccess();
        var previous = _relationalRootLoadTask;
        var previousCancellation = _relationalRootCancellation;
        var callbacks = previousCancellation?.Cancel() ?? Task.CompletedTask;
        var cancellation = new ExplorerCancellationLifetime(); _relationalRootCancellation = cancellation;
        var ct = cancellation.Token;
        int loadVersion = ++_scopeLoadVersion;
        RetireRelationalExplorerContext();
        RootNodes.Clear(); _relationalExplorerNodes.Clear(); _expandedObjectExplorerNodeKeys.Clear();
        _relationalTextFileNameSeeds.Clear(); _relationalTextSeedCharacters = 0; _relationalExplorerScope = null; _relationalReferenceCatalogue = null;
        _referenceIndex = ScopeReferenceIndex.Empty; ObjectExplorerSearchTextBox.Clear(); SetActiveScope(scope);
        var task = LoadAsync(); _relationalRootLoadTask = task; return task;
        async Task LoadAsync()
        {
            try
            {
                await callbacks;
                try { await previous; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { InternalLogService.Error(ex, "Previous relational root load failed before retry."); }
                ct.ThrowIfCancellationRequested();
                if (scope == null)
                {
                    _relationalIndexProgress = null; _relationalHighlightStyles = new Dictionary<string, FrozenDictionary<string, ReferenceHighlightStyleSetting>>()
                        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                    CurrentFolderDisplay = "No scope selected"; StatusText = "Create or open a scope to load resources.";
                    ClearReferencePreview("Create or open a scope, then single-click a reference to preview it.");
                    return;
                }
                var runtime = RelationalStateRuntime;
                var edit = _relationalScopeEdit ?? throw new InvalidOperationException("The relational scope owner must be loaded first.");
                if (!string.Equals(edit.Snapshot().ScopeId, scope.ScopeId, StringComparison.Ordinal))
                    throw new InvalidOperationException("This scope does not match the selected relational owner.");
                await ShowObjectExplorerLoadingAsync("Loading scope...", scope.Name);
                var explorer = await runtime.Explorer.OpenScopeAsync(edit.ExpectedToken.Key, GetUnloadedResourceIds(), CultureInfo.CurrentCulture.Name, ct);
                var roots = await runtime.Explorer.GetRootsAsync(explorer, ct);
                ct.ThrowIfCancellationRequested();
                if (loadVersion != _scopeLoadVersion || !ReferenceEquals(runtime, _relational) || !ReferenceEquals(scope, _activeScope)) return;
                _relationalExplorerScope = explorer;
                foreach (var root in roots) RootNodes.Add(CreateRelationalExplorerNode(root));
                CurrentFolderDisplay = "Scope: " + scope.Name;
                BeginRelationalExplorerContext();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                if (loadVersion == _scopeLoadVersion) HideObjectExplorerLoading();
                if (ReferenceEquals(_relationalRootCancellation, cancellation)) _relationalRootCancellation = null;
                await cancellation.RetireAsync();
            }
        }
    }

    private async Task LoadRelationalRootsAsync()
    {
        // Save owner changes before calling this hook; only persisted metadata is read here.
        await LoadRelationalScopeAsync(_activeScope);
    }

    private async Task LoadRelationalChildrenAsync(FileSystemNode node)
    {
        Dispatcher.VerifyAccess();
        if (node.IsLoaded || !node.IsDirectory || !node.IsScopeResourceLoaded) return;
        var runtime = RelationalStateRuntime; var scope = _relationalExplorerNodeViews.GetValueOrDefault(node) ?? _relationalExplorerScope;
        var loader = _relationalChildrenLoader; var owner = _relationalExplorerOwner;
        if (scope == null || loader == null || !_relationalExplorerNodes.TryGetValue(node, out var summary)) return;
        var ct = _relationalExplorerCancellation!.Token;
        try
        {
            var batch = await loader.ReadAsync(scope, summary, ct);
            if (!AcceptsRelationalExplorer(owner, runtime) || !_relationalExplorerNodes.ContainsKey(node)) return;
            if (!batch.Completed || batch.State == ExplorerChildrenState.Failed)
            {
                StatusText = "Explorer children are unavailable or exceed the metadata limit. Expansion is not complete.";
                return; // Retain retryable placeholder; never cache a failure as an empty successful folder.
            }
            if ((long)_relationalExplorerNodes.Count + batch.Nodes.Length > RelationalRuntimeStateLimits.MaximumRows)
                throw new ExplorerLimitException("Loaded explorer metadata exceeds the scope budget.");
            ForgetRelationalExplorerDescendants(node); node.Children.Clear();
            foreach (var child in batch.Nodes)
            {
                var created = CreateRelationalExplorerNode(child); node.Children.Add(created);
                if (_relationalExplorerNodeViews.ContainsKey(node)) _relationalExplorerNodeViews[created] = scope;
            }
            node.IsLoaded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "Relational explorer expansion failed.");
            if (AcceptsRelationalExplorer(owner, runtime)) StatusText = "Explorer expansion did not complete. Retry after refreshing the scope.";
        }
    }

    private void ForgetRelationalExplorerDescendants(FileSystemNode node)
    {
        foreach (var child in node.Children) { ForgetRelationalExplorerDescendants(child); _relationalExplorerNodes.Remove(child); _relationalExplorerNodeViews.Remove(child); }
    }

    private async Task SearchRelationalExplorerAsync()
    {
        Dispatcher.VerifyAccess();
        if (_relationalExplorerScope == null) { StatusText = "Load a scope before searching resources."; return; }
        string query = ObjectExplorerSearchTextBox.Text.Trim();
        if (query.Length == 0) { await LoadRelationalRootsAsync(); return; }
        bool regex = ObjectExplorerRegexToggle.IsChecked == true;
        var target = GetObjectExplorerSearchTarget() == ObjectExplorerSearchTarget.Content ? IndexSearchTarget.Content : IndexSearchTarget.Name;
        var runtime = RelationalStateRuntime; var owner = _relationalExplorerOwner;
        var searchOwner = _relationalSearchOwner ?? throw new InvalidOperationException("Explorer scope lifetime has not begun.");
        var resourceIds = GetUnloadedResourceIds(); var sortCulture = CultureInfo.CurrentCulture.Name;
        long scopeKey = _relationalScopeEdit!.ExpectedToken.Key;
        var identity = Guid.NewGuid();
        var previousSearch = _relationalExplorerSearchTask; var previousCancellation = _relationalSearchCancellation;
        Task previousCallbacks = previousCancellation?.Cancel() ?? Task.CompletedTask; searchOwner.Cancel();
        _relationalExplorerSearchIdentity = identity;
        // Validate regex synchronously before starting the request; core matching runs on the bounded worker.
        try { _ = new IndexTextMatcher(query, regex); }
        catch (ArgumentException)
        {
            _relationalSearchCancellation = null;
            var rejected = RejectAsync(); _relationalExplorerSearchTask = rejected;
            await rejected; return;
            async Task RejectAsync()
            {
                try { await Task.WhenAll(previousCallbacks, previousSearch); }
                finally
                {
                    if (previousCancellation != null) await previousCancellation.RetireAsync();
                    if (AcceptsRelationalExplorer(owner, runtime) && _relationalExplorerSearchIdentity == identity)
                    { HideObjectExplorerLoading(); StatusText = "The regular expression is invalid."; }
                }
            }
        }
        var cancellation = new ExplorerCancellationLifetime(_relationalExplorerCancellation!.Token);
        _relationalSearchCancellation = cancellation;
        var ct = cancellation.Token;
        var task = RunSearchAsync(); _relationalExplorerSearchTask = task;
        try { await task; }
        finally { if (AcceptsRelationalExplorer(owner, runtime) && _relationalExplorerSearchIdentity == identity) HideObjectExplorerLoading(); }

        async Task RunSearchAsync()
        {
            try
            {
                // Serialize superseding jobs after actual SQL/text-reader disposal, not merely cancellation signals.
                try { await Task.WhenAll(previousCallbacks, previousSearch); }
                finally { if (previousCancellation != null) await previousCancellation.RetireAsync(); }
                ct.ThrowIfCancellationRequested();
                if (!AcceptsRelationalExplorer(owner, runtime) || _relationalExplorerSearchIdentity != identity) return;
                await ShowObjectExplorerLoadingAsync("Searching resources...", query);
                // The job includes .NET text scanning and result assembly; SQL providers remain genuinely async.
                await Task.Run(async () =>
                {
                    var scope = await runtime.Explorer.OpenScopeAsync(scopeKey, resourceIds, sortCulture, ct).ConfigureAwait(false);
                    var request = new ExplorerSearchRequest(scope, query, regex, target, identity);
                    var results = new ExplorerSearchResultBuilder(scope, identity, new(MaximumMetadataRows: RelationalExplorerResultLimit));
                    DateTime lastPaint = DateTime.MinValue;
                    await foreach (var batch in searchOwner.SearchAsync(runtime.Explorer, runtime.SearchSources, request, ct).ConfigureAwait(false))
                    {
                        results.Append(batch);
                        if (batch.Coverage.GenerationChanged)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!AcceptsRelationalExplorer(owner, runtime) || _relationalExplorerSearchIdentity != identity) return;
                                RootNodes.Clear(); _relationalExplorerNodes.Clear(); _relationalExplorerNodeViews.Clear();
                                StatusText = "Search changed during indexing or source updates. Results were discarded; run the search again.";
                            });
                            return;
                        }
                        if (!batch.Coverage.Completed && DateTime.UtcNow - lastPaint < TimeSpan.FromMilliseconds(250)) continue;
                        lastPaint = DateTime.UtcNow; var tree = results.Snapshot();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!AcceptsRelationalExplorer(owner, runtime) || _relationalExplorerSearchIdentity != identity || !searchOwner.Accepts(identity)) return;
                            RootNodes.Clear(); _relationalExplorerNodes.Clear(); _relationalExplorerNodeViews.Clear();
                            foreach (var root in tree) RootNodes.Add(MapResult(root));
                            var coverage = batch.Coverage;
                            StatusText = $"{coverage.Matched} matches across {coverage.Considered} resources. " +
                                (coverage.FullyCurrent ? "Search complete." : $"{(coverage.Completed ? "Scan ended" : "Scanning")}; {coverage.Incomplete} incomplete, {coverage.IndexStaleOrUnindexed} stale/unindexed. " +
                                (coverage.PhysicalDiscoveryNotFrozen ? "Physical discovery is not frozen. " : "") +
                                (!coverage.DiscoveryReconciled ? "Derived discovery is not reconciled." : coverage.IsSubset ? "Unloaded resources are excluded." : ""));
                        });
                    }
                }, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                InternalLogService.Error(ex, "Relational explorer search did not complete.");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!AcceptsRelationalExplorer(owner, runtime) || _relationalExplorerSearchIdentity != identity) return;
                    StatusText = ex is ExplorerLimitException ? "Search reached the bounded result-tree limit; results are partial, not complete."
                        : "Search did not complete. Refresh the scope and retry.";
                });
            }
            finally
            {
                await cancellation.RetireAsync();
                if (ReferenceEquals(_relationalSearchCancellation, cancellation)) _relationalSearchCancellation = null;
            }
        }
        FileSystemNode MapResult(ExplorerResultNode result)
        {
            var node = CreateRelationalExplorerNode(result.Node with { ChildrenState = ExplorerChildrenState.Loaded });
            foreach (var child in result.Children) node.Children.Add(MapResult(child));
            if (result.Match is { } match)
            {
                if (match.ContentMatched) { node.ContentSearchPattern = match.Query; node.ContentSearchUseRegex = match.UseRegex; }
                foreach (int column in match.MatchingColumnIndexes) node.SpreadsheetSearchFilters[column] = match.Query;
            }
            return node;
        }
    }

    private async Task PrepareRelationalTextFileNameSeedAsync(string path, long? preferredScopeResourceKey = null)
    {
        Dispatcher.VerifyAccess();
        var runtime = RelationalStateRuntime; var scope = _relationalExplorerScope;
        var owner = _relationalExplorerOwner; if (scope == null) return;
        var address = await runtime.Explorer.ResolveAddressAsync(scope, path, preferredScopeResourceKey,
            _relationalExplorerCancellation!.Token);
        if (!AcceptsRelationalExplorer(owner, runtime)) return;
        RememberSeed(path, address?.TextFileNameSeed);
        if (address != null)
        {
            RememberSeed(address.CanonicalLocator, address.TextFileNameSeed);
            RememberSeed(address.ReadableLocator, address.TextFileNameSeed);
        }
        void RememberSeed(string key, string? seed)
        {
            long size = (long)key.Length + (seed?.Length ?? 0);
            if (size > 4 * 1024 * 1024) throw new ExplorerLimitException("TXT hierarchy exceeds its metadata budget.");
            if (_relationalTextFileNameSeeds.Count >= 1000 || _relationalTextSeedCharacters + size > 4 * 1024 * 1024)
            { _relationalTextFileNameSeeds.Clear(); _relationalTextSeedCharacters = 0; }
            if (_relationalTextFileNameSeeds.TryGetValue(key, out var previous)) _relationalTextSeedCharacters -= key.Length + (previous?.Length ?? 0);
            _relationalTextFileNameSeeds[key] = seed; _relationalTextSeedCharacters += size;
        }
    }

    private string? GetRelationalTextFileNameSeed(string path) => _relationalTextFileNameSeeds.GetValueOrDefault(path);

    private ContextMenu CreateRelationalObjectExplorerContextMenu(FileSystemNode node)
    {
        var menu = new ContextMenu();
        if (!_relationalExplorerNodes.TryGetValue(node, out var summary)) return menu;
        bool present = summary.Exists && summary.IsScopeResourceLoaded;
        // Comparison owner supplies the actual selected-source route. These availability checks use summaries only.
        bool canCompare = GetRelationalComparisonAvailability(node, out bool canCompareTableData);
        if (present && canCompare)
        {
            Add(summary.Category == ExplorerCategory.Tables ? "Compare Table Metadata" : "Compare",
                () => CompareObjectExplorerNodeAsync(node, false));
            if (canCompareTableData)
                Add("Compare Table Data", () => CompareObjectExplorerNodeAsync(node, true));
        }
        if (summary.IsScopeResourceRoot && summary.SnapshotKey.HasValue)
        {
            Add("History", () => InvokeSnapshotAsync(RelationalDatabaseHistoryHandler), RelationalDatabaseHistoryHandler != null);
            Add("Export DB Data", () => InvokeSnapshotAsync(RelationalDatabaseExportHandler), RelationalDatabaseExportHandler != null);
        }
        if (summary.IsScopeResourceRoot && !string.IsNullOrEmpty(summary.ScopeResourceId))
        {
            Add(summary.IsScopeResourceLoaded ? "Unload" : "Load", () => SetScopeResourceLoadedAsync(summary.ScopeResourceId, !summary.IsScopeResourceLoaded));
            Add("Remove From Scope", () => RemoveScopeResourceAsync(summary.ScopeResourceId), RelationalScopeResourceRemovalHandler != null);
        }
        if (summary.IsVirtualFolder) Add("Disband", () => DisbandVirtualFolderAsync(summary.VirtualFolderId));
        Add("New Virtual Folder Here", () => CreateVirtualFolderAsync(summary.NaturalParentKey));
        if (summary.IsDirectory && !summary.IsVirtualFolder && summary.IsScopeResourceLoaded)
            Add("New Virtual Folder Inside", () => CreateVirtualFolderAsync(summary.NodeKey));
        Add("Add Existing Resources", AddRelationalExistingResourcesAsync);
        if (summary.Role == ExplorerNodeRole.DiagramRoot) Add("New Diagram", () => { CreateNewBlankDiagram(); return Task.CompletedTask; });
        if (present && !summary.IsVirtualDocument && summary.ResourceKind is ResourceKind.File or ResourceKind.Folder && !summary.IsVirtualFolder)
            Add("Open Containing Folder", () => { OpenContainingFolder(node); return Task.CompletedTask; });
        return menu;
        void Add(string label, Func<Task> command, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled, ToolTip = enabled ? null : "Selected relational operation is not connected." };
            item.Click += async (_, _) =>
            {
                try { await command(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { InternalLogService.Error(ex, "Relational explorer command failed."); StatusText = "The selected command did not complete: " + ex.Message; }
            };
            menu.Items.Add(item);
        }
        Task InvokeSnapshotAsync(Func<ExplorerNodeSummary, CancellationToken, Task>? handler) => handler == null
            ? throw new InvalidOperationException("The selected snapshot operation is not connected.")
            : handler(summary, _relationalExplorerCancellation?.Token ?? CancellationToken.None);
    }

    private async Task AddRelationalExistingResourcesAsync()
    {
        RequireRelationalWrite();
        var scope = _relationalExplorerScope ?? throw new InvalidOperationException("Select a relational scope first.");
        var runtime = RelationalStateRuntime; var owner = _relationalExplorerOwner;
        var token = (_relationalExplorerCancellation ?? throw new InvalidOperationException("Explorer scope lifetime has not begun.")).Token;
        var catalogue = new ExplorerResourceCatalogue(runtime.Session);
        var picker = new RelationalQueryPickerWindow<ExplorerResourceCandidate>("Add Existing Resource", async (cursor, ct) =>
        {
            var page = await catalogue.ListAsync(scope, (ExplorerResourceCatalogueCursor?)cursor, ct: ct);
            return new(page.Items, page.Next);
        }, item => $"{item.Header.Type}: {item.Header.Name} | {item.Header.Source} | {item.Header.Details}") { Owner = this };
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _relationalExplorerPickerDrains.Add(drained.Task);
        ExplorerResourceCandidate candidate;
        try
        {
            picker.LinkOwnerLifetime(token);
            token.ThrowIfCancellationRequested();
            if (!AcceptsRelationalExplorer(owner, runtime)) throw new OperationCanceledException("Scope changed before choosing a resource.");
            if (picker.ShowDialog() != true || picker.SelectedSummary == null) return;
            candidate = picker.SelectedSummary;
        }
        finally
        {
            // Modal return does not mean its reader or first cancellation callback batch has finished.
            try { await picker.DrainQueriesAsync(); }
            finally
            {
                _relationalExplorerPickerDrains.Remove(drained.Task);
                drained.TrySetResult();
            }
        }
        token.ThrowIfCancellationRequested();
        if (!AcceptsRelationalExplorer(owner, runtime)) throw new OperationCanceledException("Scope changed while choosing a resource.");
        if (RelationalScopeResourceAdditionHandler != null)
            await RelationalScopeResourceAdditionHandler(candidate, token);
        else
        {
            if (candidate.Header.Kind is not (ResourceKind.File or ResourceKind.Folder))
                throw new InvalidOperationException("Adding typed snapshot/diagram targets requires the selected state command provider.");
            if (_activeScope!.Resources.Any(r => ExplorerResourceCatalogue.SameIdentity(r.Kind, r.Path, candidate.Header.Kind, candidate.Header.Path))) return;
            _activeScope.Resources.Add(candidate.Header.CreateScopedResource());
            await SaveRelationalScopeAsync(token);
        }
        await LoadRelationalRootsAsync();
    }

    private async Task RemoveRelationalScopeResourceAsync(string resourceId)
    {
        RequireRelationalWrite();
        var resource = _relationalExplorerScope?.Resources.FirstOrDefault(r => r.ResourceId.Equals(resourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected scope resource is missing.");
        var handler = RelationalScopeResourceRemovalHandler ?? throw new InvalidOperationException(
            "Removing a resource and clearing selected diagram links requires the selected state command provider.");
        await handler(resource, _relationalExplorerCancellation!.Token);
        await LoadRelationalRootsAsync();
    }

    private void QueueRelationalExplorerFocus(string path, long? scopeResourceKey = null)
    {
        Dispatcher.VerifyAccess();
        var scope = _relationalExplorerScope; if (scope == null || _relationalExplorerCancellation == null) return;
        var runtime = RelationalStateRuntime; var owner = _relationalExplorerOwner;
        var token = _relationalExplorerCancellation.Token; long generation = ++_relationalExplorerFocusGeneration;
        var previous = _relationalExplorerFocusTask; _relationalExplorerFocusTask = FocusAsync();
        async Task FocusAsync()
        {
            try
            {
                await previous; token.ThrowIfCancellationRequested();
                if (generation != _relationalExplorerFocusGeneration || !AcceptsRelationalExplorer(owner, runtime)) return;
                var view = scope with { VirtualFolders = [] }; // Legacy explicit focus is a natural, ungrouped subtree.
                var summary = scopeResourceKey.HasValue ? await runtime.Explorer.GetResourceRootAsync(view, scopeResourceKey.Value, token)
                    : (await runtime.Explorer.ResolveAddressAsync(view, path, ct: token))?.Node;
                if (summary?.IsDirectory != true) throw new InvalidOperationException("The selected folder cannot be resolved in this scope.");
                if (generation != _relationalExplorerFocusGeneration || !AcceptsRelationalExplorer(owner, runtime)) return;
                RootNodes.Clear(); _relationalExplorerNodes.Clear(); _relationalExplorerNodeViews.Clear(); ObjectExplorerSearchTextBox.Clear();
                var node = CreateRelationalExplorerNode(summary); _relationalExplorerNodeViews[node] = view; node.IsExpanded = true; RootNodes.Add(node);
                await LoadRelationalChildrenAsync(node);
                if (generation == _relationalExplorerFocusGeneration && AcceptsRelationalExplorer(owner, runtime) && node.IsLoaded)
                    StatusText = "Focused resource '" + node.Name + "'.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalLogService.Error(ex, "Relational explorer focus failed.");
                if (generation == _relationalExplorerFocusGeneration && AcceptsRelationalExplorer(owner, runtime)) StatusText = "Resource focus did not complete.";
            }
        }
    }

    // Separate from legacy ReferenceEntity resolution. This is dictionary access only during paint.
    private IReadOnlyDictionary<string, ReferenceHighlightStyleSetting> GetRelationalReferenceHighlightStylesForFile(string filePath)
    {
        string language = _relationalIndexLanguages?.LanguageFor(filePath) ?? CodeWindowSettings.GetDefaultLanguageForExtension(filePath);
        return _relationalHighlightStyles.TryGetValue(language, out var styles) ? styles : EmptyRelationalHighlightStyles;
    }

    // Call after highlight/language preference changes, outside editor paint.
    private void RebuildRelationalReferenceHighlightStyles()
    {
        Dispatcher.VerifyAccess();
        _relationalIndexLanguages = ExplorerIndexLanguagePolicy.Capture(_appSettings.CodeWindows);
        var paint = _relationalReferenceCatalogue?.Paint;
        if (paint == null) return;
        var owner = _relationalExplorerOwner; var runtime = RelationalStateRuntime;
        long generation = ++_relationalHighlightGeneration;
        var ct = _relationalExplorerCancellation!.Token;
        var previous = _relationalHighlightTask;
        ImmutableArray<ReferenceStyleValue> styles;
        try { styles = ReferenceStyleValue.CaptureWithRuntimeDefaults(_appSettings.ReferenceHighlights); }
        catch (ExplorerLimitException ex)
        {
            InternalLogService.Error(ex, "Relational highlight metadata exceeds its budget.");
            StatusText = "Reference highlight rules exceed the metadata budget; the last prepared styles are retained.";
            return;
        }
        _relationalHighlightTask = BuildAsync();
        async Task BuildAsync()
        {
            try
            {
                await previous.ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                var maps = await Task.Run(() =>
                {
                    var shared = new SharedReferenceHighlightStyles(paint, styles, CodeWindowSettings.SupportedLanguages, ct);
                    var values = new Dictionary<ReferenceStyleValue, ReferenceHighlightStyleSetting>();
                    return shared.ByLanguage.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenDictionary(name => name.Key, name =>
                    {
                        if (!values.TryGetValue(name.Value, out var value)) values.Add(name.Value, value = name.Value.ToSetting());
                        return value;
                    }, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                }, ct).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!AcceptsRelationalExplorer(owner, runtime) || generation != _relationalHighlightGeneration) return;
                    _relationalHighlightStyles = maps;
                    ApplyReferenceHighlightsToOpenWindows(); ApplyReferenceHighlightsToPreview();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalLogService.Error(ex, "Relational highlight metadata construction failed.");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (AcceptsRelationalExplorer(owner, runtime) && generation == _relationalHighlightGeneration)
                        StatusText = "Reference highlights could not be prepared. Reference coverage is not proof of paint readiness.";
                });
            }
        }
    }

    // Documents/navigation owner can ask for a fresh typed scope; empty/stale indexes cannot mean "no targets".
    private async Task<ExplorerScope> CaptureRelationalReferenceScopeAsync(CancellationToken ct = default)
    {
        Dispatcher.VerifyAccess();
        if (_relationalIndexProgress?.FullyPublished != true || _relationalReferenceCatalogue?.Coverage.FullyPublished != true)
            throw new InvalidOperationException("Derived references are still indexing or incomplete.");
        var runtime = RelationalStateRuntime; var owner = _relationalExplorerOwner;
        await using var linked = new ExplorerCancellationLifetime(ct, _relationalExplorerCancellation!.Token);
        var scope = _relationalExplorerScope ?? throw new InvalidOperationException("No relational scope is selected.");
        var context = await ReadContextAsync();
        if (!AcceptsRelationalExplorer(owner, runtime) || !runtime.References.IsDiscoveryReady(context) ||
            context.CatalogueGeneration != _relationalReferenceCatalogue.Paint.Context.CatalogueGeneration ||
            context.ScopeVersion != scope.Context.ScopeVersion || context.SnapshotCatalogueVersion != scope.Context.SnapshotCatalogueVersion ||
            context.DiagramCatalogueVersion != scope.Context.DiagramCatalogueVersion)
            throw new IndexGenerationChangedException();
        return scope;
        async Task<IndexRequestContext> ReadContextAsync()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return await runtime.Index.CaptureContextAsync(scope.Context.ScopeKey, scope.Context.UnloadedScopeResourceKeys,
                token: linked.Token).ConfigureAwait(false);
        }
    }

    // Parent awaits before disposing/replacing the runtime or finishing window close.
    private async Task StopRelationalExplorerAsync()
    {
        Dispatcher.VerifyAccess();
        var rootCancellation = _relationalRootCancellation;
        var rootCallbacks = rootCancellation?.Cancel() ?? Task.CompletedTask;
        RetireRelationalExplorerContext();
        try { await Task.WhenAll(rootCallbacks, _relationalRootLoadTask, _relationalExplorerRetirement); }
        finally { if (rootCancellation != null) await rootCancellation.RetireAsync(); }
    }
}

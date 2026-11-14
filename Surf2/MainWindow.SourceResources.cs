using System.IO;
using System.Windows;
using System.Windows.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.LiveSql;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RemoteFolders;
using Surf2.Services.SourceResources;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public partial class MainWindow
{
    private readonly Dictionary<Guid, GitCheckoutInfo> _gitCheckouts = [];
    private readonly Dictionary<FileSystemNode, Func<Task>> _sourceActions = [];
    private readonly Dictionary<FileSystemNode, ScopedResource> _sourceActionTargets = [];
    private readonly Dictionary<Guid, List<Window>> _sourceWindows = [];
    private readonly HashSet<Guid> _sourceChecks = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _sourcePreparations = [];
    private readonly HashSet<Task> _sourcePreparationDrains = [];
    private readonly Dictionary<Guid, Task> _sourceMemberDrains = [];
    private readonly Dictionary<Guid, PairedFolderWatch> _pairedFolderWatches = [];
    private readonly HashSet<Guid> _pendingBlobRefreshes = [];
    private readonly HashSet<Task> _sourceBackgroundTasks = [];

    private void SourceAdmissionRejected(Guid memberId, string reason)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_relationalStateClosing || _activeScope?.Resources.FirstOrDefault(r => r.PublicId == memberId) is not { } member ||
                ResourceWorkspace.IsAdmitted(memberId)) return;
            if (_sourcePreparations.TryGetValue(member.ContainerId, out var preparation)) preparation.Cancel();
            CloseResourceViews(member);
            ResourceWorkspace.Block(member, reason, release: true);
            string token = ResourceWorkspace.UnloadedToken(member);
            if (!_workspaceState.UnloadedResourceIds.Contains(token)) _workspaceState.UnloadedResourceIds.Add(token);
            StatusText = member.DisplayName + ": " + reason;
            try { await SaveWorkspaceStateAsync(); await LoadRelationalRootsAsync(); }
            catch (Exception error) { StatusText = RemoteFolderServices.DescribeError(error); }
        });
    }

    private async Task StopSourcePreparationsAsync()
    {
        foreach (var preparation in _sourcePreparations.Values.ToArray()) preparation.Cancel();
        await Task.WhenAll(_sourcePreparationDrains.ToArray());
        await Task.WhenAll(_sourceBackgroundTasks.ToArray());
        foreach (var watcher in _pairedFolderWatches.Values) watcher.Dispose();
        _pairedFolderWatches.Clear();
    }

    private void StartPendingSourceRefreshes()
    {
        if (_activeScope is not { } scope) return;
        foreach (var id in _pendingBlobRefreshes.ToArray())
        {
            _pendingBlobRefreshes.Remove(id);
            if (scope.Resources.FirstOrDefault(r => r.PublicId == id) is not { } member || !ResourceWorkspace.IsAdmitted(id)) continue;
            var task = RefreshAsync(); _sourceBackgroundTasks.Add(task); _ = RetireAsync(task);
            async Task RetireAsync(Task work) { try { await work; } finally { _sourceBackgroundTasks.Remove(work); } }
            async Task RefreshAsync()
            {
                await Task.Yield();
                try
                {
                    if (!ReferenceEquals(scope, _activeScope) || _relationalStateClosing) return;
                    string? previous = ResourceWorkspace.Prepared(id)?.PhysicalRoot;
                    bool ready = await PrepareSourceResourceAsync(scope, member, true, CancellationToken.None);
                    if (!ReferenceEquals(scope, _activeScope) || _relationalStateClosing) return;
                    if (!ready || previous != ResourceWorkspace.Prepared(id)?.PhysicalRoot)
                    { CloseResourceViews(member); await LoadRelationalRootsAsync(); }
                    if (ResourceWorkspace.Status(id) is { } status) StatusText = member.DisplayName + ": " + status;
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (ReferenceEquals(scope, _activeScope)) StatusText = RemoteFolderServices.DescribeError(error); }
            }
        }
    }

    private PairedFolderWatch WatchPairedFolder(ScopedResource member, string folder)
    {
        if (_pairedFolderWatches.Remove(member.PublicId, out var previous)) previous.Dispose();
        PairedFolderWatch? watch = null;
        watch = new(folder, () =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (!_pairedFolderWatches.TryGetValue(member.PublicId, out var current) || !ReferenceEquals(current, watch) || !ResourceWorkspace.IsAdmitted(member.PublicId)) return;
                ResourceWorkspace.Block(member, "Local files changed. Load this resource to reconcile the paired locations.");
                CloseResourceViews(member);
                ResourceWorkspace.Block(member, "Local files changed. Load this resource to reconcile the paired locations.", release: true);
                if (!_workspaceState.UnloadedResourceIds.Contains(ResourceWorkspace.UnloadedToken(member))) _workspaceState.UnloadedResourceIds.Add(ResourceWorkspace.UnloadedToken(member));
                try { await SaveWorkspaceStateAsync(); await LoadRelationalRootsAsync(); }
                catch (Exception error) { StatusText = RemoteFolderServices.DescribeError(error); }
            });
        });
        _pairedFolderWatches[member.PublicId] = watch; return watch;
    }

    private async Task PrepareSourceScopeAsync(Scope scope, IReadOnlySet<string> unloaded, CancellationToken ct)
    {
        ResourceWorkspace.Register(scope);
        foreach (var container in scope.ResourceContainers)
        {
            Guid active = ResourceWorkspace.ActiveMember(scope, container);
            foreach (var resource in scope.Resources.Where(r => r.ContainerId == container.ContainerId))
            {
                if (resource.PublicId != active || ResourceWorkspace.IsMarkedUnloaded(resource, unloaded))
                { if (ResourceWorkspace.IsAdmitted(resource.PublicId)) CloseResourceViews(resource); ResourceWorkspace.Block(resource, release: true); continue; }
                if (!ResourceWorkspace.IsAdmitted(resource.PublicId) || scope.ScopeId != _activeScope?.ScopeId)
                {
                    if (resource.Source?.Provider == SourceProvider.AzureBlob && resource.PairedResourceId == null)
                    {
                        PreparedFolderSource? cached = null;
                        try { cached = RemoteFolderServices.Default.Cache.TryOpenCurrent(resource.Source); } catch (IOException) { }
                        if (cached != null)
                        {
                            ResourceWorkspace.Admit(resource, cached, "Checking for updates in the background…");
                            _pendingBlobRefreshes.Add(resource.PublicId); continue;
                        }
                    }
                    using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (resource.PairedResourceId == null) startup.CancelAfter(TimeSpan.FromSeconds(30));
                    try { await PrepareSourceResourceAsync(scope, resource, resource.Source?.Provider == SourceProvider.AzureBlob, startup.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        ResourceWorkspace.Block(resource, "Startup preparation timed out. Use Load to retry.");
                        if (!_workspaceState.UnloadedResourceIds.Contains(ResourceWorkspace.UnloadedToken(resource))) _workspaceState.UnloadedResourceIds.Add(ResourceWorkspace.UnloadedToken(resource));
                    }
                }
            }
        }
    }

    private async Task<bool> PrepareSourceResourceAsync(Scope scope, ScopedResource resource, bool refresh, CancellationToken ct)
    {
        if (!scope.Resources.Contains(resource)) throw new OperationCanceledException("The resource is no longer in this scope.", ct);
        long requestedSelection = _relationalSelectionGeneration;
        while (_sourceChecks.Contains(resource.PublicId))
        {
            if (_sourcePreparations.TryGetValue(resource.ContainerId, out var pending)) pending.Cancel();
            if (_sourceMemberDrains.TryGetValue(resource.PublicId, out var drain)) await drain.WaitAsync(ct);
            else await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (requestedSelection != _relationalSelectionGeneration) throw new OperationCanceledException("The selected scope changed.", ct);
        }
        _sourceChecks.Add(resource.PublicId);
        var runtime = _relational;
        long selection = _relationalSelectionGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_sourcePreparations.TryGetValue(resource.ContainerId, out var prior)) prior.Cancel();
        _sourcePreparations[resource.ContainerId] = cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sourcePreparationDrains.Add(completion.Task);
        _sourceMemberDrains[resource.PublicId] = completion.Task;
        ct = cancellation.Token;
        string configuration = System.Text.Json.JsonSerializer.Serialize(new { resource.Path, resource.Source, resource.PairedResourceId });
        bool Current() => !ct.IsCancellationRequested && !_relationalStateClosing && ReferenceEquals(runtime, _relational) && selection == _relationalSelectionGeneration &&
            scope.Resources.Contains(resource) && ReferenceEquals(ResourceWorkspace.Find(resource.PublicId), resource) &&
            configuration == System.Text.Json.JsonSerializer.Serialize(new { resource.Path, resource.Source, resource.PairedResourceId });
        void Admit(PreparedFolderSource? prepared = null, string? status = null)
        {
            if (!Current()) { prepared?.Dispose(); throw new OperationCanceledException("The selected source context changed.", ct); }
            ResourceWorkspace.Admit(resource, prepared, status);
        }
        try
        {
            var folders = RemoteFolderServices.Default;
            var progress = new Progress<FolderSourceProgress>(p => StatusText = resource.DisplayName + ": " + p.Phase +
                (p.TotalBytes is { } total ? $" ({p.CompletedBytes:N0} / {total:N0} bytes)" : p.Files > 0 ? $" ({p.Files:N0} files)" : ""));
            var pair = resource.PairedResourceId is { } id ? scope.Resources.FirstOrDefault(r => r.PublicId == id) : null;
            var blob = resource.Source?.Provider == SourceProvider.AzureBlob ? resource : pair?.Source?.Provider == SourceProvider.AzureBlob ? pair : null;
            var local = (resource.Source == null || resource.Source.Provider == SourceProvider.LocalFileSystem) && resource.Kind == ResourceKind.Folder ? resource : pair != null && (pair.Source == null || pair.Source.Provider == SourceProvider.LocalFileSystem) ? pair : null;
            if (blob?.Source is { } blobSource && local != null)
            {
                ResourceWorkspace.Block(resource, "Checking paired locations…");
                GateSourceReads();
                await using var plan = await folders.Blob.CompareAsync(blobSource, local.Path, progress, ct);
                if (plan.HasDifferences)
                {
                    CloseResourceViews(resource);
                    var wizard = new BlobReconciliationWindow(folders.Blob, plan) { Owner = this };
                    if (wizard.ShowDialog() != true) throw new IOException("Reconciliation was declined. Load the resource to compare the locations again.");
                }
                // Start observation before the final local verification so no edit can slip between verification and admission.
                var watch = WatchPairedFolder(resource, local.Path);
                var prepared = await folders.Blob.OpenVerifiedPairAsync(plan, ct);
                if (watch.Changed) { prepared.Dispose(); throw new FolderConflictException("Local files changed during final verification. Load the resource and review again."); }
                Admit(prepared);
            }
            else if (resource.Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob)
            {
                PreparedFolderSource prepared;
                string? stale = null;
                try
                {
                    prepared = resource.Source.Provider == SourceProvider.Git
                        ? await folders.Git.PrepareAsync(resource.Source, resource.DisplayName, refresh, progress, ct)
                        : await folders.Blob.PrepareAsync(resource.Source, resource.DisplayName, refresh, progress, ct);
                }
                catch (Exception error) when (error is IOException or Azure.RequestFailedException)
                {
                    // A standalone source may retain its last complete publication. Paired sources never use this fallback.
                    var retained = folders.Cache.TryOpenCurrent(resource.Source);
                    if (retained == null) throw;
                    prepared = retained;
                    stale = "Refresh failed; using last complete local copy from " + prepared.Manifest.PreparedAtUtc.LocalDateTime.ToString("g") + ". " + RemoteFolderServices.DescribeError(error);
                }
                if (ct.IsCancellationRequested) { prepared.Dispose(); ct.ThrowIfCancellationRequested(); }
                Admit(prepared, stale);
            }
            else if (resource.Source?.Provider == SourceProvider.LiveSql)
            {
                string connection = folders.Credentials.Read(resource.Source) ?? throw new IOException("This desktop needs credentials. Use Reconnect on the resource.");
                var service = new LiveSqlService(connection);
                var qualification = await service.QualifyAsync(ct); qualification.RequireQualified();
                LiveSourceWorkspace.Register(resource, service, qualification);
                Admit();
            }
            else
            {
                if (resource.Kind == ResourceKind.Folder)
                {
                    GitCheckoutInfo? git = null;
                    try { git = await folders.Git.InspectAsync(resource.Path, ct); }
                    catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                    { StatusText = "Folder is available; Git details could not be read: " + RemoteFolderServices.DescribeError(error); }
                    if (git != null) _gitCheckouts[resource.PublicId] = git; else _gitCheckouts.Remove(resource.PublicId);
                }
                Admit();
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (_sourcePreparations.GetValueOrDefault(resource.ContainerId) == cancellation)
                ResourceWorkspace.Block(resource, "Loading cancelled.");
            throw;
        }
        catch (Exception error)
        {
            if (!Current()) throw new OperationCanceledException("The selected source context changed.", error, ct);
            string reason = RemoteFolderServices.DescribeError(error);
            ResourceWorkspace.Block(resource, reason); CloseResourceViews(resource); ResourceWorkspace.Block(resource, reason, release: true);
            if (!_workspaceState.UnloadedResourceIds.Contains(ResourceWorkspace.UnloadedToken(resource))) _workspaceState.UnloadedResourceIds.Add(ResourceWorkspace.UnloadedToken(resource));
            StatusText = resource.DisplayName + ": " + RemoteFolderServices.DescribeError(error);
            return false;
        }
        finally
        {
            _sourceChecks.Remove(resource.PublicId);
            if (!ResourceWorkspace.IsAdmitted(resource.PublicId) && _pairedFolderWatches.Remove(resource.PublicId, out var watch)) watch.Dispose();
            if (_sourcePreparations.GetValueOrDefault(resource.ContainerId) == cancellation) _sourcePreparations.Remove(resource.ContainerId);
            completion.TrySetResult(); _sourcePreparationDrains.Remove(completion.Task); _sourceMemberDrains.Remove(resource.PublicId);
        }
    }

    private ScopedResource? SourceMember(ExplorerNodeSummary summary) => summary.ScopeResourceKey is { } key &&
        _relationalExplorerScope?.Resources.FirstOrDefault(r => r.ScopeResourceKey == key) is { } item
        ? _activeScope?.Resources.FirstOrDefault(r => r.PublicId == item.PublicId) : null;

    private ExplorerNodeSummary DecorateSourceNode(ExplorerNodeSummary summary)
    {
        if (!summary.IsScopeResourceRoot || SourceMember(summary) is not { } member) return summary;
        var icon = member.Kind == ResourceKind.LiveDatabase ? FileSystemNodeIconKind.LiveDatabase : member.Source?.Provider switch
        {
            SourceProvider.Git => FileSystemNodeIconKind.GitRepository,
            SourceProvider.AzureBlob => FileSystemNodeIconKind.BlobStorage,
            _ when _gitCheckouts.ContainsKey(member.PublicId) => FileSystemNodeIconKind.GitFolder,
            _ => summary.IconKind
        };
        string details = member.Details;
        if (_gitCheckouts.TryGetValue(member.PublicId, out var git)) details += "\nBranch: " + git.Branch + "\nCommit: " + git.Commit;
        if (ResourceWorkspace.Prepared(member.PublicId) is { } prepared) details += "\nLast prepared: " + prepared.Manifest.PreparedAtUtc.LocalDateTime.ToString("g") + "\n" + prepared.Revision;
        if (ResourceWorkspace.Status(member.PublicId) is { } status) details += "\n" + status;
        return summary with { IconKind = icon, ToolTip = details };
    }

    private void AddSourceActionRow(FileSystemNode node, ExplorerNodeSummary summary)
    {
        if (!summary.IsScopeResourceRoot || SourceMember(summary) is not { } member || _activeScope == null || member.Kind is ResourceKind.File or ResourceKind.Diagram) return;
        var alternatives = _activeScope.Resources.Where(r => r.ContainerId == member.ContainerId && r.PublicId != member.PublicId).ToArray();
        string label = alternatives.Length == 0 ? member.Kind switch
        {
            ResourceKind.LiveDatabase => "Create or link a snapshot…",
            ResourceKind.DatabaseSnapshot => "Link a direct SQL connection…",
            _ when member.Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob => "Link a local folder…",
            _ when _gitCheckouts.ContainsKey(member.PublicId) => "Add Git repo connection…",
            _ => "Pair with Blob Storage…"
        } : alternatives.Length == 1 ? "Switch to " + ResourceSourceLabel(alternatives[0]) : "Switch source…";
        var action = new FileSystemNode("source-action://" + member.PublicId.ToString("N"), false, displayName: label,
            iconKind: FileSystemNodeIconKind.SourceAction, parentKey: node.NodeKey, scopeResourceId: member.ResourceId);
        _sourceActions[action] = () => alternatives.Length == 0 ? LinkSourceAsync(member) : SwitchSourceAsync(member, alternatives);
        if (alternatives.Length == 1) _sourceActionTargets[action] = alternatives[0];
        node.Children.Insert(0, action);
    }

    private static string ResourceSourceLabel(ScopedResource member) => member.DisplayName + " — " + (member.Kind switch
    {
        ResourceKind.DatabaseSnapshot => "snapshot",
        ResourceKind.LiveDatabase => "direct SQL connection",
        _ => member.Source?.Provider switch
        {
            SourceProvider.Git => "direct Git connection (" + (string.IsNullOrEmpty(member.Source.Ref) ? "default branch" : member.Source.Ref) +
                (string.IsNullOrEmpty(member.Source.Subfolder) ? "" : ", " + member.Source.Subfolder) + ")",
            SourceProvider.AzureBlob => "Blob Storage: " + member.Source.Container + "/" + member.Source.Prefix,
            _ => "local folder: " + member.Path
        }
    });

    private void RequireSourceContext(Scope scope, ScopedResource member, long selection)
    {
        if (_relationalStateClosing || selection != _relationalSelectionGeneration || !ReferenceEquals(scope, _activeScope) || !scope.Resources.Contains(member))
            throw new OperationCanceledException("The selected scope or resource changed. Repeat the action in its current scope.");
    }

    private async Task RenameSourceContainerAsync(ScopedResource member)
    {
        if (_activeScope is not { } scope) return;
        long selection = _relationalSelectionGeneration;
        RequireSourceContext(scope, member, selection);
        var container = scope.ResourceContainers.Single(c => c.ContainerId == member.ContainerId);
        var dialog = new RenameResourceWindow(container.Name) { Owner = this, Title = "Rename Resource Container" };
        if (dialog.ShowDialog() != true) return;
        RequireSourceContext(scope, member, selection);
        container.Name = dialog.ResourceName;
        if (scope.Resources.Count(r => r.ContainerId == container.ContainerId) == 1) member.DisplayNameOverride = dialog.ResourceName;
        await SaveScopeLibraryAsync(); RequireSourceContext(scope, member, selection);
        await LoadRelationalRootsAsync();
    }

    private async Task SwitchSourceAsync(ScopedResource current, IReadOnlyList<ScopedResource> choices)
    {
        if (_activeScope == null) return;
        var scope = _activeScope;
        long selection = _relationalSelectionGeneration;
        RequireSourceContext(scope, current, selection);
        var selected = choices.Count == 1 ? choices[0] : SelectSourceMember(choices);
        if (selected == null) return;
        var before = _workspaceState.UnloadedResourceIds.ToList();
        bool pairedBlob = selected.PairedResourceId.HasValue && (selected.Source?.Provider == SourceProvider.AzureBlob ||
            _activeScope.Resources.Any(r => r.PublicId == selected.PairedResourceId && r.Source?.Provider == SourceProvider.AzureBlob));
        if (pairedBlob) { ResourceWorkspace.Block(current); CloseResourceViews(current); }
        if (!await PrepareSourceResourceAsync(scope, selected, selected.Source?.Provider == SourceProvider.AzureBlob, CancellationToken.None))
        {
            RequireSourceContext(scope, current, selection);
            if (!pairedBlob) _workspaceState.UnloadedResourceIds = before;
            await SaveWorkspaceStateAsync(); RequireSourceContext(scope, current, selection); await LoadRelationalRootsAsync(); return;
        }
        RequireSourceContext(scope, current, selection);
        CloseResourceViews(current); ResourceWorkspace.Block(current, release: true);
        ResourceWorkspace.Select(scope, selected);
        _workspaceState.UnloadedResourceIds.RemoveAll(id => id == selected.ResourceId || id == current.ResourceId || id == ResourceWorkspace.UnloadedToken(current));
        await LoadRelationalRootsAsync(); RequireSourceContext(scope, current, selection); await SaveWorkspaceStateAsync();
    }

    private ScopedResource? SelectSourceMember(IReadOnlyList<ScopedResource> choices)
    {
        var window = new Window { Owner = this, Title = "Choose source", Width = 650, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Margin = new(12), ItemsSource = choices.Select(ResourceSourceLabel).ToArray() };
        var panel = new DockPanel(); var use = new Button { Content = "Use selected source", Margin = new(12), HorizontalAlignment = HorizontalAlignment.Right };
        use.Click += (_, _) => { if (list.SelectedIndex >= 0) window.DialogResult = true; };
        DockPanel.SetDock(use, Dock.Bottom); panel.Children.Add(use); panel.Children.Add(list); window.Content = panel;
        return window.ShowDialog() == true ? choices[list.SelectedIndex] : null;
    }

    private async Task LinkSourceAsync(ScopedResource member)
    {
        if (_activeScope == null) return;
        var scope = _activeScope; long selection = _relationalSelectionGeneration;
        RequireSourceContext(scope, member, selection);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_relationalStateLifetime.Token,
            _relationalSelectionLifetime?.Source.Token ?? CancellationToken.None);
        var ct = cancellation.Token;
        if (member.Kind == ResourceKind.LiveDatabase) { await CaptureLiveSourceSnapshotAsync(member); return; }
        if (member.PairedResourceId is { } existingId && _activeScope.Resources.FirstOrDefault(r => r.PublicId == existingId) is { } existing)
        {
            if (existing.Kind == ResourceKind.LiveDatabase) await ReconnectSourceAsync(existing);
            else await SwitchSourceAsync(member, [existing]);
            return;
        }
        ScopedResource? added = member.Kind == ResourceKind.DatabaseSnapshot ? ScopeSourceActions.AddLiveSql(this, scope, member)
            : member.Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob ? await ScopeSourceActions.LinkLocalAsync(this, scope, member, ct)
            : _gitCheckouts.ContainsKey(member.PublicId) ? await ScopeSourceActions.AddGitAsync(this, scope, member, ct)
            : await ScopeSourceActions.AddBlobAsync(this, scope, member, ct);
        RequireSourceContext(scope, member, selection);
        if (added == null) return;
        if (member.Kind == ResourceKind.DatabaseSnapshot && _relational != null &&
            !await SourceSnapshotLinker.VerifyExplicitAsync(this, _relational, member, added, ct))
        {
            RequireSourceContext(scope, member, selection);
            scope.Resources.Remove(added); member.PairedResourceId = null; scope.EnsureResourceContainers(); return;
        }
        RequireSourceContext(scope, member, selection);
        await SaveScopeLibraryAsync(ct); RequireSourceContext(scope, member, selection); ResourceWorkspace.Register(scope);
        if (added.PairedResourceId.HasValue && (added.Source?.Provider == SourceProvider.AzureBlob || member.Source?.Provider == SourceProvider.AzureBlob))
        {
            CloseResourceViews(member); ResourceWorkspace.Block(member);
            await PrepareSourceResourceAsync(scope, member, true, ct); RequireSourceContext(scope, member, selection);
        }
        await LoadRelationalRootsAsync();
    }

    private async Task RefreshSourceAsync(ScopedResource resource)
    {
        if (_activeScope == null) return;
        var scope = _activeScope; long selection = _relationalSelectionGeneration;
        RequireSourceContext(scope, resource, selection);
        bool pairedBlob = resource.PairedResourceId.HasValue && (resource.Source?.Provider == SourceProvider.AzureBlob ||
            _activeScope.Resources.Any(r => r.PublicId == resource.PairedResourceId && r.Source?.Provider == SourceProvider.AzureBlob));
        ResourceWorkspace.Block(resource);
        if (pairedBlob) GateSourceReads(); else CloseResourceViews(resource);
        bool ready = await PrepareSourceResourceAsync(scope, resource, true, CancellationToken.None);
        RequireSourceContext(scope, resource, selection);
        if (ready)
            _workspaceState.UnloadedResourceIds.RemoveAll(id => id == resource.ResourceId || id == ResourceWorkspace.UnloadedToken(resource));
        await LoadRelationalRootsAsync(); RequireSourceContext(scope, resource, selection); await SaveWorkspaceStateAsync();
    }

    private Task OpenLiveQueryAsync(ScopedResource source, string? schema = null, string? table = null)
    {
        var service = LiveSourceWorkspace.Service(source.PublicId);
        var window = new SqlQueryWindow(service, table == null ? null : "SELECT TOP (@limit) * FROM " + SqlName.FormatMultipartName(schema!, table)) { Owner = this };
        TrackSourceWindow(source, window); window.Show(); return Task.CompletedTask;
    }

    private void TrackSourceWindow(ScopedResource source, Window window)
    {
        if (!_sourceWindows.TryGetValue(source.PublicId, out var list)) _sourceWindows[source.PublicId] = list = [];
        if (list.Contains(window)) return;
        list.Add(window); window.Closed += (_, _) => list.Remove(window);
    }

    private void TrackResourceWindow(Window window, long? snapshotKey = null, string? path = null)
    {
        if (_activeScope == null) return;
        foreach (var member in _activeScope.Resources)
        {
            bool owns = snapshotKey.HasValue && _relationalExplorerScope?.Resources.Any(r => r.PublicId == member.PublicId && r.Snapshot?.SnapshotKey == snapshotKey) == true;
            if (path != null)
            {
                string locator = ResourceWorkspace.ToPersistentPath(path, member.PublicId);
                owns |= ResourceWorkspace.TryParse(locator, out var id, out _) && id == member.PublicId;
                owns |= LiveSourceWorkspace.TryParse(path, out id, out _) && id == member.PublicId;
                owns |= member.Kind == ResourceKind.Folder && Path.IsPathFullyQualified(path) && Path.IsPathFullyQualified(member.PhysicalPath) && ExplorerCompatibility.IsWithinPhysicalRoot(path, member.PhysicalPath);
            }
            if (owns) TrackSourceWindow(member, window);
        }
    }

    private async Task ReconnectSourceAsync(ScopedResource resource)
    {
        if (_activeScope is not { } scope) return;
        long selection = _relationalSelectionGeneration;
        RequireSourceContext(scope, resource, selection);
        if (resource.Source is not { } source || source.Provider == SourceProvider.LocalFileSystem) return;
        if (source.Provider == SourceProvider.LiveSql)
        {
            var dialog = new LiveSqlConnectionWindow(RemoteFolderServices.Default.Credentials.Read(source)) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            RequireSourceContext(scope, resource, selection);
            if (source.Endpoint != dialog.Server || source.DatabaseName != dialog.Database)
            {
                if (MessageBox.Show(this, "This connection points to a different server or database. Change this resource's source? Existing snapshot links will be checked before refresh.",
                    "Change SQL source", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                source = source.Copy(); source.SourceId = Guid.NewGuid(); source.ConfigurationRevision = 1;
                source.Endpoint = dialog.Server; source.DatabaseName = dialog.Database;
                resource.Source = source; resource.Path = "sql://" + source.SourceId.ToString("N");
            }
            RemoteFolderServices.Default.Credentials.Save(source, dialog.ConnectionString);
        }
        else if (source.Provider == SourceProvider.Git)
        {
            var dialog = new AddGitSourceWindow(source, source.Endpoint) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result == null) return;
            RequireSourceContext(scope, resource, selection);
            var replacement = dialog.Result.Copy(); replacement.SourceId = Guid.NewGuid(); replacement.ConfigurationRevision = 1;
            resource.Source = replacement; resource.Path = replacement.Endpoint;
        }
        else if (source.Provider == SourceProvider.AzureBlob)
        {
            var dialog = new AddBlobSourceWindow(source) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result == null) return;
            RequireSourceContext(scope, resource, selection);
            string? credential = RemoteFolderServices.Default.Credentials.Read(dialog.Result);
            var replacement = dialog.Result.Copy(); replacement.SourceId = Guid.NewGuid(); replacement.ConfigurationRevision = 1;
            if (credential != null) RemoteFolderServices.Default.Credentials.Save(replacement, credential);
            resource.Source = replacement; resource.Path = replacement.Endpoint.TrimEnd('/') + "/" + replacement.Container + "/" + replacement.Prefix;
        }
        RequireSourceContext(scope, resource, selection);
        if (_sourcePreparations.TryGetValue(resource.ContainerId, out var preparation)) preparation.Cancel();
        await SaveScopeLibraryAsync(); RequireSourceContext(scope, resource, selection);
        ResourceWorkspace.Register(scope);
        await RefreshSourceAsync(resource);
    }

    private Task CaptureLiveSourceSnapshotAsync(ScopedResource live, ScopedResource? snapshotResource = null) =>
        RunRelationalSnapshotWorkflowAsync(async work =>
        {
            if (work.Scope == null || live.Source == null) return;
            string connection = RemoteFolderServices.Default.Credentials.Read(live.Source) ?? throw new IOException("Reconnect this SQL resource first.");
            var scope = work.Scope.Snapshot();
            SnapshotSummary? replacement = null;
            if (snapshotResource != null)
            {
                int ordinal = scope.Resources.ToList().FindIndex(r => r.PublicId == snapshotResource.PublicId);
                var targets = await work.Runtime.StateStore.ReadRuntimeScopeTargetsAsync(work.Scope.ExpectedToken, work.Token);
                long key = targets.Single(t => t.ResourceOrdinal == ordinal).SnapshotKey ?? throw new IOException("The linked snapshot is unavailable.");
                replacement = await work.Runtime.Snapshots.GetSnapshotAsync(key, work.Token);
                if (replacement?.CurrentVersionKey is { } version)
                {
                    var previous = await work.Runtime.Snapshots.ReadSnapshotOriginAsync(key, work.Token);
                    var current = await new LiveSqlService(connection).OriginAsync(live.Source.SourceId, work.Token);
                    if (previous == null || !previous.AgreesWith(current))
                        if (MessageBox.Show(this, $"The saved and current database origins do not fully agree.\n\nSaved: {previous?.Server}/{previous?.Database} ({previous?.Identifier})\nCurrent: {current.Server}/{current.Database} ({current.Identifier})\n\nUse this source to refresh the selected snapshot?",
                            "Confirm snapshot source", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                }
            }
            var capture = new RelationalDatabaseCaptureWindow(work.Runtime, scope, work.Scope.ExpectedToken, replacement,
                allowTargetSelection: false, sourceConnectionString: connection, sourceId: live.Source.SourceId) { Owner = this };
            TrackSourceWindow(live, capture); OwnRelationalSnapshotWindow(work, capture, capture.CloseAndDrainAsync);
            bool? accepted;
            try { accepted = capture.ShowDialog(); } finally { await capture.CloseCompletion; }
            if (accepted != true || capture.Result is not { } result) return;
            RequireRelationalSnapshotContext(work);
            var updated = result.UpdatedScope;
            var liveMember = updated.Resources.Single(r => r.PublicId == live.PublicId);
            var snapshot = snapshotResource == null ? updated.Resources.Last(r => r.Kind == ResourceKind.DatabaseSnapshot && r.Path == result.Snapshot.SnapshotId)
                : updated.Resources.Single(r => r.PublicId == snapshotResource.PublicId);
            snapshot.PairedResourceId = liveMember.PublicId;
            if (liveMember.PairedResourceId == null)
            { snapshot.ContainerId = liveMember.ContainerId; liveMember.PairedResourceId = snapshot.PublicId; }
            updated.EnsureResourceContainers();
            var token = await work.Runtime.StateStore.SaveScopeAsync(updated, result.UpdatedScopeToken, Guid.NewGuid(), work.Token);
            ResourceWorkspace.Register(updated);
            await AdoptRelationalSnapshotScopeAsync(work, result with { UpdatedScopeToken = token, UpdatedScope = updated });
            StatusText = result.SkippedTables.Count == 0 ? "Snapshot refresh completed." : "Snapshot refreshed; skipped missing tables: " + string.Join(", ", result.SkippedTables);
        });

    private ContextMenu? SourceActionContextMenu(FileSystemNode node)
    {
        if (!_sourceActionTargets.TryGetValue(node, out var target) || target.Kind != ResourceKind.DatabaseSnapshot || _activeScope == null) return null;
        var live = _activeScope.Resources.FirstOrDefault(r => r.PublicId == target.PairedResourceId && r.Kind == ResourceKind.LiveDatabase);
        if (live == null) return null;
        var menu = new ContextMenu(); var refresh = new MenuItem { Header = "Refresh Snapshot" };
        refresh.Click += async (_, _) => await CaptureLiveSourceSnapshotAsync(live, target); menu.Items.Add(refresh); return menu;
    }

    private void GateSourceReads()
    {
        // A no-change pair check may keep resident windows, but pending reads must not publish during the gate.
        _relationalDocumentContextGeneration++;
        RetireRelationalExplorerContext();
        RetireRelationalNameCatalogue();
        _referenceIndex = ScopeReferenceIndex.Empty;
        ++_relationalComparisonGeneration;
        _ = _activeRelationalComparison?.Cancel();
        _relationalPreviewLifetime?.InvalidateContext();
        _relationalNavigationLifetime?.InvalidateContext();
        foreach (var owner in _relationalDocumentLifetimes.Values) owner.InvalidateContext();
        foreach (var owner in _relationalMenuLifetimes.Values) owner.InvalidateContext();
    }

    private void CloseResourceViews(ScopedResource resource)
    {
        if (_pairedFolderWatches.Remove(resource.PublicId, out var pairedWatch)) pairedWatch.Dispose();
        GateSourceReads();
        bool Owns(OpenDocumentState state)
        {
            if (state.PersistentSourcePath is { } source && ResourceWorkspace.TryParse(source, out var id, out _)) return id == resource.PublicId;
            if (LiveSourceWorkspace.TryParse(state.PersistentSourcePath ?? state.FilePath, out var live, out _)) return live == resource.PublicId;
            if (resource.Kind == ResourceKind.DatabaseSnapshot && state.BoundSnapshotKey.HasValue)
                return _relationalExplorerScope?.Resources.Any(r => r.PublicId == resource.PublicId && r.Snapshot?.SnapshotKey == state.BoundSnapshotKey) == true;
            return resource.Kind == ResourceKind.Folder && Path.IsPathFullyQualified(resource.PhysicalPath) && ExplorerCompatibility.IsWithinPhysicalRoot(state.FilePath, resource.PhysicalPath) ||
                resource.Kind == ResourceKind.File && string.Equals(resource.Path, state.FilePath, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var window in _openWindows.Values.Where(w => Owns(w.State)).ToArray()) CloseOpenWindow(window);
        foreach (var window in _openSpreadsheetWindows.Values.Where(w => Owns(w.State)).ToArray()) CloseSpreadsheetWindow(window);
        if (_sourceWindows.Remove(resource.PublicId, out var windows)) foreach (var window in windows.ToArray()) window.Close();
        ClearReferencePreview("This resource is unloaded.");
    }

    private bool GatePairedLocalChanges(PhysicalReferenceInvalidation invalidation)
    {
        if (_activeScope == null || _relationalExplorerScope == null) return false;
        var changed = _relationalExplorerScope.Resources.Where(r => invalidation.ScopeResourceKeys.Contains(r.ScopeResourceKey) && ResourceWorkspace.IsAdmitted(r.PublicId))
            .Select(r => _activeScope.Resources.FirstOrDefault(m => m.PublicId == r.PublicId)).Where(r => r?.PairedResourceId != null).Cast<ScopedResource>()
            .Where(r => r.Source?.Provider == SourceProvider.AzureBlob || _activeScope.Resources.Any(p => p.PublicId == r.PairedResourceId && p.Source?.Provider == SourceProvider.AzureBlob)).ToArray();
        foreach (var member in changed)
        {
            ResourceWorkspace.Block(member, "Local files changed. Reconciliation is required."); CloseResourceViews(member);
            ResourceWorkspace.Block(member, "Local files changed. Reconciliation is required.", release: true);
            if (!_workspaceState.UnloadedResourceIds.Contains(ResourceWorkspace.UnloadedToken(member))) _workspaceState.UnloadedResourceIds.Add(ResourceWorkspace.UnloadedToken(member));
        }
        if (changed.Length > 0) _ = Dispatcher.InvokeAsync(async () => { await SaveWorkspaceStateAsync(); await LoadRelationalRootsAsync(); });
        return changed.Length > 0;
    }
}

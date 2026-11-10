using System.IO;
using System.Windows;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Capture;

namespace Surf2;

public partial class MainWindow
{
    private readonly Dictionary<Task, AccessCancellation> _relationalComparisonOperations = [];
    private readonly HashSet<Window> _relationalComparisonWindows = [];
    private readonly HashSet<Task> _relationalComparisonRetirements = [];
    private AccessCancellation? _activeRelationalComparison;
    private long _relationalComparisonGeneration;
    private int _relationalComparisonRetirementFailures;
    private bool _relationalComparisonsStopping;

    private void InitializeRelationalComparisons()
    {
        Dispatcher.VerifyAccess();
        if (_relationalComparisonOperations.Count != 0 || _relationalComparisonRetirements.Count != 0 ||
            _relationalComparisonWindows.Count != 0)
            throw new InvalidOperationException("Drain the previous comparison context before initializing a runtime.");
        if (_relationalComparisonRetirementFailures != 0)
            throw new IOException("The previous comparison context did not retire cleanly.");
        _relationalComparisonsStopping = false;
        ++_relationalComparisonGeneration;
    }

    // Context-menu eligibility reads only already-selected metadata; never issues SQL.
    private bool GetRelationalComparisonAvailability(FileSystemNode node, out bool tableData)
    {
        tableData = false;
        if (_relational == null || !_relationalExplorerNodes.TryGetValue(node, out var summary)) return false;
        var target = RelationalComparisonService.FromNode(_relational.Session.Epoch, summary);
        tableData = target?.Resource.Kind == ComparisonResourceKind.TableMetadata && summary.HasFullData;
        return target != null;
    }
    private Task CompareRelationalObjectExplorerNodeAsync(FileSystemNode node, bool tableData) =>
        RunRelationalComparisonAsync(async (service, scope, generation, ct) =>
        {
            if (scope == null || !_relationalExplorerNodes.TryGetValue(node, out var summary))
                throw new InvalidOperationException("Open a scope before comparing its resources.");
            var source = await service.CreateSourceAsync(summary, tableData, ct)
                ?? throw new InvalidOperationException("This Object Explorer item cannot be compared.");
            var candidates = await service.GetCandidatesAsync(source, scope, ct);
            if (!await CanPublishRelationalComparisonAsync(service, scope, generation, ct)) return;
            if (candidates.Items.Count == 0) { StatusText = "No comparable resources were found in the active scope."; return; }
            if (!candidates.Complete)
                MessageBox.Show(this, candidates.LimitReason, "Comparison choices", MessageBoxButton.OK, MessageBoxImage.Information);
            var picker = new ComparisonResourcePickerWindow(candidates.Items.Select(x => x.Resource))
            { Owner = this, Title = "Select " + source.Resource.TypeDisplay + " to Compare" };
            if (picker.ShowDialog() != true || picker.SelectedResource == null) return;
            var chosen = candidates.Items.Single(x => ReferenceEquals(x.Resource, picker.SelectedResource));
            await OpenRelationalComparisonCoreAsync(service, source, chosen, scope, generation, ct);
        });

    // History callers supply typed immutable revision/version locators, not virtual paths guessed by name.
    private Task OpenRelationalComparisonAsync(RelationalComparisonTarget left, RelationalComparisonTarget right) =>
        RunRelationalComparisonAsync((service, scope, generation, ct) =>
            OpenRelationalComparisonCoreAsync(service, left, right, scope, generation, ct));

    private Task OpenRelationalComparisonResourcesAsync(ComparisonResource left, ComparisonResource right) =>
        RunRelationalComparisonAsync(async (service, scope, generation, ct) =>
        {
            var a = await ResolveRelationalComparisonTargetAsync(left, ct);
            var b = await ResolveRelationalComparisonTargetAsync(right, ct);
            await OpenRelationalComparisonCoreAsync(service, a, b, scope, generation, ct);
        });
    private async Task<RelationalComparisonTarget> ResolveRelationalComparisonTargetAsync(ComparisonResource resource, CancellationToken ct)
    {
        if (_relational == null) throw new InvalidOperationException("Relational storage is not selected.");
        if (resource.ExplorerNode != null && _relationalExplorerNodes.TryGetValue(resource.ExplorerNode, out var node))
            return await new RelationalComparisonService(_relational).CreateSourceAsync(node, resource.IsTableData, ct)
                ?? throw new KeyNotFoundException("The comparison resource no longer exists.");
        if (resource.IsCollection) throw new InvalidOperationException("Select an identity-bound collection from Object Explorer or History.");
        if (!DatabaseDocumentService.IsDatabaseDocumentPath(resource.Path)) return new(resource, _relational.Session.Epoch);
        if (_relationalDocuments == null) throw new InvalidOperationException("Document access is unavailable.");
        var address = await _relationalDocuments.ResolveAsync(resource.Path, _relationalExplorerScope, ct: ct);
        if (address.Resource == null) throw new KeyNotFoundException("The comparison target is not a database resource.");
        var selected = await _relational.Snapshots.ResolveResourceAsync(address.Resource.ResourceKey, ct: ct)
            ?? throw new KeyNotFoundException("The comparison resource was removed.");
        return RelationalComparisonService.FromResource(selected, _relational.Session.Epoch);
    }

    private async Task OpenRelationalComparisonCoreAsync(RelationalComparisonService service,
        RelationalComparisonTarget? left, RelationalComparisonTarget? right, ExplorerScope? scope,
        long generation, CancellationToken ct)
    {
        if (left == null && right == null) return;
        string title = "Compare " + (left?.Resource.DisplayName ?? "(missing left)") + " and " + (right?.Resource.DisplayName ?? "(missing right)");
        _appSettings.ResourceComparison ??= new();
        var options = new ComparisonOptions(_appSettings.ResourceComparison.IgnoreWhitespaceByDefault, _appSettings.ResourceComparison.IgnoreCaseByDefault);
        ComparisonResultStore? result = null;
        if (left?.Resource.IsCollection == true && right?.Resource.IsCollection == true)
        {
            result = await service.CompareCollectionsAsync(left, right, scope, options, ct);
            try
            {
                if (!await CanPublishRelationalComparisonAsync(service, scope, generation, ct)) return;
                var window = new RelationalComparisonResultWindow(result, title,
                    (row, token) => RunRelationalComparisonAsync((s, currentScope, g, cancellation) =>
                        OpenRelationalComparisonCoreAsync(s, row.Left, row.Right, currentScope, g, cancellation), token),
                    (updated, token) => service.CompareCollectionsAsync(left, right, scope, updated, token), options) { Owner = this };
                result = null; // The window owns cleanup, including a failed Show.
                OwnRelationalComparisonWindow(window); return;
            }
            finally { if (result != null) await result.DisposeAsync(); }
        }
        if ((left ?? right)!.Resource.IsTableData && (right ?? left)!.Resource.IsTableData)
        {
            ComparisonTableInput? a = left == null ? null : await service.DescribeTableAsync(left, ct);
            ComparisonTableInput? b = right == null ? null : await service.DescribeTableAsync(right, ct);
            a ??= new(b!.Headers, b.PreferredKeys, 0, EmptyComparisonRows());
            b ??= new(a.Headers, a.PreferredKeys, 0, EmptyComparisonRows());
            string[] keys = a.PreferredKeys.Where(k => b.Headers.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (keys.Length != a.PreferredKeys.Count) keys = [];
            if (!await CanPublishRelationalComparisonAsync(service, scope, generation, ct)) return;
            if (keys.Length == 0)
            {
                string[] common = a.Headers.Intersect(b.Headers, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (common.Length == 0) throw new InvalidOperationException("No common table data columns are available to use as a key.");
                var picker = new TableDataKeyPickerWindow(common) { Owner = this };
                if (picker.ShowDialog() != true || picker.SelectedColumns.Count == 0) return;
                keys = picker.SelectedColumns.ToArray();
            }
            result = await service.CompareTablesAsync(a, b, keys, ct);
            try
            {
                if (!await CanPublishRelationalComparisonAsync(service, scope, generation, ct)) return;
                var window = new RelationalComparisonResultWindow(result, title + " (key: " + string.Join(", ", keys) + ")") { Owner = this };
                result = null;
                OwnRelationalComparisonWindow(window); return;
            }
            finally { if (result != null) await result.DisposeAsync(); }
        }
        string leftText = left == null ? "" : await service.ReadTextAsync(left, ct);
        string rightText = right == null ? "" : await service.ReadTextAsync(right, ct);
        // DiffPlex's WPF presenter materializes a diff model; bound that independently
        // from selected-document reads instead of allowing an unbounded UI allocation.
        if (leftText.Length + (long)rightText.Length > 256 * 1024 ||
            leftText.Count(c => c == '\n') + rightText.Count(c => c == '\n') > 20_000)
            throw new ComparisonLimitException("The selected pair exceeds the interactive diff budget (256 KiB characters / 20,000 lines). No content was truncated.");
        if (!await CanPublishRelationalComparisonAsync(service, scope, generation, ct)) return;
        var template = (left ?? right)!.Resource;
        OwnRelationalComparisonWindow(new ResourceFileDiffWindow(left?.Resource ?? CreateMissingComparisonResource(template, "(missing left)"),
            right?.Resource ?? CreateMissingComparisonResource(template, "(missing right)"), leftText, rightText,
            options.IgnoreWhitespace, options.IgnoreCase) { Owner = this });
        StatusText = title + ".";
    }
    private void OwnRelationalComparisonWindow(Window window)
    {
        _relationalComparisonWindows.Add(window);
        window.Closed += (_, _) =>
        {
            _relationalComparisonWindows.Remove(window);
            if (window is RelationalComparisonResultWindow resultWindow)
                TrackRelationalComparisonRetirement(resultWindow.Disposal);
        };
        try { window.Show(); StatusText = window.Title + "."; }
        catch
        {
            _relationalComparisonWindows.Remove(window);
            TrackRelationalComparisonRetirement(CloseRelationalComparisonWindowAsync(window));
            throw;
        }
    }
    private Task RunRelationalComparisonAsync(Func<RelationalComparisonService, ExplorerScope?, long, CancellationToken, Task> action,
        CancellationToken parentToken = default)
    {
        if (_relationalComparisonsStopping) return Task.CompletedTask;
        _ = _activeRelationalComparison?.Cancel();
        var owner = new AccessCancellation(); _activeRelationalComparison = owner;
        var runtime = _relational; var scope = _relationalExplorerScope; long generation = ++_relationalComparisonGeneration;
        Task work = CoreAsync(); _relationalComparisonOperations.Add(work, owner);
        TrackRelationalComparisonRetirement(RetireAsync(work)); return work;
        async Task CoreAsync()
        {
            await Task.Yield();
            using var registration = parentToken.Register(() => _ = owner.Cancel());
            try
            {
                owner.Token.ThrowIfCancellationRequested();
                if (_relationalComparisonsStopping || generation != _relationalComparisonGeneration) return;
                if (runtime == null) throw new InvalidOperationException("Relational storage is not selected.");
                StatusText = "Preparing comparison...";
                await action(new(runtime), scope, generation, owner.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (generation == _relationalComparisonGeneration) StatusText = "Could not compare resources: " + ex.Message; }
            finally { await owner.RetireAsync(); }
        }
        async Task RetireAsync(Task task)
        {
            try { await task; }
            finally
            {
                _relationalComparisonOperations.Remove(task);
                if (ReferenceEquals(_activeRelationalComparison, owner)) _activeRelationalComparison = null;
            }
        }
    }
    private async Task<bool> CanPublishRelationalComparisonAsync(RelationalComparisonService service, ExplorerScope? scope, long generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_relationalComparisonsStopping || generation != _relationalComparisonGeneration || !ReferenceEquals(scope, _relationalExplorerScope)) return false;
        await service.RequireScopeAsync(scope, ct);
        return !_relationalComparisonsStopping && generation == _relationalComparisonGeneration && ReferenceEquals(scope, _relationalExplorerScope) && !ct.IsCancellationRequested;
    }
    // Synchronous transition barrier: capture/remove ONLY old windows before any
    // cleanup await, so later new-context windows cannot enter this retirement.
    private void InvalidateRelationalComparisons()
    {
        Dispatcher.VerifyAccess();
        ++_relationalComparisonGeneration;
        var operations = _relationalComparisonOperations.ToArray();
        var windows = _relationalComparisonWindows.ToArray();
        _relationalComparisonWindows.ExceptWith(windows);
        _activeRelationalComparison = null;
        foreach (var operation in operations) _ = operation.Value.Cancel();
        // Close starts synchronously, while each window's SQL/file work drains
        // asynchronously. Closed handlers and this owner share the same task.
        foreach (var window in windows)
            TrackRelationalComparisonRetirement(CloseRelationalComparisonWindowAsync(window));
    }
    private static async Task CloseRelationalComparisonWindowAsync(Window window)
    {
        if (window is RelationalComparisonResultWindow resultWindow) await resultWindow.CloseAndDisposeAsync();
        else window.Close();
    }
    private void TrackRelationalComparisonRetirement(Task work)
    {
        if (!_relationalComparisonRetirements.Add(work)) return;
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try { await work; }
            catch (Exception) { _relationalComparisonRetirementFailures++; }
            finally { _relationalComparisonRetirements.Remove(work); }
        }
    }
    // Shutdown refuses new work, then drains both prior context retirements and
    // the current operation/window set before the parent retires the SQL epoch.
    private async Task StopRelationalComparisonsAsync()
    {
        Dispatcher.VerifyAccess();
        _relationalComparisonsStopping = true;
        InvalidateRelationalComparisons();
        while (_relationalComparisonOperations.Count != 0 || _relationalComparisonRetirements.Count != 0)
        {
            var tasks = _relationalComparisonOperations.Keys.Concat(_relationalComparisonRetirements).Distinct().ToArray();
            try { await Task.WhenAll(tasks); }
            catch (Exception) { /* Tracked observers retain only a bounded failure count. */ }
            await Task.Yield();
        }
        if (_relationalComparisonRetirementFailures != 0)
            throw new IOException("One or more comparison resources could not be retired cleanly.");
    }
    private static async IAsyncEnumerable<CaptureRow> EmptyComparisonRows() { await Task.CompletedTask; yield break; }
}

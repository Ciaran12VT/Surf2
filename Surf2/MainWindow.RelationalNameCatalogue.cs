using System.Globalization;
using System.Windows.Threading;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;

namespace Surf2;

public partial class MainWindow
{
    private sealed class RelationalNameCatalogueState(RelationalRuntime runtime, ExplorerNameCatalogue catalogue)
    {
        public RelationalRuntime Runtime { get; } = runtime;
        public ExplorerNameCatalogue Catalogue { get; set; } = catalogue;
        public ExplorerCancellationLifetime Lifetime { get; } = new();
        public PhysicalReferenceWatch? Watch { get; set; }
        public DispatcherTimer? Reconciliation { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
        public bool RefreshPending { get; set; }
    }

    private RelationalNameCatalogueState? _relationalNameCatalogue;
    private Task _relationalNameCatalogueRetirement = Task.CompletedTask;

    private static async Task<ExplorerNameCatalogue> ReadRelationalNameCatalogueAsync(RelationalRuntime runtime,
        long scopeKey, IReadOnlySet<string> unloaded, string culture, CancellationToken ct)
    {
        var scope = await runtime.Explorer.OpenScopeAsync(scopeKey, unloaded, culture, ct).ConfigureAwait(false);
        return await BuildRelationalNameCatalogueAsync(runtime, scope, ct).ConfigureAwait(false);
    }

    private static async Task<ExplorerNameCatalogue> BuildRelationalNameCatalogueAsync(RelationalRuntime runtime,
        ExplorerScope scope, CancellationToken ct)
    {
        var catalogue = await runtime.Explorer.BuildNameCatalogueAsync(scope, ct).ConfigureAwait(false);
        await runtime.Explorer.RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        return catalogue;
    }

    private void SetRelationalNameCatalogue(RelationalRuntime runtime, ExplorerNameCatalogue catalogue)
    {
        RetireRelationalNameCatalogue();
        _relationalExplorerScope = catalogue.Scope;
        runtime.SelectExplorerScope(catalogue.Scope);
        var state = new RelationalNameCatalogueState(runtime, catalogue);
        _relationalNameCatalogue = state;
        if (catalogue.Scope.Resources.Any(resource => resource.IsLoaded && resource.Kind is ResourceKind.File or ResourceKind.Folder))
            state.Work = RefreshRelationalNameCatalogueAsync(state, installWatch: true);
    }

    private async Task<bool> RefreshActiveRelationalNameCatalogueAsync()
    {
        if (_relational == null || _relationalScopeEdit == null || _relationalExplorerCancellation == null) return false;
        var runtime = _relational;
        var owner = _relationalExplorerOwner;
        var ct = _relationalExplorerCancellation.Token;
        try
        {
            var names = await ReadRelationalNameCatalogueAsync(runtime, _relationalScopeEdit.SubjectKey,
                GetUnloadedResourceIds(), CultureInfo.CurrentCulture.Name, ct);
            if (!AcceptsRelationalExplorer(owner, runtime)) return false;
            SetRelationalNameCatalogue(runtime, names);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }

    private void QueueRelationalNameCatalogueRefresh(RelationalNameCatalogueState state)
    {
        if (!ReferenceEquals(_relationalNameCatalogue, state)) return;
        state.RefreshPending = true;
        if (state.Work.IsCompleted) state.Work = RefreshRelationalNameCatalogueAsync(state);
    }

    private async Task RefreshRelationalNameCatalogueAsync(RelationalNameCatalogueState state, bool installWatch = false)
    {
        await Task.Yield();
        var ct = state.Lifetime.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (installWatch)
            {
                var scope = state.Catalogue.Scope;
                state.Watch = await Task.Run(() => new PhysicalReferenceWatch(scope,
                    _ => Dispatcher.BeginInvoke(() => QueueRelationalNameCatalogueRefresh(state)), namesOnly: true), ct);
                ct.ThrowIfCancellationRequested();
                // Reconcile the interval before the watcher was attached and recover from dropped events.
                state.Reconciliation = new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background,
                    (_, _) => QueueRelationalNameCatalogueRefresh(state), Dispatcher);
            }
            while (state.RefreshPending && ReferenceEquals(_relationalNameCatalogue, state))
            {
                state.RefreshPending = false;
                var scope = state.Catalogue.Scope;
                var updated = await Task.Run(() => state.Runtime.Explorer.BuildNameCatalogueAsync(scope, ct), ct);
                ct.ThrowIfCancellationRequested();
                if (ReferenceEquals(_relationalNameCatalogue, state)) state.Catalogue = updated;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { InternalLogService.Error(ex, "The scope's cached resource names could not be refreshed."); }
    }

    private void RetireRelationalNameCatalogue()
    {
        if (_relationalNameCatalogue is not { } state) return;
        state.Runtime.SelectExplorerScope(null);
        _relationalNameCatalogue = null;
        state.Reconciliation?.Stop();
        var callbacks = state.Lifetime.Cancel();
        _relationalNameCatalogueRetirement = Task.WhenAll(_relationalNameCatalogueRetirement, RetireAsync());

        async Task RetireAsync()
        {
            try { await Task.WhenAll(callbacks, state.Work).ConfigureAwait(false); }
            finally
            {
                try { if (state.Watch is { } watch) await Task.Run(watch.Dispose).ConfigureAwait(false); }
                finally { await state.Lifetime.RetireAsync().ConfigureAwait(false); }
            }
        }
    }
}

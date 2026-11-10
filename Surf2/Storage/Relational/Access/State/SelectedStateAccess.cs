using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access.State;

/// <summary>Summary pages and one bounded active aggregate. No root libraries or implicit create/delete.</summary>
public sealed class SelectedStateAccess
{
    private readonly RelationalSession _session;
    private readonly RelationalStateStore _store;
    private readonly StateLimits _limits;
    public SelectedStateAccess(RelationalSession session, StateLimits? limits = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session)); _limits = limits ?? new(); _limits.Validate();
        if (session.IsMigrationValidation) throw new ArgumentException("Runtime state access cannot use a migration-validation session.", nameof(session));
        _store = new(session, limits: _limits);
    }
    public Guid Epoch => _session.Epoch;

    public async Task<StateCataloguePage<ScopeSummary>> ListScopesAsync(int pageSize = 100, StateCatalogueCursor<ScopeSummary>? after = null, CancellationToken ct = default) =>
        Page(await _store.ListScopesAsync(pageSize, after?.Native, ct));
    public async Task<StateCataloguePage<DiagramSummary>> ListDiagramsAsync(int pageSize = 100, StateCatalogueCursor<DiagramSummary>? after = null, CancellationToken ct = default) =>
        Page(await _store.ListDiagramsAsync(pageSize, after?.Native, ct));
    public async Task<StateCataloguePage<WorkbenchSummary>> ListWorkbenchesAsync(int pageSize = 100, StateCatalogueCursor<WorkbenchSummary>? after = null, CancellationToken ct = default) =>
        Page(await _store.ListWorkbenchesAsync(pageSize, after?.Native, ct));
    private static StateCataloguePage<T> Page<T>(StatePage<T> page) =>
        new(Array.AsReadOnly(page.Items.ToArray()), page.Next == null ? null : new(page.Next));

    public Task<StateLoad<StateEditSession<Scope>>> LoadScopeAsync(long key, CancellationToken ct = default) =>
        LoadAsync("Scope", key, token => _store.ReadScopeAsync(key, token), v => StateCopies.Scope(v, new(_limits)),
            _store.SaveScopeAsync, token => _store.ReadScopeTokenAsync(key, token), ct);
    public Task<StateLoad<StateEditSession<DiagramState>>> LoadDiagramAsync(long key, CancellationToken ct = default) =>
        LoadAsync("Diagram", key, token => _store.ReadDiagramAsync(key, token), CopyDiagram,
            _store.SaveDiagramAsync, token => _store.ReadDiagramTokenAsync(key, token), ct);
    public Task<StateLoad<StateEditSession<WorkbenchAggregate>>> LoadWorkbenchAsync(long key, CancellationToken ct = default) =>
        LoadAsync("Workbench", key, token => _store.ReadWorkbenchAsync(key, token), CopyWorkbench,
            _store.SaveWorkbenchAsync, token => _store.ReadWorkbenchTokenAsync(key, token), ct);
    public Task<StateLoad<StateEditSession<WorkspaceState>>> LoadWorkspaceAsync(CancellationToken ct = default) =>
        LoadAsync("Workspace", null, _store.ReadWorkspaceAsync, v => StateCopies.Workspace(v, new(_limits)),
            _store.SaveWorkspaceAsync, _store.ReadWorkspaceTokenAsync, ct);
    public Task<StateLoad<StateEditSession<ScopeSelection>>> LoadScopeSelectionAsync(CancellationToken ct = default) =>
        LoadAsync("ScopeSelection", 1, _store.ReadScopeSelectionAsync, v => v with { },
            _store.SaveScopeSelectionAsync, _store.ReadScopeSelectionTokenAsync, ct);

    public Task<StateLoad<StateEditSession<Scope>>> LoadScopeAsync(ScopeSummary chosen, CancellationToken ct = default) =>
        LoadAsync("Scope", chosen.Token.Key, token => _store.ReadScopeAsync(chosen.Token.Key, token), v => StateCopies.Scope(v, new(_limits)),
            _store.SaveScopeAsync, token => _store.ReadScopeTokenAsync(chosen.Token.Key, token), ct, chosen.Token);
    public Task<StateLoad<StateEditSession<DiagramState>>> LoadDiagramAsync(DiagramSummary chosen, CancellationToken ct = default) =>
        LoadAsync("Diagram", chosen.Token.Key, token => _store.ReadDiagramAsync(chosen.Token.Key, token), CopyDiagram,
            _store.SaveDiagramAsync, token => _store.ReadDiagramTokenAsync(chosen.Token.Key, token), ct, chosen.Token);
    public Task<StateLoad<StateEditSession<WorkbenchAggregate>>> LoadWorkbenchAsync(WorkbenchSummary chosen, CancellationToken ct = default) =>
        LoadAsync("Workbench", chosen.Token.Key, token => _store.ReadWorkbenchAsync(chosen.Token.Key, token), CopyWorkbench,
            _store.SaveWorkbenchAsync, token => _store.ReadWorkbenchTokenAsync(chosen.Token.Key, token), ct, chosen.Token);

    private Task<StateLoad<StateEditSession<T>>> LoadAsync<T>(string kind, long? key,
        Func<CancellationToken, Task<SelectedState<T>?>> read, Func<T, T> copy,
        Func<T, StateToken, Guid, CancellationToken, Task<StateToken>> save, Func<CancellationToken, Task<StateToken?>> probe, CancellationToken ct,
        StateToken? expected = null) where T : class
    {
        if (key is <= 0) throw new ArgumentOutOfRangeException(nameof(key));
        if (expected != null && expected.Epoch != Epoch) throw new ArgumentException("The selected summary belongs to a different database epoch.");
        return LoadSelectedAsync(kind, key, _session.Epoch, async token =>
        {
            var selected = await read(token).ConfigureAwait(false);
            if (selected != null && expected != null && !StateEditSession<T>.SameToken(selected.Token, expected))
                throw new StateConflictException("selected " + kind);
            return selected;
        }, copy, save, probe, ct);
    }

    internal static async Task<StateLoad<StateEditSession<T>>> LoadSelectedAsync<T>(string kind, long? key, Guid epoch,
        Func<CancellationToken, Task<SelectedState<T>?>> read, Func<T, T> copy,
        Func<T, StateToken, Guid, CancellationToken, Task<StateToken>> save, Func<CancellationToken, Task<StateToken?>> probe, CancellationToken ct) where T : class
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var selected = await read(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (selected == null) return StateLoad<StateEditSession<T>>.Missing();
            if (selected.Token.Epoch != epoch) throw new ArgumentException("The loaded owner belongs to a different database epoch.");
            return StateLoad<StateEditSession<T>>.Ready(new(kind, key ?? selected.Token.Key, selected, copy, save, probe));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return StateLoad<StateEditSession<T>>.Cancelled(); }
        catch (Exception error) { return StateLoad<StateEditSession<T>>.Failed(error); }
    }

    private DiagramState CopyDiagram(DiagramState source)
    {
        var budget = new StateBudget(_limits);
        var document = StateCopies.Diagram(source.Document, budget);
        var pasted = StateCopies.PastedImages(document, source.PastedImages, budget);
        return new(document, pasted, StateCopies.FallbackImages(document, pasted, source.PastedImageFallbacks, budget));
    }
    private WorkbenchAggregate CopyWorkbench(WorkbenchAggregate source)
    {
        var budget = new StateBudget(_limits);
        var model = StateCopies.Workbench(source.Workbench, budget);
        var pasted = StateCopies.PastedImages(model.ActiveDiagramSnapshot, source.PastedImages, budget);
        return new(model, pasted, StateCopies.FallbackImages(model.ActiveDiagramSnapshot, pasted, source.PastedImageFallbacks, budget));
    }
}

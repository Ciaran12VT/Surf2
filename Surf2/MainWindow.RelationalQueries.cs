using Surf2.Storage.Relational.Access;

namespace Surf2;

public partial class MainWindow
{
    private readonly Dictionary<object, QueryLifetime> _relationalAuxiliaryOwners = [];
    private readonly List<Task> _relationalAuxiliaryRetirements = [];
    private bool _relationalAuxiliaryClosing;

    private QueryRequest BeginRelationalAuxiliaryRequest(object key)
    {
        Dispatcher.VerifyAccess();
        if (_relationalAuxiliaryClosing) throw new OperationCanceledException("The window is closing.");
        if (!_relationalAuxiliaryOwners.TryGetValue(key, out var owner))
            _relationalAuxiliaryOwners.Add(key, owner = new(RelationalStateRuntime.Session.Epoch, _relationalExplorerScope?.Context.ScopeKey));
        return owner.BeginRequest();
    }

    private void InvalidateRelationalAuxiliaryQueries()
    {
        if (_relational == null) return;
        foreach (var owner in _relationalAuxiliaryOwners.Values)
            owner.ChangeContext(_relational.Session.Epoch, _relationalExplorerScope?.Context.ScopeKey);
    }

    private void RetireRelationalAuxiliaryOwner(object key)
    {
        if (!_relationalAuxiliaryOwners.Remove(key, out var owner)) return;
        owner.Dispose();
        _relationalAuxiliaryRetirements.RemoveAll(task => task.IsCompletedSuccessfully);
        _relationalAuxiliaryRetirements.Add(owner.DisposeAsync().AsTask());
    }

    private async Task DrainRelationalAuxiliaryQueriesAsync()
    {
        _relationalAuxiliaryClosing = true;
        foreach (var key in _relationalAuxiliaryOwners.Keys.ToArray()) RetireRelationalAuxiliaryOwner(key);
        await Task.WhenAll(_relationalAuxiliaryRetirements);
        _relationalAuxiliaryRetirements.Clear();
    }
}

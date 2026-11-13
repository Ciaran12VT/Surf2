using Surf2.Services.RelationalExplorer;

namespace Surf2.Services;

public sealed partial class RelationalRuntime
{
    private ExplorerScope? _selectedExplorerScope;

    // Browsing uses the membership snapshot explicitly selected by this window. Index publication
    // and unrelated catalogue writes do not revoke that selection. Writes retain their SQL fences.
    public void SelectExplorerScope(ExplorerScope? scope)
    {
        if (scope != null && scope.Context.Epoch != Session.Epoch)
            throw new ArgumentException("The scope belongs to another database session.", nameof(scope));
        Volatile.Write(ref _selectedExplorerScope, scope);
    }

    public bool IsSelectedExplorerScope(ExplorerScope? scope)
    {
        Session.RequireCurrentIdentity();
        return scope != null && SameExplorerSelection(Volatile.Read(ref _selectedExplorerScope), scope);
    }

    public static bool SameExplorerSelection(ExplorerScope? left, ExplorerScope? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;
        var a = left.Context; var b = right.Context;
        return a.Epoch == b.Epoch && a.ScopeKey == b.ScopeKey && a.ScopeVersion == b.ScopeVersion &&
            a.SnapshotCatalogueVersion == b.SnapshotCatalogueVersion && a.DiagramCatalogueVersion == b.DiagramCatalogueVersion &&
            a.UnloadedScopeResourceKeys.SequenceEqual(b.UnloadedScopeResourceKeys) &&
            left.Resources.Length == right.Resources.Length && left.Resources.Zip(right.Resources).All(pair =>
                pair.First.ScopeResourceKey == pair.Second.ScopeResourceKey && pair.First.IsLoaded == pair.Second.IsLoaded &&
                pair.First.IncludeChildren == pair.Second.IncludeChildren && pair.First.Kind == pair.Second.Kind &&
                pair.First.Path == pair.Second.Path);
    }
}

using System.Collections.Immutable;
using Surf2.Models;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed record ExplorerResultNode(ExplorerNodeSummary Node, ImmutableArray<ExplorerResultNode> Children,
    ExplorerSearchHit? Match = null);

// Retains only matches and their natural ancestors, not the traversed source tree or any document body.
// Virtual-folder moves are applied after filtering, as in FileTreeService.CreateFilteredRoots.
public sealed class ExplorerSearchResultBuilder
{
    private sealed class Entry(ExplorerNodeSummary node)
    {
        internal ExplorerNodeSummary Node = node;
        internal ExplorerSearchHit? Match;
        internal readonly List<Entry> Children = [];
        internal readonly Dictionary<string, Entry> ByOccurrence = new(StringComparer.Ordinal);
    }
    private readonly ExplorerScope _scope;
    private readonly Guid _requestIdentity;
    private readonly ExplorerMetadataBudget _budget;
    private readonly int _maximumDepth;
    private readonly ExplorerLimits _limits;
    private readonly Entry _root;
    private bool _invalidated;
    private bool _completed;
    public ExplorerSearchResultBuilder(ExplorerScope scope, Guid requestIdentity, ExplorerLimits? limits = null)
    {
        if (requestIdentity == Guid.Empty) throw new ArgumentException("A search owner identity is required.");
        var bound = limits ?? new(); bound.Validate();
        _limits = bound;
        _scope = scope; _requestIdentity = requestIdentity; _budget = new(bound); _maximumDepth = bound.MaximumDepth;
        _root = new(new("result-root", "", "", "", ExplorerCompatibility.RootKey, "", "", ExplorerNodeRole.VirtualFolder,
            ResourceKind.Folder, FileSystemNodeIconKind.Folder, ExplorerAvailability.Present,
            true, false, false, true, ExplorerChildrenState.Loaded));
    }
    public bool Completed => _completed && !_invalidated;
    public void Append(ExplorerSearchBatch batch)
    {
        if (batch.RequestIdentity != _requestIdentity) throw new InvalidOperationException("The result belongs to a different search owner.");
        if (_invalidated || _completed) throw new InvalidOperationException("This result builder is no longer accepting batches.");
        if (batch.Coverage.GenerationChanged) { Invalidate(); return; }
        foreach (var hit in batch.Hits)
        {
            if (hit.Ancestors.Length >= _maximumDepth) throw new ExplorerLimitException("Result ancestry exceeds its depth budget.");
            Entry parent = _root;
            foreach (var node in hit.Ancestors.Add(hit.Node))
            {
                if (!parent.ByOccurrence.TryGetValue(node.OccurrenceKey, out var entry))
                {
                    _budget.Add(node.Name, node.FullPath, node.NodeKey, node.ParentNodeKey, node.ScopeResourceId);
                    entry = new(node); parent.Children.Add(entry); parent.ByOccurrence.Add(node.OccurrenceKey, entry);
                }
                parent = entry;
            }
            parent.Match = hit;
        }
        _completed = batch.Coverage.Completed;
    }
    public void Invalidate() { _invalidated = true; _root.Children.Clear(); _root.ByOccurrence.Clear(); }

    // Provisional snapshots may be displayed, but only Completed admits a final tree.
    public ImmutableArray<ExplorerResultNode> Snapshot()
    {
        if (_invalidated) throw new IndexGenerationChangedException();
        return Place(_root, new ExplorerMetadataBudget(_limits));
    }
    private ImmutableArray<ExplorerResultNode> Place(Entry parent, ExplorerMetadataBudget budget)
    {
        var nested = parent.Children.ToDictionary(e => e.Node.OccurrenceKey,
            e => new ExplorerResultNode(e.Node with { ChildrenState = ExplorerChildrenState.Loaded },
                e.Children.Count != 0 ? Place(e, budget) : [], e.Match), StringComparer.Ordinal);
        var placed = ExplorerCompatibility.ApplyVirtualFolders(_scope, parent.Node.NodeKey, parent.Children.Select(e => e.Node), parent.Node.ScopeResourceKey);
        var result = ImmutableArray.CreateBuilder<ExplorerResultNode>();
        foreach (var node in placed.Where(n => n.EffectiveParentOccurrenceKey == null && n.ParentNodeKey.Equals(parent.Node.NodeKey, StringComparison.OrdinalIgnoreCase)))
        {
            budget.Add(node.Name, node.FullPath, node.NodeKey, node.ParentNodeKey);
            if (node.Role != ExplorerNodeRole.VirtualFolder)
                result.Add(nested[node.OccurrenceKey] with { Node = node with { ChildrenState = ExplorerChildrenState.Loaded } });
            else
            {
                var moved = placed.Where(n => n.Role != ExplorerNodeRole.VirtualFolder && n.EffectiveParentOccurrenceKey == node.OccurrenceKey)
                    .Select(n =>
                    {
                        budget.Add(n.Name, n.FullPath, n.NodeKey, n.ParentNodeKey);
                        return nested[n.OccurrenceKey] with { Node = n with { ChildrenState = ExplorerChildrenState.Loaded } };
                    }).ToImmutableArray();
                result.Add(new(node with { ChildrenState = ExplorerChildrenState.Loaded }, moved));
            }
        }
        return result.ToImmutable();
    }
}

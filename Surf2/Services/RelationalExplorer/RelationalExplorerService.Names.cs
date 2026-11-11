using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Surf2.Models;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed class ExplorerNameCatalogue
{
    // Siblings share their natural ancestry. The catalogue does not hold source bodies or WPF nodes.
    internal readonly record struct Entry(ExplorerNodeSummary Node, ImmutableArray<ExplorerNodeSummary> Ancestors);

    internal ExplorerNameCatalogue(ExplorerScope scope, ImmutableArray<ExplorerNodeSummary> roots,
        ImmutableArray<Entry> entries, long incomplete, bool truncated)
    {
        Scope = scope;
        Roots = roots;
        Entries = entries;
        Incomplete = incomplete;
        Truncated = truncated;
    }

    public ExplorerScope Scope { get; }
    public ImmutableArray<ExplorerNodeSummary> Roots { get; }
    public bool Truncated { get; }
    internal ImmutableArray<Entry> Entries { get; }
    internal long Incomplete { get; }
}

public sealed partial class RelationalExplorerService
{
    public async Task<ExplorerNameCatalogue> BuildNameCatalogueAsync(ExplorerScope scope, CancellationToken ct = default)
    {
        if (scope.Context.RestrictDocumentKeys)
            throw new ArgumentException("A name catalogue requires a scope context.", nameof(scope));

        var entries = ImmutableArray.CreateBuilder<ExplorerNameCatalogue.Entry>();
        long incomplete = 0, characters = 0;
        // The combined inventory can hold a full database catalogue and a full file inventory.
        long maximumEntries = 2L * _limits.MaximumMetadataRows;
        long maximumCharacters = 16L * _limits.MaximumMetadataCharacters;
        var traversalLimits = _limits with { MaximumMetadataCharacters = maximumCharacters };
        var naturalRoots = await ReadRootLevelAsync(scope, ct).ConfigureAwait(false);
        var roots = PlaceRootLevel(scope, naturalRoots);

        // Keep database results even if a large physical folder fills the inventory.
        foreach (var root in naturalRoots.OrderBy(root => root.ResourceKind is ResourceKind.File or ResourceKind.Folder))
        await foreach (var candidate in WalkNodeAsync(scope, root, [], 0, traversalLimits, ct).ConfigureAwait(false))
        {
            if (candidate.TraversalFailure.HasValue)
            {
                incomplete++;
                continue;
            }

            var node = candidate.Node;
            characters += (long)node.OccurrenceKey.Length + node.Name.Length + node.MatchName.Length + node.FullPath.Length +
                node.NodeKey.Length + node.ParentNodeKey.Length + node.SchemaName.Length + node.ObjectName.Length;
            if (entries.Count >= maximumEntries || characters > maximumCharacters)
                return new ExplorerNameCatalogue(scope, roots, entries.ToImmutable(), incomplete + 1, truncated: true);
            entries.Add(new(node, candidate.Ancestors));
        }

        ct.ThrowIfCancellationRequested();
        return new ExplorerNameCatalogue(scope, roots, entries.ToImmutable(), incomplete, truncated: false);
    }

    public ExplorerSearchBatch SearchNames(ExplorerNameCatalogue catalogue, ExplorerSearchRequest request, CancellationToken ct = default)
    {
        if (request.Target != IndexSearchTarget.Name || request.RequestIdentity == Guid.Empty || !ReferenceEquals(request.Scope, catalogue.Scope))
            throw new ArgumentException("A name search requires a request for the catalogue's scope.", nameof(request));

        var matcher = new IndexTextMatcher(request.Query, request.UseRegex, request.RegexTimeout);
        var hits = ImmutableArray.CreateBuilder<ExplorerSearchHit>();
        long incomplete = catalogue.Incomplete, unpublished = 0;
        foreach (var entry in catalogue.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var node = entry.Node;
            if (IsDocument(node)) unpublished++;
            try
            {
                bool matches = matcher.IsMatch(node.MatchName);
                if (node.Availability == ExplorerAvailability.Missing) incomplete++;
                if (matches)
                    hits.Add(new(node, entry.Ancestors, false, [], request.Query, request.UseRegex,
                        IndexFreshness.Unindexed, node.Availability != ExplorerAvailability.Missing));
            }
            catch (RegexMatchTimeoutException) { incomplete++; }
        }

        ct.ThrowIfCancellationRequested();
        return new(request.RequestIdentity, hits.ToImmutable(), [], new(catalogue.Entries.Length, hits.Count,
            incomplete, unpublished, catalogue.Scope.Context.DiscoveryReconciled, true, false,
            PhysicalDiscoveryNotFrozen: catalogue.Scope.Resources.Any(resource => resource.IsLoaded && resource.Kind == ResourceKind.Folder)));
    }
}

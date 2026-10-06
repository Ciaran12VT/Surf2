using System.Collections.Immutable;
using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalExplorerService
{
    // Clicked occurrences carry both the document owner and the preferred alias membership.
    public async Task<ExplorerAddress?> ResolveAddressAsync(ExplorerScope scope, ExplorerNodeSummary selected,
        CancellationToken ct = default)
    {
        if (selected.IsDirectory || selected.ScopeResourceKey is not > 0)
            throw new ArgumentException("Select a document occurrence with a typed scope membership.", nameof(selected));
        string locator = selected.FullPath;
        if (selected.Role == ExplorerNodeRole.DatabaseDocument)
        {
            if (selected.SnapshotKey is not > 0 || selected.SnapshotResourceKey is not > 0 || selected.Category == null)
                throw new ArgumentException("The selected metadata document has incomplete owner keys.", nameof(selected));
            locator = ExplorerCompatibility.MetadataDocumentPath(scope.Context.Epoch, selected.SnapshotKey.Value,
                selected.SnapshotResourceKey.Value, selected.Category.Value);
            if (!locator.Equals(selected.FullPath, StringComparison.Ordinal)) throw new IndexGenerationChangedException();
        }
        var address = await ResolveAddressAsync(scope, locator, selected.ScopeResourceKey, ct).ConfigureAwait(false);
        if (address != null && (address.Node.ScopeResourceKey != selected.ScopeResourceKey ||
            address.Node.SnapshotKey != selected.SnapshotKey || address.Node.SnapshotResourceKey != selected.SnapshotResourceKey ||
            address.Node.SourceRevisionKey != selected.SourceRevisionKey || address.Node.DiagramRevisionKey != selected.DiagramRevisionKey))
            throw new IndexGenerationChangedException();
        return address;
    }

    // Preferred membership distinguishes duplicate aliases. No source content is read to resolve an address.
    public async Task<ExplorerAddress?> ResolveAddressAsync(ExplorerScope scope, string locator,
        long? preferredScopeResourceKey = null, CancellationToken ct = default)
    {
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        var memberships = scope.Resources.Where(r => preferredScopeResourceKey == null || r.ScopeResourceKey == preferredScopeResourceKey);
        var chain = new List<ExplorerNodeSummary>();
        string canonical = locator, readable = locator;
        bool tableData = false;
        if (DatabaseDocumentService.TryParseDocumentPath(locator, out var reference))
        {
            string segment = reference.SnapshotId;
            var candidates = memberships.Where(r => r.Kind == ResourceKind.DatabaseSnapshot && r.Snapshot != null).ToArray();
            bool typed = segment.StartsWith('@');
            long selectedResourceKey = 0;
            ExplorerResource? resource;
            if (typed)
            {
                if (!ExplorerCompatibility.TryParseDatabaseIdentity(reference, out var epoch, out var snapshotKey, out selectedResourceKey))
                    throw new ArgumentException("The typed database document address is invalid.", nameof(locator));
                if (epoch != scope.Context.Epoch) throw new IndexGenerationChangedException();
                // Dataset keys are not table metadata keys. Capture/Documents resolves typed data identities.
                if (reference.DocumentType.Equals("table-data", StringComparison.OrdinalIgnoreCase)) return null;
                resource = candidates.FirstOrDefault(r => r.Snapshot!.SnapshotKey == snapshotKey);
            }
            else resource = candidates.FirstOrDefault(r => r.Snapshot!.SnapshotId.Equals(segment, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(r => ExplorerCompatibility.SnapshotSegment(r.Snapshot!).Equals(segment, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(r => SnapshotMatches(r, Uri.UnescapeDataString(segment)));
            if (resource == null) return null;
            ExplorerCategory? category = reference.DocumentType.ToLowerInvariant() switch
            {
                "table" or "table-data" => ExplorerCategory.Tables,
                "object" => reference.ObjectKind switch
                {
                    SqlDatabaseObjectKind.StoredProcedure => ExplorerCategory.Procedures,
                    SqlDatabaseObjectKind.View => ExplorerCategory.Views,
                    SqlDatabaseObjectKind.Function => ExplorerCategory.Functions,
                    SqlDatabaseObjectKind.Trigger => ExplorerCategory.Triggers, _ => null
                }, _ => null
            };
            if (category == null) return null;
            var root = await ResourceNodeAsync(resource, ExplorerCompatibility.RootKey, ct).ConfigureAwait(false);
            var parent = CategoryNode(resource, category.Value);
            ExplorerDatabaseItem? selected = null;
            var budget = new ExplorerMetadataBudget(_limits);
            await foreach (var item in _metadata.ReadDatabaseCategoryAsync(scope, resource, category.Value, ct).ConfigureAwait(false))
            {
                budget.Add(item.Resource.SchemaName, item.Resource.ObjectName, item.Resource.OriginalResourceKey);
                if (selected == null && item.Resource.SnapshotKey == resource.Snapshot!.SnapshotKey &&
                    (typed ? item.Resource.ResourceKey == selectedResourceKey :
                        SqlName.FormatPlainMultipartName(item.Resource.SchemaName, item.Resource.ObjectName)
                            .Equals(reference.FullName, StringComparison.OrdinalIgnoreCase))) selected = item;
            }
            if (selected == null) return null;
            var leaf = DatabaseNode(scope, resource, parent, selected);
            tableData = reference.DocumentType.Equals("table-data", StringComparison.OrdinalIgnoreCase);
            canonical = typed ? leaf.FullPath : ExplorerCompatibility.DatabaseLocator(resource.Snapshot!, category.Value, leaf.SchemaName, leaf.ObjectName, true, tableData);
            readable = ExplorerCompatibility.DatabaseLocator(resource.Snapshot!, category.Value, leaf.SchemaName, leaf.ObjectName, false, tableData);
            chain.Add(root); chain.Add(parent); chain.Add(leaf);
        }
        else if (DiagramDocumentService.IsDiagramDocumentPath(locator))
        {
            string id = DiagramDocumentService.GetDiagramId(locator);
            var resource = memberships.FirstOrDefault(r => r.Kind == ResourceKind.Diagram && r.Path.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (resource == null) return null;
            chain.Add(DiagramRoot()); chain.Add(DiagramNode(resource, ExplorerCompatibility.DiagramRootPath));
        }
        else
        {
            if (!Path.IsPathFullyQualified(locator)) return null;
            string path = ExplorerCompatibility.NormalizePhysicalPath(locator);
            var resource = memberships.Where(r => r.Kind is ResourceKind.File or ResourceKind.Folder)
                .Where(r => ExplorerCompatibility.NormalizePhysicalPath(r.Path).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    r.Kind == ResourceKind.Folder && ExplorerCompatibility.IsWithinPhysicalRoot(path, r.Path))
                .OrderByDescending(r => ExplorerCompatibility.NormalizePhysicalPath(r.Path).Length)
                .ThenBy(ExplorerCompatibility.ResourceDisplayName, ExplorerCompatibility.Comparer(scope.SortCultureName)).FirstOrDefault();
            if (resource == null) return null;
            var root = await ResourceNodeAsync(resource, ExplorerCompatibility.RootKey, ct).ConfigureAwait(false);
            chain.Add(root);
            string rootPath = ExplorerCompatibility.NormalizePhysicalPath(resource.Path);
            if (rootPath.Equals(path, StringComparison.OrdinalIgnoreCase) && resource.Kind == ResourceKind.Folder) return null;
            if (!rootPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = Path.GetRelativePath(rootPath, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length >= _limits.MaximumDepth) throw new ExplorerLimitException("The address exceeds its ancestry depth limit.");
                string current = rootPath;
                for (int i = 0; i < parts.Length; i++)
                {
                    string parent = i == 0 ? root.NodeKey : current;
                    current = Path.Combine(current, parts[i]);
                    bool directory = i < parts.Length - 1;
                    var availability = await _physical.ProbeAsync(current, directory, ct).ConfigureAwait(false);
                    if (availability == ExplorerAvailability.Missing) return null;
                    if (availability == ExplorerAvailability.Inaccessible) throw new UnauthorizedAccessException("The selected address is inaccessible.");
                    if (availability == ExplorerAvailability.Failed) throw new IOException("The selected address cannot be inspected.");
                    chain.Add(PhysicalNode(resource, parent, new(current, directory, availability)));
                }
            }
        }
        var effective = new List<ExplorerNodeSummary>();
        // Clipboard hierarchy is the logical tree, not the workspace's filtered/unloaded visual tree.
        var hierarchyScope = scope with { Resources = scope.Resources.Select(r => r with { IsLoaded = true }).ToImmutableArray() };
        for (int i = 0; i < chain.Count; i++)
        {
            var natural = chain[i];
            // Selected sibling metadata is sufficient to reproduce duplicate-key moves exactly.
            var siblings = i == 0 ? await ReadRootLevelAsync(hierarchyScope, ct).ConfigureAwait(false)
                : await ReadNaturalChildrenAsync(hierarchyScope, chain[i - 1] with { IsScopeResourceLoaded = true }, ct).ConfigureAwait(false);
            var placed = ExplorerCompatibility.ApplyVirtualFolders(scope, natural.NaturalParentKey, siblings, i == 0 ? null : chain[i - 1].ScopeResourceKey);
            var node = (placed.FirstOrDefault(n => n.OccurrenceKey == natural.OccurrenceKey) ?? natural) with
                { IsScopeResourceLoaded = natural.IsScopeResourceLoaded };
            if (node.EffectiveParentOccurrenceKey != null)
            {
                var folder = placed.FirstOrDefault(n => n.Role == ExplorerNodeRole.VirtualFolder && n.OccurrenceKey == node.EffectiveParentOccurrenceKey);
                if (folder != null) effective.Add(folder);
            }
            effective.Add(node);
        }
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        var target = effective[^1];
        // A table-data address binds metadata identity; its immutable dataset revision is resolved by the capture provider.
        return new(target, canonical, readable, effective.Select(n => n.Name).ToImmutableArray(),
            tableData ? null : target.SourceRevisionKey ?? target.DiagramRevisionKey, tableData);
    }

    private static bool SnapshotMatches(ExplorerResource r, string value) =>
        r.Snapshot!.SnapshotId.Equals(value, StringComparison.OrdinalIgnoreCase) ||
        ExplorerCompatibility.SnapshotName(r.Snapshot).Equals(value, StringComparison.OrdinalIgnoreCase) ||
        r.Snapshot.DisplayName.Equals(value, StringComparison.OrdinalIgnoreCase) ||
        r.Snapshot.DatabaseName.Equals(value, StringComparison.OrdinalIgnoreCase);
}

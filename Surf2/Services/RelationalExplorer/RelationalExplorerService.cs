using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using Surf2.Models;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalExplorerService
{
    private readonly IExplorerMetadataQueries _metadata;
    private readonly IPhysicalExplorerQueries _physical;
    private readonly ExplorerLimits _limits;
    private readonly Func<ExplorerScope, bool>? _selectedScope;
    public RelationalExplorerService(IExplorerMetadataQueries metadata, IPhysicalExplorerQueries? physical = null,
        ExplorerLimits? limits = null, Func<ExplorerScope, bool>? selectedScope = null)
    {
        _metadata = metadata; _physical = physical ?? new SourceResources.SourcePhysicalExplorerQueries(); _selectedScope = selectedScope;
        _limits = limits ?? new(); _limits.Validate();
    }
    public Task<ExplorerScope> OpenScopeAsync(long scopeKey, IReadOnlySet<string>? unloadedResourceIds = null,
        string? sortCultureName = null, CancellationToken ct = default) =>
        _metadata.ReadScopeAsync(scopeKey, unloadedResourceIds, sortCultureName, ct);

    public async Task<ImmutableArray<ExplorerNodeSummary>> GetRootsAsync(ExplorerScope scope, CancellationToken ct = default)
    {
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        var roots = await ReadRootLevelAsync(scope, ct).ConfigureAwait(false);
        var nodes = PlaceRootLevel(scope, roots);
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        return nodes;
    }

    private static ImmutableArray<ExplorerNodeSummary> PlaceRootLevel(ExplorerScope scope, ImmutableArray<ExplorerNodeSummary> roots) =>
        ExplorerCompatibility.ApplyVirtualFolders(scope, ExplorerCompatibility.RootKey, roots)
            .Where(node => node.EffectiveParentOccurrenceKey == null && node.ParentNodeKey.Equals(ExplorerCompatibility.RootKey, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

    public async Task<ExplorerNodeSummary> GetResourceRootAsync(ExplorerScope scope, long scopeResourceKey, CancellationToken ct = default)
    {
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        var resource = scope.Resources.SingleOrDefault(r => r.ScopeResourceKey == scopeResourceKey)
            ?? throw new KeyNotFoundException("The selected resource is not in this scope.");
        var node = resource.Kind == ResourceKind.Diagram ? DiagramNode(resource, ExplorerCompatibility.DiagramRootPath)
            : await ResourceNodeAsync(resource, ExplorerCompatibility.RootKey, ct).ConfigureAwait(false);
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        return node;
    }

    public async IAsyncEnumerable<ExplorerChildrenBatch> GetChildrenAsync(ExplorerScope scope, ExplorerNodeSummary parent,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new([], ExplorerChildrenState.Loading, false);
        ImmutableArray<ExplorerNodeSummary> nodes = [];
        string? failure = null;
        try { nodes = await ReadChildrenListAsync(scope, parent, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExplorerLimitException or SnapshotReadLimitException)
        { failure = ex is ExplorerLimitException or SnapshotReadLimitException ? "MetadataLimit" : ex is UnauthorizedAccessException ? "Inaccessible" : "Unavailable"; }
        if (failure != null) { yield return new([], ExplorerChildrenState.Failed, false, failure); yield break; }
        for (int i = 0; i < nodes.Length; i += _limits.PageSize)
        {
            ct.ThrowIfCancellationRequested();
            yield return new(nodes.Skip(i).Take(_limits.PageSize).ToImmutableArray(), ExplorerChildrenState.Loaded, false);
        }
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        yield return new([], ExplorerChildrenState.Loaded, true);
    }

    internal async Task<ImmutableArray<ExplorerNodeSummary>> ReadChildrenListAsync(ExplorerScope scope,
        ExplorerNodeSummary parent, CancellationToken ct)
    {
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        if (!parent.IsDirectory || !parent.IsScopeResourceLoaded || parent.Availability != ExplorerAvailability.Present) return [];
        if (parent.Role == ExplorerNodeRole.VirtualFolder)
        {
            var natural = await FindParentAsync(scope, parent.NaturalParentKey, ct, parent.ScopeResourceKey).ConfigureAwait(false);
            var level = natural == null ? await ReadRootLevelAsync(scope, ct).ConfigureAwait(false)
                : await ReadNaturalChildrenAsync(scope, natural, ct).ConfigureAwait(false);
            var placed = ExplorerCompatibility.ApplyVirtualFolders(scope, parent.NaturalParentKey, level, parent.ScopeResourceKey);
            await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
            return placed.Where(n => n.EffectiveParentOccurrenceKey == parent.OccurrenceKey).ToImmutableArray();
        }
        var children = ExplorerCompatibility.ApplyVirtualFolders(scope, parent.NodeKey,
            await ReadNaturalChildrenAsync(scope, parent, ct).ConfigureAwait(false), parent.ScopeResourceKey);
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        return children.Where(n => n.EffectiveParentOccurrenceKey == null && n.ParentNodeKey.Equals(parent.NodeKey, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
    }

    private async Task<ImmutableArray<ExplorerNodeSummary>> ReadRootLevelAsync(ExplorerScope scope, CancellationToken ct)
    {
        var nodes = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
        foreach (var resource in scope.Resources.Where(r => r.Kind != ResourceKind.Diagram))
            nodes.Add(await ResourceNodeAsync(resource, ExplorerCompatibility.RootKey, ct).ConfigureAwait(false));
        nodes.Add(DiagramRoot());
        return nodes.ToImmutable();
    }

    private async Task<ImmutableArray<ExplorerNodeSummary>> ReadNaturalChildrenAsync(ExplorerScope scope,
        ExplorerNodeSummary parent, CancellationToken ct, ExplorerLimits? traversalLimits = null)
    {
        if (!parent.IsScopeResourceLoaded || parent.Availability != ExplorerAvailability.Present) return [];
        if (parent.Role == ExplorerNodeRole.DiagramRoot)
        {
            return scope.Resources.Where(r => r.Kind == ResourceKind.Diagram)
                .OrderBy(ExplorerCompatibility.ResourceDisplayName, ExplorerCompatibility.Comparer(scope.SortCultureName))
                .Select(r => DiagramNode(r, parent.NodeKey)).ToImmutableArray();
        }
        var resource = scope.Resources.FirstOrDefault(r => r.ScopeResourceKey == parent.ScopeResourceKey);
        if (resource?.Kind == ResourceKind.LiveDatabase)
            return parent.Role == ExplorerNodeRole.Resource ? SourceResources.LiveSourceWorkspace.Categories(resource)
                : SourceResources.LiveSourceWorkspace.Objects(resource, parent);
        if (parent.ResourceKind == ResourceKind.DatabaseSnapshot && parent.Role == ExplorerNodeRole.Resource)
            return Enum.GetValues<ExplorerCategory>().Select(c => CategoryNode(resource!, c)).ToImmutableArray();
        if (parent.Role == ExplorerNodeRole.Category)
        {
            var budget = new ExplorerMetadataBudget(traversalLimits ?? _limits);
            var rows = new List<ExplorerDatabaseItem>();
            await foreach (var item in _metadata.ReadDatabaseCategoryAsync(scope, resource!, parent.Category!.Value, ct).ConfigureAwait(false))
            {
                budget.Add(item.Resource.SchemaName, item.Resource.ObjectName, item.Resource.OriginalResourceKey);
                rows.Add(item);
            }
            var comparer = ExplorerCompatibility.Comparer(scope.SortCultureName);
            return rows.OrderBy(r => r.Resource.SchemaName, comparer).ThenBy(r => r.Resource.ObjectName, comparer)
                .Select(r => DatabaseNode(scope, resource!, parent, r)).ToImmutableArray();
        }
        var entries = await _physical.ReadDirectoryAsync(parent.FullPath, scope.SortCultureName, traversalLimits ?? _limits, ct).ConfigureAwait(false);
        return entries.Select(e => PhysicalNode(resource, parent.NodeKey, e)).ToImmutableArray();
    }

    internal Task RequireCurrentAsync(ExplorerScope scope, CancellationToken ct) => CheckAsync(scope, ct);
    private async Task CheckAsync(ExplorerScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_selectedScope?.Invoke(scope) == true) return;
        bool current = _metadata is IExplorerDomainFence domain
            ? await domain.IsDomainCurrentAsync(scope.Context, ct).ConfigureAwait(false)
            : await _metadata.IsCurrentAsync(scope.Context, ct).ConfigureAwait(false);
        if (!current) throw new IndexGenerationChangedException();
    }

    private async Task<ExplorerNodeSummary> ResourceNodeAsync(ExplorerResource resource, string parent, CancellationToken ct)
    {
        var availability = resource.Kind == ResourceKind.DatabaseSnapshot
            ? resource.Snapshot == null ? ExplorerAvailability.Missing : ExplorerAvailability.Present
            : resource.Kind == ResourceKind.LiveDatabase || resource.Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob && !resource.IsLoaded ? ExplorerAvailability.Present
            : await _physical.ProbeAsync(resource.Path, resource.Kind == ResourceKind.Folder, ct).ConfigureAwait(false);
        return ResourceNode(resource, parent, availability);
    }

    private static ExplorerNodeSummary ResourceNode(ExplorerResource resource, string parent, ExplorerAvailability availability)
    {
        string name = ExplorerCompatibility.ResourceDisplayName(resource);
        bool directory = resource.Kind is ResourceKind.Folder or ResourceKind.DatabaseSnapshot or ResourceKind.LiveDatabase;
        return new("resource:" + resource.ScopeResourceKey, name + (availability == ExplorerAvailability.Missing ? " (missing)" : ""),
            name, resource.Path, SourceNodeKey(resource, resource.Path), parent, parent, ExplorerNodeRole.Resource, resource.Kind,
            resource.Kind == ResourceKind.DatabaseSnapshot ? FileSystemNodeIconKind.Database : resource.Kind == ResourceKind.LiveDatabase
                ? FileSystemNodeIconKind.LiveDatabase : directory ? FileSystemNodeIconKind.Folder : FileSystemNodeIconKind.File,
            availability, directory, false, true, resource.IsLoaded,
            directory && resource.IsLoaded && availability == ExplorerAvailability.Present ? ExplorerChildrenState.Unloaded : ExplorerChildrenState.Loaded,
            resource.ScopeResourceKey, resource.ResourceId, resource.Snapshot?.SnapshotKey, SourceOrdinal: resource.SortOrdinal);
    }
    private static ExplorerNodeSummary DiagramRoot() => new("diagrams", "Diagrams", "Diagrams",
        ExplorerCompatibility.DiagramRootPath, ExplorerCompatibility.DiagramRootPath, ExplorerCompatibility.RootKey,
        ExplorerCompatibility.RootKey, ExplorerNodeRole.DiagramRoot, ResourceKind.Diagram, FileSystemNodeIconKind.Diagrams,
        ExplorerAvailability.Present, true, false, false, true, ExplorerChildrenState.Unloaded);
    private static ExplorerNodeSummary DiagramNode(ExplorerResource resource, string parent)
    {
        bool exists = resource.DiagramRevisionKey != null;
        string name = ExplorerCompatibility.ResourceDisplayName(resource);
        string path = DiagramDocumentService.CreateDiagramDocumentPath(resource.Path);
        return new("diagram:" + resource.ScopeResourceKey, name + (exists ? "" : " (missing)"), name, path, path, parent, parent,
            ExplorerNodeRole.Diagram, ResourceKind.Diagram, FileSystemNodeIconKind.File,
            exists ? ExplorerAvailability.Present : ExplorerAvailability.Missing, false, true, true, resource.IsLoaded,
            ExplorerChildrenState.Loaded, resource.ScopeResourceKey, resource.ResourceId, DiagramRevisionKey: resource.DiagramRevisionKey,
            HasUnresolvedQueries: resource.HasUnresolvedQueries, SourceOrdinal: resource.SortOrdinal);
    }
    private static ExplorerNodeSummary CategoryNode(ExplorerResource resource, ExplorerCategory category)
    {
        string name = ExplorerCompatibility.CategoryName(category), path = resource.Snapshot!.SnapshotId + "/" + name;
        return new("category:" + resource.ScopeResourceKey + ":" + category, name, name, path, path, resource.Path, resource.Path,
            ExplorerNodeRole.Category, ResourceKind.DatabaseSnapshot, FileSystemNodeIconKind.Folder,
            ExplorerAvailability.Present, true, false, false, resource.IsLoaded, ExplorerChildrenState.Unloaded,
            resource.ScopeResourceKey, SnapshotKey: resource.Snapshot.SnapshotKey, Category: category);
    }
    private static ExplorerNodeSummary DatabaseNode(ExplorerScope scope, ExplorerResource resource, ExplorerNodeSummary parent, ExplorerDatabaseItem item)
    {
        string name = SqlName.FormatPlainMultipartName(item.Resource.SchemaName, item.Resource.ObjectName);
        string key = ExplorerCompatibility.DatabaseLocator(resource.Snapshot!, parent.Category!.Value, item.Resource.SchemaName, item.Resource.ObjectName);
        string path = ExplorerCompatibility.MetadataDocumentPath(scope.Context.Epoch, item.Resource.SnapshotKey,
            item.Resource.ResourceKey, parent.Category.Value);
        return new("database:" + resource.ScopeResourceKey + ":" + item.Resource.ResourceKey,
            name + (item.HasFullData ? $" ({item.ReportedRowCount} rows)" : ""), name, path, key, parent.NodeKey, parent.NodeKey,
            ExplorerNodeRole.DatabaseDocument, ResourceKind.DatabaseSnapshot, FileSystemNodeIconKind.File, ExplorerAvailability.Present,
            false, true, false, resource.IsLoaded, ExplorerChildrenState.Loaded, resource.ScopeResourceKey, "",
            item.Resource.SnapshotKey, item.Resource.ResourceKey, item.Resource.RevisionKey, Category: parent.Category,
            SchemaName: item.Resource.SchemaName, ObjectName: item.Resource.ObjectName, SourceOrdinal: item.Resource.SortOrdinal,
            HasFullData: item.HasFullData, ReportedRowCount: item.ReportedRowCount);
    }
    private static ExplorerNodeSummary PhysicalNode(ExplorerResource? resource, string parent, PhysicalExplorerEntry entry) => new(
        "physical:" + resource?.ScopeResourceKey + ":" + entry.Path, entry.DisplayName ?? Path.GetFileName(entry.Path), entry.DisplayName ?? Path.GetFileName(entry.Path), entry.Path,
        entry.LogicalKey ?? SourceNodeKey(resource, entry.Path), parent, parent, entry.IsDirectory ? ExplorerNodeRole.PhysicalDirectory : ExplorerNodeRole.PhysicalFile,
        entry.IsDirectory ? ResourceKind.Folder : ResourceKind.File,
        entry.IsDirectory ? FileSystemNodeIconKind.Folder : FileSystemNodeIconKind.File, entry.Availability, entry.IsDirectory, false, false,
        resource?.IsLoaded ?? true, entry.IsDirectory ? ExplorerChildrenState.Unloaded : ExplorerChildrenState.Loaded,
        resource?.ScopeResourceKey, IsReparsePoint: entry.IsReparsePoint);

    private static string SourceNodeKey(ExplorerResource? resource, string path) => resource?.PublicId is { } id && id != Guid.Empty &&
        (resource.Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob || SourceResources.ResourceWorkspace.Prepared(id) != null)
            ? SourceResources.ResourceWorkspace.ToPersistentPath(path, id) : path;

    private async Task<ExplorerNodeSummary?> FindParentAsync(ExplorerScope scope, string key, CancellationToken ct, long? preferredResourceKey = null)
    {
        if (key.Equals(ExplorerCompatibility.RootKey, StringComparison.OrdinalIgnoreCase)) return null;
        if (key.Equals(ExplorerCompatibility.DiagramRootPath, StringComparison.OrdinalIgnoreCase)) return DiagramRoot();
        foreach (var resource in scope.Resources.Where(r => r.Kind != ResourceKind.Diagram &&
                     (preferredResourceKey == null || r.ScopeResourceKey == preferredResourceKey)))
        {
            if (resource.Path.Equals(key, StringComparison.OrdinalIgnoreCase) || SourceNodeKey(resource, resource.Path).Equals(key, StringComparison.Ordinal))
                return await ResourceNodeAsync(resource, ExplorerCompatibility.RootKey, ct).ConfigureAwait(false);
            if (SourceResources.ResourceWorkspace.TryParse(key, out var memberId, out var relative) && memberId == resource.PublicId)
            {
                string physical = SourceResources.ResourceWorkspace.Resolve(key);
                relative = relative.TrimEnd('/');
                int slash = relative.LastIndexOf('/');
                string parentKey = SourceResources.ResourceWorkspace.DirectoryLocator(memberId, slash < 0 ? "" : relative[..slash]);
                return PhysicalNode(resource, parentKey, new(physical, true, ExplorerAvailability.Present,
                    DisplayName: relative[(slash + 1)..], LogicalKey: key));
            }
            if (resource.Kind == ResourceKind.LiveDatabase)
            {
                var category = SourceResources.LiveSourceWorkspace.Categories(resource).FirstOrDefault(n => n.NodeKey == key);
                if (category != null) return category;
            }
            if (resource.Snapshot != null)
                foreach (var category in Enum.GetValues<ExplorerCategory>())
                {
                    var node = CategoryNode(resource, category);
                    if (node.NodeKey.Equals(key, StringComparison.OrdinalIgnoreCase)) return node;
                }
            if (resource.Kind == ResourceKind.Folder && Path.IsPathFullyQualified(resource.Path) && !key.StartsWith("surf2://", StringComparison.OrdinalIgnoreCase) &&
                !key.StartsWith("db://", StringComparison.OrdinalIgnoreCase) && !key.StartsWith("source://", StringComparison.OrdinalIgnoreCase) && ExplorerCompatibility.IsWithinPhysicalRoot(key, resource.Path))
                return PhysicalNode(resource, Path.GetDirectoryName(key)!, new(key, true, ExplorerAvailability.Present));
        }
        var folder = scope.VirtualFolders.FirstOrDefault(f => ExplorerCompatibility.VirtualKey(f.Id).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (folder == null) throw new KeyNotFoundException("The requested explorer parent is not in this scope.");
        string parent = ExplorerCompatibility.Parent(folder.ParentNodeKey);
        return new("virtual:" + folder.Key, folder.Name, folder.Name, folder.Id, key, parent, parent,
            ExplorerNodeRole.VirtualFolder, ResourceKind.Folder, FileSystemNodeIconKind.VirtualFolder, ExplorerAvailability.Present,
            true, false, false, true, ExplorerChildrenState.Unloaded, VirtualFolderId: folder.Id);
    }
}

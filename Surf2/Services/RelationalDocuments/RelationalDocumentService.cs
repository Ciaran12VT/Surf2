using System.Collections.Immutable;
using System.IO;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Selected identities and content only; does not load a persistence library or write sources.</summary>
public sealed partial class RelationalDocumentService : IAsyncDisposable
{
    private readonly RelationalRuntime _runtime;
    private readonly DocumentTextCache _text;
    private readonly IExplorerDomainFence _domain;
    public RelationalDocumentService(RelationalRuntime runtime, DocumentOpenLimits? limits = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Limits = limits ?? new();
        Limits.Validate();
        _text = new(Limits, runtime.QueryMetrics.Diagnostics, runtime.Session.Epoch);
        _domain = new RelationalExplorerMetadataQueries(runtime.Session, runtime.Index);
    }
    public DocumentOpenLimits Limits { get; }
    public Guid Epoch => _runtime.Session.Epoch;
    public ByteCacheUsage CacheUsage => _text.Usage;
    public int ActiveReads => _text.ActiveReads;

    public async Task RequireCurrentAsync(ExplorerScope? scope, CancellationToken ct = default)
    {
        if (scope != null && (scope.Context.Epoch != Epoch || !await _domain.IsDomainCurrentAsync(scope.Context, ct).ConfigureAwait(false)))
            throw new IndexGenerationChangedException();
        await _runtime.Session.RequireReadyAsync(ct).ConfigureAwait(false);
    }

    public async Task<RelationalDocumentAddress> ResolveAsync(string path, ExplorerScope? scope,
        OpenDocumentState? saved = null, CancellationToken ct = default)
    {
        RejectUnresolvedSavedTarget(saved);
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        SnapshotResourceSummary? resource;
        if (saved?.TargetState == SavedDocumentTargetState.Resolved)
        {
            if (saved.BoundSnapshotKey is not > 0 || saved.BoundResourceKey is not > 0)
                throw Missing("The saved document has an incomplete identity binding.");
            resource = await _runtime.Snapshots.ResolveResourceAsync(saved.BoundResourceKey.Value, ct: ct).ConfigureAwait(false);
            if (resource == null || resource.SnapshotKey != saved.BoundSnapshotKey)
                throw Missing("The saved resource no longer exists in its bound snapshot.");
            return await AddressAsync(resource, scope, ct).ConfigureAwait(false);
        }
        if (!DatabaseDocumentService.IsDatabaseDocumentPath(path))
        {
            string fullPath = Path.GetFullPath(path);
            var address = scope == null ? null : await _runtime.Explorer.ResolveAddressAsync(scope, fullPath, ct: ct).ConfigureAwait(false);
            return new(PhysicalDocumentIdentity.From(Epoch, fullPath), fullPath, fullPath,
                Path.GetFileName(fullPath), address?.TextFileNameSeed, null, address);
        }
        if (!DatabaseDocumentService.TryParseDocumentPath(path, out var reference)) throw Missing("The database document address is invalid.");
        if (TryParseIdentityPath(reference, out long snapshotKey, out long resourceKey))
        {
            resource = await _runtime.Snapshots.ResolveResourceAsync(resourceKey, ct: ct).ConfigureAwait(false);
            if (resource == null || resource.SnapshotKey != snapshotKey) throw Missing("The selected resource no longer exists.");
            return await AddressAsync(resource, scope, ct).ConfigureAwait(false);
        }
        SnapshotSummary snapshot = await ResolveSnapshotAsync(reference.SnapshotId, ct).ConfigureAwait(false);
        DatabaseVersionedResourceKind kind = RequestedKind(reference);
        resource = await FindByNameAsync(snapshot.SnapshotKey, kind, reference.FullName, ct).ConfigureAwait(false);
        return await AddressAsync(resource ?? throw Missing("The selected resource was not found."), scope, ct).ConfigureAwait(false);
    }

    public static void RejectUnresolvedSavedTarget(OpenDocumentState? saved)
    {
        if (saved?.TargetState is SavedDocumentTargetState.Missing or SavedDocumentTargetState.Ambiguous)
            throw new DocumentTargetException(saved.TargetState, "The saved document target is " + saved.TargetState.ToString().ToLowerInvariant() + ". Its locator was not rebound by name.");
    }

    public static int SnapshotMatchRank(SnapshotSummary snapshot, string segment)
    {
        if (snapshot.SnapshotId.Equals(segment, StringComparison.OrdinalIgnoreCase)) return 0;
        if (ExplorerCompatibility.SnapshotSegment(snapshot).Equals(segment, StringComparison.OrdinalIgnoreCase)) return 1;
        string decoded = Uri.UnescapeDataString(segment);
        return snapshot.SnapshotId.Equals(decoded, StringComparison.OrdinalIgnoreCase) ||
            ExplorerCompatibility.SnapshotName(snapshot).Equals(decoded, StringComparison.OrdinalIgnoreCase) ||
            snapshot.DisplayName.Equals(decoded, StringComparison.OrdinalIgnoreCase) ||
            snapshot.DatabaseName.Equals(decoded, StringComparison.OrdinalIgnoreCase) ? 2 : int.MaxValue;
    }

    private async Task<SnapshotSummary> ResolveSnapshotAsync(string segment, CancellationToken ct)
    {
        SnapshotCursor? cursor = null;
        SnapshotSummary? selected = null;
        int best = int.MaxValue;
        bool ambiguous = false;
        do
        {
            var page = await _runtime.Snapshots.ListSnapshotsAsync(pageSize: 64, cursor: cursor, ct: ct).ConfigureAwait(false);
            foreach (var snapshot in page.Items)
            {
                int rank = SnapshotMatchRank(snapshot, segment);
                if (rank < best) { selected = snapshot; best = rank; ambiguous = false; }
                else if (rank != int.MaxValue && rank == best) ambiguous = true;
            }
            cursor = page.Next;
        } while (cursor != null);
        if (ambiguous) throw new DocumentTargetException(SavedDocumentTargetState.Ambiguous, "The snapshot locator has multiple owners. Select a bound explorer item.");
        return selected ?? throw Missing("The selected snapshot was not found.");
    }

    private async Task<SnapshotResourceSummary?> FindByNameAsync(long snapshot, DatabaseVersionedResourceKind kind,
        string fullName, CancellationToken ct)
    {
        SnapshotCursor? cursor = null;
        SnapshotResourceSummary? selected = null;
        do
        {
            var page = await _runtime.Snapshots.ListResourcesAsync(snapshot, kind, pageSize: 64, cursor: cursor, ct: ct).ConfigureAwait(false);
            foreach (var resource in page.Items)
                if (SqlName.FormatPlainMultipartName(resource.SchemaName, resource.ObjectName).Equals(fullName, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected != null) throw new DocumentTargetException(SavedDocumentTargetState.Ambiguous, "The document locator has multiple resources. Select an identity-bound explorer item.");
                    selected = resource;
                }
            cursor = page.Next;
        } while (cursor != null);
        return selected;
    }

    public async Task<RelationalDocumentAddress> ResolveResourceAsync(long snapshotKey, long resourceKey,
        ExplorerScope? scope, CancellationToken ct = default, long? preferredScopeResourceKey = null)
    {
        var resource = await _runtime.Snapshots.ResolveResourceAsync(resourceKey, ct: ct).ConfigureAwait(false);
        if (resource == null || resource.SnapshotKey != snapshotKey) throw Missing("The selected resource identity was not found.");
        return await AddressAsync(resource, scope, ct, preferredScopeResourceKey).ConfigureAwait(false);
    }

    public async Task<RelationalDocumentAddress> ResolveExplorerNodeAsync(ExplorerScope scope,
        ExplorerNodeSummary selected, CancellationToken ct = default)
    {
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        var member = DocumentExplorerSelection.SelectedMembership(scope, selected);
        if (selected.Role == ExplorerNodeRole.DatabaseDocument)
        {
            if (selected.SnapshotKey is not > 0 || selected.SnapshotResourceKey is not > 0)
                throw Missing("The selected explorer document has an incomplete identity.");
            var resource = await _runtime.Snapshots.ResolveResourceAsync(selected.SnapshotResourceKey.Value, ct: ct).ConfigureAwait(false)
                ?? throw Missing("The selected explorer resource was removed.");
            DocumentExplorerSelection.ValidateResource(selected, member, resource);
            return await AddressAsync(resource, scope, ct, member.ScopeResourceKey).ConfigureAwait(false);
        }
        if (selected.Role is not (ExplorerNodeRole.PhysicalFile or ExplorerNodeRole.Resource) ||
            selected.ResourceKind != ResourceKind.File || member.Kind is not (ResourceKind.File or ResourceKind.Folder))
            throw Missing("The selected explorer item is not a file document.");
        string path = Path.GetFullPath(selected.FullPath);
        var explorer = await _runtime.Explorer.ResolveAddressAsync(scope, path, member.ScopeResourceKey, ct).ConfigureAwait(false);
        if (explorer == null || explorer.Node.ScopeResourceKey != member.ScopeResourceKey)
            throw Missing("The selected file is no longer in its explorer membership.");
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        return new(PhysicalDocumentIdentity.From(Epoch, path), path, path, Path.GetFileName(path),
            explorer.TextFileNameSeed, null, explorer);
    }

    private async Task<RelationalDocumentAddress> AddressAsync(SnapshotResourceSummary resource, ExplorerScope? scope,
        CancellationToken ct, long? preferredScopeResourceKey = null)
    {
        var snapshot = await _runtime.Snapshots.GetSnapshotAsync(resource.SnapshotKey, ct).ConfigureAwait(false) ?? throw Missing("The snapshot is not published.");
        var category = Category(resource.Kind);
        bool data = resource.Kind == DatabaseVersionedResourceKind.TableData;
        ExplorerAddress? explorer = null;
        if (scope != null)
        {
            var membership = DocumentExplorerSelection.SnapshotMembership(scope, resource.SnapshotKey, preferredScopeResourceKey);
            // Explicit/saved opens may target documents outside the selected scope. Reference queries
            // independently enforce loaded membership; opening is not a scope search or a new permission check.
            if (membership != null)
                explorer = await BoundAddressAsync(scope, membership, resource, category, data, ct).ConfigureAwait(false);
        }
        else if (preferredScopeResourceKey.HasValue) throw Missing("A selected alias requires its original explorer scope.");
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        string name = SqlName.FormatPlainMultipartName(resource.SchemaName, resource.ObjectName);
        string path = IdentityPath(Epoch, resource, category, data);
        return new(new SnapshotDocumentIdentity(Epoch, resource.SnapshotKey, resource.ResourceKey, data), path,
            data ? "document.csv" : "document.sql", name + (data ? " data.csv" : ".sql"), explorer?.TextFileNameSeed,
            SnapshotResourceLocator.From(Epoch, resource), explorer, snapshot);
    }

    // Reproduce effective ancestry by identity, including duplicate-key virtual-folder moves.
    // Only selected root/category siblings are requested; definitions and datasets are never read.
    private async Task<ExplorerAddress?> BoundAddressAsync(ExplorerScope scope, ExplorerResource membership,
        SnapshotResourceSummary resource, ExplorerCategory category, bool data, CancellationToken ct)
    {
        var hierarchyScope = scope with { Resources = scope.Resources.Select(r => r with { IsLoaded = true }).ToImmutableArray() };
        var roots = await _runtime.Explorer.GetRootsAsync(hierarchyScope, ct).ConfigureAwait(false);
        var root = await FindInLevelAsync(hierarchyScope, roots, n => n.Role == ExplorerNodeRole.Resource && n.ScopeResourceKey == membership.ScopeResourceKey, ct).ConfigureAwait(false);
        if (root.Node == null) return null;
        var categories = await ChildrenAsync(hierarchyScope, root.Node, ct).ConfigureAwait(false);
        var parent = await FindInLevelAsync(hierarchyScope, categories, n => n.Category == category, ct).ConfigureAwait(false);
        if (parent.Node == null) return null;
        var leaves = await ChildrenAsync(hierarchyScope, parent.Node, ct).ConfigureAwait(false);
        // Data identity is independent. Metadata may be absent; it is not rebound to another table.
        var leaf = data ? (Node: (ExplorerNodeSummary?)null, Folder: (ExplorerNodeSummary?)null) :
            await FindInLevelAsync(hierarchyScope, leaves, n => n.SnapshotResourceKey == resource.ResourceKey, ct).ConfigureAwait(false);
        string canonical = ExplorerCompatibility.DatabaseLocator(membership.Snapshot!, category, resource.SchemaName, resource.ObjectName, true, data);
        var node = leaf.Node ?? new ExplorerNodeSummary("database:" + membership.ScopeResourceKey + ":" + resource.ResourceKey,
            SqlName.FormatPlainMultipartName(resource.SchemaName, resource.ObjectName), SqlName.FormatPlainMultipartName(resource.SchemaName, resource.ObjectName),
            canonical, canonical, parent.Node.NodeKey, parent.Node.NodeKey, ExplorerNodeRole.DatabaseDocument,
            ResourceKind.DatabaseSnapshot, FileSystemNodeIconKind.File, ExplorerAvailability.Present, false, true, false, true,
            ExplorerChildrenState.Loaded, membership.ScopeResourceKey, SnapshotKey: resource.SnapshotKey,
            SnapshotResourceKey: resource.ResourceKey, SourceRevisionKey: resource.RevisionKey, Category: category);
        var names = new[] { root.Folder?.Name, root.Node.Name, parent.Folder?.Name, parent.Node.Name, leaf.Folder?.Name, node.MatchName }
            .Where(n => n != null).Select(n => n!).ToImmutableArray();
        return new(node, canonical, ExplorerCompatibility.DatabaseLocator(membership.Snapshot!, category, resource.SchemaName, resource.ObjectName, false, data), names, resource.RevisionKey, data);
    }

    private async Task<(ExplorerNodeSummary? Node, ExplorerNodeSummary? Folder)> FindInLevelAsync(ExplorerScope scope,
        ImmutableArray<ExplorerNodeSummary> level, Func<ExplorerNodeSummary, bool> match, CancellationToken ct)
    {
        var found = level.FirstOrDefault(match);
        if (found != null) return (found, null);
        foreach (var folder in level.Where(n => n.IsVirtualFolder))
        {
            found = (await ChildrenAsync(scope, folder, ct).ConfigureAwait(false)).FirstOrDefault(match);
            if (found != null) return (found, folder);
        }
        return (null, null);
    }

    private async Task<ImmutableArray<ExplorerNodeSummary>> ChildrenAsync(ExplorerScope scope, ExplorerNodeSummary parent, CancellationToken ct)
    {
        var result = ImmutableArray.CreateBuilder<ExplorerNodeSummary>();
        await foreach (var page in _runtime.Explorer.GetChildrenAsync(scope, parent, ct).ConfigureAwait(false))
        {
            if (page.State == ExplorerChildrenState.Failed) throw new IOException("The selected document ancestry could not be loaded.");
            result.AddRange(page.Nodes);
        }
        return result.ToImmutable();
    }

    private static DatabaseVersionedResourceKind RequestedKind(DatabaseDocumentReference reference) => reference.DocumentType.ToLowerInvariant() switch
    {
        "table" => DatabaseVersionedResourceKind.TableMetadata,
        "table-data" => DatabaseVersionedResourceKind.TableData,
        "object" when reference.ObjectKind.HasValue => SnapshotIdentity.ResourceKind(reference.ObjectKind.Value) ?? throw Missing("Unknown object kind is not addressable."),
        _ => throw Missing("The database document type is not supported.")
    };
    private static ExplorerCategory Category(DatabaseVersionedResourceKind? kind) => kind switch
    {
        DatabaseVersionedResourceKind.StoredProcedure => ExplorerCategory.Procedures,
        DatabaseVersionedResourceKind.View => ExplorerCategory.Views,
        DatabaseVersionedResourceKind.Function => ExplorerCategory.Functions,
        DatabaseVersionedResourceKind.Trigger => ExplorerCategory.Triggers,
        DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData => ExplorerCategory.Tables,
        _ => throw Missing("The selected resource is not an addressable document.")
    };
    public static string IdentityPath(Guid epoch, SnapshotResourceSummary resource, ExplorerCategory category, bool data) =>
        $"db://@{epoch:N}:{resource.SnapshotKey}/" + (category == ExplorerCategory.Tables ? data ? "table-data/" : "table/" : "object/" + ExplorerCompatibility.ObjectKind(category) + "/") +
        resource.ResourceKey + (data ? ".csv" : ".sql");

    private bool TryParseIdentityPath(DatabaseDocumentReference reference, out long snapshot, out long resource)
    {
        snapshot = resource = 0;
        if (!reference.SnapshotId.StartsWith('@')) return false;
        string[] owner = reference.SnapshotId[1..].Split(':');
        if (owner.Length != 2 || !Guid.TryParseExact(owner[0], "N", out var epoch) ||
            !long.TryParse(owner[1], out snapshot) || snapshot <= 0 || !long.TryParse(reference.FullName, out resource) || resource <= 0)
            return false;
        if (epoch != Epoch) throw Missing("This document address belongs to another database session.");
        return true;
    }
    private static DocumentTargetException Missing(string message) => new(SavedDocumentTargetState.Missing, message);
    public ValueTask DisposeAsync() => _text.DisposeAsync();
}

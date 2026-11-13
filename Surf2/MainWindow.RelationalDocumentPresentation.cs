using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;

namespace Surf2;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _relationalDocumentToolTips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string PersistentPath, string ReadablePath)> _relationalDocumentLinks = new(StringComparer.OrdinalIgnoreCase);
    private string _metadataEditorOriginalLink = string.Empty;
    private string _metadataEditorDisplayedLink = string.Empty;

    private void RememberRelationalDocumentPresentation(RelationalDocumentAddress address)
    {
        if (address.Resource == null || address.Snapshot == null) return;
        var membership = address.ExplorerAddress?.Node.ScopeResourceKey is long key
            ? _relationalExplorerScope?.Resources.FirstOrDefault(resource => resource.ScopeResourceKey == key)
            : _relationalExplorerScope?.Resources.FirstOrDefault(resource => resource.Snapshot?.SnapshotKey == address.Snapshot.SnapshotKey);
        string resourceName = membership == null ? ExplorerCompatibility.SnapshotName(address.Snapshot)
            : ExplorerCompatibility.ResourceDisplayName(membership);
        string readable = RelationalDocumentService.ReadablePath(address);
        if (address.PersistentDocumentPath is { } persistent)
            _relationalDocumentLinks[address.DocumentPath] = (persistent, readable);
        _relationalDocumentToolTips[address.DocumentPath] = $"Resource: {resourceName} (Database){Environment.NewLine}{readable}";
    }

    private string GetRelationalPersistentDocumentPath(string path)
    {
        if (_relationalDocumentLinks.TryGetValue(path, out var link)) return link.PersistentPath;
        var scope = _relationalExplorerScope;
        if (scope?.DatabaseCatalogue == null || !DurableDocumentLink.TryParseSessionPath(path, out var selected) ||
            selected.Epoch != scope.Context.Epoch || selected.Epoch != _relational?.Session.Epoch) return path;
        var snapshot = scope.Resources.FirstOrDefault(resource => resource.Snapshot?.SnapshotKey == selected.SnapshotKey)?.Snapshot;
        if (snapshot == null || !scope.DatabaseCatalogue.TryGetValue(snapshot.SnapshotKey, out var catalogue)) return path;
        var item = catalogue.FirstOrDefault(item => item.Resource.ResourceKey == selected.ResourceKey && item.Resource.Kind == selected.Kind);
        return item == null ? path : DurableDocumentLink.Create(snapshot.PublicId, item.Resource.PublicId, selected.Kind);
    }

    private string NormalizeRelationalMetadataLink(string link) =>
        RewriteRelationalMetadataLink(link, GetRelationalPersistentDocumentPath);

    private string GetReadableRelationalMetadataLink(string link) =>
        RewriteRelationalMetadataLink(link, GetRelationalReadableDocumentPath);

    private string GetRelationalReadableDocumentPath(string path)
    {
        if (_relationalDocumentLinks.TryGetValue(path, out var cached)) return cached.ReadablePath;
        foreach (var entry in _relationalDocumentLinks.Values)
            if (entry.PersistentPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return entry.ReadablePath;
        return GetLoadedRelationalReadableDocumentPath(path) ?? path;
    }

    private static string RewriteRelationalMetadataLink(string link, Func<string, string> rewrite)
        => DurableDocumentLink.Rewrite(link, rewrite);

    private string? GetLoadedRelationalReadableDocumentPath(string path)
    {
        var scope = _relationalExplorerScope;
        if (scope?.DatabaseCatalogue == null || !DurableDocumentLink.TryParse(GetRelationalPersistentDocumentPath(path), out var identity)) return null;
        var snapshot = scope.Resources.FirstOrDefault(resource => resource.Snapshot?.PublicId == identity.SnapshotId)?.Snapshot;
        if (snapshot == null || !scope.DatabaseCatalogue.TryGetValue(snapshot.SnapshotKey, out var catalogue)) return null;
        var item = catalogue.FirstOrDefault(item => item.Resource.PublicId == identity.ResourceId && item.Resource.Kind == identity.Kind);
        if (item == null) return null;
        ExplorerCategory? category = identity.Kind switch
        {
            DatabaseVersionedResourceKind.StoredProcedure => ExplorerCategory.Procedures,
            DatabaseVersionedResourceKind.Function => ExplorerCategory.Functions,
            DatabaseVersionedResourceKind.View => ExplorerCategory.Views,
            DatabaseVersionedResourceKind.Trigger => ExplorerCategory.Triggers,
            DatabaseVersionedResourceKind.TableMetadata or DatabaseVersionedResourceKind.TableData => ExplorerCategory.Tables,
            _ => null
        };
        return category == null ? null : ExplorerCompatibility.DatabaseLocator(snapshot, category.Value,
            item.Resource.SchemaName, item.Resource.ObjectName, tableData: identity.Kind == DatabaseVersionedResourceKind.TableData);
    }

    private string GetMetadataEditorLink()
    {
        string displayed = MetadataLinkTextBox.Text.Trim();
        // A readable label is presentation. Editing documentation must not replace its durable target with a name lookup.
        string link = _relational != null && displayed == _metadataEditorDisplayedLink
            ? _metadataEditorOriginalLink : displayed;
        return NormalizeDatabaseMetadataLink(link);
    }

    private string? GetRelationalDocumentToolTip(string filePath)
    {
        if (_relationalDocumentToolTips.TryGetValue(filePath, out var value)) return value;
        if (!filePath.StartsWith("db://@", StringComparison.OrdinalIgnoreCase)) return null;
        var state = _workspaceState.OpenDocuments.FirstOrDefault(document => document.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(state?.DisplayName) ? "Database resource" : state.DisplayName;
    }
}

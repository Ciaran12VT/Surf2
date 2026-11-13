using Surf2.Models;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;

namespace Surf2;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _relationalDocumentToolTips = new(StringComparer.OrdinalIgnoreCase);

    private void RememberRelationalDocumentPresentation(RelationalDocumentAddress address)
    {
        if (address.Resource == null || address.Snapshot == null) return;
        var membership = address.ExplorerAddress?.Node.ScopeResourceKey is long key
            ? _relationalExplorerScope?.Resources.FirstOrDefault(resource => resource.ScopeResourceKey == key)
            : _relationalExplorerScope?.Resources.FirstOrDefault(resource => resource.Snapshot?.SnapshotKey == address.Snapshot.SnapshotKey);
        string resourceName = membership == null ? ExplorerCompatibility.SnapshotName(address.Snapshot)
            : ExplorerCompatibility.ResourceDisplayName(membership);
        string readable = RelationalDocumentService.ReadablePath(address);
        _relationalDocumentToolTips[address.DocumentPath] = $"Resource: {resourceName} (Database){Environment.NewLine}{readable}";
    }

    private string? GetRelationalDocumentToolTip(string filePath)
    {
        if (_relationalDocumentToolTips.TryGetValue(filePath, out var value)) return value;
        if (!filePath.StartsWith("db://@", StringComparison.OrdinalIgnoreCase)) return null;
        var state = _workspaceState.OpenDocuments.FirstOrDefault(document => document.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(state?.DisplayName) ? "Database resource" : state.DisplayName;
    }
}

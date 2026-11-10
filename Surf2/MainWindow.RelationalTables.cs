using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public partial class MainWindow
{
    private async Task<RelationalDocumentAddress?> FindRelationalRelatedTableAsync(string path,
        DatabaseVersionedResourceKind wanted, OpenDocumentState? saved = null, CancellationToken ct = default)
    {
        if (!DatabaseDocumentService.TryParseDocumentPath(path, out var parsed) ||
            parsed.DocumentType is not ("table" or "table-data")) return null;
        var documents = _relationalDocuments ?? throw new InvalidOperationException("Document access is unavailable.");
        var address = await documents.ResolveAsync(path, _relationalExplorerScope, saved, ct);
        if (address.Resource == null) return null;
        var source = await RelationalStateRuntime.Snapshots.ResolveResourceAsync(address.Resource.ResourceKey, ct: ct);
        if (source == null) return null;
        SnapshotResourceSummary? related = null;
        SnapshotCursor? cursor = null;
        int count = 0;
        do
        {
            var page = await RelationalStateRuntime.Snapshots.ListResourcesAsync(source.SnapshotKey, wanted, cursor: cursor, ct: ct);
            foreach (var resource in page.Items)
            {
                if (++count > RelationalRuntimeStateLimits.MaximumRows)
                    throw new InvalidOperationException("Related-table metadata exceeds its budget.");
                if (!string.Equals(resource.SchemaName, source.SchemaName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(resource.ObjectName, source.ObjectName, StringComparison.OrdinalIgnoreCase)) continue;
                if (related != null) throw new DocumentTargetException(SavedDocumentTargetState.Ambiguous,
                    "The related table has multiple current identities.");
                related = resource;
            }
            cursor = page.Next;
        } while (cursor != null);
        return related == null ? null : await documents.ResolveResourceAsync(related.SnapshotKey, related.ResourceKey, _relationalExplorerScope, ct);
    }

    private async Task<string?> OpenRelationalRelatedTableDataAsync(string tablePath, FloatingCodeWindow? fallback)
    {
        var context = _relationalDocumentContextGeneration;
        try
        {
            await using var request = BeginRelationalAuxiliaryRequest((object?)fallback?.State ?? this);
            var related = await FindRelationalRelatedTableAsync(tablePath, DatabaseVersionedResourceKind.TableData, ct: request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            if (related == null || context != _relationalDocumentContextGeneration) return null;
            _openWindows.TryGetValue(tablePath, out var tableWindow);
            await OpenFileAsync(related.DocumentPath, sourceWindow: tableWindow ?? fallback, suppressHistory: true);
            return _openSpreadsheetWindows.ContainsKey(related.DocumentPath) ? related.DocumentPath : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { StatusText = "Could not open related table data: " + error.Message; return null; }
    }

    private async Task<string> GetRelationalFilterLinkAsync(FloatingSpreadsheetWindow window)
    {
        await using var request = BeginRelationalAuxiliaryRequest(window.State);
        var ct = request.CancellationToken;
        var documents = _relationalDocuments ?? throw new InvalidOperationException("Document access is unavailable.");
        var address = await documents.ResolveAsync(window.State.FilePath, _relationalExplorerScope, window.State, ct);
        if (address.Resource == null) return address.DocumentPath;
        var metadata = await FindRelationalRelatedTableAsync(window.State.FilePath, DatabaseVersionedResourceKind.TableMetadata, window.State, ct);
        address = metadata ?? address;
        if (address.ExplorerAddress != null) return address.ExplorerAddress.ReadableLocator;
        var resource = await RelationalStateRuntime.Snapshots.ResolveResourceAsync(address.Resource!.ResourceKey, ct: ct)
            ?? throw new InvalidOperationException("The selected table is unavailable.");
        return ExplorerCompatibility.DatabaseLocator(address.Snapshot!, ExplorerCategory.Tables,
            resource.SchemaName, resource.ObjectName, canonical: false, tableData: metadata == null);
    }
}

using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public partial class MainWindow
{
    private readonly object _relationalMetadataLinkOwner = new();

    private async Task<bool> OpenRelationalMetadataLinkAsync(string path, MetadataLinkTarget target)
    {
        var documents = _relationalDocuments;
        var runtime = _relational;
        var scope = _relationalExplorerScope;
        long generation = _relationalDocumentContextGeneration;
        if (documents == null || runtime == null) return false;
        bool IsCurrent() => ReferenceEquals(runtime, _relational) && generation == _relationalDocumentContextGeneration &&
            RelationalRuntime.SameExplorerSelection(scope, _relationalExplorerScope);
        try
        {
            await using var request = BeginRelationalAuxiliaryRequest(_relationalMetadataLinkOwner);
            var ct = request.CancellationToken;
            var address = await documents.ResolveAsync(path, scope, ct: ct);
            if (target.HasColumnFilters && DatabaseDocumentService.TryParseDocumentPath(address.DocumentPath, out var parsed) &&
                parsed.DocumentType == "table-data")
                address = await FindRelationalRelatedTableAsync(address.DocumentPath,
                    DatabaseVersionedResourceKind.TableMetadata, ct: ct) ?? address;
            if (!IsCurrent()) return false;
            EnsureCodeViewVisible();
            await TryOpenRelationalFileAsync(address.DocumentPath, targetLine: target.LineNumber, ct: ct,
                preferredResolvedAddress: address);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent()) return false;

            // The general opener returns "handled" even for declined or failed requests. Success here requires a published window.
            bool codeOpened = _relationalDocumentWindows.TryGetValue(address.Identity, out var code);
            bool gridOpened = _openSpreadsheetWindows.ContainsKey(address.DocumentPath);
            if (!codeOpened && !gridOpened) return false;
            SetActiveWorkspaceView(WorkspaceViewKind.Code);
            if (code != null) RevealRelationalDocument(code, target.LineNumber, null, null, suppressHistory: false);

            string? dataPath = gridOpened ? address.DocumentPath : null;
            if (DatabaseDocumentService.TryParseDocumentPath(address.DocumentPath, out var table) && table.DocumentType == "table")
                dataPath = await OpenRelationalRelatedTableDataAsync(address.DocumentPath, code);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent()) return false;
            if (dataPath != null && _openSpreadsheetWindows.TryGetValue(dataPath, out var grid))
            {
                if (target.HasColumnFilters) grid.ApplyColumnFiltersByName(target.ColumnFilters);
                SetActiveCodeWindow(null, syncOpenTabsSelection: false);
                BringToFront(grid);
                RevealWindow(grid);
            }
            StatusText = "Opened metadata link: " + address.DisplayName +
                (target.LineNumber.HasValue ? " line " + target.LineNumber.Value : string.Empty);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            if (IsCurrent()) StatusText = "Could not open metadata link: " + error.Message;
            return false;
        }
    }
}

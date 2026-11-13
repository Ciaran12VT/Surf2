using Surf2.Services.RelationalExplorer;

namespace Surf2.Services.RelationalDocuments;

public sealed partial class RelationalDocumentService
{
    public static string ReadablePath(RelationalDocumentAddress address)
    {
        if (address.Resource == null || address.Snapshot == null) return address.DocumentPath;
        if (address.ExplorerAddress != null) return address.ExplorerAddress.ReadableLocator;
        return address.ReadableDocumentPath ?? address.DisplayName;
    }
}

using Surf2.Models;

namespace Surf2.Storage;

public sealed class SqlServerWorkbenchStore : IWorkbenchStore
{
    private const string DocumentKey = "workbench-library";
    private readonly SqlServerDocumentStore _documentStore = new();
    private readonly JsonWorkbenchStore _jsonFallback = new();

    public async Task<WorkbenchLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        WorkbenchLibrary? workbenchLibrary = await _documentStore.LoadAsync<WorkbenchLibrary>(DocumentKey, cancellationToken);
        if (workbenchLibrary != null)
        {
            return workbenchLibrary;
        }

        workbenchLibrary = await _jsonFallback.LoadAsync(cancellationToken);
        await _documentStore.SaveAsync(DocumentKey, workbenchLibrary, cancellationToken);
        return workbenchLibrary;
    }

    public Task SaveAsync(WorkbenchLibrary workbenchLibrary, CancellationToken cancellationToken = default)
    {
        return _documentStore.SaveAsync(DocumentKey, workbenchLibrary, cancellationToken);
    }
}

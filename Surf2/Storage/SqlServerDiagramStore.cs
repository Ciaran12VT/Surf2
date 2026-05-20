using Surf2.Models;

namespace Surf2.Storage;

public sealed class SqlServerDiagramStore : IDiagramStore
{
    private const string DocumentKey = "diagram-library";
    private readonly SqlServerDocumentStore _documentStore = new();
    private readonly JsonDiagramStore _jsonFallback = new();

    public async Task<DiagramLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        DiagramLibrary? diagramLibrary = await _documentStore.LoadAsync<DiagramLibrary>(DocumentKey, cancellationToken);
        if (diagramLibrary != null)
        {
            return diagramLibrary;
        }

        diagramLibrary = await _jsonFallback.LoadAsync(cancellationToken);
        await _documentStore.SaveAsync(DocumentKey, diagramLibrary, cancellationToken);
        return diagramLibrary;
    }

    public Task SaveAsync(DiagramLibrary diagramLibrary, CancellationToken cancellationToken = default)
    {
        return _documentStore.SaveAsync(DocumentKey, diagramLibrary, cancellationToken);
    }
}

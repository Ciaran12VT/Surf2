using Surf2.Models;

namespace Surf2.Storage;

public interface IDiagramStore
{
    Task<DiagramLibrary> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DiagramLibrary diagramLibrary, CancellationToken cancellationToken = default);
}

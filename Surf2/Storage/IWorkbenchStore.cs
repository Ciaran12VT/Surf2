using Surf2.Models;

namespace Surf2.Storage;

public interface IWorkbenchStore
{
    Task<WorkbenchLibrary> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WorkbenchLibrary workbenchLibrary, CancellationToken cancellationToken = default);
}

using Surf2.Models;

namespace Surf2.Storage;

public interface IWorkspaceStore
{
    Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default);
}

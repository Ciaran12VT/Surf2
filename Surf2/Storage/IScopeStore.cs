using Surf2.Models;

namespace Surf2.Storage;

public interface IScopeStore
{
    Task<ScopeLibrary> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ScopeLibrary scopeLibrary, CancellationToken cancellationToken = default);
}

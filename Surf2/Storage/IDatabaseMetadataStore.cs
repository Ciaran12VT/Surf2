using Surf2.Models;

namespace Surf2.Storage;

public interface IDatabaseMetadataStore
{
    Task<DatabaseSnapshotLibrary> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DatabaseSnapshotLibrary snapshotLibrary, CancellationToken cancellationToken = default);
}

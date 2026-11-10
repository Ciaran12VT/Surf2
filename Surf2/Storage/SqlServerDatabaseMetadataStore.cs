using Surf2.Models;

namespace Surf2.Storage;

public sealed class SqlServerDatabaseMetadataStore : IDatabaseMetadataStore
{
    private const string DocumentKey = "database-snapshots";
    private readonly SqlServerDocumentStore _documentStore;

    public SqlServerDatabaseMetadataStore(SqlServerConnectionOptions? options = null)
    {
        _documentStore = new(options ?? SqlServerConnectionOptions.CreateDefault());
    }
    private readonly JsonDatabaseMetadataStore _jsonFallback = new();

    public async Task<DatabaseSnapshotLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        DatabaseSnapshotLibrary? snapshotLibrary = await _documentStore.LoadAsync<DatabaseSnapshotLibrary>(DocumentKey, cancellationToken);
        if (snapshotLibrary != null)
        {
            return snapshotLibrary;
        }

        snapshotLibrary = await _jsonFallback.LoadAsync(cancellationToken);
        await _documentStore.SaveAsync(DocumentKey, snapshotLibrary, cancellationToken);
        return snapshotLibrary;
    }

    public Task SaveAsync(DatabaseSnapshotLibrary snapshotLibrary, CancellationToken cancellationToken = default)
    {
        return _documentStore.SaveAsync(DocumentKey, snapshotLibrary, cancellationToken);
    }
}

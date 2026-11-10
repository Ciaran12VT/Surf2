using Surf2.Models;

namespace Surf2.Storage;

public sealed class SqlServerWorkspaceStore : IWorkspaceStore
{
    private const string DocumentKey = "workspace-state";
    private readonly SqlServerDocumentStore _documentStore;

    public SqlServerWorkspaceStore(SqlServerConnectionOptions? options = null)
    {
        _documentStore = new(options ?? SqlServerConnectionOptions.CreateDefault());
    }
    private readonly JsonWorkspaceStore _jsonFallback = new();

    public async Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        WorkspaceState? state = await _documentStore.LoadAsync<WorkspaceState>(DocumentKey, cancellationToken);
        if (state != null)
        {
            return state;
        }

        state = await _jsonFallback.LoadAsync(cancellationToken);
        await _documentStore.SaveAsync(DocumentKey, state, cancellationToken);
        return state;
    }

    public Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default)
    {
        return _documentStore.SaveAsync(DocumentKey, state, cancellationToken);
    }
}

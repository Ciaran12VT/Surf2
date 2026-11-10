using Surf2.Models;

namespace Surf2.Storage;

public sealed class SqlServerScopeStore : IScopeStore
{
    private const string DocumentKey = "scope-library";
    private readonly SqlServerDocumentStore _documentStore;

    public SqlServerScopeStore(SqlServerConnectionOptions? options = null)
    {
        _documentStore = new(options ?? SqlServerConnectionOptions.CreateDefault());
    }
    private readonly JsonScopeStore _jsonFallback = new();

    public async Task<ScopeLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        ScopeLibrary? scopeLibrary = await _documentStore.LoadAsync<ScopeLibrary>(DocumentKey, cancellationToken);
        if (scopeLibrary != null)
        {
            return scopeLibrary;
        }

        scopeLibrary = await _jsonFallback.LoadAsync(cancellationToken);
        await _documentStore.SaveAsync(DocumentKey, scopeLibrary, cancellationToken);
        return scopeLibrary;
    }

    public Task SaveAsync(ScopeLibrary scopeLibrary, CancellationToken cancellationToken = default)
    {
        return _documentStore.SaveAsync(DocumentKey, scopeLibrary, cancellationToken);
    }
}

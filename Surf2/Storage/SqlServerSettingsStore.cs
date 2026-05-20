using Surf2.Models;

namespace Surf2.Storage;

public sealed class SqlServerSettingsStore : ISettingsStore
{
    private const string DocumentKey = "app-settings";
    private readonly SqlServerDocumentStore _documentStore = new();
    private readonly JsonSettingsStore _jsonFallback = new();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings? settings = await _documentStore.LoadAsync<AppSettings>(DocumentKey, cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = await _jsonFallback.LoadAsync(cancellationToken);
        await _documentStore.SaveAsync(DocumentKey, settings, cancellationToken);
        return settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        return _documentStore.SaveAsync(DocumentKey, settings, cancellationToken);
    }
}

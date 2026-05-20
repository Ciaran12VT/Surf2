using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class JsonDatabaseMetadataStore : IDatabaseMetadataStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _stateFilePath;

    public JsonDatabaseMetadataStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string stateDirectory = Path.Combine(appData, "Surf2");
        _stateFilePath = Path.Combine(stateDirectory, "database-snapshots.json");
    }

    public async Task<DatabaseSnapshotLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new DatabaseSnapshotLibrary();
        }

        await using FileStream stream = File.OpenRead(_stateFilePath);
        DatabaseSnapshotLibrary? library = await JsonSerializer.DeserializeAsync<DatabaseSnapshotLibrary>(stream, SerializerOptions, cancellationToken);
        return library ?? new DatabaseSnapshotLibrary();
    }

    public async Task SaveAsync(DatabaseSnapshotLibrary snapshotLibrary, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, snapshotLibrary, SerializerOptions, cancellationToken);
    }
}

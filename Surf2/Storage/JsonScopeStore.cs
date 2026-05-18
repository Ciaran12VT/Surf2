using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class JsonScopeStore : IScopeStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _stateFilePath;

    public JsonScopeStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string stateDirectory = Path.Combine(appData, "Surf2");
        _stateFilePath = Path.Combine(stateDirectory, "scopes.json");
    }

    public async Task<ScopeLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new ScopeLibrary();
        }

        await using FileStream stream = File.OpenRead(_stateFilePath);
        ScopeLibrary? library = await JsonSerializer.DeserializeAsync<ScopeLibrary>(stream, SerializerOptions, cancellationToken);
        return library ?? new ScopeLibrary();
    }

    public async Task SaveAsync(ScopeLibrary scopeLibrary, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, scopeLibrary, SerializerOptions, cancellationToken);
    }
}

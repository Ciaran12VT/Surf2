using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class JsonDiagramStore : IDiagramStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _stateFilePath;

    public JsonDiagramStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string stateDirectory = Path.Combine(appData, "Surf2");
        _stateFilePath = Path.Combine(stateDirectory, "diagrams.json");
    }

    public async Task<DiagramLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new DiagramLibrary();
        }

        await using FileStream stream = File.OpenRead(_stateFilePath);
        DiagramLibrary? library = await JsonSerializer.DeserializeAsync<DiagramLibrary>(stream, SerializerOptions, cancellationToken);
        return library ?? new DiagramLibrary();
    }

    public async Task SaveAsync(DiagramLibrary diagramLibrary, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, diagramLibrary, SerializerOptions, cancellationToken);
    }
}

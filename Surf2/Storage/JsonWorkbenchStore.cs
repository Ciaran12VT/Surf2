using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class JsonWorkbenchStore : IWorkbenchStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _stateFilePath;

    public JsonWorkbenchStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string stateDirectory = Path.Combine(appData, "Surf2");
        _stateFilePath = Path.Combine(stateDirectory, "workbenches.json");
    }

    public async Task<WorkbenchLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new WorkbenchLibrary();
        }

        await using FileStream stream = File.OpenRead(_stateFilePath);
        WorkbenchLibrary? library = await JsonSerializer.DeserializeAsync<WorkbenchLibrary>(stream, SerializerOptions, cancellationToken);
        return library ?? new WorkbenchLibrary();
    }

    public async Task SaveAsync(WorkbenchLibrary workbenchLibrary, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, workbenchLibrary, SerializerOptions, cancellationToken);
    }
}

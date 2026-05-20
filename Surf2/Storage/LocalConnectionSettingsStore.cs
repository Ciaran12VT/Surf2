using System.IO;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class LocalConnectionSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;

    public LocalConnectionSettingsStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string stateDirectory = Path.Combine(appData, "Surf2");
        _stateFilePath = Path.Combine(stateDirectory, "connection-settings.json");
    }

    public PersistenceConnectionSettings Load()
    {
        if (!File.Exists(_stateFilePath))
        {
            return new PersistenceConnectionSettings();
        }

        using FileStream stream = File.OpenRead(_stateFilePath);
        PersistenceConnectionSettings? settings = JsonSerializer.Deserialize<PersistenceConnectionSettings>(stream, SerializerOptions);
        return settings ?? new PersistenceConnectionSettings();
    }

    public void Save(PersistenceConnectionSettings settings)
    {
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(_stateFilePath);
        JsonSerializer.Serialize(stream, settings, SerializerOptions);
    }
}

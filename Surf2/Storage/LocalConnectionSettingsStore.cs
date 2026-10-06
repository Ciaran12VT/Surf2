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

    public LocalConnectionSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Surf2", "connection-settings.json"))
    {
    }

    public LocalConnectionSettingsStore(string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        _stateFilePath = Path.GetFullPath(stateFilePath);
    }

    public PersistenceConnectionSettings Load()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(_stateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PersistenceConnectionSettings();
        }

        // Readers retain one complete version while an atomic save replaces its directory entry.
        using (stream)
        {
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("Connection settings exceed the bootstrap size limit.");
            return JsonSerializer.Deserialize<PersistenceConnectionSettings>(stream, SerializerOptions)
                ?? throw new InvalidDataException("Connection settings are empty.");
        }
    }

    public void Save(PersistenceConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string pendingPath = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".pending";
        bool createdPending = false;
        try
        {
            using (var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdPending = true;
                JsonSerializer.Serialize(stream, settings, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_stateFilePath))
                File.Replace(pendingPath, _stateFilePath, destinationBackupFileName: null);
            else
                File.Move(pendingPath, _stateFilePath);
        }
        finally
        {
            // Never remove an existing bootstrap or a partial file owned by another save.
            if (createdPending) File.Delete(pendingPath);
        }
    }
}

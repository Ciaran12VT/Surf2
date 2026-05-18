using System.IO;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class JsonWorkspaceStore : IWorkspaceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;

    public JsonWorkspaceStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string stateDirectory = Path.Combine(appData, "Surf2");
        _stateFilePath = Path.Combine(stateDirectory, "workspace-state.json");
    }

    public async Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new WorkspaceState();
        }

        await using FileStream stream = File.OpenRead(_stateFilePath);
        WorkspaceState? state = await JsonSerializer.DeserializeAsync<WorkspaceState>(stream, SerializerOptions, cancellationToken);
        return state ?? new WorkspaceState();
    }

    public async Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken);
    }
}

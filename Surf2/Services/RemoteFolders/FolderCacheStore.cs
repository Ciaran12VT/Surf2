using System.IO;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

public sealed record FolderCacheSettings(long PerSourceBytes = 2L * 1024 * 1024 * 1024,
    long TotalBytes = 10L * 1024 * 1024 * 1024, long FreeSpaceReserveBytes = 1024L * 1024 * 1024)
{
    public void Validate()
    {
        if (PerSourceBytes < 1024 * 1024 || TotalBytes < PerSourceBytes || FreeSpaceReserveBytes < 0)
            throw new ArgumentException("The per-source limit must be at least 1 MiB, the total limit must be at least the per-source limit, and the disk reserve cannot be negative.");
    }
}
public sealed record FolderCacheEntry(string Key, Guid SourceId, string Name, string Generation,
    DateTimeOffset LastUsedUtc, DateTimeOffset LastRefreshUtc, long Bytes = 0);

/// <summary>Serializes downloads and cleanup across desktop instances; generations publish through one atomic pointer.</summary>
public sealed class FolderCacheStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string Root { get; }
    public FolderCacheStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Surf2", "SourceCache"));
    }
    public FolderCacheSettings ReadSettings()
    {
        string path = Path.Combine(Root, "settings.json");
        var settings = File.Exists(path) ? ReadJson<FolderCacheSettings>(path) : new();
        settings.Validate(); return settings;
    }
    public async Task SaveSettingsAsync(FolderCacheSettings settings, CancellationToken ct = default)
    {
        settings.Validate();
        using var operation = await AcquireOperationAsync(ct);
        WriteJson(Path.Combine(Root, "settings.json"), settings);
    }
    public async Task<IReadOnlyList<FolderCacheEntry>> ListAsync(CancellationToken ct = default)
    {
        using var operation = await AcquireOperationAsync(ct);
        var result = new List<FolderCacheEntry>();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            ct.ThrowIfCancellationRequested();
            string head = Path.Combine(directory, "current.json");
            if (File.Exists(head)) result.Add(ReadJson<FolderCacheEntry>(head) with { Bytes = DirectoryBytes(directory), LastUsedUtc = File.GetLastWriteTimeUtc(head) });
            else if (Path.GetFileName(directory).Length == 64 && Path.GetFileName(directory).All(char.IsAsciiHexDigit))
                result.Add(new(Path.GetFileName(directory), Guid.Empty, "Incomplete preparation", "", Directory.GetLastWriteTimeUtc(directory), DateTimeOffset.MinValue, DirectoryBytes(directory)));
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        using var operation = await AcquireOperationAsync(ct);
        string path = SourceRoot(key);
        if (!Directory.Exists(path)) return;
        FolderSourcePaths.RequireNoReparseAncestors(Root, path);
        // Prove all generations are unused before mutating any of them.
        var leases = new List<FileStream>();
        try
        {
            foreach (string generation in Directory.EnumerateDirectories(path))
            {
                FolderSourcePaths.RequireNoReparseAncestors(Root, generation);
                leases.Add(new FileStream(Path.Combine(generation, ".usage"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete));
            }
            // Retain exclusion while deleting: a concurrent reader must not acquire a generation
            // between the in-use check and deletion. Delete-sharing permits our own cleanup.
            DeleteOwnedDirectory(path);
        }
        catch (IOException) { throw new IOException("This local copy is in use. Close its resources and retry."); }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
    public PreparedFolderSource? TryOpenCurrent(ResourceSourceDefinition source)
    {
        string key = FolderSourcePaths.ConfigurationKey(source), directory = SourceRoot(key), head = Path.Combine(directory, "current.json");
        if (!File.Exists(head)) return null;
        var entry = ReadJson<FolderCacheEntry>(head);
        string generation = FolderSourcePaths.SafeCombine(directory, entry.Generation);
        if (!Directory.Exists(generation)) return null;
        var manifest = ReadJson<FolderSourceManifest>(Path.Combine(generation, "manifest.json"));
        if (manifest.ConfigurationKey != key) throw new IOException("The local copy does not match this source configuration.");
        string files = Path.Combine(generation, "content");
        if (manifest.RootRelativePath.Length != 0) files = FolderSourcePaths.SafeCombine(files, manifest.RootRelativePath);
        var lease = new FileStream(Path.Combine(generation, ".usage"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
        try { File.SetLastWriteTimeUtc(head, DateTime.UtcNow); } catch (IOException) { }
        try { return new(files, manifest, lease); }
        catch { lease.Dispose(); throw; }
    }
    internal void MarkCurrentChecked(ResourceSourceDefinition source)
    {
        string sourceRoot = SourceRoot(FolderSourcePaths.ConfigurationKey(source));
        string head = Path.Combine(sourceRoot, "current.json");
        var entry = ReadJson<FolderCacheEntry>(head);
        PruneRetiredGenerations(sourceRoot, FolderSourcePaths.SafeCombine(sourceRoot, entry.Generation));
        var now = DateTimeOffset.UtcNow;
        WriteJson(head, entry with { LastUsedUtc = now, LastRefreshUtc = now });
    }
    internal string SourceRoot(string key)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid cache identity.");
        return FolderSourcePaths.SafeCombine(Root, key);
    }
    internal string CreateStage(ResourceSourceDefinition source)
    {
        string parent = SourceRoot(FolderSourcePaths.ConfigurationKey(source));
        Directory.CreateDirectory(parent);
        FolderSourcePaths.RequireNoReparseAncestors(Root, parent);
        string head = Path.Combine(parent, "current.json");
        string? current = File.Exists(head) ? FolderSourcePaths.SafeCombine(parent, ReadJson<FolderCacheEntry>(head).Generation) : null;
        PruneRetiredGenerations(parent, current);
        string path = Path.Combine(parent, "generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "content"));
        File.WriteAllBytes(Path.Combine(path, ".usage"), []);
        return path;
    }
    internal PreparedFolderSource Publish(ResourceSourceDefinition source, string name, string stage, FolderSourceManifest manifest)
    {
        string key = FolderSourcePaths.ConfigurationKey(source), sourceRoot = SourceRoot(key);
        if (!Path.GetDirectoryName(stage)!.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid cache publication path.");
        WriteJson(Path.Combine(stage, "manifest.json"), manifest);
        CheckCapacity(source, 4096);
        WriteJson(Path.Combine(sourceRoot, "current.json"), new FolderCacheEntry(key, source.SourceId, name, Path.GetFileName(stage), DateTimeOffset.UtcNow, manifest.PreparedAtUtc));
        var prepared = TryOpenCurrent(source)!;
        PruneRetiredGenerations(sourceRoot, stage);
        return prepared;
    }
    // Call only while holding the cache operation lock. Active reader/review leases exclude deletion.
    private void PruneRetiredGenerations(string sourceRoot, string? current)
    {
        foreach (string old in Directory.EnumerateDirectories(sourceRoot).Where(p => !p.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                FolderSourcePaths.RequireNoReparseAncestors(Root, old);
                using var exclusion = new FileStream(Path.Combine(old, ".usage"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
                DeleteOwnedDirectory(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* In-use generations are retired by a later refresh or explicit cleanup. */ }
        }
    }
    internal void CheckCapacity(ResourceSourceDefinition source, long additionalBytes = 0)
    {
        var settings = ReadSettings();
        long total = DirectoryBytes(Root), own = DirectoryBytes(SourceRoot(FolderSourcePaths.ConfigurationKey(source)));
        if (additionalBytes < 0 || total > settings.TotalBytes - additionalBytes || own > settings.PerSourceBytes - additionalBytes)
            throw new FolderCapacityException("The source would exceed the configured local-cache limit. Choose a smaller source, remove an unused copy, or increase the limit in Local Cache settings.");
        RequireDriveSpace(Root, additionalBytes, settings.FreeSpaceReserveBytes);
    }
    public static void RequireDriveSpace(string destination, long additionalBytes, long reserve)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
        if (drive.AvailableFreeSpace < checked(additionalBytes + reserve))
            throw new FolderCapacityException("The destination drive does not have enough free space after the configured reserve.");
    }
    internal async Task<IDisposable> AcquireOperationAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Root);
            FolderSourcePaths.RequireNoReparseAncestors(Root, Root);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { return new Operation(new FileStream(Path.Combine(Root, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                catch (IOException) { await Task.Delay(150, ct); }
            }
        }
        catch { Gate.Release(); throw; }
    }
    internal void DeleteOwnedDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cache cleanup cannot delete an unowned directory.");
        if (!Directory.Exists(full)) return;
        FolderSourcePaths.RequireNoReparseAncestors(Root, full);
        _ = DirectoryBytes(full); // Refuse traversal through a reparse point before recursive cleanup.
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, true);
    }
    internal static long DirectoryBytes(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A cache directory contains a link or junction.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("A cache directory contains a link or junction.");
                if ((attrs & FileAttributes.Directory) != 0) pending.Push(entry); else total = checked(total + new FileInfo(entry).Length);
            }
        }
        return total;
    }
    internal T ReadJson<T>(string path)
    {
        if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new IOException("A local manifest exceeds the supported size.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _json) ?? throw new IOException("A local manifest is invalid.");
    }
    internal void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, _json);
                if (file.Length > 64L * 1024 * 1024) throw new FolderCapacityException("The local manifest exceeds its supported size. Select a smaller source.");
                file.Flush(true);
            }
            File.Move(pending, path, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private sealed class Operation(FileStream file) : IDisposable
    {
        private FileStream? _file = file;
        public void Dispose() { var owned = Interlocked.Exchange(ref _file, null); if (owned == null) return; owned.Dispose(); Gate.Release(); }
    }
}

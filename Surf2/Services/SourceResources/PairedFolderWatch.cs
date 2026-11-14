using System.IO;

namespace Surf2.Services.SourceResources;

/// <summary>All local changes invalidate a reviewed Blob pair, including files excluded from parsing.</summary>
public sealed class PairedFolderWatch : IDisposable
{
    private readonly FileSystemWatcher _watch;
    private int _changes;
    public bool Changed => Volatile.Read(ref _changes) != 0;
    public PairedFolderWatch(string folder, Action changed)
    {
        _watch = new FileSystemWatcher(folder) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.Security };
        void Invalidate() { Interlocked.Increment(ref _changes); changed(); }
        _watch.Changed += (_, _) => Invalidate(); _watch.Created += (_, _) => Invalidate();
        _watch.Deleted += (_, _) => Invalidate(); _watch.Renamed += (_, _) => Invalidate();
        _watch.Error += (_, _) => Invalidate();
        _watch.EnableRaisingEvents = true;
    }
    public void Dispose() => _watch.Dispose();
}

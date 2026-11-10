using System.IO;

namespace Surf2.Services.RelationalGrid;

internal sealed class GridOwnedWorkspace : IDisposable
{
    private readonly object _gate = new();
    private readonly GridLimits _limits;
    private readonly OwnedScratchLease _lease;
    private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private bool _disposed;
    internal string DirectoryPath { get; }

    internal GridOwnedWorkspace(GridLimits limits)
    {
        _limits = limits;
        string parent = Path.GetFullPath(limits.StagingDirectory ?? Path.Combine(Path.GetTempPath(), "Surf2", "relational-grid"));
        _lease = new OwnedScratchLease(parent);
        DirectoryPath = _lease.DirectoryPath;
    }

    internal FileStream Create(string suffix)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_files.Count >= _limits.MaxOwnedFiles) throw new GridLimitException("Grid staging file-count quota exceeded.");
            string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + suffix);
            _lease.RegisterFile(path);
            _files.Add(path, 0);
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous);
        }
    }

    internal void Reserve(string path, long bytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_files.ContainsKey(path) || bytes < 0) throw new InvalidOperationException("Unowned grid staging write.");
            if (bytes > _limits.MaxDiskBytes - _bytes) throw new GridLimitException("Grid disk quota exceeded, including sort merge workspace; no rows were truncated.");
            _files[path] += bytes;
            _bytes += bytes;
        }
    }

    internal void Delete(string path)
    {
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out long bytes)) return;
            _lease.ValidateOwnedPath(path);
            File.Delete(path);
            _lease.ForgetDeletedFile(path);
            _files.Remove(path);
            _bytes -= bytes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception> failures = [];
            try
            {
                foreach (string path in _files.Keys.ToArray())
                    try { _lease.ValidateOwnedPath(path); File.Delete(path); _files.Remove(path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures.Add(e); }
            }
            finally
            {
                // Source/query cleanup has closed payload streams. Release the live marker even if a file could not be deleted.
                try { _lease.Dispose(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures.Add(e); }
            }
            if (failures.Count != 0) throw new AggregateException("Owned grid staging cleanup failed.", failures);
        }
    }
}

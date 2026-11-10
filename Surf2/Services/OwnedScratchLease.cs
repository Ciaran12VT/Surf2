using System.Globalization;
using System.IO;
using System.Text;

namespace Surf2.Services;

/// <summary>Crash recovery for generated scratch files only. An exclusive marker protects live operations.</summary>
public sealed class OwnedScratchLease : IDisposable
{
    private const string Marker = ".surf2-owned-v1";
    private const string Header = "Surf2 scratch ownership v1";
    private const int MaximumFiles = 32768, MaximumMarkerBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
        { ".index", ".rows", ".sort", ".spool" };
    private readonly object _gate = new();
    private readonly FileStream _marker;
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recorded = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maximumManifestEntries;
    private readonly long _startedAtTicks;
    private bool _disposed;
    public string DirectoryPath { get; }

    public OwnedScratchLease(string parent, TimeSpan? abandonedAge = null, int maximumManifestEntries = MaximumFiles)
    {
        if (maximumManifestEntries is < 1 or > MaximumFiles) throw new ArgumentOutOfRangeException(nameof(maximumManifestEntries));
        _maximumManifestEntries = maximumManifestEntries;
        _startedAtTicks = DateTime.UtcNow.Ticks;
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        VerifyNotRedirected(parent);
        Directory.CreateDirectory(parent);
        VerifyNotRedirected(parent);
        RecoverAbandoned(parent, abandonedAge ?? TimeSpan.FromDays(2));
        DirectoryPath = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        _marker = new FileStream(Path.Combine(DirectoryPath, Marker), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.Delete);
        WriteMarker(Header + "\n" + _startedAtTicks.ToString(CultureInfo.InvariantCulture) + "\n");
    }

    public void RegisterFile(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string full = Path.GetFullPath(path), name = Path.GetFileName(full);
            VerifyNotRedirected(DirectoryPath);
            if (!string.Equals(Path.GetDirectoryName(full), DirectoryPath, StringComparison.OrdinalIgnoreCase) || !ValidName(name))
                throw new IOException("Only generated files in the owned scratch directory can be registered.");
            if (_registered.Contains(name)) return;
            if (_registered.Count == _maximumManifestEntries) throw new IOException("Scratch ownership file budget exceeded.");
            if (!_recorded.Contains(name))
            {
                if (_recorded.Count == _maximumManifestEntries) CompactManifest();
                WriteMarker(name + "\n");
                _recorded.Add(name);
            }
            _registered.Add(name);
        }
    }

    public void ForgetDeletedFile(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateOwnedPath(path);
            string full = Path.GetFullPath(path);
            try
            {
                _ = File.GetAttributes(full);
                throw new IOException("A live scratch payload cannot be removed from its ownership record.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            _registered.Remove(Path.GetFileName(full));
        }
    }

    private void CompactManifest()
    {
        VerifyNotRedirected(DirectoryPath);
        string markerPath = Path.Combine(DirectoryPath, Marker);
        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Scratch ownership markers must not be redirected.");
        string text = Header + "\n" + _startedAtTicks.ToString(CultureInfo.InvariantCulture) + "\n" +
            string.Join("\n", _registered) + (_registered.Count == 0 ? "" : "\n");
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        // Keep the exclusive live handle throughout rewriting. A crash-torn marker
        // is conservatively left untouched by recovery rather than trusted for deletion.
        _marker.Position = 0;
        _marker.Write(bytes);
        _marker.SetLength(bytes.Length);
        _marker.Flush(flushToDisk: true);
        _recorded.Clear();
        _recorded.UnionWith(_registered);
    }

    private void WriteMarker(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        _marker.Write(bytes);
        _marker.Flush(flushToDisk: true);
    }

    public void ValidateOwnedPath(string path)
    {
        string full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!string.Equals(Path.GetDirectoryName(full), DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
                !_registered.Contains(Path.GetFileName(full))) throw new IOException("Unregistered scratch cleanup path.");
            VerifyNotRedirected(DirectoryPath);
            try
            {
                if ((File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Owned scratch files must not be redirected or replaced with directories.");
            }
            catch (FileNotFoundException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Callers have already disposed/deleted their payload files. Deleting the marker
            // while its exclusive handle is held prevents a recovery race with this operation.
            try
            {
                VerifyNotRedirected(DirectoryPath);
                if (!Directory.EnumerateFileSystemEntries(DirectoryPath).Any(path => Path.GetFileName(path) != Marker))
                    File.Delete(Path.Combine(DirectoryPath, Marker));
                else return; // Retain the recovery record after a payload cleanup failure, but release its live lock.
            }
            finally { _marker.Dispose(); }
            try { Directory.Delete(DirectoryPath, recursive: false); }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static int RecoverAbandoned(string parent, TimeSpan age, int maximumCandidates = 128)
    {
        if (age < TimeSpan.Zero || maximumCandidates is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(age));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!Directory.Exists(parent)) return 0;
        VerifyNotRedirected(parent);
        int checkedCount = 0, recovered = 0;
        foreach (string candidate in Directory.EnumerateDirectories(parent))
        {
            if (++checkedCount > maximumCandidates) break;
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), parent, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                VerifyNotRedirected(candidate);
                string markerPath = Path.Combine(candidate, Marker);
                if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0) continue;
                using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                if (marker.Length > MaximumMarkerBytes) continue;
                using var reader = new StreamReader(marker, Encoding.ASCII, false, 4096, leaveOpen: true);
                if (reader.ReadLine() != Header || !long.TryParse(reader.ReadLine(), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
                    ticks < DateTime.MinValue.Ticks || ticks > DateTime.UtcNow.Ticks || DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < age) continue;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? name;
                bool valid = true;
                while ((name = reader.ReadLine()) != null)
                    if (!ValidName(name) || !names.Add(name) || names.Count > MaximumFiles) { valid = false; break; }
                if (!valid) continue;
                // Unknown files or directories invalidate ownership, rather than becoming deletion candidates.
                int entries = 0;
                foreach (string entry in Directory.EnumerateFileSystemEntries(candidate))
                {
                    if (++entries > MaximumFiles + 1 || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                        (Path.GetFileName(entry) != Marker && !names.Contains(Path.GetFileName(entry)))) { valid = false; break; }
                }
                if (!valid) continue;
                foreach (string ownedName in names) File.Delete(Path.Combine(candidate, ownedName));
                File.Delete(markerPath);
                marker.Dispose();
                Directory.Delete(candidate, recursive: false);
                recovered++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Active or inaccessible: leave untouched. */ }
        }
        return recovered;
    }

    private static bool ValidName(string name) => name.Length > 32 && Guid.TryParseExact(name[..32], "N", out _) &&
        Suffixes.Contains(name[32..]) && Path.GetFileName(name) == name;

    private static void VerifyNotRedirected(string path)
    {
        for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory.FullName); }
            catch (DirectoryNotFoundException) { continue; }
            catch (FileNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scratch directories must not be redirected.");
        }
    }
}

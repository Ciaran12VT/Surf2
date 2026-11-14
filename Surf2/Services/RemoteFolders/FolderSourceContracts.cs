using System.IO;
using System.Security.Cryptography;
using System.Text;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

public sealed record FolderSourceProgress(string Phase, long CompletedBytes = 0, long? TotalBytes = null, int Files = 0);
public sealed record FolderSourceItem(string LogicalPath, string CacheRelativePath, long ByteCount,
    DateTimeOffset? LastModified, string Revision, string Sha256);
public sealed record FolderSourceManifest(string ConfigurationKey, string Revision, DateTimeOffset PreparedAtUtc,
    IReadOnlyList<FolderSourceItem> Items, string RootRelativePath = "");
public sealed record FolderAccessResult(string Description, long? EstimatedBytes = null, int? FileCount = null);

/// <summary>The owner retains this lease while browsing/indexing the prepared generation.</summary>
public sealed class PreparedFolderSource(string physicalRoot, FolderSourceManifest manifest, IDisposable? lease = null) : IDisposable
{
    public string PhysicalRoot { get; } = physicalRoot;
    public FolderSourceManifest Manifest { get; } = manifest;
    internal PreparedFolderIndex Index { get; } = new(physicalRoot, manifest.Items);
    public string Revision => Manifest.Revision;
    public string ResolveItem(string logicalPath)
    {
        return Index.TryFile(logicalPath, out var item) ? item.PhysicalPath!
            : throw new FileNotFoundException("The item is not in the prepared source.", logicalPath);
    }
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    private IDisposable? _lease = lease;
}

public sealed class FolderSourceException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class FolderCapacityException(string message) : IOException(message);
public sealed class FolderConflictException(string message) : IOException(message);

public static class FolderSourcePaths
{
    public static string ConfigurationKey(ResourceSourceDefinition source) => Hash(string.Join("\n",
        source.SourceId.ToString("N"), source.ConfigurationRevision, source.Provider, source.Endpoint,
        source.Container, source.Prefix, source.Ref, source.Subfolder, source.DatabaseName, source.AuthenticationKind));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "";
        if (prefix.StartsWith('/') || prefix.Contains('\\') || prefix.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Use a relative Blob folder prefix with forward slashes.");
        return prefix.TrimEnd('/') + "/";
    }
    public static string SafeCombine(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new IOException("An item path is not relative.");
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("An item escapes its source folder.");
        return candidate;
    }
    public static void RequireWindowsRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.StartsWith('/')) throw new IOException("An item cannot be represented as a Windows relative path: " + path);
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
                throw new IOException("An item cannot be represented safely on Windows: " + path);
        }
    }
    public static void RequireNoReparseAncestors(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links and junctions are not supported in a managed transfer.");
            if (current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current);
        }
        throw new IOException("The item is outside its source folder.");
    }
    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
    internal static string CacheName(string logicalPath)
    {
        string extension = Path.GetExtension(logicalPath);
        if (extension.Length > 20 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = "";
        return "objects/" + Hash(logicalPath) + extension;
    }
}

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;

namespace Surf2.Services.RemoteFolders;

internal sealed record PreparedFolderEntry(string LogicalPath, string Name, bool IsDirectory, string? PhysicalPath, string VirtualPath)
{
    internal string Path(bool virtualDirectories) => IsDirectory && virtualDirectories ? VirtualPath
        : PhysicalPath ?? throw new IOException("This logical directory cannot be represented as a physical Windows directory.");
}

/// <summary>One immutable lookup index per prepared generation; warm reads never rescan the manifest.</summary>
internal sealed class PreparedFolderIndex
{
    private readonly FrozenDictionary<string, PreparedFolderEntry> _files, _directories;
    private readonly FrozenDictionary<string, string> _physicalFiles, _virtualDirectories;
    private readonly FrozenDictionary<string, string?> _physicalDirectories;
    private readonly FrozenDictionary<string, ImmutableArray<PreparedFolderEntry>> _children;

    internal PreparedFolderIndex(string root, IReadOnlyList<FolderSourceItem> items)
    {
        var files = new Dictionary<string, PreparedFolderEntry>(StringComparer.Ordinal);
        var directories = new Dictionary<string, PreparedFolderEntry>(StringComparer.Ordinal);
        var physicalFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var physicalDirectories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var virtualDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<PreparedFolderEntry>>(StringComparer.Ordinal);
        string physicalRoot = NormalizePhysical(root);
        directories.Add("", new("", "", true, root, root));
        physicalDirectories.Add(physicalRoot, ""); virtualDirectories.Add(physicalRoot, ""); children.Add("", []);
        foreach (var item in items)
        {
            string logical = item.LogicalPath;
            if (string.IsNullOrEmpty(logical) || logical.Split('/').Any(p => p.Length == 0))
                throw new IOException("A prepared source contains an ambiguous logical item path.");
            string physical = FolderSourcePaths.SafeCombine(root, item.CacheRelativePath);
            var file = new PreparedFolderEntry(logical, Name(logical), false, physical, physical);
            if (!files.TryAdd(logical, file) || !physicalFiles.TryAdd(NormalizePhysical(physical), logical))
                throw new IOException("A prepared source contains duplicate or conflicting file mappings.");
            AddChild(Parent(logical), file);
            string parent = Parent(logical);
            while (parent.Length != 0 && !directories.ContainsKey(parent))
            {
                string? realPath;
                try { realPath = FolderSourcePaths.SafeCombine(root, parent); }
                catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { realPath = null; }
                string virtualPath = System.IO.Path.Combine(root, "tree", FolderSourcePaths.Hash(parent));
                var directory = new PreparedFolderEntry(parent, Name(parent), true, realPath, virtualPath);
                directories.Add(parent, directory); virtualDirectories.Add(NormalizePhysical(virtualPath), parent);
                if (realPath != null)
                {
                    string key = NormalizePhysical(realPath);
                    // Case-distinct Blob directories are valid in the virtual projection, but have no
                    // unambiguous equivalent on an ordinary Windows directory tree.
                    if (physicalDirectories.TryGetValue(key, out var previous) && previous != parent) physicalDirectories[key] = null;
                    else physicalDirectories[key] = parent;
                }
                AddChild(Parent(parent), directory);
                parent = Parent(parent);
            }
        }
        _files = files.ToFrozenDictionary(StringComparer.Ordinal);
        _directories = directories.ToFrozenDictionary(StringComparer.Ordinal);
        _physicalFiles = physicalFiles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _physicalDirectories = physicalDirectories.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _virtualDirectories = virtualDirectories.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _children = children.ToFrozenDictionary(pair => pair.Key,
            pair => pair.Value.OrderBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.IsDirectory ? 0 : 1).ToImmutableArray(), StringComparer.Ordinal);

        void AddChild(string parent, PreparedFolderEntry child)
        {
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(child);
        }
    }

    internal bool TryFile(string logical, out PreparedFolderEntry file) => _files.TryGetValue(logical, out file!);
    internal bool ContainsDirectory(string logical) => _directories.ContainsKey(logical);
    internal string DirectoryPath(string logical, bool virtualDirectories) => _directories.TryGetValue(logical, out var directory)
        ? directory.Path(virtualDirectories) : throw new DirectoryNotFoundException("The directory is not in the prepared source.");
    internal ImmutableArray<PreparedFolderEntry> Children(string logical) => _children.GetValueOrDefault(logical, []);
    internal bool TryPhysicalFile(string path, out string logical)
    {
        string? key = TryNormalizePhysical(path);
        if (key != null && _physicalFiles.TryGetValue(key, out logical!)) return true;
        logical = ""; return false;
    }
    internal bool TryPhysicalDirectory(string path, bool virtualDirectories, out string logical)
    {
        string? key = TryNormalizePhysical(path);
        if (key == null) { logical = ""; return false; }
        if (virtualDirectories) return _virtualDirectories.TryGetValue(key, out logical!);
        if (_physicalDirectories.TryGetValue(key, out var candidate) && candidate != null) { logical = candidate; return true; }
        logical = ""; return false;
    }
    private static string NormalizePhysical(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    private static string? TryNormalizePhysical(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return NormalizePhysical(path); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { return null; }
    }
    private static string Parent(string logical) { int slash = logical.LastIndexOf('/'); return slash < 0 ? "" : logical[..slash]; }
    private static string Name(string logical) { int slash = logical.LastIndexOf('/'); return slash < 0 ? logical : logical[(slash + 1)..]; }
}

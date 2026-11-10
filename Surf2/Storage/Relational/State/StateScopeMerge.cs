using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

internal static class StateScopeMerge
{
    private const string FolderPrefix = "surf2://virtual-folder/";
    private const string MissingPrefix = "surf2://missing-node/";

    // Prepare the entire detached graph before publishing or mutating an editor.
    internal static Scope Prepare(Scope target, Scope source, StateLimits? limits = null)
    {
        var budget = new StateBudget(limits ?? new());
        var result = StateCopies.Scope(target, budget);
        var additions = StateCopies.Scope(source, budget);
        ValidateIds(result); ValidateIds(additions);
        var used = result.Resources.Select(r => r.ResourceId).Concat(additions.Resources.Select(r => r.ResourceId))
            .Concat(result.VirtualFolders.Select(f => f.VirtualFolderId)).Concat(additions.VirtualFolders.Select(f => f.VirtualFolderId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { result, additions })
            foreach (var key in scope.VirtualFolders.SelectMany(f => f.ChildNodeKeys.Prepend(f.ParentNodeKey)))
            {
                used.Add(key);
                if (key.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase)) used.Add(key[FolderPrefix.Length..]);
            }
        string Fresh() { string id; do { id = Guid.NewGuid().ToString("N"); } while (!used.Add(id)); return id; }
        var resources = additions.Resources.ToDictionary(r => r.ResourceId, _ => Fresh(), StringComparer.OrdinalIgnoreCase);
        var folders = additions.VirtualFolders.ToDictionary(f => f.VirtualFolderId, _ => Fresh(), StringComparer.OrdinalIgnoreCase);
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceNodes = additions.Resources.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var targetNodes = result.Resources.Select(r => r.Path).Concat(result.Resources.Select(r => r.ResourceId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Missing(string raw, bool folder)
        {
            if (!missing.TryGetValue(raw, out string? replacement))
                missing.Add(raw, replacement = (folder ? FolderPrefix : MissingPrefix) + Fresh());
            return replacement;
        }
        string Remap(string raw)
        {
            if (raw.Equals("__root__", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(raw)) return raw;
            if (raw.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase))
                return folders.TryGetValue(raw[FolderPrefix.Length..], out string? folder) ? FolderPrefix + folder : Missing(raw, true);
            if (resources.TryGetValue(raw, out string? resource)) return resource;
            if (sourceNodes.TryGetValue(raw, out int matches))
            {
                if (matches != 1 || targetNodes.Contains(raw))
                    throw new InvalidDataException("Merge has an ambiguous resource node address: " + raw + ". Resolve its folder membership before merging.");
                return raw;
            }
            // A missing source node must not acquire an unrelated destination node by coincidence.
            return targetNodes.Contains(raw) ? Missing(raw, false) : raw;
        }
        foreach (var resource in additions.Resources) resource.ResourceId = resources[resource.ResourceId];
        foreach (var folder in additions.VirtualFolders)
        {
            folder.VirtualFolderId = folders[folder.VirtualFolderId];
            folder.ParentNodeKey = Remap(folder.ParentNodeKey);
            for (int i = 0; i < folder.ChildNodeKeys.Count; i++) folder.ChildNodeKeys[i] = Remap(folder.ChildNodeKeys[i]);
        }
        foreach (var resource in additions.Resources) result.Resources.Add(resource);
        foreach (var folder in additions.VirtualFolders) result.VirtualFolders.Add(folder);
        // Include rewritten keys in the effective output budget, not just the source representation.
        return StateCopies.Scope(result, new(limits ?? new()));
    }

    private static void ValidateIds(Scope scope)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in scope.Resources.Select(r => r.ResourceId).Concat(scope.VirtualFolders.Select(f => f.VirtualFolderId)))
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw new InvalidDataException("Merge requires nonempty, unambiguous resource and virtual-folder IDs. The destination was not changed.");
    }
}

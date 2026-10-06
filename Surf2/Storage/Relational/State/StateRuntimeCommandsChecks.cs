using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

/// <summary>Pure checks callable by the parent regression harness without SQL or a WPF window.</summary>
public static class StateRuntimeCommandsChecks
{
    public static IReadOnlyList<string> RunPure()
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("State runtime command check failed: " + name);
            passed.Add(name);
        }
        const string prefix = "surf2://virtual-folder/";
        var target = new Scope { Name = "Destination" };
        target.Resources.Add(new() { ResourceId = "existing-resource", Kind = ResourceKind.File, Path = @"C:\destination\existing.txt" });
        target.VirtualFolders.Add(new() { VirtualFolderId = "missing-folder", Name = "Unrelated destination folder" });
        var source = new Scope { Name = "Source" };
        source.Resources.Add(new() { ResourceId = "source-resource", Kind = ResourceKind.Folder, Path = @"C:\source", DisplayNameOverride = "Alias",
            DetailsOverride = "Details\0surrogate:\uD800", IncludeChildren = false, AddedAtUtc = DateTimeOffset.UnixEpoch });
        source.VirtualFolders.Add(new() { VirtualFolderId = "parent", Name = "Parent", ChildNodeKeys = [prefix + "child", "source-resource", prefix + "missing-folder", @"C:\destination\existing.txt"] });
        source.VirtualFolders.Add(new() { VirtualFolderId = "child", Name = "Child", ParentNodeKey = "SURF2://VIRTUAL-FOLDER/PARENT",
            ChildNodeKeys = [@"C:\source", prefix + "missing-folder", "still-missing"] });
        var merged = StateScopeMerge.Prepare(target, source);
        Check(target.Resources.Count == 1 && target.VirtualFolders.Count == 1 && source.Resources[0].ResourceId == "source-resource" && source.VirtualFolders[0].VirtualFolderId == "parent",
            "merge preparation never mutates either input");
        Check(merged.Name == target.Name && merged.ScopeId == target.ScopeId && merged.Resources.Count == 2 && merged.VirtualFolders.Count == 3,
            "merge retains destination owner and source ordering");
        var resource = merged.Resources[1]; var parent = merged.VirtualFolders[1]; var child = merged.VirtualFolders[2];
        Check(resource.ResourceId != "source-resource" && parent.VirtualFolderId != "parent" && child.VirtualFolderId != "child" &&
            merged.Resources.Select(r => r.ResourceId).Concat(merged.VirtualFolders.Select(f => f.VirtualFolderId)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5,
            "all copied resource and folder identities are fresh and unique");
        Check(parent.ChildNodeKeys[0] == prefix + child.VirtualFolderId && child.ParentNodeKey == prefix + parent.VirtualFolderId && parent.ChildNodeKeys[1] == resource.ResourceId,
            "nested parents and resource member IDs remap together");
        Check(parent.ChildNodeKeys[2] == child.ChildNodeKeys[1] && parent.ChildNodeKeys[2] != prefix + "missing-folder" &&
            !merged.VirtualFolders.Any(f => prefix + f.VirtualFolderId == parent.ChildNodeKeys[2]),
            "repeated missing folder references remain consistently missing rather than binding the destination");
        Check(parent.ChildNodeKeys[3].StartsWith("surf2://missing-node/", StringComparison.Ordinal) && child.ChildNodeKeys[2] == "still-missing",
            "colliding missing physical references cannot acquire destination targets");
        Check(resource.Path == source.Resources[0].Path && resource.DisplayNameOverride == "Alias" && resource.DetailsOverride == source.Resources[0].DetailsOverride &&
            resource.AddedAtUtc == DateTimeOffset.UnixEpoch && !resource.IncludeChildren && child.ChildNodeKeys[0] == @"C:\source",
            "merge preserves writable resource scalars and valid literal node paths");
        var ambiguous = new Scope(); ambiguous.Resources.Add(new() { ResourceId = "same" }); ambiguous.Resources.Add(new() { ResourceId = "SAME" });
        bool rejected = false;
        try { StateScopeMerge.Prepare(target, ambiguous); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && target.Resources.Count == 1 && ambiguous.Resources.Count == 2, "case-variant duplicate IDs reject before any destination mutation");
        var physicalCollision = new Scope(); physicalCollision.Resources.Add(new() { ResourceId = "another", Kind = ResourceKind.Folder, Path = @"C:\source" });
        rejected = false;
        try { StateScopeMerge.Prepare(physicalCollision, source); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && physicalCollision.Resources.Count == 1, "ambiguous literal folder membership addresses reject before mutation");
        rejected = false;
        try { StateScopeMerge.Prepare(target, source, new StateLimits { MaximumRows = 1 }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "combined source and destination graph obey the selected aggregate row budget");
        return passed;
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public static class ExplorerCompatibility
{
    public const string RootKey = "__root__";
    public const string DiagramRootPath = DiagramDocumentService.DiagramRootPath;
    public static string VirtualKey(string id) => "surf2://virtual-folder/" + id;
    public static string CategoryName(ExplorerCategory category) => category switch
    {
        ExplorerCategory.Procedures => "Stored Procedures", ExplorerCategory.Views => "Views",
        ExplorerCategory.Functions => "Functions", ExplorerCategory.Triggers => "Triggers",
        ExplorerCategory.Tables => "Tables", _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
    public static SqlDatabaseObjectKind ObjectKind(ExplorerCategory category) => category switch
    {
        ExplorerCategory.Procedures => SqlDatabaseObjectKind.StoredProcedure,
        ExplorerCategory.Views => SqlDatabaseObjectKind.View, ExplorerCategory.Functions => SqlDatabaseObjectKind.Function,
        ExplorerCategory.Triggers => SqlDatabaseObjectKind.Trigger, _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
    public static string SnapshotName(SnapshotSummary snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.DisplayName) && !snapshot.DisplayName.Equals("Database Snapshot", StringComparison.OrdinalIgnoreCase))
            return snapshot.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(snapshot.DatabaseName)) return snapshot.DatabaseName.Trim();
        return string.IsNullOrWhiteSpace(snapshot.DisplayName) ? snapshot.SnapshotId : snapshot.DisplayName.Trim();
    }
    public static string SnapshotSegment(SnapshotSummary snapshot, bool canonical = false) => EscapeSnapshot(
        canonical && !string.IsNullOrWhiteSpace(snapshot.SnapshotId) ? snapshot.SnapshotId : SnapshotName(snapshot));
    public static string EscapeSnapshot(string value) => value.Replace("%", "%25", StringComparison.Ordinal)
        .Replace("/", "%2F", StringComparison.Ordinal).Replace("\\", "%5C", StringComparison.Ordinal)
        .Replace("#", "%23", StringComparison.Ordinal).Replace("?", "%3F", StringComparison.Ordinal);
    public static string DatabaseLocator(SnapshotSummary snapshot, ExplorerCategory category, string schema,
        string name, bool canonical = false, bool tableData = false)
    {
        string prefix = "db://" + SnapshotSegment(snapshot, canonical);
        string fullName = Uri.EscapeDataString(SqlName.FormatPlainMultipartName(schema, name));
        return category == ExplorerCategory.Tables
            ? prefix + (tableData ? "/table-data/" : "/table/") + fullName + (tableData ? ".csv" : ".sql")
            : prefix + "/object/" + ObjectKind(category) + "/" + fullName + ".sql";
    }
    public static string MetadataDocumentPath(Guid epoch, long snapshotKey, long resourceKey, ExplorerCategory category)
    {
        if (epoch == Guid.Empty || snapshotKey <= 0 || resourceKey <= 0 || !Enum.IsDefined(category))
            throw new ArgumentException("Metadata document paths require a session and typed resource identity.");
        return "db://@" + epoch.ToString("N") + ":" + snapshotKey.ToString(CultureInfo.InvariantCulture) +
            (category == ExplorerCategory.Tables ? "/table/" : "/object/" + ObjectKind(category) + "/") +
            resourceKey.ToString(CultureInfo.InvariantCulture) + ".sql";
    }
    internal static bool TryParseDatabaseIdentity(DatabaseDocumentReference reference,
        out Guid epoch, out long snapshotKey, out long resourceKey)
    {
        epoch = Guid.Empty; snapshotKey = resourceKey = 0;
        if (!reference.SnapshotId.StartsWith('@')) return false;
        var owner = reference.SnapshotId.AsSpan(1);
        int separator = owner.IndexOf(':');
        return separator > 0 && Guid.TryParseExact(owner[..separator], "N", out epoch) && epoch != Guid.Empty &&
            long.TryParse(owner[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out snapshotKey) && snapshotKey > 0 &&
            long.TryParse(reference.FullName, NumberStyles.None, CultureInfo.InvariantCulture, out resourceKey) && resourceKey > 0;
    }
    public static string ResourceDisplayName(ExplorerResource resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.Alias)) return resource.Alias;
        if (resource.Kind == ResourceKind.DatabaseSnapshot && resource.Snapshot != null) return resource.Snapshot.DisplayName;
        if (resource.Kind == ResourceKind.Diagram && !string.IsNullOrWhiteSpace(resource.DiagramName)) return resource.DiagramName;
        if (resource.Kind == ResourceKind.DatabaseSnapshot) return string.IsNullOrWhiteSpace(resource.Path) ? "Database" : resource.Path;
        if (resource.Kind == ResourceKind.Diagram) return string.IsNullOrWhiteSpace(resource.Path) ? "Diagram" : resource.Path;
        string leaf = Path.GetFileName(resource.Path);
        return string.IsNullOrWhiteSpace(leaf) ? resource.Path : leaf;
    }

    public static ImmutableArray<int> MatchingColumns(JsonElement row, ImmutableArray<string> headers,
        IndexTextMatcher matcher, CancellationToken ct = default, IReadOnlySet<int>? alreadyMatched = null)
    {
        if (row.ValueKind != JsonValueKind.Object) return [];
        var result = ImmutableArray.CreateBuilder<int>();
        for (int i = 0; i < headers.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (alreadyMatched?.Contains(i) == true) continue;
            foreach (var property in row.EnumerateObject())
            {
                if (!property.Name.Equals(headers[i], StringComparison.OrdinalIgnoreCase)) continue;
                if (matcher.IsMatch(CaptureDisplay.Value(property.Value))) result.Add(i);
                break; // First matching property, including duplicate/case-variant keys.
            }
        }
        return result.ToImmutable();
    }

    public static IComparer<string> Comparer(string cultureName) => StringComparer.Create(CultureInfo.GetCultureInfo(cultureName), false);

    // Move only the first matching non-virtual sibling. Duplicate aliases and child keys retain legacy behaviour.
    public static ImmutableArray<ExplorerNodeSummary> ApplyVirtualFolders(ExplorerScope scope, string naturalParent,
        IEnumerable<ExplorerNodeSummary> children, long? parentResourceKey = null)
    {
        var nodes = children.ToList();
        foreach (var folder in scope.VirtualFolders.Where(f => Parent(f.ParentNodeKey).Equals(naturalParent, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.Name, Comparer(scope.SortCultureName)).ThenBy(f => f.SortOrdinal))
        {
            string key = VirtualKey(folder.Id);
            string occurrence = "virtual:" + folder.Key + (parentResourceKey.HasValue ? ":" + parentResourceKey : "");
            foreach (string childKey in folder.ChildNodeKeys)
            {
                int i = nodes.FindIndex(n => n.Role != ExplorerNodeRole.VirtualFolder && n.EffectiveParentOccurrenceKey == null &&
                    n.ParentNodeKey.Equals(naturalParent, StringComparison.OrdinalIgnoreCase) &&
                    n.NodeKey.Equals(childKey, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                var moved = nodes[i] with { ParentNodeKey = key, NaturalParentKey = naturalParent, EffectiveParentOccurrenceKey = occurrence };
                nodes.RemoveAt(i);
                nodes.Add(moved);
            }
            nodes.Add(new(occurrence, folder.Name, folder.Name, folder.Id, key, naturalParent,
                naturalParent, ExplorerNodeRole.VirtualFolder, ResourceKind.Folder, FileSystemNodeIconKind.VirtualFolder,
                ExplorerAvailability.Present, true, false, false, true, ExplorerChildrenState.Unloaded,
                ScopeResourceKey: parentResourceKey, VirtualFolderId: folder.Id));
        }
        return nodes.ToImmutableArray();
    }
    public static string Parent(string? key) => string.IsNullOrWhiteSpace(key) ? RootKey : key;
    public static string NormalizePhysicalPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static bool IsWithinPhysicalRoot(string path, string root) => NormalizePhysicalPath(path).StartsWith(
        NormalizePhysicalPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RemoteFolders;

namespace Surf2.Services.SourceResources;

/// <summary>Desktop-only source resolution. Portable state never contains a managed cache path.</summary>
public static class ResourceWorkspace
{
    private sealed record Entry(ScopedResource Resource, string ScopeId, bool Admitted,
        PreparedFolderSource? Prepared, string? Status, string Configuration);
    private static string Configuration(ScopedResource resource) => JsonSerializer.Serialize(new { resource.Path, resource.Source, resource.PairedResourceId });
    private static readonly ConcurrentDictionary<Guid, Entry> Entries = new();
    public static event Action<Guid, string>? AdmissionRejected;
    private static readonly object SelectionLock = new();
    private static Dictionary<Guid, Guid>? _selection;
    private static string SelectionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Surf2", "SourceSelections.json");

    public static Guid ActiveMember(Scope scope, ResourceContainer container)
    {
        lock (SelectionLock)
        {
            EnsureSelections();
            if (_selection!.TryGetValue(container.ContainerId, out var selected) &&
                scope.Resources.Any(r => r.ContainerId == container.ContainerId && r.PublicId == selected)) return selected;
        }
        return scope.Resources.Any(r => r.ContainerId == container.ContainerId && r.PublicId == container.DefaultResourceId)
            ? container.DefaultResourceId : scope.Resources.First(r => r.ContainerId == container.ContainerId).PublicId;
    }

    public static void Select(Scope scope, ScopedResource resource)
    {
        if (!scope.Resources.Contains(resource)) throw new ArgumentException("The resource does not belong to this scope.");
        lock (SelectionLock)
        {
            EnsureSelections();
            _selection![resource.ContainerId] = resource.PublicId;
            Directory.CreateDirectory(Path.GetDirectoryName(SelectionFile)!);
            string temporary = SelectionFile + ".pending";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_selection));
            File.Move(temporary, SelectionFile, true);
        }
    }

    private static void EnsureSelections()
    {
        if (_selection != null) return;
        try { _selection = File.Exists(SelectionFile) ? JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(File.ReadAllText(SelectionFile)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _selection = []; }
    }

    public static void Register(Scope scope)
    {
        scope.EnsureResourceContainers();
        foreach (var resource in scope.Resources)
        {
            string configuration = Configuration(resource);
            Entries.AddOrUpdate(resource.PublicId, _ => new(resource, scope.ScopeId, false, null, null, configuration), (_, old) =>
            {
                bool changed = old.Configuration != configuration;
                if (changed) old.Prepared?.Dispose();
                return changed ? new(resource, scope.ScopeId, false, null, null, configuration) : old with { Resource = resource, ScopeId = scope.ScopeId };
            });
        }
    }

    public static void Admit(ScopedResource resource, PreparedFolderSource? prepared = null, string? status = null)
    {
        if (!Entries.TryGetValue(resource.PublicId, out var old)) throw new InvalidOperationException("Register the scope first.");
        if (prepared != null && !ReferenceEquals(old.Prepared, prepared)) old.Prepared?.Dispose();
        resource.RuntimePath = prepared?.PhysicalRoot ?? old.Prepared?.PhysicalRoot;
        resource.RuntimeAvailable = true; resource.RuntimeStatus = status;
        Entries[resource.PublicId] = old with { Resource = resource, Admitted = true, Prepared = prepared ?? old.Prepared, Status = status, Configuration = Configuration(resource) };
    }

    public static void Block(ScopedResource resource, string? reason = null, bool release = false)
    {
        if (!Entries.TryGetValue(resource.PublicId, out var old)) return;
        Entries[resource.PublicId] = old with { Admitted = false, Status = reason, Prepared = release ? null : old.Prepared };
        resource.RuntimeAvailable = false; resource.RuntimeStatus = reason;
        if (release) { old.Prepared?.Dispose(); resource.RuntimePath = null; }
    }

    /// <summary>Reject provider access and notify the desktop to retire all views of this source.</summary>
    public static void Reject(ScopedResource resource, string reason)
    {
        if (!Entries.TryGetValue(resource.PublicId, out var entry) || !entry.Admitted) return;
        Block(resource, reason);
        AdmissionRejected?.Invoke(resource.PublicId, reason);
    }

    public static void ReleaseExcept(IReadOnlySet<Guid>? members = null)
    {
        foreach (var pair in Entries)
            if (members?.Contains(pair.Key) != true && Entries.TryRemove(pair.Key, out var entry))
            { entry.Prepared?.Dispose(); entry.Resource.RuntimePath = null; entry.Resource.RuntimeAvailable = false; }
    }

    public static bool IsAdmitted(Guid member) => Entries.TryGetValue(member, out var entry) && entry.Admitted;
    public static PreparedFolderSource? Prepared(Guid member) => Entries.GetValueOrDefault(member)?.Prepared;
    public static string? Status(Guid member) => Entries.GetValueOrDefault(member)?.Status;
    public static ScopedResource? Find(Guid member) => Entries.GetValueOrDefault(member)?.Resource;
    public static string UnloadedToken(ScopedResource member) => "container:" + member.ContainerId.ToString("N");
    public static bool IsMarkedUnloaded(ScopedResource member, IReadOnlySet<string> ids) =>
        ids.Contains(member.ResourceId) || ids.Contains(UnloadedToken(member));
    public static bool IsSnapshotBlocked(string snapshotId) => Entries.Values.Where(e =>
        e.Resource.Kind == ResourceKind.DatabaseSnapshot && e.Resource.Path.Equals(snapshotId, StringComparison.OrdinalIgnoreCase))
        .ToArray() is { Length: > 0 } matching && matching.All(e => !e.Admitted);

    public static ExplorerScope Project(ExplorerScope scope, Scope model)
    {
        model.EnsureResourceContainers();
        var active = model.ResourceContainers.Select(c => ActiveMember(model, c)).ToHashSet();
        var excluded = scope.Context.UnloadedScopeResourceKeys.ToHashSet();
        var projected = ImmutableArray.CreateBuilder<ExplorerResource>();
        foreach (var resource in scope.Resources)
        {
            if (resource.SortOrdinal < 0 || resource.SortOrdinal >= model.Resources.Count) continue;
            var member = model.Resources[(int)resource.SortOrdinal];
            bool selected = active.Contains(member.PublicId);
            Entries.TryGetValue(member.PublicId, out var entry);
            bool managed = member.Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob or SourceProvider.LiveSql;
            bool admitted = resource.IsLoaded && selected && (entry?.Admitted ?? !managed);
            if (!admitted) excluded.Add(resource.ScopeResourceKey);
            if (!selected) continue;
            string path = member.Kind == ResourceKind.Folder ? entry?.Prepared?.PhysicalRoot ?? member.PhysicalPath : resource.Path;
            var container = model.ResourceContainers.Single(c => c.ContainerId == member.ContainerId);
            string name = container.Name;
            if (string.IsNullOrWhiteSpace(member.DisplayNameOverride) && model.Resources.Count(r => r.ContainerId == member.ContainerId) == 1)
                name = member.Kind switch
                {
                    ResourceKind.DatabaseSnapshot when resource.Snapshot != null => ExplorerCompatibility.SnapshotName(resource.Snapshot),
                    ResourceKind.Diagram when !string.IsNullOrWhiteSpace(resource.DiagramName) => resource.DiagramName,
                    ResourceKind.LiveDatabase when !string.IsNullOrWhiteSpace(member.Source?.DatabaseName) => member.Source.DatabaseName,
                    _ => name
                };
            projected.Add(resource with { Path = path, Alias = name, IsLoaded = admitted,
                PublicId = member.PublicId, ContainerId = member.ContainerId, Source = member.Source?.Copy(),
                LogicalPath = member.Path, Status = entry?.Status });
        }
        var order = model.ResourceContainers.Select((container, index) => (container.ContainerId, index)).ToDictionary(pair => pair.ContainerId, pair => pair.index);
        return scope with { Resources = projected.OrderBy(resource => order[resource.ContainerId]).ToImmutableArray(), Context = scope.Context with
            { UnloadedScopeResourceKeys = excluded.Order().ToImmutableArray() } };
    }

    public static string Locator(Guid member, string relative) => "source://" + member.ToString("N") + "/" +
        string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    public static string DirectoryLocator(Guid member, string relative) => Locator(member, relative.TrimEnd('/')) + (relative.TrimEnd('/').Length == 0 ? "" : "/");

    public static bool TryParse(string path, out Guid member, out string relative)
    {
        member = default; relative = "";
        if (!path.StartsWith("source://", StringComparison.OrdinalIgnoreCase)) return false;
        int slash = path.IndexOf('/', 9);
        if (slash < 0 || !Guid.TryParse(path[9..slash], out member)) throw new IOException("The source link is invalid.");
        relative = string.Join('/', path[(slash + 1)..].Split('/').Select(Uri.UnescapeDataString));
        return true;
    }

    public static string Resolve(string locator)
    {
        if (!TryParse(locator, out var member, out var relative)) return locator;
        if (!Entries.TryGetValue(member, out var entry) || !entry.Admitted)
            throw new IOException("This resource is unloaded or its selected source is unavailable. Load it before opening the document.");
        if (entry.Prepared is { } prepared)
        {
            if (relative.Length == 0) return prepared.PhysicalRoot;
            bool directory = relative.EndsWith('/');
            if (!directory && prepared.Index.TryFile(relative, out var file)) return file.PhysicalPath!;
            relative = relative.TrimEnd('/');
            if (prepared.Index.ContainsDirectory(relative))
                return prepared.Index.DirectoryPath(relative, entry.Resource.Source?.Provider == SourceProvider.AzureBlob);
            return prepared.ResolveItem(relative);
        }
        if (relative.Length == 0) return entry.Resource.PhysicalPath;
        return FolderSourcePaths.SafeCombine(entry.Resource.PhysicalPath, relative);
    }

    public static string ToPersistentPath(string path, Guid? preferredMember = null)
    {
        if (path.StartsWith("source://", StringComparison.OrdinalIgnoreCase)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;
        IEnumerable<Entry> candidates = preferredMember is { } requested
            ? Entries.TryGetValue(requested, out var exact) ? [exact] : [] : Entries.Values;
        var matches = new List<string>();
        foreach (var entry in candidates)
        {
            // Explicit local bindings also retain their member identity when opened through the explorer.
            if (entry.Prepared == null && preferredMember == null) continue;
            string root = entry.Prepared?.PhysicalRoot ?? entry.Resource.PhysicalPath;
            if (entry.Resource.Kind != ResourceKind.Folder || !Path.IsPathFullyQualified(root)) continue;
            if (entry.Prepared is { } prepared)
            {
                if (prepared.Index.TryPhysicalFile(path, out var file)) matches.Add(Locator(entry.Resource.PublicId, file));
                else if (prepared.Index.TryPhysicalDirectory(path, entry.Resource.Source?.Provider == SourceProvider.AzureBlob, out var directory))
                    matches.Add(DirectoryLocator(entry.Resource.PublicId, directory));
                continue;
            }
            if (!path.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                !ExplorerCompatibility.IsWithinPhysicalRoot(path, root)) continue;
            if (path.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            { matches.Add(Locator(entry.Resource.PublicId, "")); continue; }
            matches.Add(Locator(entry.Resource.PublicId, Path.GetRelativePath(root, path).Replace('\\', '/')));
        }
        return matches.Distinct(StringComparer.Ordinal).Take(2).ToArray() is [var only] ? only : path;
    }

    public static bool IsBlockedPath(string path)
    {
        if (TryParse(path, out var member, out _)) return !IsAdmitted(member);
        if (!Path.IsPathFullyQualified(path)) return false;
        var matches = Entries.Values.Where(e => e.Resource.Kind is ResourceKind.File or ResourceKind.Folder && Path.IsPathFullyQualified(e.Prepared?.PhysicalRoot ?? e.Resource.PhysicalPath) &&
            (e.Resource.Kind == ResourceKind.File ? string.Equals(path, e.Resource.PhysicalPath, StringComparison.OrdinalIgnoreCase) :
                ExplorerCompatibility.IsWithinPhysicalRoot(path, e.Prepared?.PhysicalRoot ?? e.Resource.PhysicalPath))).ToArray();
        return matches.Length != 0 && matches.All(e => !e.Admitted);
    }

    internal static (PreparedFolderSource Prepared, Guid Member, string LogicalDirectory)? FindDirectory(string path)
    {
        foreach (var pair in Entries)
        {
            if (!pair.Value.Admitted || pair.Value.Resource.Source?.Provider is not (SourceProvider.AzureBlob or SourceProvider.Git) || pair.Value.Prepared is not { } prepared) continue;
            if (prepared.Index.TryPhysicalDirectory(path, pair.Value.Resource.Source.Provider == SourceProvider.AzureBlob, out var directory))
                return (prepared, pair.Key, directory);
        }
        return null;
    }
}

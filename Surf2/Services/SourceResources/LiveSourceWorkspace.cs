using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Surf2.Models;
using Surf2.Services.LiveSql;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RemoteFolders;

namespace Surf2.Services.SourceResources;

public static class LiveSourceWorkspace
{
    private sealed record Session(ScopedResource Resource, LiveSqlService Service, SqlAccessQualification Qualification);
    private static readonly ConcurrentDictionary<Guid, Session> Sessions = new();
    private static readonly ConcurrentDictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase);
    public static void ReleaseExcept(IReadOnlySet<Guid>? keep = null)
    {
        foreach (var member in Sessions.Keys.Where(id => keep?.Contains(id) != true))
        {
            Sessions.TryRemove(member, out _);
            foreach (var path in Paths.Where(p => TryParse(p.Value, out var id, out _) && id == member).Select(p => p.Key))
                Paths.TryRemove(path, out _);
            try { RemoteFolderServices.Default.Cache.DeleteOwnedDirectory(DefinitionRoot(member)); }
            catch (IOException) { /* A closing document can briefly own a handle; the bounded cache remains disposable. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static string DefinitionRoot(Guid member) => Path.Combine(RemoteFolderServices.Default.Cache.Root,
        "live-definitions", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), member.ToString("N"));
    public static void Register(ScopedResource resource, LiveSqlService service, SqlAccessQualification qualification) =>
        Sessions[resource.PublicId] = new(resource, service, qualification);
    public static LiveSqlService Service(Guid id) => Sessions.TryGetValue(id, out var session) && ResourceWorkspace.IsAdmitted(id)
        ? session.Service : throw new IOException("The live SQL resource is unavailable. Load it or reconnect first.");
    public static bool Available(Guid id) => Sessions.ContainsKey(id) && ResourceWorkspace.IsAdmitted(id);
    internal static void RejectService(LiveSqlService service, string reason)
    {
        foreach (var session in Sessions.Values.Where(s => ReferenceEquals(s.Service, service)))
            ResourceWorkspace.Reject(session.Resource, reason);
    }
    public static IReadOnlyList<LiveSqlObject> RegisteredObjects(Guid id) => Available(id) && Sessions.TryGetValue(id, out var session)
        ? session.Qualification.Objects : [];
    public static async Task<IReadOnlyList<LiveSqlObject>> RefreshObjectsAsync(Guid id, CancellationToken ct)
    {
        var session = Sessions.TryGetValue(id, out var value) ? value : throw new IOException("The live SQL resource is unavailable.");
        var qualification = await Service(id).QualifyAsync(ct).ConfigureAwait(false);
        qualification.RequireQualified();
        if (!Available(id) || !Sessions.TryUpdate(id, session with { Qualification = qualification }, session))
            throw new IOException("The live SQL resource changed while its objects were being read.");
        return qualification.Objects;
    }
    public static async Task VerifyObjectsAsync(Guid id, IReadOnlyList<LiveSqlObject> expected, CancellationToken ct)
    {
        var current = await Service(id).QualifyAsync(ct).ConfigureAwait(false); current.RequireQualified();
        if (!Available(id) || !expected.SequenceEqual(current.Objects))
        {
            if (ResourceWorkspace.Find(id) is { } resource) ResourceWorkspace.Reject(resource, "The live SQL catalog changed during indexing. Reload the resource.");
            throw new IOException("The live SQL catalog changed during indexing. Refresh the resource to retry.");
        }
    }
    public static string Locator(Guid member, LiveSqlObject item) => "livesql://" + member.ToString("N") + "/" + item.Type + "/" +
        Uri.EscapeDataString(item.Schema) + "/" + Uri.EscapeDataString(item.Name);
    public static bool TryParse(string path, out Guid member, out (string Type, string Schema, string Name) item)
    {
        member = default; item = default;
        if (!path.StartsWith("livesql://", StringComparison.OrdinalIgnoreCase)) return false;
        var pieces = path[10..].Split('/');
        if (pieces.Length != 4 || !Guid.TryParse(pieces[0], out member)) throw new IOException("The live SQL document link is invalid.");
        item = (pieces[1], Uri.UnescapeDataString(pieces[2]), Uri.UnescapeDataString(pieces[3])); return true;
    }
    public static string ToPersistent(string physical) => Paths.GetValueOrDefault(physical, physical);
    internal static string ResolveMaterialized(string locator)
    {
        if (!TryParse(locator, out var member, out _)) return locator;
        if (!Available(member)) throw new IOException("The live SQL source is no longer available.");
        string path = Path.Combine(DefinitionRoot(member), FolderSourcePaths.Hash(locator) + ".sql");
        if (!Paths.TryGetValue(path, out var registered) || registered != locator)
            throw new IOException("The live SQL definition has not been prepared in this session.");
        return path;
    }
    public static Task<string> MaterializeAsync(string locator, CancellationToken ct) => MaterializeCoreAsync(locator, false, ct);
    internal static Task<string> MaterializeForIndexAsync(string locator, CancellationToken ct) => MaterializeCoreAsync(locator, true, ct);
    private static async Task<string> MaterializeCoreAsync(string locator, bool qualifiedIndex, CancellationToken ct)
    {
        if (!TryParse(locator, out var member, out var identity)) return locator;
        var service = Service(member);
        string definition;
        try
        {
            var inventory = qualifiedIndex ? RegisteredObjects(member) : (await service.QualifyAsync(ct).ConfigureAwait(false)).RequireObjects();
            var item = inventory.SingleOrDefault(o => o.Type == identity.Type && o.Schema == identity.Schema && o.Name == identity.Name)
                ?? throw new IOException("The live object no longer exists under this name.");
            definition = qualifiedIndex ? await service.ReadQualifiedDefinitionAsync(item, ct).ConfigureAwait(false)
                : await service.ReadDefinitionAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.SqlClient.SqlException or IOException)
        { RejectService(service, "The live SQL definition could not be verified. Reload the resource."); throw; }
        if (Encoding.UTF8.GetByteCount(definition) > 16 * 1024 * 1024) throw new IOException("The definition exceeds the document size limit.");
        var cache = RemoteFolderServices.Default.Cache;
        using var operation = await cache.AcquireOperationAsync(ct).ConfigureAwait(false);
        var settings = cache.ReadSettings();
        string root = DefinitionRoot(member);
        long bytes = Encoding.UTF8.GetByteCount(definition);
        if (FolderCacheStore.DirectoryBytes(root) > settings.PerSourceBytes - bytes ||
            FolderCacheStore.DirectoryBytes(cache.Root) > settings.TotalBytes - bytes)
            throw new FolderCapacityException("The live SQL definition would exceed Local Cache storage limits.");
        FolderCacheStore.RequireDriveSpace(cache.Root, bytes, settings.FreeSpaceReserveBytes);
        Directory.CreateDirectory(root); string path = Path.Combine(root, FolderSourcePaths.Hash(locator) + ".sql");
        FolderSourcePaths.RequireNoReparseAncestors(cache.Root, root);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, definition, ct).ConfigureAwait(false);
            if (!Available(member)) throw new IOException("The resource was unloaded during the read.");
            File.Move(temporary, path, true); Paths[path] = locator; return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ImmutableArray<ExplorerNodeSummary> Categories(ExplorerResource resource) => Enum.GetValues<ExplorerCategory>().Select(c =>
        new ExplorerNodeSummary("livecategory:" + resource.PublicId + ":" + c, ExplorerCompatibility.CategoryName(c), ExplorerCompatibility.CategoryName(c),
            "livesql-category://" + resource.PublicId + "/" + c, "livesql-category://" + resource.PublicId + "/" + c,
            resource.Path, resource.Path, ExplorerNodeRole.Category, ResourceKind.LiveDatabase, FileSystemNodeIconKind.Folder,
            ExplorerAvailability.Present, true, false, false, resource.IsLoaded, ExplorerChildrenState.Unloaded,
            resource.ScopeResourceKey, resource.ResourceId, Category: c)).ToImmutableArray();
    public static ImmutableArray<ExplorerNodeSummary> Objects(ExplorerResource resource, ExplorerNodeSummary parent)
    {
        if (!Available(resource.PublicId)) return [];
        return Sessions[resource.PublicId].Qualification.Objects.Where(o => Category(o.Type) == parent.Category)
            .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => new ExplorerNodeSummary("live:" + resource.PublicId + ":" + o.ObjectId, o.Schema + "." + o.Name, o.Name,
                Locator(resource.PublicId, o), Locator(resource.PublicId, o), parent.NodeKey, parent.NodeKey,
                ExplorerNodeRole.LiveDatabaseDocument, ResourceKind.LiveDatabase, FileSystemNodeIconKind.File, ExplorerAvailability.Present,
                false, true, false, true, ExplorerChildrenState.Loaded, resource.ScopeResourceKey, resource.ResourceId,
                Category: parent.Category, SchemaName: o.Schema, ObjectName: o.Name)).ToImmutableArray();
    }
    private static ExplorerCategory Category(string type) => type switch
    { "U" => ExplorerCategory.Tables, "V" => ExplorerCategory.Views, "P" => ExplorerCategory.Procedures, "TR" => ExplorerCategory.Triggers, _ => ExplorerCategory.Functions };
}

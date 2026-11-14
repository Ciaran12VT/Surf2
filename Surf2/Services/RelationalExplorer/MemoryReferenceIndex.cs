using System.IO;
using System.Data;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.SourceResources;
using Surf2.Storage.Relational;

namespace Surf2.Services.RelationalExplorer;

public sealed record MemoryReferenceProgress(int Discovered, int Completed, int Failed, bool Discovering, string? File = null);

public static class MemoryReferenceIndex
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules" };

    public static ScopeReferenceIndex CreateNamedIndex(ExplorerScope scope)
    {
        var index = new ScopeReferenceIndex();
        foreach (var resource in scope.Resources.Where(resource => resource.IsLoaded && resource.Kind == ResourceKind.File))
            index.Add(FileReference(resource.Path));
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && r.Kind == ResourceKind.LiveDatabase))
            foreach (var item in LiveSourceWorkspace.RegisteredObjects(resource.PublicId))
                index.Add(new ReferenceEntity
                {
                    Name = item.Name, QualifiedName = SqlName.FormatPlainMultipartName(item.Schema, item.Name),
                    Kind = item.Type switch { "U" => ReferenceEntityKind.Table, "V" => ReferenceEntityKind.View,
                        "P" or "PC" => ReferenceEntityKind.StoredProcedure, "TR" or "TA" => ReferenceEntityKind.Trigger, _ => ReferenceEntityKind.Function },
                    FilePath = LiveSourceWorkspace.Locator(resource.PublicId, item),
                    Language = CodeWindowSettings.SqlServerLanguage, ContainerName = ExplorerCompatibility.ResourceDisplayName(resource)
                });
        if (scope.DatabaseCatalogue == null) return index;
        foreach (var group in scope.DatabaseCatalogue)
        {
            var owner = scope.Resources.FirstOrDefault(r => r.IsLoaded && r.Snapshot?.SnapshotKey == group.Key);
            if (owner == null) continue;
            foreach (var item in group.Value)
            {
                if (item.Resource.Kind == DatabaseVersionedResourceKind.TableData) continue;
                var kind = item.ObjectKind switch
                {
                    SqlDatabaseObjectKind.StoredProcedure => ReferenceEntityKind.StoredProcedure,
                    SqlDatabaseObjectKind.View => ReferenceEntityKind.View,
                    SqlDatabaseObjectKind.Function => ReferenceEntityKind.Function,
                    SqlDatabaseObjectKind.Trigger => ReferenceEntityKind.Trigger,
                    _ when item.Resource.Kind == DatabaseVersionedResourceKind.TableMetadata => ReferenceEntityKind.Table,
                    _ => ReferenceEntityKind.File
                };
                if (kind == ReferenceEntityKind.File) continue;
                var category = kind switch
                {
                    ReferenceEntityKind.StoredProcedure => ExplorerCategory.Procedures,
                    ReferenceEntityKind.View => ExplorerCategory.Views,
                    ReferenceEntityKind.Function => ExplorerCategory.Functions,
                    ReferenceEntityKind.Trigger => ExplorerCategory.Triggers,
                    _ => ExplorerCategory.Tables
                };
                index.Add(new ReferenceEntity
                {
                    Name = item.Resource.ObjectName,
                    QualifiedName = SqlName.FormatPlainMultipartName(item.Resource.SchemaName, item.Resource.ObjectName),
                    Kind = kind,
                    FilePath = RelationalDocumentService.IdentityPath(scope.Context.Epoch, item.Resource, category, false),
                    LineNumber = 1, ColumnNumber = 1, EndLineNumber = 1, EndColumnNumber = 1,
                    Language = CodeWindowSettings.SqlServerLanguage,
                    ContainerName = owner.Snapshot!.DisplayName
                });
            }
        }
        return index;
    }

    public static async Task<IReadOnlyList<ReferenceEntity>> LoadTableFieldsAsync(RelationalSession session,
        ExplorerScope scope, CancellationToken ct)
    {
        var tables = scope.DatabaseCatalogue?.Values.SelectMany(items => items)
            .Where(item => item.Resource.Kind == DatabaseVersionedResourceKind.TableMetadata)
            .GroupBy(item => item.Resource.RevisionKey)
            .ToDictionary(group => group.Key, group => group.ToArray()) ?? [];
        if (tables.Count == 0) return [];
        await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT c.TableMetadataRevisionKey, LEFT(c.ColumnName,65537),
 ROW_NUMBER() OVER(PARTITION BY c.TableMetadataRevisionKey ORDER BY c.SourceOrdinal,c.SortOrdinal,c.ColumnRevisionKey)
FROM OPENJSON(@Revisions) WITH(RevisionKey bigint '$') requested
JOIN surf.TableMetadataRevision t ON t.RevisionKey=requested.RevisionKey
JOIN surf.TableColumnRevision c ON c.TableMetadataRevisionKey=t.RevisionKey
ORDER BY c.TableMetadataRevisionKey,c.SourceOrdinal,c.SortOrdinal,c.ColumnRevisionKey;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Revisions", SqlDbType.NVarChar, JsonSerializer.Serialize(tables.Keys), -1));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<ReferenceEntity>();
        long characters = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = reader.GetString(1);
            foreach (var table in tables[reader.GetInt64(0)])
            {
            characters += name.Length + table.Resource.SchemaName.Length + table.Resource.ObjectName.Length;
            if (name.Length > 65536 || result.Count >= 100_000 || characters > 16 * 1024 * 1024)
                throw new ExplorerLimitException("The table field catalogue exceeds its metadata limit.");
            int line = checked((int)reader.GetInt64(2) + (table.HasFullData ? 3 : 2));
            string container = SqlName.FormatPlainMultipartName(table.Resource.SchemaName, table.Resource.ObjectName);
            result.Add(new ReferenceEntity
            {
                Name = name, QualifiedName = container + "." + name, Kind = ReferenceEntityKind.Field,
                FilePath = RelationalDocumentService.IdentityPath(scope.Context.Epoch, table.Resource, ExplorerCategory.Tables, false),
                LineNumber = line, EndLineNumber = line, ColumnNumber = 5, EndColumnNumber = 5 + name.Length,
                Language = CodeWindowSettings.SqlServerLanguage, ContainerName = container
            });
            }
        }
        return result;
    }

    public static async Task<ScopeReferenceIndex> BuildAsync(ExplorerScope scope, ExplorerIndexLanguagePolicy languages,
        Func<MemoryReferenceProgress, Task> report, CancellationToken ct, IReadOnlyList<ReferenceEntity>? tableFields = null,
        Func<ScopeReferenceIndex, Task>? namesReady = null)
    {
        int failed = 0, completed = 0;
        var liveInventories = new Dictionary<Guid, IReadOnlyList<LiveSql.LiveSqlObject>>();
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && r.Kind == ResourceKind.LiveDatabase))
        {
            try { liveInventories[resource.PublicId] = await LiveSourceWorkspace.RefreshObjectsAsync(resource.PublicId, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed++;
                if (ResourceWorkspace.Find(resource.PublicId) is { } member) ResourceWorkspace.Reject(member, error.Message);
            }
        }
        var index = CreateNamedIndex(scope);
        foreach (var field in tableFields ?? []) index.Add(field);
        var files = new Dictionary<string, ReferenceSource>(StringComparer.Ordinal);
        var physical = new SourcePhysicalExplorerQueries();
        void AddSource(string physicalPath, string locator, string name, string language, bool live = false)
        {
            string key = locator.StartsWith("source://", StringComparison.Ordinal) || live ? locator : locator.ToUpperInvariant();
            files.TryAdd(key, new(physicalPath, locator, name, language, live));
            if (files.Count > 100_000) throw new ExplorerLimitException("The selected scope exceeds 100,000 reference documents.");
        }
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && r.Kind is ResourceKind.File or ResourceKind.Folder))
        {
            ct.ThrowIfCancellationRequested();
            if (resource.Kind == ResourceKind.File)
            {
                try { string path = Path.GetFullPath(resource.Path); AddSource(path, path, Path.GetFileName(path), languages.LanguageFor(path)); }
                catch (ArgumentException) { failed++; }
            }
            else await DiscoverAsync(resource, resource.Path, 0).ConfigureAwait(false);
        }
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && r.Kind == ResourceKind.LiveDatabase))
            foreach (var item in LiveSourceWorkspace.RegisteredObjects(resource.PublicId))
                AddSource("", LiveSourceWorkspace.Locator(resource.PublicId, item), item.Schema + "." + item.Name,
                    CodeWindowSettings.SqlServerLanguage, true);
        await report(new(files.Count, 0, failed, false)).ConfigureAwait(false);
        var names = CreateNamedIndex(scope);
        foreach (var field in tableFields ?? []) names.Add(field);
        foreach (var source in files.Values.Where(s => !s.Live))
        {
            var reference = FileReference(source.Locator, source.Name);
            index.Add(reference); names.Add(reference);
        }
        // Publish an independent, immutable-by-convention index before any source body is parsed.
        if (namesReady != null) await namesReady(names).ConfigureAwait(false);
        var parsers = new Dictionary<string, IReferenceDefinitionParser>
        {
            [CodeWindowSettings.CSharpLanguage] = new CSharpReferenceDefinitionParser(),
            [CodeWindowSettings.VisualBasicLanguage] = new VisualBasicReferenceDefinitionParser(),
            [CodeWindowSettings.SqlServerLanguage] = new SqlServerReferenceDefinitionParser(),
            [CodeWindowSettings.JavaScriptLanguage] = new JavaScriptReferenceDefinitionParser()
        };
        foreach (var source in files.Values)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string path = source.Live ? await LiveSourceWorkspace.MaterializeForIndexAsync(source.Locator, ct).ConfigureAwait(false) : source.PhysicalPath;
                if (!source.Live && ResourceWorkspace.IsBlockedPath(source.Locator)) throw new IOException("The source is no longer loaded.");
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("A scoped file is unavailable.");
                index.AddFile();
                if (parsers.TryGetValue(source.Language, out var parser))
                {
                    if (info.Length > 32L * 1024 * 1024) throw new IOException("The source exceeds the reference parsing limit.");
                    string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    foreach (var entity in parser.Parse(source.Locator, text))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (source.Live && entity.Kind is ReferenceEntityKind.Table or ReferenceEntityKind.View or
                            ReferenceEntityKind.StoredProcedure or ReferenceEntityKind.Function or ReferenceEntityKind.Trigger) continue;
                        index.Add(entity);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
            {
                failed++;
                if (source.Live && LiveSourceWorkspace.TryParse(source.Locator, out var memberId, out _) && ResourceWorkspace.Find(memberId) is { } member)
                {
                    ResourceWorkspace.Reject(member, "Live SQL access changed during reference indexing. Reload to verify access.");
                    throw new IOException("The live SQL source could not be verified completely. Its references were not published; reload the resource to retry.", ex);
                }
            }
            completed++;
            await report(new(files.Count, completed, failed, false, source.Name)).ConfigureAwait(false);
        }
        foreach (var live in liveInventories)
            try { await LiveSourceWorkspace.VerifyObjectsAsync(live.Key, live.Value, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (ResourceWorkspace.Find(live.Key) is { } member) ResourceWorkspace.Reject(member, error.Message);
                throw new IOException("The live SQL source changed during reference indexing; reload to retry.", error);
            }
        return index;

        async Task DiscoverAsync(ExplorerResource resource, string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth >= 128) { failed++; return; }
            try
            {
                foreach (var entry in await physical.ReadDirectoryAsync(directory, scope.SortCultureName, new ExplorerLimits(), ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsReparsePoint) continue;
                    string name = entry.DisplayName ?? Path.GetFileName(entry.Path);
                    if (entry.IsDirectory)
                    {
                        if (!ExcludedDirectories.Contains(name)) await DiscoverAsync(resource, entry.Path, depth + 1).ConfigureAwait(false);
                    }
                    else
                    {
                        string locator = entry.LogicalKey ?? ResourceWorkspace.ToPersistentPath(entry.Path, resource.PublicId);
                        AddSource(entry.Path, locator, name, languages.LanguageFor(name));
                        if (files.Count % 100 == 0) await report(new(files.Count, completed, failed, true)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExplorerLimitException)
            {
                failed++;
            }
        }
    }

    private sealed record ReferenceSource(string PhysicalPath, string Locator, string Name, string Language, bool Live);
    private static ReferenceEntity FileReference(string path, string? name = null) => new()
    {
        Name = Path.GetFileNameWithoutExtension(name ?? path), QualifiedName = name ?? Path.GetFileName(path),
        Kind = ReferenceEntityKind.File, FilePath = path, LineNumber = 1, ColumnNumber = 1,
        EndLineNumber = 1, EndColumnNumber = 1, Language = "File"
    };
}

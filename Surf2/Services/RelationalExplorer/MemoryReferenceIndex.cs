using System.IO;
using System.Data;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;
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
        var index = CreateNamedIndex(scope);
        foreach (var field in tableFields ?? []) index.Add(field);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int failed = 0, completed = 0;
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && r.Kind is ResourceKind.File or ResourceKind.Folder))
        {
            ct.ThrowIfCancellationRequested();
            if (resource.Kind == ResourceKind.File)
            {
                try { files.Add(Path.GetFullPath(resource.Path)); }
                catch (ArgumentException) { failed++; }
            }
            else await DiscoverAsync(resource.Path, 0).ConfigureAwait(false);
        }
        await report(new(files.Count, 0, failed, false)).ConfigureAwait(false);
        var names = CreateNamedIndex(scope);
        foreach (var field in tableFields ?? []) names.Add(field);
        foreach (string path in files)
        {
            var reference = FileReference(path);
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
        foreach (string path in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("A scoped file is unavailable.");
                index.AddFile();
                if (parsers.TryGetValue(languages.LanguageFor(path), out var parser))
                {
                    if (info.Length > 32L * 1024 * 1024) throw new IOException("The source exceeds the reference parsing limit.");
                    string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    foreach (var entity in parser.Parse(path, text))
                    {
                        ct.ThrowIfCancellationRequested();
                        index.Add(entity);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failed++;
            }
            completed++;
            await report(new(files.Count, completed, failed, false, Path.GetFileName(path))).ConfigureAwait(false);
        }
        return index;

        async Task DiscoverAsync(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth >= 128) { failed++; return; }
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!ExcludedDirectories.Contains(Path.GetFileName(path))) await DiscoverAsync(path, depth + 1).ConfigureAwait(false);
                    }
                    else
                    {
                        files.Add(Path.GetFullPath(path));
                        if (files.Count > 100_000) throw new ExplorerLimitException("The selected scope exceeds 100,000 physical files.");
                        if (files.Count % 100 == 0) await report(new(files.Count, completed, failed, true)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
    }

    private static ReferenceEntity FileReference(string path) => new()
    {
        Name = Path.GetFileNameWithoutExtension(path), QualifiedName = Path.GetFileName(path),
        Kind = ReferenceEntityKind.File, FilePath = path, LineNumber = 1, ColumnNumber = 1,
        EndLineNumber = 1, EndColumnNumber = 1, Language = "File"
    };
}

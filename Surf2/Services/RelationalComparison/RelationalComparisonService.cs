using System.IO;
using System.Runtime.CompilerServices;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalComparison;

public sealed partial class RelationalComparisonService
{
    private readonly RelationalRuntime _runtime;
    public ComparisonLimits Limits { get; }
    public RelationalComparisonService(RelationalRuntime runtime, ComparisonLimits? limits = null)
    { _runtime = runtime; Limits = limits ?? new(); Limits.Validate(); }

    public static RelationalComparisonTarget? FromNode(Guid epoch, ExplorerNodeSummary node)
    {
        if (!node.IsScopeResourceLoaded || !node.Exists || node.Role is ExplorerNodeRole.Diagram or ExplorerNodeRole.DiagramRoot) return null;
        ComparisonResourceKind kind;
        string type, comparisonType;
        if (node.IsVirtualFolder) { kind = ComparisonResourceKind.VirtualFolder; type = comparisonType = "Virtual Folder"; }
        else if (node.Role == ExplorerNodeRole.Category)
        { kind = ComparisonResourceKind.DatabaseObjectFolder; type = ExplorerCompatibility.CategoryName(node.Category!.Value); comparisonType = "DatabaseFolder:" + type; }
        else if (node.SnapshotKey.HasValue && node.IsScopeResourceRoot)
        { kind = ComparisonResourceKind.DatabaseSnapshot; type = "Database Snapshot"; comparisonType = "DatabaseSnapshot"; }
        else if (node.Role == ExplorerNodeRole.DatabaseDocument)
        {
            kind = Kind(node.Category);
            type = kind == ComparisonResourceKind.TableMetadata ? "Table Metadata" : node.Category == ExplorerCategory.Procedures ? "Stored Procedure" : kind.ToString();
            comparisonType = "DatabaseObject:" + (kind == ComparisonResourceKind.TableMetadata ? "TableMetadata" : ObjectKind(node.Category).ToString());
        }
        else { kind = node.IsDirectory ? ComparisonResourceKind.Folder : ComparisonResourceKind.File; type = comparisonType = kind.ToString(); }
        bool collection = node.IsDirectory || node.IsVirtualFolder;
        var resource = new ComparisonResource(node.Name, type, node.FullPath, kind, comparisonType,
            collection, !collection, false, "relational:" + epoch.ToString("N") + ":" + node.OccurrenceKey,
            SnapshotId: node.SnapshotKey?.ToString() ?? "", DatabaseFolderName: node.Category.HasValue ? ExplorerCompatibility.CategoryName(node.Category.Value) : "",
            DatabaseObjectKind: node.Category == ExplorerCategory.Tables ? null : ObjectKind(node.Category),
            DatabaseObjectName: SqlName.FormatPlainMultipartName(node.SchemaName, node.ObjectName),
            TableSchemaName: node.SchemaName, TableName: node.ObjectName, SyntaxPath: node.IsVirtualDocument ? "document.sql" : node.FullPath);
        return new(resource, epoch, node.SnapshotKey, node.SnapshotResourceKey, node.SourceRevisionKey,
            Category: node.Category, ExplorerNode: node);
    }

    public async Task<RelationalComparisonTarget?> CreateSourceAsync(ExplorerNodeSummary node, bool tableData, CancellationToken ct = default)
    {
        var target = FromNode(_runtime.Session.Epoch, node);
        if (target == null || !tableData) return target;
        if (target.Resource.Kind != ComparisonResourceKind.TableMetadata || !node.HasFullData || target.SnapshotKey == null) return null;
        var data = await FindByNameAsync(target.SnapshotKey.Value, DatabaseVersionedResourceKind.TableData, node.SchemaName, node.ObjectName, null, ct).ConfigureAwait(false);
        return data == null ? null : FromResource(data, target.Epoch);
    }

    public async Task<ComparisonCandidates> GetCandidatesAsync(RelationalComparisonTarget source, ExplorerScope scope, CancellationToken ct = default)
    {
        RequireEpoch(source);
        var items = new List<RelationalComparisonTarget>(); long bytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var node in WalkScopeAsync(scope, ct).ConfigureAwait(false))
        {
            var target = await CreateSourceAsync(node, source.Resource.IsTableData, ct).ConfigureAwait(false);
            if (target == null || target.Resource.ComparisonTypeKey != source.Resource.ComparisonTypeKey || SameTarget(target, source) || !seen.Add(TargetKey(target))) continue;
            long size = 512 + 2L * (target.Resource.DisplayName.Length + target.Resource.Path.Length + target.Resource.IdentityKey.Length);
            if (items.Count == Limits.MaximumCandidates || size > Limits.MaximumCandidateBytes - bytes)
                return new(items, false, "The comparison picker reached its metadata limit. Narrow the active scope for further choices.");
            items.Add(target); bytes += size;
        }
        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Resource.DisplayName + "\0" + a.Resource.Path, b.Resource.DisplayName + "\0" + b.Resource.Path));
        await RequireScopeAsync(scope, ct).ConfigureAwait(false);
        return new(items, true);
    }

    public async IAsyncEnumerable<ExplorerNodeSummary> WalkScopeAsync(ExplorerScope scope,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await RequireScopeAsync(scope, ct).ConfigureAwait(false);
        var roots = await _runtime.Explorer.GetRootsAsync(scope, ct).ConfigureAwait(false);
        foreach (var root in roots)
            await foreach (var node in WalkAsync(scope, root, 0, ct).ConfigureAwait(false)) yield return node;
        await RequireScopeAsync(scope, ct).ConfigureAwait(false);
    }
    private async IAsyncEnumerable<ExplorerNodeSummary> WalkAsync(ExplorerScope scope, ExplorerNodeSummary node, int depth,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 128) throw new ComparisonLimitException("Comparison hierarchy exceeds its depth limit.");
        if (!node.IsScopeResourceLoaded || node.Role is ExplorerNodeRole.Diagram or ExplorerNodeRole.DiagramRoot) yield break;
        yield return node;
        if (!node.IsDirectory || !node.Exists || node.IsReparsePoint) yield break;
        await foreach (var batch in _runtime.Explorer.GetChildrenAsync(scope, node, ct).ConfigureAwait(false))
        {
            if (batch.State == ExplorerChildrenState.Failed) throw new IOException("Comparison discovery failed; its result is incomplete.");
            foreach (var child in batch.Nodes)
                await foreach (var descendant in WalkAsync(scope, child, depth + 1, ct).ConfigureAwait(false)) yield return descendant;
        }
    }
    public async Task<string> ReadTextAsync(RelationalComparisonTarget target, CancellationToken ct = default)
    {
        RequireEpoch(target);
        if (target.Resource.IsCollection || target.Resource.IsTableData) throw new InvalidOperationException("Select a text document for text comparison.");
        if (!target.RevisionKey.HasValue)
        {
            await using var file = await IndexedFileRead.OpenAsync(target.Resource.Path, Limits.MaximumTextCharacters, ct).ConfigureAwait(false);
            if (target.ExpectedTextDigest != null && target.ExpectedTextDigest != ComparisonTextHash.Digest(file.Text, new()))
                throw new IOException("This file changed after the collection was compared. Run the comparison again.");
            return file.Text;
        }
        if (target.Resource.Kind == ComparisonResourceKind.TableMetadata)
        {
            if (target.VersionKey.HasValue)
            {
                await using var historical = await _runtime.Snapshots.OpenHistoricalSnapshotAsync(target.SnapshotKey!.Value, target.VersionKey.Value, ct).ConfigureAwait(false);
                var entry = await FindHistoricalTableAsync(historical, target, ct).ConfigureAwait(false);
                return RelationalDocumentService.RenderTable(await historical.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false), Limits.MaximumTextCharacters * 2L);
            }
            var details = await _runtime.Snapshots.ReadTableMetadataAsync(target.RevisionKey.Value, ct).ConfigureAwait(false);
            return RelationalDocumentService.RenderTable(details, Limits.MaximumTextCharacters * 2L);
        }
        var value = await _runtime.Snapshots.ReadObjectAsync(target.RevisionKey.Value, ct).ConfigureAwait(false);
        if (value.Definition.Length > Limits.MaximumTextCharacters) throw new ComparisonLimitException("Selected code exceeds the comparison text limit.");
        // Collection comparisons historically used raw definitions (not editor fallback comments).
        return value.Definition;
    }
    internal async Task<HistoricalSnapshotEntry> FindHistoricalTableAsync(HistoricalSnapshotContext context,
        RelationalComparisonTarget target, CancellationToken ct)
    {
        await foreach (var entry in context.StreamAsync(HistoricalCollection.Tables, ct).ConfigureAwait(false))
            if (entry.ResourceKey == target.ResourceKey && entry.RevisionKey == target.RevisionKey &&
                (!target.HistoricalEntryKey.HasValue || entry.EntryKey == target.HistoricalEntryKey)) return entry;
        throw new SnapshotConcurrencyException();
    }
    internal async Task<SnapshotResourceSummary?> FindByNameAsync(long snapshotKey, DatabaseVersionedResourceKind kind,
        string schema, string name, long? versionKey, CancellationToken ct)
    {
        SnapshotCursor? cursor = null;
        do
        {
            var page = await _runtime.Snapshots.ListResourcesAsync(snapshotKey, kind, versionKey, 64, cursor, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
                if (item.SchemaName.Equals(schema, StringComparison.OrdinalIgnoreCase) && item.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase)) return item;
            cursor = page.Next;
        } while (cursor != null);
        return null;
    }
    public Task RequireScopeAsync(ExplorerScope? scope, CancellationToken ct = default) => RequireScopeCoreAsync(scope, ct);
    private async Task RequireScopeCoreAsync(ExplorerScope? scope, CancellationToken ct)
    {
        await _runtime.Session.RequireReadyAsync(ct).ConfigureAwait(false);
        if (scope != null && !await new RelationalExplorerMetadataQueries(_runtime.Session, _runtime.Index).IsDomainCurrentAsync(scope.Context, ct).ConfigureAwait(false))
            throw new IndexGenerationChangedException();
    }
    private void RequireEpoch(RelationalComparisonTarget target)
    {
        if (target.Epoch != _runtime.Session.Epoch) throw new IndexGenerationChangedException();
    }
    public static bool SameTarget(RelationalComparisonTarget left, RelationalComparisonTarget right) => TargetKey(left) == TargetKey(right);
    private static string TargetKey(RelationalComparisonTarget t) => t.ResourceKey.HasValue
        ? $"{t.Epoch}:{t.ResourceKey}:{t.RevisionKey}:{t.VersionKey}:{t.HistoricalEntryKey}"
        : t.Resource.IdentityKey;
    private static ComparisonResourceKind Kind(ExplorerCategory? category) => category switch
    {
        ExplorerCategory.Procedures => ComparisonResourceKind.StoredProcedure,
        ExplorerCategory.Views => ComparisonResourceKind.View,
        ExplorerCategory.Functions => ComparisonResourceKind.Function,
        ExplorerCategory.Triggers => ComparisonResourceKind.Trigger,
        ExplorerCategory.Tables => ComparisonResourceKind.TableMetadata,
        _ => ComparisonResourceKind.File
    };
    private static SqlDatabaseObjectKind? ObjectKind(ExplorerCategory? category) => category is null or ExplorerCategory.Tables ? null : ExplorerCompatibility.ObjectKind(category.Value);
    public static RelationalComparisonTarget FromResource(SnapshotResourceSummary item, Guid epoch, long? versionKey = null, long? entryKey = null)
    {
        bool data = item.Kind == DatabaseVersionedResourceKind.TableData;
        ExplorerCategory category = item.Kind switch
        {
            DatabaseVersionedResourceKind.StoredProcedure => ExplorerCategory.Procedures,
            DatabaseVersionedResourceKind.View => ExplorerCategory.Views,
            DatabaseVersionedResourceKind.Function => ExplorerCategory.Functions,
            DatabaseVersionedResourceKind.Trigger => ExplorerCategory.Triggers,
            _ => ExplorerCategory.Tables
        };
        string name = SqlName.FormatPlainMultipartName(item.SchemaName, item.ObjectName);
        var resource = new ComparisonResource(name + (data ? " data" : ""), data ? "Table Data" : Kind(category).ToString(),
            RelationalDocumentService.IdentityPath(epoch, item, category, data), data ? ComparisonResourceKind.TableData : Kind(category),
            "DatabaseObject:" + (data ? "TableData" : category == ExplorerCategory.Tables ? "TableMetadata" : ObjectKind(category).ToString()),
            false, !data, data, $"relational:{epoch}:{item.ResourceKey}:{item.RevisionKey}:{versionKey}:{entryKey}",
            SnapshotId: item.SnapshotKey.ToString(), DatabaseObjectKind: ObjectKind(category), DatabaseObjectName: name,
            TableSchemaName: item.SchemaName, TableName: item.ObjectName, SyntaxPath: data ? "document.csv" : "document.sql");
        return new(resource, epoch, item.SnapshotKey, item.ResourceKey, item.RevisionKey, versionKey, entryKey, category);
    }
}

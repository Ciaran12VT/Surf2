using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalComparison;

public sealed partial class RelationalComparisonService
{
    public Task<ComparisonResultStore> CompareCollectionsAsync(RelationalComparisonTarget left, RelationalComparisonTarget right,
        ExplorerScope? scope, ComparisonOptions options, CancellationToken ct = default) => Task.Run(async () =>
    {
        await RequireScopeAsync(scope, ct).ConfigureAwait(false);
        var result = await ComparisonCollectionEngine.BuildAsync(Collection(left, scope, options, ct), Collection(right, scope, options, ct), Limits, ct).ConfigureAwait(false);
        try { await RequireScopeAsync(scope, ct).ConfigureAwait(false); return result; }
        catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
    }, ct);

    private async IAsyncEnumerable<ComparisonCollectionEntry> Collection(RelationalComparisonTarget target, ExplorerScope? scope,
        ComparisonOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        RequireEpoch(target);
        if (target.Resource.Kind is ComparisonResourceKind.DatabaseSnapshot or ComparisonResourceKind.DatabaseObjectFolder)
        {
            await foreach (var entry in SnapshotCollection(target, options, ct).ConfigureAwait(false)) yield return entry;
            yield break;
        }
        if (target.ExplorerNode == null || scope == null) throw new InvalidOperationException("A selected scope hierarchy is required for folder comparison.");
        await foreach (var batch in _runtime.Explorer.GetChildrenAsync(scope, target.ExplorerNode, ct).ConfigureAwait(false))
        {
            if (batch.State == ExplorerChildrenState.Failed) throw new IOException("Comparison discovery failed; no partial result was published.");
            foreach (var child in batch.Nodes)
                await foreach (var item in NodeCollection(child, Segment(child.Name), scope, options, 0, ct).ConfigureAwait(false)) yield return item;
        }
    }
    private async IAsyncEnumerable<ComparisonCollectionEntry> NodeCollection(ExplorerNodeSummary node, string path,
        ExplorerScope scope, ComparisonOptions options, int depth, [EnumeratorCancellation] CancellationToken ct)
    {
        if (depth > 128) throw new ComparisonLimitException("Collection hierarchy exceeds its depth limit.");
        var target = await CreateSourceAsync(node, false, ct).ConfigureAwait(false);
        if (target == null)
        {
            if (!node.Exists) throw new IOException("A compared source is missing or inaccessible: " + node.Name);
            yield break;
        }
        if (!target.Resource.IsCollection) { yield return await Leaf(path, target, options, ct).ConfigureAwait(false); yield break; }
        yield return new(path, true, "", target);
        if (target.SnapshotKey.HasValue)
        {
            await foreach (var entry in SnapshotCollection(target, options, ct).ConfigureAwait(false))
                yield return entry with { RelativePath = path + "/" + entry.RelativePath };
            yield break;
        }
        if (node.IsReparsePoint) throw new IOException("Redirected folders are not traversed during collection comparison.");
        await foreach (var batch in _runtime.Explorer.GetChildrenAsync(scope, node, ct).ConfigureAwait(false))
        {
            if (batch.State == ExplorerChildrenState.Failed) throw new IOException("Comparison discovery failed; no partial result was published.");
            foreach (var child in batch.Nodes)
                await foreach (var entry in NodeCollection(child, path + "/" + Segment(child.Name), scope, options, depth + 1, ct).ConfigureAwait(false)) yield return entry;
        }
    }
    private async IAsyncEnumerable<ComparisonCollectionEntry> SnapshotCollection(RelationalComparisonTarget target,
        ComparisonOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        bool all = target.Resource.Kind == ComparisonResourceKind.DatabaseSnapshot;
        await using var historical = target.VersionKey.HasValue
            ? await _runtime.Snapshots.OpenHistoricalSnapshotAsync(target.SnapshotKey!.Value, target.VersionKey.Value, ct).ConfigureAwait(false) : null;
        foreach (var category in Enum.GetValues<ExplorerCategory>())
        {
            if (!all && target.Category != category) continue;
            string folder = ExplorerCompatibility.CategoryName(category);
            if (all) yield return Folder(folder, target, category);
            if (historical != null)
            {
                await foreach (var entry in historical.StreamAsync(category == ExplorerCategory.Tables ? HistoricalCollection.Tables : HistoricalCollection.Objects, ct).ConfigureAwait(false))
                {
                    if (category != ExplorerCategory.Tables && entry.ObjectKind != ObjectKind(category)) continue;
                    var item = await historical.ResourceSummary(entry, ct).ConfigureAwait(false);
                    var leaf = FromResource(item, target.Epoch, target.VersionKey, entry.EntryKey);
                    string text = category == ExplorerCategory.Tables
                        ? RelationalDocuments.RelationalDocumentService.RenderTable(await historical.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false), Limits.MaximumTextCharacters * 2L)
                        : await ReadTextAsync(leaf, ct).ConfigureAwait(false);
                    yield return new((all ? folder + "/" : "") + leaf.Resource.DatabaseObjectName + ".sql", false,
                        ComparisonTextHash.Digest(text, options), leaf);
                }
            }
            else
            {
                SnapshotCursor? cursor = null;
                var kind = category == ExplorerCategory.Tables ? DatabaseVersionedResourceKind.TableMetadata : SnapshotIdentity.ResourceKind(ObjectKind(category)!.Value);
                do
                {
                    var page = await _runtime.Snapshots.ListResourcesAsync(target.SnapshotKey!.Value, kind, pageSize: 64, cursor: cursor, ct: ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        var leaf = FromResource(item, target.Epoch);
                        yield return await Leaf((all ? folder + "/" : "") + leaf.Resource.DatabaseObjectName + ".sql", leaf, options, ct).ConfigureAwait(false);
                    }
                    cursor = page.Next;
                } while (cursor != null);
            }
        }
        if (!all) yield break;
        bool addedFolder = false;
        if (historical != null)
        {
            await foreach (var entry in historical.StreamAsync(HistoricalCollection.Tables, ct).ConfigureAwait(false))
            {
                if (entry.Table?.HasFullData != true) continue;
                var data = await FindByNameAsync(target.SnapshotKey!.Value, DatabaseVersionedResourceKind.TableData, entry.SchemaName, entry.Name, target.VersionKey, ct).ConfigureAwait(false);
                if (data == null) continue;
                if (!addedFolder) { yield return Folder("Full Table Data", target, ExplorerCategory.Tables); addedFolder = true; }
                var leaf = FromResource(data, target.Epoch, target.VersionKey);
                yield return await Leaf("Full Table Data/" + leaf.Resource.DatabaseObjectName + ".csv", leaf, options, ct).ConfigureAwait(false);
            }
        }
        else
        {
            SnapshotCursor? cursor = null;
            do
            {
                var page = await _runtime.Snapshots.ListTablesAsync(target.SnapshotKey!.Value, pageSize: 64, cursor: cursor, ct: ct).ConfigureAwait(false);
                foreach (var table in page.Items.Where(x => x.HasFullData))
                {
                    var data = await FindByNameAsync(target.SnapshotKey.Value, DatabaseVersionedResourceKind.TableData, table.Resource.SchemaName, table.Resource.ObjectName, null, ct).ConfigureAwait(false);
                    if (data == null) continue;
                    if (!addedFolder) { yield return Folder("Full Table Data", target, ExplorerCategory.Tables); addedFolder = true; }
                    var leaf = FromResource(data, target.Epoch);
                    yield return await Leaf("Full Table Data/" + leaf.Resource.DatabaseObjectName + ".csv", leaf, options, ct).ConfigureAwait(false);
                }
                cursor = page.Next;
            } while (cursor != null);
        }
    }
    private async Task<ComparisonCollectionEntry> Leaf(string path, RelationalComparisonTarget target, ComparisonOptions options, CancellationToken ct)
    {
        if (target.Resource.IsTableData)
        {
            var table = await DescribeTableAsync(target, ct).ConfigureAwait(false);
            using var hash = new ComparisonTextHash(options);
            AppendCsv(table.Headers);
            await foreach (var row in table.Rows.WithCancellation(ct).ConfigureAwait(false))
                if (row.Value.ValueKind == JsonValueKind.Object)
                    AppendCsv(table.Headers.Select(h => ComparisonTableEngine.Value(row.Value, h)));
            return new(path, false, hash.Finish(), target);
            void AppendCsv(IEnumerable<string> cells)
            {
                bool first = true;
                foreach (string cell in cells)
                {
                    if (!first) hash.Append(","); first = false;
                    hash.Append(CaptureDisplay.EscapeDelimited(cell, ','));
                }
                hash.Append(Environment.NewLine);
            }
        }
        string text = await ReadTextAsync(target, ct).ConfigureAwait(false);
        if (!target.RevisionKey.HasValue) target = target with { ExpectedTextDigest = ComparisonTextHash.Digest(text, new()) };
        return new(path, false, ComparisonTextHash.Digest(text, options), target);
    }
    private static string Segment(string x) => x.Replace('\\', '/').Trim('/');
    private static ComparisonCollectionEntry Folder(string path, RelationalComparisonTarget target, ExplorerCategory category) =>
        new(path, true, "", target with { Category = category, Resource = target.Resource with
        { DisplayName = path, Kind = ComparisonResourceKind.DatabaseObjectFolder, DatabaseFolderName = path } });
}

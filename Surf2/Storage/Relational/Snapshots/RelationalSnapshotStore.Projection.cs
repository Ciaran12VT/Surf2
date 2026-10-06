using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class RelationalSnapshotStore
{
    /// <summary>Replay all later changes in legacy order into a bounded metadata/identity work plan; dispose after the operation.</summary>
    public async Task<HistoricalSnapshotContext> OpenHistoricalSnapshotAsync(long snapshotKey, long versionKey, CancellationToken ct = default)
    {
        var connection = await Open(ct).ConfigureAwait(false);
        return await HistoricalSnapshotContext.BuildAsync(connection, Epoch, snapshotKey, versionKey, ct).ConfigureAwait(false);
    }

    /// <summary>Selected-version table scalars and replayed root children, including tables resurrected from a data companion.</summary>
    public async Task<TableMetadataDetails> ReadHistoricalTableMetadataAsync(long snapshotKey, long versionKey, long resourceKey, CancellationToken ct = default)
    {
        await using var context = await OpenHistoricalSnapshotAsync(snapshotKey, versionKey, ct).ConfigureAwait(false);
        HistoricalSnapshotEntry? selected = null;
        await foreach (var table in context.StreamAsync(HistoricalCollection.Tables, ct).ConfigureAwait(false))
            if (table.ResourceKey == resourceKey) { selected=table; break; }
        selected ??= await context.FindResource(resourceKey, ct).ConfigureAwait(false);
        if (selected?.Collection != HistoricalCollection.Tables) throw new KeyNotFoundException("Table absent from the selected historical version.");
        return await context.ReadTableMetadataAsync(selected, ct).ConfigureAwait(false);
    }

    private async Task<SnapshotPage<SnapshotResourceSummary>> HistoricalResourcePage(long snapshotKey, long versionKey,
        HistoricalCollection? collection, DatabaseVersionedResourceKind? kind, int pageSize, SnapshotCursor? cursor, CancellationToken ct)
    {
        await using var context = await OpenHistoricalSnapshotAsync(snapshotKey, versionKey, ct).ConfigureAwait(false);
        var page = await context.ReadResources(collection, kind, pageSize, cursor, ct).ConfigureAwait(false);
        var items = new List<SnapshotResourceSummary>(page.Items.Count);
        var budget = new SnapshotMetadataBudget();
        foreach (var entry in page.Items) items.Add(await context.ResourceSummary(entry, ct, budget).ConfigureAwait(false));
        return new(items,page.Next,Epoch);
    }

    private async Task<SnapshotPage<ObjectSummary>> HistoricalObjectPage(long snapshotKey, long versionKey,
        DatabaseVersionedResourceKind? kind, int pageSize, SnapshotCursor? cursor, CancellationToken ct)
    {
        await using var context = await OpenHistoricalSnapshotAsync(snapshotKey, versionKey, ct).ConfigureAwait(false);
        var page = await context.ReadResources(HistoricalCollection.Objects,kind,pageSize,cursor,ct).ConfigureAwait(false);
        var items = new List<ObjectSummary>(page.Items.Count);
        var budget = new SnapshotMetadataBudget();
        foreach(var entry in page.Items) items.Add(await context.ObjectSummary(entry,ct,budget).ConfigureAwait(false));
        return new(items,page.Next,Epoch);
    }

    private async Task<SnapshotPage<TableSummary>> HistoricalTablePage(long snapshotKey, long versionKey,
        int pageSize, SnapshotCursor? cursor, CancellationToken ct)
    {
        await using var context = await OpenHistoricalSnapshotAsync(snapshotKey, versionKey, ct).ConfigureAwait(false);
        var page = await context.ReadResources(HistoricalCollection.Tables,DatabaseVersionedResourceKind.TableMetadata,pageSize,cursor,ct).ConfigureAwait(false);
        var items = new List<TableSummary>(page.Items.Count);
        var budget = new SnapshotMetadataBudget();
        foreach(var entry in page.Items)
        {
            var resource=await context.ResourceSummary(entry,ct,budget).ConfigureAwait(false);
            items.Add(new(resource,entry.Table!.HasFullData,entry.Table.FullDataRowCount,entry.Table.FullDataImportedAtUtc));
        }
        return new(items,page.Next,Epoch);
    }
}

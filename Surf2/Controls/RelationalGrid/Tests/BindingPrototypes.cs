#if RELATIONAL_GRID_BINDING_PROTOTYPE
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
using System.IO;
using Surf2.Controls.RelationalGrid;
using Surf2.Services.RelationalGrid;

internal static class BindingPrototypes
{
    private static int _checks;
    public static async Task Main()
    {
        var limits = new GridBindingLimits { PageRows = 8, PageBytes = 8192, MaxItems = 16, MaxBytes = 32768 };
        await using var source = new ProbeSource();
        await using var query = await source.CreateQueryAsync();
        var items = new GridViewportItems(source, limits);
        items.Bind(query);
        Check(items.UpdateCount(new(1_000_000, true)) && items.Count == 1_000_000 && items.ResidentItems == 0,
            "a million-row extent allocates no row objects");
        Check(items.IndexOf(new object()) == -1 && source.Reads == 0 && source.Queries == 1, "count/currency lookup performs no provider work");
        for (int i = 0; i < 1000; i++)
        {
            var row = (GridViewportRow)items[(i * 7919) % items.Count]!;
            Check(row.IsLoading && row[0] == "" && items.IndexOf(row) == row.Position, "missing requested rows are identifiable loading objects");
            Check(items.ResidentItems <= limits.MaxItems && items.ResidentBytes <= limits.MaxBytes, "random dataset scrolling plateaus within item/byte budgets");
        }
        Check(source.Reads == 0 && source.Queries == 1, "no indexer/placeholder/property started a query or page read");
        Throws<GridLimitException>(() => items.GetEnumerator().MoveNext());
        var visible = (GridViewportRow)items[900_000]!;
        items.Pin(visible, true);
        long binding = items.Generation;
        var page = Page(source, query, 900_000, "base");
        Check(!items.Publish(page, binding - 1) && !visible.IsLoaded, "obsolete binding publication is ignored");
        Check(!items.Publish(page with { QueryGeneration = query.Generation + 1 }, binding), "obsolete provider query publication is ignored");
        Check(!items.Publish(page with { SourceId = Guid.NewGuid() }, binding), "foreign source publication is ignored");
        Check(!items.Publish(page with { OverlayGeneration = query.OverlayGeneration + 1 }, binding), "foreign overlay publication is ignored");
        Check(items.Publish(page, binding) && visible.IsLoaded && visible[0] == "base", "one async page hydrates a pinned existing placeholder");
        visible[0] = "off-page edit";
        Check(source.OverlayGeneration == 1 && source.LastOrdinal == 3_000_007 && source.Edited == "off-page edit", "edits address the raw ordinal, not result position");
        items.Publish(page, binding);
        Check(visible[0] == "off-page edit", "late same-query pages never overwrite a locally committed cell");
        for (int i = 0; i < 100; i++) _ = items[i];
        Check(ReferenceEquals(items[900_000], visible), "realized row pin survives cache pressure");
        items.Pin(visible, false);
        for (int i = 100; i < 200; i++) _ = items[i];
        Check(source.Edited == "off-page edit" && items.ResidentItems <= limits.MaxItems, "eviction does not touch source session edits");
        Throws<InvalidOperationException>(() => visible[0] = "stale edit");
        var reloaded = (GridViewportRow)items[900_000]!;
        Check(visible.Equals(reloaded) && visible.GetHashCode() == reloaded.GetHashCode(), "evicted/recreated positions retain stable WPF item equality");
        Check(items.UpdateCount(new(1_200_000, false)) && items.IndexOf(reloaded) == 900_000, "pending count growth does not renumber items");
        items.Fail(new IOException("read failed"), items.Generation);
        Check(reloaded.Error == "read failed" && !reloaded.IsLoading, "read failure is not an empty data row");
        items.ClearError();
        Check(items.Error == null && reloaded.IsLoading, "retry clears loading-row error without discarding the overlay");
        items.Publish(Page(source, query, 900_000, new string('x', 9000)), items.Generation);
        Check(items.Error is GridLimitException && items.ResidentBytes <= limits.MaxBytes && !reloaded.IsLoaded,
            "a page exceeding UI byte bounds fails without retaining its payload");
        await using var replacement = await source.CreateQueryAsync();
        items.Bind(replacement);
        Check(items.Count == 0 && items.ResidentItems == 0 && source.Edited == "off-page edit", "replacing a view clears only bounded view items, not source edits");
        items.UpdateCount(new(3, true));
        Check(items.Cast<object>().Count() == 3, "only a small bounded list may be enumerated");
        Check(!items.Publish(page, binding), "old results cannot repopulate a replacement view");
        items.UpdateCount(new((long)int.MaxValue + 1, true));
        Check(items.Count == 0 && items.UnaddressableCount == (long)int.MaxValue + 1 && items.Error is GridLimitException,
            "counts beyond WPF Int32 addressing are explicit, never truncated");
        Check(source.Queries == 2 && !source.Disposed, "an unaddressable grid still leaves the source available to operation-owned output");
        items.Close();
        Check(items.Count == 0 && items.ResidentItems == 0 && !source.Disposed, "view close leaves provider disposal to the explicit logical lifetime owner");
        Check(!items.Publish(page, items.Generation), "closed view rejects all later publications");
        Console.WriteLine($"PASS: {_checks} pure WPF-binding model assertions (no WPF, SQL, or application build).");
    }

    private static GridPage Page(ProbeSource source, IGridQuerySession query, long position, string value) =>
        new(source.Descriptor.SourceId, query.Generation, query.OverlayGeneration, new(position, 8, 8192),
            [new(source.Descriptor.SourceId, 3_000_007, [value], 232 + value.Length * 4L)], 232 + value.Length * 4L, true)
        { IsRangeComplete = true };

    private sealed class ProbeSource : IDataGridSource
    {
        public GridDescriptor Descriptor { get; } = new(Guid.NewGuid(), "probe", [new(0, "A", "A")], null, null, 1);
        public Task<long> DisplayRowCount => Task.FromResult(1_000_000L);
        public bool IsInvalidated => false;
        public long OverlayGeneration { get; private set; }
        public long OverlayBytes => Edited.Length * 4L;
        public long Queries { get; private set; }
        public int Reads { get; set; }
        public long LastOrdinal { get; private set; }
        public string Edited { get; private set; } = "";
        public bool Disposed { get; private set; }
        public void SetCell(GridRow row, int columnOrdinal, string? value)
        { LastOrdinal = row.RowOrdinal; Edited = value ?? ""; OverlayGeneration++; }
        public void ClearEdits() { Edited = ""; OverlayGeneration++; }
        public Task<IGridQuerySession> CreateQueryAsync(GridQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IGridQuerySession>(new ProbeQuery(this, ++Queries, OverlayGeneration));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class ProbeQuery(ProbeSource source, long generation, long overlay) : IGridQuerySession
    {
        public GridDescriptor Descriptor => source.Descriptor;
        public long Generation => generation;
        public long OverlayGeneration => overlay;
        public string Fingerprint => "probe";
        public GridCount Count => new(1_000_000, true);
        public Task<long> Completion => Task.FromResult(1_000_000L);
        public GridCacheStatistics CacheStatistics => default;
        public bool TryGetCachedPage(GridPageRequest request, out GridPage? page) { page = null; return false; }
        public Task<GridPage> ReadPageAsync(GridPageRequest request, CancellationToken cancellationToken = default)
        { source.Reads++; return Task.FromResult(Page(source, this, request.Start, source.Edited)); }
        public async IAsyncEnumerable<GridRow> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("FAIL: " + message); _checks++; }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
#endif

using System.Runtime.CompilerServices;
using Surf2.Services.RelationalGrid;

namespace Surf2.Services.LiveSql;

/// <summary>Display adapter over typed scratch rows. Query sessions never execute SQL.</summary>
public sealed class LiveSqlGridSource(LiveSqlResult result) : IDataGridSource
{
    private readonly CancellationTokenSource _lifetime = new();
    private long _generation;
    public LiveSqlResult Result => result;
    public GridDescriptor Descriptor { get; } = new(result.Id, result.Id.ToString("N"), result.Columns.Select(c => new GridColumn(c.Ordinal, c.Name, c.Name)).ToArray(), result.RowCount, result.RowCount, 1);
    public Task<long> DisplayRowCount => Task.FromResult((long)result.RowCount);
    public bool IsInvalidated => _lifetime.IsCancellationRequested;
    public long OverlayGeneration => 0;
    public long OverlayBytes => 0;
    public void SetCell(GridRow row, int columnOrdinal, string? value) => throw new InvalidOperationException("Live SQL results are read-only.");
    public void ClearEdits() { }
    public async Task<IGridQuerySession> CreateQueryAsync(GridQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new();
        if ((query.Sorts?.Count ?? 0) > 8 || (query.Filters?.Count ?? 0) > 128) throw new GridLimitException("Query adapter filter/sort limits exceeded.");
        foreach (int ordinal in (query.Filters ?? []).Select(f => f.ColumnOrdinal).Concat((query.Sorts ?? []).Select(s => s.ColumnOrdinal)))
            if (ordinal < 0 || ordinal >= result.Columns.Count) throw new ArgumentOutOfRangeException(nameof(query));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var rows = new List<(int Ordinal, LiveSqlCell[] Keys)>();
        for (int i = 0; i < result.RowCount; i++)
        {
            var cells = await result.ReadRowAsync(i, linked.Token).ConfigureAwait(false);
            if ((query.Filters ?? []).Any(filter => !Matches(cells[filter.ColumnOrdinal], filter.Text))) continue;
            rows.Add((i, (query.Sorts ?? []).Select(sort => cells[sort.ColumnOrdinal]).ToArray()));
        }
        if (query.Sorts is { Count: > 0 } sorts)
            rows.Sort((a, b) =>
            {
                for (int i = 0; i < sorts.Count; i++) { int compared = LiveSqlCell.Compare(a.Keys[i], b.Keys[i]); if (compared != 0) return sorts[i].Descending ? -Math.Sign(compared) : Math.Sign(compared); }
                return a.Ordinal.CompareTo(b.Ordinal);
            });
        return new Session(this, result, rows.Select(r => r.Ordinal).ToArray(), Interlocked.Increment(ref _generation), _lifetime.Token);
    }
    private static bool Matches(LiveSqlCell cell, string expression)
    {
        if (expression.Equals("IS NULL", StringComparison.OrdinalIgnoreCase)) return cell.Kind == "Null";
        if (expression.Equals("IS NOT NULL", StringComparison.OrdinalIgnoreCase)) return cell.Kind != "Null";
        foreach (string operation in new[] { ">=", "<=", "!=", ">", "<", "=" })
            if (expression.StartsWith(operation, StringComparison.Ordinal))
            {
                if (cell.Kind == "Null") return false;
                int compared = LiveSqlCell.Compare(cell, new(cell.Kind, expression[operation.Length..].Trim()));
                return operation switch { ">=" => compared >= 0, "<=" => compared <= 0, "!=" => compared != 0, ">" => compared > 0, "<" => compared < 0, _ => compared == 0 };
            }
        return cell.Kind != "Null" && (cell.Value ?? "").Contains(expression, StringComparison.OrdinalIgnoreCase);
    }
    public async ValueTask DisposeAsync() { _lifetime.Cancel(); await result.DisposeAsync(); }

    private sealed class Session : IGridQuerySession
    {
        private readonly LiveSqlResult _result;
        private readonly int[] _ordinals;
        private readonly CancellationTokenSource _lifetime;
        private GridPage? _cached;
        public GridDescriptor Descriptor { get; }
        public long Generation { get; }
        public long OverlayGeneration => 0;
        public string Fingerprint => Descriptor.SourceId.ToString("N") + ":" + Generation;
        public GridCount Count => new(_ordinals.Length, true);
        public Task<long> Completion => Task.FromResult((long)_ordinals.Length);
        public GridCacheStatistics CacheStatistics => new(_cached == null ? 0 : 1, _cached?.EstimatedBytes ?? 0, 0);
        internal Session(LiveSqlGridSource source, LiveSqlResult result, int[] ordinals, long generation, CancellationToken ct)
        { Descriptor = source.Descriptor; _result = result; _ordinals = ordinals; Generation = generation; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); }
        public bool TryGetCachedPage(GridPageRequest request, out GridPage? page)
        { page = _cached?.Request == request && !_lifetime.IsCancellationRequested ? _cached : null; return page != null; }
        public async Task<GridPage> ReadPageAsync(GridPageRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Start < 0 || request.MaxRows is < 1 or > 1024 || request.MaxBytes is < 1024 or > 128 * 1024 * 1024) throw new GridLimitException("Invalid live result page bounds.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var rows = new List<GridRow>(); long bytes = 0;
            for (long index = request.Start; index < _ordinals.Length && rows.Count < request.MaxRows; index++)
            {
                int ordinal = _ordinals[index]; var cells = (await _result.ReadRowAsync(ordinal, linked.Token).ConfigureAwait(false)).Select(c => c.Display).ToArray();
                long size = 128 + cells.Sum(c => c.Length * 2L + 32);
                if (size > request.MaxBytes && rows.Count == 0) throw new GridLimitException("A result row exceeds the requested page byte budget.");
                if (bytes + size > request.MaxBytes) break;
                rows.Add(new(Descriptor.SourceId, ordinal, cells, size)); bytes += size;
            }
            linked.Token.ThrowIfCancellationRequested();
            return _cached = new(Descriptor.SourceId, Generation, 0, request, rows, bytes, request.Start + rows.Count < _ordinals.Length) { IsRangeComplete = true };
        }
        public async IAsyncEnumerable<GridRow> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            foreach (int ordinal in _ordinals)
            {
                var cells = (await _result.ReadRowAsync(ordinal, linked.Token).ConfigureAwait(false)).Select(c => c.Display).ToArray();
                yield return new(Descriptor.SourceId, ordinal, cells, 128 + cells.Sum(c => c.Length * 2L + 32));
            }
        }
        public ValueTask DisposeAsync() { _lifetime.Cancel(); _cached = null; return ValueTask.CompletedTask; }
    }
}

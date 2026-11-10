using System.IO;
using System.Text;

namespace Surf2.Services.RelationalGrid;

public sealed record GridColumn(int Ordinal, string Header, string SourceName, bool IsSynthetic = false);
public sealed record GridDescriptor(Guid SourceId, string Revision, IReadOnlyList<GridColumn> Columns,
    long? ReportedRowCount, long? SourceRowCount, int DisplayFormatVersion);
public sealed record GridRow(Guid SourceId, long RowOrdinal, IReadOnlyList<string> Cells, long EstimatedBytes)
{
    internal IReadOnlyList<int>? SourceCellLengths { get; init; }
}
public sealed record GridFilter(int ColumnOrdinal, string Text);
public sealed record GridSort(int ColumnOrdinal, bool Descending = false);
public sealed record GridQuery(IReadOnlyList<GridFilter>? Filters = null,
    IReadOnlyList<int>? AnyMatchColumnOrdinals = null, IReadOnlyList<GridSort>? Sorts = null,
    string? SortCultureName = null);
public readonly record struct GridPageRequest(long Start, int MaxRows, long MaxBytes);
public sealed record GridPage(Guid SourceId, long QueryGeneration, long OverlayGeneration,
    GridPageRequest Request, IReadOnlyList<GridRow> Rows, long EstimatedBytes, bool HasMore)
{
    public bool IsRangeComplete { get; init; }
}
public readonly record struct GridCount(long AvailableRows, bool IsComplete);
public readonly record struct GridCacheStatistics(int Pages, long Bytes, int InFlightReads);

// Properties and TryGetCachedPage perform no I/O and do not start requests.
// Close means DisposeAsync, not a WPF Unloaded/reparenting notification.
public interface IDataGridSource : IAsyncDisposable
{
    GridDescriptor Descriptor { get; }
    Task<long> DisplayRowCount { get; }
    bool IsInvalidated { get; }
    long OverlayGeneration { get; }
    long OverlayBytes { get; }
    void SetCell(GridRow row, int columnOrdinal, string? value);
    void ClearEdits();
    Task<IGridQuerySession> CreateQueryAsync(GridQuery? query = null, CancellationToken cancellationToken = default);
}

public interface IGridQuerySession : IAsyncDisposable
{
    GridDescriptor Descriptor { get; }
    long Generation { get; }
    long OverlayGeneration { get; }
    string Fingerprint { get; }
    GridCount Count { get; }
    Task<long> Completion { get; }
    GridCacheStatistics CacheStatistics { get; }
    bool TryGetCachedPage(GridPageRequest request, out GridPage? page);
    Task<GridPage> ReadPageAsync(GridPageRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<GridRow> StreamAsync(CancellationToken cancellationToken = default);
}

public sealed record CsvGridOpenOptions(Encoding? DefaultEncoding = null, bool DetectByteOrderMarks = true);
public sealed record GridExportResult(string Destination, long RowCount, int ColumnCount, long ByteCount);
public sealed record GridClipboardResult(string Text, long RowCount, int ColumnCount);
public sealed class GridLimitException(string message) : InvalidOperationException(message);
public sealed class GridSourceChangedException(string message) : IOException(message);

public sealed record GridLimits
{
    public int MaxColumns { get; init; } = 1024;
    public int MaxCellCharacters { get; init; } = 1024 * 1024;
    public long MaxHeaderBytes { get; init; } = 2 * 1024 * 1024;
    public long MaxRowBytes { get; init; } = 8 * 1024 * 1024;
    public int PageRows { get; init; } = 128;
    public long PageBytes { get; init; } = 8 * 1024 * 1024;
    public int CachePages { get; init; } = 8;
    public long CacheBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxConcurrentPageReads { get; init; } = 2;
    public int MaxPendingPageReads { get; init; } = 32;
    public int MaxQueries { get; init; } = 4;
    public int MaxConcurrentStreams { get; init; } = 2;
    public int MaxEditCells { get; init; } = 10000;
    public long MaxEditBytes { get; init; } = 8 * 1024 * 1024;
    public int SortRunRows { get; init; } = 2048;
    public long SortRunBytes { get; init; } = 16 * 1024 * 1024;
    public int MergeFanIn { get; init; } = 8;
    public int MaxSortRuns { get; init; } = 2048;
    public int MaxOwnedFiles { get; init; } = 8192;
    public long MaxDiskBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaxClipboardBytes { get; init; } = 16 * 1024 * 1024;
    public string? StagingDirectory { get; init; }

    internal void Validate()
    {
        if (MaxColumns is < 1 or > 4096 || MaxCellCharacters is < 1 or > 4 * 1024 * 1024 ||
            MaxHeaderBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaxRowBytes < GridValues.RowOverhead + MaxColumns * GridValues.CellOverhead || MaxRowBytes is < 1024 or > 64 * 1024 * 1024 || PageRows is < 1 or > 1024 ||
            PageBytes < MaxRowBytes || PageBytes > 128 * 1024 * 1024 || CachePages is < 1 or > 64 ||
            CacheBytes < PageBytes + 256 || CacheBytes > 512 * 1024 * 1024 ||
            MaxConcurrentPageReads is < 1 or > 8 || MaxPendingPageReads < MaxConcurrentPageReads || MaxPendingPageReads > 256 ||
            MaxQueries is < 1 or > 8 || MaxConcurrentStreams is < 1 or > 4 || MaxEditCells is < 1 or > 100000 || MaxEditBytes is < 1024 or > 64 * 1024 * 1024 ||
            SortRunRows is < 1 or > 65536 || SortRunBytes < MaxRowBytes + 64 || SortRunBytes > 128 * 1024 * 1024 ||
            MergeFanIn is < 2 or > 16 || MaxSortRuns is < 1 or > 8192 || MaxOwnedFiles < MaxSortRuns + 8 || MaxOwnedFiles > 32768 ||
            MaxDiskBytes < MaxRowBytes * 2 || MaxClipboardBytes is < 1024 or > 128 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(GridLimits), "Invalid grid memory, concurrency or disk budgets.");
    }
}

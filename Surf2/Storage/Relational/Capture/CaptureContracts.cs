using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

public sealed record CaptureColumnDefinition(string SourceName, string? SourceDataType = null,
    int? SourceMaxLength = null, byte? SourcePrecision = null, int? SourceScale = null,
    bool? SourceNullable = null, int? SourceOrdinal = null, bool? SourceIdentity = null);

public static class CaptureColumns
{
    // When metadata is absent, infer first-seen physical names from one row.
    // Repeated names remain ordered row exceptions, not duplicate columns.
    public static IReadOnlyList<CaptureColumnDefinition> FromFirstRow(JsonElement firstRow)
    {
        var inferred = new List<CaptureColumnDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int propertyCount = 0;
        if (firstRow.ValueKind == JsonValueKind.Object)
            foreach (var property in firstRow.EnumerateObject())
            {
                if (++propertyCount > CaptureLimits.MaximumColumns + CaptureLimits.MaximumExceptionsPerRow)
                    throw new CaptureLimitException("First-row inference exceeds the 160-property row limit.");
                if (!names.Add(property.Name)) continue;
                if (inferred.Count == CaptureLimits.MaximumColumns)
                    throw new CaptureLimitException("First-row inference exceeds the 128-column layout limit.");
                inferred.Add(new(property.Name));
            }
        var columns = inferred.ToArray();
        _ = new CaptureLayout(columns);
        return Array.AsReadOnly(columns);
    }
}

public sealed record CaptureDataSetCreate(long RevisionKey, IReadOnlyList<CaptureColumnDefinition> Columns,
    long ReportedRowCount, DateTimeOffset ImportedAtUtc, long? ExpectedActualRowCount = null);

public sealed record CaptureDataSetSummary(long DataSetKey, long RevisionKey, long LayoutKey,
    long ReportedRowCount, long ActualRowCount, DateTimeOffset ImportedAtUtc,
    string State, int DisplayFormatVersion);

public sealed record CaptureDataSetDescriptor(Guid Epoch, CaptureDataSetSummary Summary,
    IReadOnlyList<CaptureColumnDefinition> Columns);

public sealed record CaptureWriteHandle(Guid Epoch, long DataSetKey, long LayoutKey);
public sealed record CaptureWriteProgress(long NextRowOrdinal, string State, long? ExpectedActualRowCount);
public sealed record CaptureImportState(CaptureWriteHandle Handle, CaptureDataSetSummary Summary,
    CaptureWriteProgress Progress, IReadOnlyList<CaptureColumnDefinition> Columns);

public sealed record CaptureRow(long RowOrdinal, JsonElement Value, long EstimatedBytes);

public sealed record CaptureFilter(int ColumnOrdinal, string Text);
public sealed record CaptureSort(int ColumnOrdinal, bool Descending = false);

// An active OR group has the legacy grid's precedence: other column filters are
// ignored. With no active group filters the ordinary AND filters apply.
public sealed record CaptureQuery(IReadOnlyList<CaptureFilter>? Filters = null,
    IReadOnlyList<int>? AnyMatchColumnOrdinals = null, IReadOnlyList<CaptureSort>? Sorts = null,
    string? SortCultureName = null);

public sealed record CapturePageCursor(Guid Epoch, long DataSetKey, string QueryFingerprint,
    long NextOffset, long AfterRowOrdinal);

public sealed record CapturePage(IReadOnlyList<CaptureRow> Rows, CapturePageCursor? NextCursor,
    long EstimatedBytes);

public sealed class CaptureLimitException(string message) : InvalidOperationException(message);

public sealed record CaptureLimits
{
    public const int MaximumColumns = 128;
    public const int MaximumRowUtf8Bytes = 1024 * 1024;
    public const int MaximumShapeBytes = 64 * 1024;
    public const int MaximumExceptionsPerRow = 32;
    public const long MaximumEstimatedRowBytes = 16 * 1024 * 1024;
    public const long MaximumBufferBytes = 256 * 1024 * 1024;
    public const long MinimumWriteBatchBytes = 64 * 1024;

    // Write-only floor for bulk buffers. Wide layouts also reserve the fixed
    // generated row/exception schemas and enough room for one empty object row.
    public static long GetMinimumWriteBatchBytes(int columnCount)
    {
        if (columnCount is < 0 or > MaximumColumns) throw new ArgumentOutOfRangeException(nameof(columnCount));
        return Math.Max(MinimumWriteBatchBytes, BulkTableOverheadBytes(columnCount) + 4144 + columnCount * 256L);
    }

    internal static long BulkTableOverheadBytes(int columnCount) => 4096L + (11L + columnCount) * 2048;

    public int MaxRowUtf8Bytes { get; init; } = MaximumRowUtf8Bytes;
    public int MaxTokenUtf8Bytes { get; init; } = MaximumRowUtf8Bytes;
    public int MaxExceptionUtf8BytesPerRow { get; init; } = MaximumRowUtf8Bytes;
    public int MaxExceptionsPerRow { get; init; } = MaximumExceptionsPerRow;
    public int WriteBatchRows { get; init; } = 128;
    public long WriteBatchBytes { get; init; } = 16 * 1024 * 1024;
    public int ReadBatchRows { get; init; } = 128;
    public long ReadBatchBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxPageRows { get; init; } = 256;
    public long MaxPageBytes { get; init; } = 16 * 1024 * 1024;
    public int SortRunRows { get; init; } = 128;
    public long SortRunBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxSpoolBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaxSortRuns { get; init; } = 4096;
    public int MergeFanIn { get; init; } = 8;
    public string? StagingDirectory { get; init; }

    internal void Validate()
    {
        if (WriteBatchBytes < MinimumWriteBatchBytes)
            throw new ArgumentOutOfRangeException(nameof(WriteBatchBytes), $"The write-only bulk budget must be at least {MinimumWriteBatchBytes} bytes; use GetMinimumWriteBatchBytes for wide layouts. Read and page budgets are independent.");
        if (MaxRowUtf8Bytes is <= 0 or > MaximumRowUtf8Bytes ||
            MaxTokenUtf8Bytes is <= 0 or > MaximumRowUtf8Bytes ||
            MaxExceptionUtf8BytesPerRow is <= 0 or > MaximumRowUtf8Bytes ||
            MaxExceptionsPerRow is <= 0 or > MaximumExceptionsPerRow ||
            WriteBatchRows is <= 0 or > 1024 || ReadBatchRows is <= 0 or > 1024 ||
            MaxPageRows is <= 0 or > 4096 || SortRunRows is <= 0 or > 4096 ||
            WriteBatchBytes is <= 0 or > MaximumBufferBytes || ReadBatchBytes is <= 0 or > MaximumBufferBytes ||
            MaxPageBytes is <= 0 or > MaximumBufferBytes || SortRunBytes is <= 0 or > MaximumBufferBytes ||
            MaxSpoolBytes <= 0 || MaxSortRuns is <= 0 or > 65536 ||
            MergeFanIn is < 2 or > 16)
            throw new ArgumentOutOfRangeException(nameof(CaptureLimits), "Invalid capture resource budget.");
    }
}

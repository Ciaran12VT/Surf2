using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Storage.Relational.Migration;

/// <summary>
/// Imports one selected dataset without materializing Rows. The coordinator owns
/// the committed revision identity and verification of the staged source fingerprint.
/// Dataset writes use their own transactions, never the coordinator's checkpoint transaction.
/// </summary>
public sealed class LegacyCaptureImporter
{
    internal MigrationProgressReporter? Progress { get; init; }
    private readonly RelationalCaptureStore _capture;
    private readonly CaptureLimits _limits = new();

    public LegacyCaptureImporter(RelationalSession session)
    {
        _capture = new RelationalCaptureStore(session ?? throw new ArgumentNullException(nameof(session)));
    }

    public async Task<CaptureDataSetSummary> ImportAsync(long revisionKey, string stagedFile,
        long dataSetObjectOffset, IReadOnlyList<CaptureColumnDefinition> columns,
        CancellationToken cancellationToken = default)
    {
        if (revisionKey <= 0) throw new ArgumentOutOfRangeException(nameof(revisionKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedFile);
        if (dataSetObjectOffset < 0) throw new ArgumentOutOfRangeException(nameof(dataSetObjectOffset));
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count > CaptureLimits.MaximumColumns)
            throw new CaptureLimitException("The source metadata exceeds the 128-column capture limit.");
        cancellationToken.ThrowIfCancellationRequested();
        var sourceColumns = columns.ToArray();
        string path = Path.GetFullPath(stagedFile);
        // On the supported Windows host, deny writes/deletes/replacement while
        // header, inference/count, and append readers independently seek this file.
        await using var pinnedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (dataSetObjectOffset >= pinnedFile.Length)
            throw new InvalidDataException("The selected dataset offset is outside the staged file.");

        var header = await LegacyProjectionReader.HeaderAsync<DataSetHeader>(path, dataSetObjectOffset,
            ["Rows"], cancellationToken);
        if (header.SchemaName == null || header.TableName == null || header.Columns == null ||
            header.RowCount == null || header.ImportedAtUtc == null)
            throw new InvalidDataException("A dataset header is incomplete or contains required null values; counts and timestamps cannot be invented.");
        var selectedColumns = CaptureImportColumns.Select(header.Columns, sourceColumns);
        bool infer = selectedColumns.Length == 0;
        var layout = new CaptureLayout(selectedColumns);
        long actualRowCount = 0;
        Progress?.SetDetail($"Counting captured rows for revision {revisionKey}");
        await foreach (var row in LegacyProjectionReader.RowsAsync(path, dataSetObjectOffset,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (infer && actualRowCount == 0)
            {
                layout = new CaptureLayout(CaptureColumns.FromFirstRow(row));
            }
            // Validate even committed/skipped rows and the Ready recovery path.
            // Only one bounded row and the fixed first-row/header layout are resident.
            _ = CaptureRowCodec.Encode(layout, row, _limits);
            actualRowCount = checked(actualRowCount + 1);
            if ((actualRowCount & 255) == 0) Progress?.SetDetail($"Counting captured rows for revision {revisionKey}: {actualRowCount:N0}");
        }
        var frozenColumns = Array.AsReadOnly(layout.Columns);
        var existing = await _capture.FindForRevisionForImportAsync(revisionKey, cancellationToken);
        CaptureWriteHandle handle;
        long committedRows;
        if (existing != null)
        {
            VerifyExisting(existing, revisionKey, header, frozenColumns, actualRowCount);
            if (existing.Summary.State == "Ready")
            {
                Progress?.SetDetail($"Rechecking completed captured rows for revision {revisionKey}: {actualRowCount:N0} rows");
                await _capture.ValidateImportRowsAsync(existing.Handle,
                    ValidationRowsAsync(cancellationToken), cancellationToken);
                Progress?.SetDetail($"Reused and verified completed capture for revision {revisionKey}: {actualRowCount:N0} rows");
                return existing.Summary;
            }
            handle = existing.Handle;
            committedRows = existing.Progress.NextRowOrdinal;
        }
        else
        {
            handle = await _capture.CreateDataSetAsync(new(revisionKey, frozenColumns, header.RowCount.Value,
                header.ImportedAtUtc.Value, actualRowCount), cancellationToken);
            committedRows = 0;
        }

        long streamedRowCount = committedRows;
        Progress?.SetDetail($"Importing captured rows for revision {revisionKey}: {committedRows:N0} / {actualRowCount:N0} already committed");
        long next = await _capture.AppendRowsAsync(handle, RemainingRowsAsync(cancellationToken),
            committedRows, cancellationToken);
        if (next != streamedRowCount || streamedRowCount != actualRowCount)
            throw new InvalidDataException("The source row stream does not agree with its validated count and committed ordinal.");
        var completed = await _capture.CompleteDataSetAsync(handle, streamedRowCount, cancellationToken);
        await _capture.ValidateImportRowsAsync(handle,
            ValidationRowsAsync(cancellationToken), cancellationToken);
        Progress?.SetDetail($"Imported and verified capture for revision {revisionKey}: {actualRowCount:N0} rows");
        return completed;

        async IAsyncEnumerable<JsonElement> RemainingRowsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var row in LegacyProjectionReader.RowsAsync(path, dataSetObjectOffset, committedRows, ct)
                .ConfigureAwait(false))
            {
                streamedRowCount = checked(streamedRowCount + 1);
                if ((streamedRowCount & 255) == 0) Progress?.SetDetail($"Streaming captured rows for revision {revisionKey}: {streamedRowCount:N0} / {actualRowCount:N0}");
                yield return row;
            }
        }

        async IAsyncEnumerable<JsonElement> ValidationRowsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            long checkedRows = 0;
            Progress?.SetDetail($"Checking captured row fidelity for revision {revisionKey}: 0 / {actualRowCount:N0}");
            await foreach (var row in LegacyProjectionReader.RowsAsync(path, dataSetObjectOffset, cancellationToken: ct))
            {
                yield return row;
                checkedRows++;
                if ((checkedRows & 255) == 0) Progress?.SetDetail($"Checking captured row fidelity for revision {revisionKey}: {checkedRows:N0} / {actualRowCount:N0}");
            }
        }
    }

    private static void VerifyExisting(CaptureImportState existing, long revisionKey, DataSetHeader header,
        IReadOnlyList<CaptureColumnDefinition> columns, long actualRowCount)
    {
        var summary = existing.Summary;
        var progress = existing.Progress;
        if (summary.RevisionKey != revisionKey || summary.ReportedRowCount != header.RowCount!.Value ||
            !summary.ImportedAtUtc.EqualsExact(header.ImportedAtUtc!.Value) ||
            summary.DisplayFormatVersion != CaptureDisplay.FormatVersion || !existing.Columns.SequenceEqual(columns))
            throw new InvalidDataException("The recovered dataset does not match the pinned source header and column layout.");
        if (summary.State is not ("Writing" or "Ready") || progress.State != summary.State ||
            progress.NextRowOrdinal != summary.ActualRowCount || progress.NextRowOrdinal < 0 ||
            progress.NextRowOrdinal > actualRowCount ||
            (progress.ExpectedActualRowCount is long expected && expected != actualRowCount))
            throw new InvalidDataException("The recovered dataset checkpoint does not match the source row count.");
        if (summary.State == "Ready" && (summary.ActualRowCount != actualRowCount ||
            progress.ExpectedActualRowCount != actualRowCount))
            throw new InvalidDataException("The ready dataset has not completed this source row stream.");
    }

    // The legacy SqlTableDataSet model has no Columns field in some formats.
    // Keep this extension local; never deserialize its Rows collection.
    private sealed class DataSetHeader
    {
        public DataSetHeader() { }

        public string SchemaName { get; set; } = "dbo";
        public string TableName { get; set; } = string.Empty;
        public long? RowCount { get; set; }
        public DateTimeOffset? ImportedAtUtc { get; set; }
        public List<string> Columns { get; set; } = [];
        [JsonIgnore]
        public string FullName => string.Empty;
    }
}

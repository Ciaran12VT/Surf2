using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private const int PerformanceRowCount = 20_000;
    private const int PerformanceExportWriteBytes = 64 * 1024;
    private static readonly string PerformancePadding = new('x', 128);
    private static readonly string[] PerformancePropertyNames = ["Id", "Text", "Amount", "Active"];

    // Explicitly opt-in. Uses the same generated, owned source/destination fixture as the SQL suite.
    public static async Task RunPerformanceAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        var session = new RelationalSession(fixture.DestinationConnectionString);
        var content = new RelationalContentStore();
        var limits = new CaptureLimits
        {
            WriteBatchRows = 128, WriteBatchBytes = 1024 * 1024,
            ReadBatchRows = 128, ReadBatchBytes = 512 * 1024,
            MaxPageRows = 64, MaxPageBytes = 256 * 1024
        };
        var capture = new RelationalCaptureStore(session, limits);
        long snapshotKey = 0, resourceKey = 0, revisionKey = 0;
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            snapshotKey = await writer.CreateSnapshotAsync(new("performance-fixture", "Bounded capture fixture",
                "owned regression database", EvidenceTime, 0));
            resourceKey = await writer.CreateResourceAsync(snapshotKey, DatabaseVersionedResourceKind.TableData,
                "dbo", "PerformanceRows", "performance-data", 0);
            revisionKey = await writer.InsertTableDataRevisionAsync(resourceKey);
        });
        CaptureColumnDefinition[] columns =
        [
            new("Id", "int"), new("Text", "nvarchar"),
            new("Amount", "decimal", 17, 38, 4), new("Active", "bit")
        ];
        // The caller-owned domain transaction above is committed before capture starts its own batches.
        var handle = await capture.CreateDataSetAsync(new(revisionKey, columns, PerformanceRowCount,
            EvidenceTime, PerformanceRowCount));
        await ThrowsPerformanceWriteBudgetAsync(() =>
            new RelationalCaptureStore(session, limits with { WriteBatchBytes = 32 * 1024 })
                .AppendRowsAsync(handle, PerformanceRowsAsync(1), 0), check,
            "Performance capture refuses an insufficient 32 KiB bulk write budget at construction or append");
        var rejectedProgress = await capture.GetWriteProgressAsync(handle);
        check(rejectedProgress.NextRowOrdinal == 0 && rejectedProgress.State == "Writing",
            "Performance bulk minimum-budget rejection commits no rows or write progress");
        check(true, FormattableString.Invariant(
            $"Performance fixture: rows={PerformanceRowCount}; write_budget={limits.WriteBatchRows} rows/{limits.WriteBatchBytes} bytes; read_budget={limits.ReadBatchRows} rows/{limits.ReadBatchBytes} bytes; page_budget={limits.MaxPageRows} rows/{limits.MaxPageBytes} bytes; export_write_budget={PerformanceExportWriteBytes} bytes. Timings include fixture generation/validation and exclude boundary full GCs. GC metrics are process-wide and instrumented; retention is a post-full-GC estimate; the 20 ms sampled live-heap peak is not RSS or a guaranteed transient maximum."));

        long written;
        CaptureDataSetSummary summary;
        await using (var measurement = new PerformanceMeasurement())
        {
            written = await capture.AppendRowsAsync(handle, PerformanceRowsAsync(PerformanceRowCount), 0);
            summary = await capture.CompleteDataSetAsync(handle);
            await measurement.ReportAsync(check, "write+seal", written);
        }
        var progress = await capture.GetWriteProgressAsync(handle);
        check(written == PerformanceRowCount && summary.ActualRowCount == PerformanceRowCount &&
            summary.ReportedRowCount == PerformanceRowCount && summary.State == "Ready" &&
            progress.NextRowOrdinal == PerformanceRowCount && progress.State == "Ready",
            "Performance streamed seed seals exactly 20,000 rows under fixed capture write budgets");
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            await writer.SealRevisionAsync(revisionKey);
            await writer.SetCurrentRevisionAsync(resourceKey, revisionKey, 0);
            await writer.PublishSnapshotAsync(snapshotKey, null);
        });
        await ThrowsAsync<InvalidOperationException>(() => capture.ReadPageAsync(handle.DataSetKey), check,
            "Performance fixture still refuses runtime capture reads while Migrating");
        // Storage-only scale evidence, not migration validation or a production Ready publisher.
        Func<Task> markTestFixtureReady = fixture.MarkTestFixtureReadyAsync;
        await markTestFixtureReady();

        long pagedRows = 0, largestPageBytes = 0;
        int pageCount = 0, largestPageRows = 0;
        bool pageBudgets = true, pageFidelity = true, byteBoundSeen = false;
        CapturePageCursor? cursor = null;
        await using (var measurement = new PerformanceMeasurement())
        {
            do
            {
                if (++pageCount > PerformanceRowCount + 1)
                    throw new InvalidOperationException("Performance capture paging did not terminate.");
                var page = await capture.ReadPageAsync(handle.DataSetKey, cursor: cursor,
                    maxRows: limits.MaxPageRows, maxBytes: limits.MaxPageBytes);
                pageBudgets &= page.Rows.Count <= limits.MaxPageRows && page.EstimatedBytes <= limits.MaxPageBytes &&
                    page.EstimatedBytes == page.Rows.Sum(row => row.EstimatedBytes) &&
                    (page.NextCursor == null || page.Rows.Count > 0);
                if (page.Rows.Count == 0 && page.NextCursor != null)
                    throw new InvalidOperationException("Performance capture paging made no progress.");
                foreach (var row in page.Rows)
                {
                    pageBudgets &= row.EstimatedBytes > 0 && row.EstimatedBytes <= limits.ReadBatchBytes;
                    pageFidelity &= row.RowOrdinal == pagedRows && PerformanceRowMatches(row.Value, pagedRows);
                    pagedRows++;
                }
                largestPageRows = Math.Max(largestPageRows, page.Rows.Count);
                largestPageBytes = Math.Max(largestPageBytes, page.EstimatedBytes);
                byteBoundSeen |= page.NextCursor != null && page.Rows.Count < limits.MaxPageRows;
                cursor = page.NextCursor;
            } while (cursor != null);
            await measurement.ReportAsync(check, "paged-read+validate", pagedRows);
        }
        check(pageBudgets && byteBoundSeen,
            "Performance pages obey configured row/estimated-byte budgets, including byte-limited nonfinal pages");
        check(pageFidelity && pagedRows == PerformanceRowCount,
            "Performance paging preserves every ordered row without retaining or combining pages");
        check(true, FormattableString.Invariant(
            $"Performance pages: count={pageCount}; largest_rows={largestPageRows}; largest_estimated_bytes={largestPageBytes}."));

        long streamedRows = 0, largestRowBytes = 0;
        bool streamFidelity = true, streamRowBudgets = true;
        await using (var measurement = new PerformanceMeasurement())
        {
            await foreach (var row in capture.StreamRowsAsync(handle.DataSetKey))
            {
                streamFidelity &= row.RowOrdinal == streamedRows && PerformanceRowMatches(row.Value, streamedRows);
                streamRowBudgets &= row.EstimatedBytes > 0 && row.EstimatedBytes <= limits.ReadBatchBytes;
                largestRowBytes = Math.Max(largestRowBytes, row.EstimatedBytes);
                streamedRows++;
            }
            await measurement.ReportAsync(check, "stream-read+validate", streamedRows);
        }
        check(streamFidelity && streamRowBudgets && streamedRows == PerformanceRowCount,
            "Performance row stream preserves all rows under the fixed read contract without a fixture row collection");

        using var export = new PerformanceHashSink(PerformanceExportWriteBytes);
        long exportedRows;
        await using (var measurement = new PerformanceMeasurement())
        {
            exportedRows = await capture.WriteDelimitedAsync(handle.DataSetKey, export);
            await measurement.ReportAsync(check, "stream-export", exportedRows);
        }
        byte[] exportHash = export.FinishHash();
        var expectedExport = ExpectedPerformanceExport();
        check(exportedRows == PerformanceRowCount && export.BytesWritten == expectedExport.Bytes &&
            exportHash.SequenceEqual(expectedExport.Hash),
            "Performance export streams all CSV bytes with exact tokens, escaping and row order to a nonretaining hash sink");
        check(export.MaxWriteBytes > 0 && export.MaxWriteBytes <= PerformanceExportWriteBytes,
            "Performance export obeys the bounded sink write contract without a whole-output buffer");
        check(true, FormattableString.Invariant(
            $"Performance export: rows={exportedRows}; utf8_bytes={export.BytesWritten}; largest_write_bytes={export.MaxWriteBytes}; sha256={Convert.ToHexString(exportHash)}."));

        var sql = await ReadPerformanceSqlEvidenceAsync(session, handle);
        check(sql.Rows == PerformanceRowCount && await capture.CountRowsAsync(handle.DataSetKey) == sql.Rows &&
            sql.LargestRowEstimate == largestRowBytes && sql.LargestRowEstimate <= limits.WriteBatchBytes &&
            sql.UsedBytes > 0 && sql.ReservedBytes >= sql.UsedBytes,
            "Performance SQL physical row count agrees with sealed and streamed evidence; table allocation is internally consistent");
        check(true, FormattableString.Invariant(
            $"Performance SQL: actual_rows={sql.Rows}; estimated_row_bytes_sum={sql.EstimatedBytes}; largest_row_estimated_bytes={sql.LargestRowEstimate}; capture_table_used_bytes={sql.UsedBytes}; capture_table_reserved_bytes={sql.ReservedBytes}. Table bytes include indexes/LOB pages, not database/log files."));
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task ThrowsPerformanceWriteBudgetAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        bool rejected = false;
        try { await action(); }
        catch (CaptureLimitException) { rejected = true; }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == nameof(CaptureLimits.WriteBatchBytes)) { rejected = true; }
        check(rejected, name);
    }

    private static string PerformanceText(long ordinal) =>
        "row " + ordinal.ToString("D5", CultureInfo.InvariantCulture) + " " + PerformancePadding +
        ", quoted \"value\"" + (ordinal % 257 == 0 ? "\nnext line" : "");

    private static string PerformanceAmount(long ordinal) => ordinal.ToString(CultureInfo.InvariantCulture) + ".2300";

    private static async IAsyncEnumerable<JsonElement> PerformanceRowsAsync(int count)
    {
        await Task.CompletedTask;
        // Generate only the next row; neither source rows nor encoded capture batches are accumulated by the fixture.
        for (int i = 0; i < count; i++)
            yield return Json("{\"Id\":" + i.ToString(CultureInfo.InvariantCulture) +
                ",\"Text\":" + JsonSerializer.Serialize(PerformanceText(i)) +
                ",\"Amount\":" + PerformanceAmount(i) + ",\"Active\":" + (i % 2 == 0 ? "true" : "false") + "}");
    }

    private static bool PerformanceRowMatches(JsonElement row, long ordinal)
    {
        if (row.ValueKind != JsonValueKind.Object) return false;
        int property = 0;
        foreach (var value in row.EnumerateObject())
            if (property == PerformancePropertyNames.Length || value.Name != PerformancePropertyNames[property++]) return false;
        return property == PerformancePropertyNames.Length && row.GetProperty("Id").TryGetInt64(out long id) && id == ordinal &&
            row.GetProperty("Text").GetString() == PerformanceText(ordinal) &&
            row.GetProperty("Amount").GetRawText() == PerformanceAmount(ordinal) &&
            row.GetProperty("Active").ValueKind == (ordinal % 2 == 0 ? JsonValueKind.True : JsonValueKind.False);
    }

    private static (long Bytes, byte[] Hash) ExpectedPerformanceExport()
    {
        using var sink = new PerformanceHashSink(PerformanceExportWriteBytes);
        sink.Write(Encoding.UTF8.GetBytes("Id,Text,Amount,Active" + Environment.NewLine));
        for (int i = 0; i < PerformanceRowCount; i++)
        {
            // Text always contains a comma/quote. This independent CSV reference uses at most one row buffer.
            string text = "\"" + PerformanceText(i).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            string record = i.ToString(CultureInfo.InvariantCulture) + "," + text + "," + PerformanceAmount(i) +
                "," + (i % 2 == 0 ? "True" : "False") + Environment.NewLine;
            sink.Write(Encoding.UTF8.GetBytes(record));
        }
        return (sink.BytesWritten, sink.FinishHash());
    }

    private sealed record PerformanceSqlEvidence(long Rows, long EstimatedBytes, long LargestRowEstimate,
        long UsedBytes, long ReservedBytes);

    private static async Task<PerformanceSqlEvidence> ReadPerformanceSqlEvidenceAsync(RelationalSession session,
        CaptureWriteHandle handle)
    {
        if (handle.Epoch != session.Epoch || handle.LayoutKey <= 0 || handle.DataSetKey <= 0)
            throw new ArgumentException("Performance SQL diagnostics require the fixture's generated capture handle.");
        // The physical identifier contains only the validated positive layout identity, never source metadata.
        string table = "[capture].[Data_" + handle.LayoutKey.ToString(CultureInfo.InvariantCulture) + "]";
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"""
            SELECT COUNT_BIG(*), COALESCE(SUM(EstimatedBytes), 0), COALESCE(MAX(EstimatedBytes), 0)
            FROM {table} WHERE DataSetKey=@DataSet;
            SELECT COALESCE(SUM(CONVERT(bigint, a.used_pages)), 0) * CONVERT(bigint, 8192),
                   COALESCE(SUM(CONVERT(bigint, a.total_pages)), 0) * CONVERT(bigint, 8192)
            FROM sys.partitions p JOIN sys.allocation_units a
              ON a.container_id=CASE WHEN a.type=2 THEN p.partition_id ELSE p.hobt_id END
            WHERE p.object_id=OBJECT_ID(@TableName, N'U') AND a.type IN (1, 2, 3);
            """;
        command.Parameters.Add(RelationalSession.Parameter("@DataSet", SqlDbType.BigInt, handle.DataSetKey));
        command.Parameters.Add(RelationalSession.Parameter("@TableName", SqlDbType.NVarChar, table, 128));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidDataException("Missing performance row diagnostics.");
        long rows = reader.GetInt64(0), estimated = reader.GetInt64(1), largest = reader.GetInt64(2);
        if (!await reader.NextResultAsync() || !await reader.ReadAsync())
            throw new InvalidDataException("Missing performance allocation diagnostics.");
        return new(rows, estimated, largest, reader.GetInt64(0), reader.GetInt64(1));
    }

    private sealed class PerformanceMeasurement : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly long _retainedBefore;
        private readonly long _allocatedBefore;
        private readonly int[] _collectionsBefore;
        private readonly Stopwatch _watch;
        private readonly Task _sampler;
        private long _sampledPeak;
        private bool _reported;

        public PerformanceMeasurement()
        {
            _retainedBefore = GC.GetTotalMemory(forceFullCollection: true);
            _sampledPeak = _retainedBefore;
            _collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            _allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            _watch = Stopwatch.StartNew();
            _sampler = SampleAsync();
        }

        private async Task SampleAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) Sample();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private void Sample()
        {
            long live = GC.GetTotalMemory(forceFullCollection: false);
            long previous = Interlocked.Read(ref _sampledPeak);
            while (live > previous)
            {
                long observed = Interlocked.CompareExchange(ref _sampledPeak, live, previous);
                if (observed == previous) break;
                previous = observed;
            }
        }

        public async Task ReportAsync(Action<bool, string> check, string phase, long rows)
        {
            if (_reported) throw new InvalidOperationException("Performance phase already reported.");
            _watch.Stop();
            Sample();
            _stop.Cancel();
            await _sampler.ConfigureAwait(false);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - _allocatedBefore;
            int gen0 = GC.CollectionCount(0) - _collectionsBefore[0];
            int gen1 = GC.CollectionCount(1) - _collectionsBefore[1];
            int gen2 = GC.CollectionCount(2) - _collectionsBefore[2];
            long retainedAfter = GC.GetTotalMemory(forceFullCollection: true);
            _reported = true;
            check(true, FormattableString.Invariant(
                $"Performance {phase}: rows={rows}; elapsed_ms={_watch.Elapsed.TotalMilliseconds:F1}; managed_allocated_delta_bytes={allocated}; retained_before_bytes={_retainedBefore}; retained_after_bytes={retainedAfter}; retained_delta_bytes={retainedAfter - _retainedBefore}; sampled_peak_managed_live_bytes={Interlocked.Read(ref _sampledPeak)}; gc_collections_gen0={gen0}; gen1={gen1}; gen2={gen2}."));
        }

        public async ValueTask DisposeAsync()
        {
            _watch.Stop();
            _stop.Cancel();
            await _sampler.ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private sealed class PerformanceHashSink(int maxWriteBytes) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; private set; }
        public int MaxWriteBytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public byte[] FinishHash() => _hash.GetHashAndReset();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maxWriteBytes) throw new CaptureLimitException("Export exceeded the fixture sink write budget.");
            _hash.AppendData(buffer);
            BytesWritten = checked(BytesWritten + buffer.Length);
            MaxWriteBytes = Math.Max(MaxWriteBytes, buffer.Length);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private sealed record CaptureFixture(CaptureWriteHandle Current, CaptureWriteHandle Previous,
        CaptureColumnDefinition[] Columns, JsonElement[] Rows, JsonElement[] PreviousRows);

    private static async Task<CaptureFixture> SeedCaptureAsync(RelationalSession session, RelationalContentStore content,
        RelationalCaptureStore capture, SnapshotFixture f, Action<bool, string> check)
    {
        CaptureColumnDefinition[] columns =
        [
            new("A", "decimal", 17, 38, -2, false, 9, true),
            new("text", "nvarchar", -1, null, null, true),
            new("odd];-- name", "source type; not executable", null, null, null, null)
        ];
        JsonElement[] rows =
        [
            Json("{\"A\":1.2300,\"A\":null,\"a\":\"case collision\",\"text\":\"alpha\",\"extra\":{\"n\":1e+02,\"n\":-0},\"odd];-- name\":false}"),
            Json("{\"a\":null,\"text\":\"\",\"extra\":[true,null,{\"x\":\"\\u00e9\\ud83d\\ude00\"}]}"),
            Json("{\"text\":\"ALPHAbeta\",\"A\":true}"),
            Json("{}"), Json("null"), Json("[1.000, {\"a\":2,\"a\":3}, false]"),
            Json("\"standalone \\u00e9\\ud83d\\ude00\""), Json("false"),
            Json("123456789012345678901234567890.000001e-27")
        ];
        JsonElement[] previous = [Json("{\"A\":-0,\"text\":\"previous\"}"), Json("{\"text\":null}"), Json("true")];
        await ThrowsAsync<SqlException>(() => capture.CreateDataSetAsync(new(long.MaxValue, columns, 0, EvidenceTime)), check,
            "Capture cannot create an independent or invented snapshot revision FK");
        await ThrowsAsync<CaptureLimitException>(() => capture.CreateDataSetAsync(new(f.CurrentDataRevision,
            Enumerable.Range(0, 129).Select(i => new CaptureColumnDefinition("C" + i)).ToArray(), 0, EvidenceTime)), check,
            "Capture rejects layouts above 128 columns without truncating");
        await ThrowsAsync<InvalidDataException>(() => capture.CreateDataSetAsync(new(f.CurrentDataRevision,
            new CaptureColumnDefinition[] { new("a"), new("A") }, 0, EvidenceTime)), check,
            "Declared case-colliding layout is rejected, not silently merged");
        var current = await capture.CreateDataSetAsync(new(f.CurrentDataRevision, columns, -99, EvidenceTime, rows.Length));
        var previousHandle = await capture.CreateDataSetAsync(new(f.OldDataRevision, columns, 1000000000000, EvidenceTime.AddTicks(1), previous.Length));
        check(current.LayoutKey == previousHandle.LayoutKey, "Exact captured layouts are reused across independent revision datasets");
        var small = new RelationalCaptureStore(session, new CaptureLimits { MaxRowUtf8Bytes = 128, MaxTokenUtf8Bytes = 128 });
        await ThrowsAsync<CaptureLimitException>(() => small.AppendRowsAsync(current,
            JsonRows(new[] { Json("{\"text\":\"" + new string('x', 256) + "\"}") }), 0), check,
            "Oversized captured input row is rejected before committing progress");
        check((await capture.GetWriteProgressAsync(current)).NextRowOrdinal == 0, "Rejected captured input leaves write progress unchanged");
        check(await capture.AppendRowsAsync(current, JsonRows(rows), 0) == rows.Length, "Captured rows append through bounded owned batch transactions");
        await ThrowsAsync<InvalidOperationException>(() => capture.AppendRowsAsync(current, JsonRows(new[] { rows[0] }), 0), check,
            "Capture rejects stale or replayed expected row ordinal");
        var import = Required(await capture.FindForRevisionForImportAsync(f.CurrentDataRevision), "capture recovery lookup");
        check(import.Progress.NextRowOrdinal == rows.Length && import.Progress.State == "Writing" && import.Summary.ReportedRowCount == -99,
            "Migrating capture recovery exposes exact staged progress and reported evidence");
        await capture.AppendRowsAsync(previousHandle, JsonRows(previous.Take(2)), 0);
        await ThrowsAsync<InvalidDataException>(() => capture.CompleteDataSetAsync(previousHandle), check,
            "Dataset cannot seal before expected actual count is reached");
        check((await capture.GetWriteProgressAsync(previousHandle)).State == "Writing", "Incomplete seal leaves dataset writable");
        await ThrowsAsync<InvalidDataException>(() => capture.AppendRowsAsync(previousHandle, JsonRows(previous.Take(2)), 2), check,
            "Dataset rejects rows beyond expected actual count atomically");
        check((await capture.GetWriteProgressAsync(previousHandle)).NextRowOrdinal == 2, "Over-count batch commits no rows or progress");
        await capture.AppendRowsAsync(previousHandle, JsonRows(previous.Skip(2)), 2);
        var summary = await capture.CompleteDataSetAsync(current);
        await capture.CompleteDataSetAsync(previousHandle);
        check(summary.State == "Ready" && summary.ActualRowCount == rows.Length && summary.ReportedRowCount == -99 &&
            summary.ImportedAtUtc.Ticks == EvidenceTime.Ticks && summary.ImportedAtUtc.Offset == EvidenceTime.Offset,
            "Dataset seal preserves actual vs reported counts and exact timestamp evidence");
        check((await capture.CompleteDataSetAsync(current)) == summary, "Dataset completion is idempotent");
        await ThrowsAsync<InvalidOperationException>(() => capture.AppendRowsAsync(current, JsonRows(new[] { rows[0] }), rows.Length), check,
            "Ready captured datasets are immutable");
        await ThrowsAsync<ArgumentException>(() => capture.GetWriteProgressAsync(current with { Epoch = Guid.NewGuid() }), check,
            "Capture write handle is bound to its connection epoch");
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var writer = new RelationalSnapshotWriter(connection, transaction, content);
            await writer.SealRevisionAsync(f.CurrentDataRevision);
            await writer.SealRevisionAsync(f.OldDataRevision);
            await writer.SetCurrentRevisionAsync(f.DataResource, f.CurrentDataRevision, 5);
            await writer.PublishSnapshotAsync(f.Key, f.CurrentVersion);
        });
        return new(current, previousHandle, columns, rows, previous);
    }

    private static async Task VerifyCaptureAsync(RelationalSession session, RelationalCaptureStore capture,
        RelationalSnapshotStore snapshots, SnapshotFixture snapshot, CaptureFixture f, Action<bool, string> check)
    {
        var descriptor = await capture.GetDescriptorAsync(f.Current.DataSetKey);
        check(descriptor.Columns.SequenceEqual(f.Columns) && descriptor.Epoch == session.Epoch &&
            descriptor.Summary.RevisionKey == snapshot.CurrentDataRevision, "Capture descriptor retains typed source layout and revision ownership");
        var selected = Required(await capture.GetForRevisionAsync(snapshot.CurrentDataRevision), "selected current capture");
        check(selected.Summary.DataSetKey == f.Current.DataSetKey && await capture.GetForRevisionAsync(snapshot.CurrentObject.RevisionKey) == null,
            "Capture revision lookup selects one dataset without loading an unrelated revision");
        var streamed = new List<CaptureRow>();
        await foreach (var row in capture.StreamRowsAsync(f.Current.DataSetKey)) streamed.Add(row);
        check(streamed.Select(x => x.RowOrdinal).SequenceEqual(Enumerable.Range(0, f.Rows.Length).Select(x => (long)x)),
            "Streamed captured ordinals are contiguous and ordered");
        check(streamed.Count == f.Rows.Length && streamed.Select((x, i) => JsonEvidenceEquals(f.Rows[i], x.Value)).All(x => x),
            "Capture round-trip preserves duplicate/property order, case, missing/null/empty, nested/root values and exact numeric tokens");
        check(CaptureDisplay.Cell(streamed[0].Value, "a") == "1.2300" && CaptureDisplay.Cell(streamed[0].Value, "odd];-- name") == "False" &&
            CaptureDisplay.Cell(streamed[1].Value, "a") == "" && CaptureDisplay.Cell(streamed[2].Value, "A") == "True",
            "Grid display uses first ordinal-ignore-case property and legacy scalar formatting");

        var paged = new List<CaptureRow>();
        CapturePageCursor? cursor = null;
        int pages = 0;
        do
        {
            if (++pages > f.Rows.Length + 1) throw new InvalidOperationException("Capture paging did not terminate.");
            var page = await capture.ReadPageAsync(f.Current.DataSetKey, cursor: cursor, maxRows: 3, maxBytes: 32 * 1024);
            check(page.Rows.Count <= 3 && page.EstimatedBytes <= 32 * 1024 && page.EstimatedBytes == page.Rows.Sum(x => x.EstimatedBytes) &&
                (page.NextCursor == null || page.Rows.Count != 0), "Captured pages obey row/byte limits and make progress");
            paged.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor != null);
        check(paged.Select(x => x.RowOrdinal).SequenceEqual(streamed.Select(x => x.RowOrdinal)) &&
            paged.Select((x, i) => JsonEvidenceEquals(f.Rows[i], x.Value)).All(x => x), "Bounded captured pages neither omit nor duplicate rows");
        long oneRowBudget = streamed[0].EstimatedBytes + streamed[1].EstimatedBytes - 1;
        var bytePage = await capture.ReadPageAsync(f.Current.DataSetKey, maxRows: 3, maxBytes: oneRowBudget);
        check(bytePage.Rows.Count == 1 && bytePage.NextCursor != null && bytePage.EstimatedBytes <= oneRowBudget,
            "Captured page stops at byte boundary before row ceiling");
        await ThrowsAsync<CaptureLimitException>(() => capture.ReadPageAsync(f.Current.DataSetKey, maxBytes: streamed[0].EstimatedBytes - 1), check,
            "Single row above requested page bytes is refused without truncation");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => capture.ReadPageAsync(f.Current.DataSetKey, maxRows: 4), check,
            "Caller cannot exceed configured captured page row ceiling");
        var first = await capture.ReadPageAsync(f.Current.DataSetKey, maxRows: 1);
        var firstCursor = Required(first.NextCursor, "captured page cursor");
        await ThrowsAsync<ArgumentException>(() => capture.ReadPageAsync(f.Previous.DataSetKey, cursor: firstCursor), check,
            "Captured cursor cannot cross datasets");
        await ThrowsAsync<ArgumentException>(() => capture.ReadPageAsync(f.Current.DataSetKey,
            cursor: firstCursor with { Epoch = Guid.NewGuid() }), check, "Captured cursor cannot cross epochs");
        var alpha = new CaptureQuery(Filters: new[] { new CaptureFilter(1, "ALPHA") }, SortCultureName: "en-US");
        await ThrowsAsync<ArgumentException>(() => capture.ReadPageAsync(f.Current.DataSetKey, alpha, firstCursor), check,
            "Captured cursor cannot cross query fingerprints");
        check(await capture.CountRowsAsync(f.Current.DataSetKey) == f.Rows.Length &&
            await capture.CountRowsAsync(f.Current.DataSetKey, alpha) == 2, "Captured counts use actual rows and case-insensitive literal filter semantics");
        var and = new CaptureQuery(Filters: new[] { new CaptureFilter(0, "no-match"), new CaptureFilter(1, "alpha") });
        var any = and with { AnyMatchColumnOrdinals = new[] { 1 } };
        check(await capture.CountRowsAsync(f.Current.DataSetKey, and) == 0 && await capture.CountRowsAsync(f.Current.DataSetKey, any) == 2,
            "Active OR group takes precedence over unrelated AND filters");
        check((await capture.FindMatchingColumnsAsync(f.Current.DataSetKey, "ALPHA")).SequenceEqual(new[] { 1 }),
            "Captured matching-column discovery is ordinal and literal");
        using var output = new MemoryStream();
        long exported = await capture.WriteDelimitedAsync(f.Current.DataSetKey, output, alpha, new[] { 1, 0 });
        string csv = Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        check(exported == 2 && csv == "text,A\nalpha,1.2300\nALPHAbeta,True\n", "Bounded filtered export honors projection and exact display tokens");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => capture.ReadPageAsync(f.Current.DataSetKey, cancellationToken: cancelled.Token), check,
            "Captured page respects cancellation");
        var oldResource = Required(await snapshots.ResolveResourceAsync(snapshot.DataResource, snapshot.MiddleVersion), "historical data resource");
        var oldDescriptor = Required(await capture.GetForRevisionAsync(oldResource.RevisionKey), "historical capture");
        check(oldDescriptor.Summary.DataSetKey == f.Previous.DataSetKey && oldDescriptor.Summary.ReportedRowCount == 1000000000000,
            "Selected reverse-history data resource resolves its own immutable dataset");
        var oldRows = new List<JsonElement>();
        await foreach (var row in capture.StreamRowsAsync(oldDescriptor.Summary.DataSetKey)) oldRows.Add(row.Value);
        check(oldRows.Count == f.PreviousRows.Length && oldRows.Select((x, i) => JsonEvidenceEquals(f.PreviousRows[i], x)).All(x => x),
            "Historical captured rows remain lossless and separate from current rows");
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async IAsyncEnumerable<JsonElement> JsonRows(IEnumerable<JsonElement> rows)
    {
        await Task.CompletedTask;
        foreach (var row in rows) yield return row;
    }

    private static bool JsonEvidenceEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var lp = left.EnumerateObject().ToArray();
                var rp = right.EnumerateObject().ToArray();
                return lp.Length == rp.Length && lp.Select((p, i) => p.Name == rp[i].Name && JsonEvidenceEquals(p.Value, rp[i].Value)).All(x => x);
            case JsonValueKind.Array:
                var la = left.EnumerateArray().ToArray();
                var ra = right.EnumerateArray().ToArray();
                return la.Length == ra.Length && la.Select((v, i) => JsonEvidenceEquals(v, ra[i])).All(x => x);
            case JsonValueKind.String: return left.GetString() == right.GetString();
            case JsonValueKind.Number: return left.GetRawText() == right.GetRawText();
            default: return true;
        }
    }
}

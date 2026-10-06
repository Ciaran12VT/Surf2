#if RELATIONAL_GRID_PROTOTYPE
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Surf2.Services;
using Surf2.Services.RelationalGrid;

internal static class GridPrototypes
{
    private static int _checks;
    private static readonly GridLimits Limits = new()
    {
        MaxColumns = 8, MaxCellCharacters = 1024, MaxRowBytes = 8192,
        PageRows = 8, PageBytes = 16384, CachePages = 2, CacheBytes = 32768,
        SortRunRows = 31, SortRunBytes = 16384, MergeFanIn = 3,
        MaxEditCells = 4, MaxEditBytes = 8192, MaxDiskBytes = 64 * 1024 * 1024
    };

    public static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Surf2-grid-prototype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CsvParityAsync();
            CapturedValues();
            await CapturedCompatibilityAsync(root);
            QueryRules();
            await FilesAsync(root);
            await QueriesAsync(root);
            await LifetimesAsync(root);
            await ScaleAsync(root);
            Check(Directory.GetDirectories(root).Length == 0, "all logical-close workspaces are removed");
            Console.WriteLine($"PASS: {_checks} grid prototype assertions (no SQL, WPF or shared build).");
        }
        finally
        {
            // The test owns this GUID directory and its flat fixture files.
            foreach (string path in Directory.GetFiles(root)) File.Delete(path);
            Directory.Delete(root, recursive: false);
        }
    }

    private static async Task CsvParityAsync()
    {
        string[] fixtures = ["", "\r\n , \r\n", "Name,N\r\nA,1\r\nB,2", "A,A\n1,2", "Column 2,,Third\nx,y,z",
            "Head\n1,2,3,4", "A,B\n\"a\r\nb\",\"x\"\"y\"\n1", "1,2\n3,4,5", "A,B\nfoo\"bar\"baz,2",
            "A,B\n\"unterminated\ncell,2", "A,B\n,\n\"\",\" \"\n,1,", "A,B\r1,2\r\n3,4\n", "A,B\n\"\"", "A,B\nfoo,",
            "2020-01-01,Text\n1,2", "A,B,C\n1\n2,3", " A , \nX,Y", "A,\nX,Y", "A,B\n%_[,I\u0131\u0130i"];
        foreach (string csv in fixtures) await ParityAsync(csv);
        var random = new Random(17);
        string alphabet = "ab01,\"\r\n \t";
        for (int i = 0; i < 500; i++)
        {
            string csv = new(Enumerable.Range(0, random.Next(1, 180)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            await ParityAsync(csv);
        }

        async Task ParityAsync(string csv)
        {
            var expected = CsvGridParser.Parse(csv);
            var records = new List<string[]>();
            using var reader = new SmallReader(csv);
            await foreach (var record in new CsvRecordReader(reader, Limits with { MaxColumns = 256 }).RecordsAsync(CancellationToken.None)) records.Add(record);
            int width = Math.Max(1, records.Count == 0 ? 1 : records.Max(r => r.Length));
            var inferred = CsvHeaderPolicy.Infer(records.FirstOrDefault(), width);
            Check(inferred.Headers.SequenceEqual(expected.Headers), "streaming CSV headers match legacy inference");
            var actual = records.Skip(inferred.HasHeader ? 1 : 0).Select(r => Enumerable.Range(0, width).Select(i => i < r.Length ? r[i] : string.Empty).ToArray()).ToArray();
            Check(actual.Length == expected.Rows.Count, "streaming CSV row counts match legacy blank removal");
            for (int r = 0; r < actual.Length; r++) Check(actual[r].SequenceEqual(Enumerable.Range(0, width).Select(i => expected.Rows[r][i])), "CSV record/cell parity across tiny read boundaries");
        }
    }

    private static void CapturedValues()
    {
        GridColumn[] columns = [new(0, "X", "X"), new(1, "N", "N"), new(2, "B", "B"), new(3, "Missing", "Missing")];
        using var document = JsonDocument.Parse("{\"x\":null,\"X\":\"later\",\"N\":1.2300,\"B\":true}");
        Check(CapturedGridValues.Cells(document.RootElement, columns)!.SequenceEqual(new[] { "", "1.2300", "True", "" }), "captured first duplicate/case lookup and raw display values");
        foreach (string token in new[] { "null", "1", "\"x\"", "[]", "true", "false" })
        {
            using var scalar = JsonDocument.Parse(token);
            Check(CapturedGridValues.Cells(scalar.RootElement, columns) == null, "captured display skips non-object rows");
        }
        using var empty = JsonDocument.Parse("{}");
        Check(CapturedGridValues.Cells(empty.RootElement, columns)!.Length == 4 &&
            !CapturedGridValues.ShouldDisplay(CapturedGridValues.Cells(empty.RootElement, columns)),
            "empty objects are padded but initial blank display records are suppressed");
    }

    private static async Task CapturedCompatibilityAsync(string root)
    {
        foreach (string json in new[] { "[]", "[{}]", "[{},null,{\"\":\"must not become a synthetic cell\",\"Later\":1}]" })
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
            var legacy = CsvGridParser.Parse(RenderLegacy([], rows));
            Check(legacy.Headers.SequenceEqual(new[] { "Column 1" }) && legacy.Rows.Count == 0,
                "actual legacy parser gives zero-layout empty captures Column 1 and no visible rows");
            await using var source = FixtureSource.Captured([], rows, root, Limits);
            await using var query = await source.CreateQueryAsync();
            Check(source.Descriptor.Columns.Single() is { Header: "Column 1", IsSynthetic: true } &&
                source.Descriptor.SourceRowCount == rows.Length && await source.DisplayRowCount == 0,
                "zero-layout descriptor has a display-only header and preserves authoritative raw count");
            Check(await query.Completion == 0 && (await query.ReadPageAsync(new(0, 8, 16384))).Rows.Count == 0,
                "zero-layout empty objects do not create phantom paged rows");
            var copy = await GridOutput.CopyAsync(query);
            Check(copy.RowCount == 0 && copy.ColumnCount == 1 && copy.Text == "Column 1" + Environment.NewLine,
                "zero-layout visible Copy includes legacy generated header only");
        }
        GridColumn[] columns = [new(0, "A", "A")];
        using var mixed = JsonDocument.Parse("[{},null,{\"A\":null},{\"A\":\" \"},{\"Later\":1},{\"A\":false},{\"A\":\"value\"}]");
        var mixedRows = mixed.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
        var expected = CsvGridParser.Parse(RenderLegacy(columns, mixedRows));
        await using var mixedSource = FixtureSource.Captured(columns, mixedRows, root, Limits);
        await using var mixedQuery = await mixedSource.CreateQueryAsync();
        Check(await mixedSource.DisplayRowCount == expected.Rows.Count && await mixedQuery.Completion == 2 &&
            mixedSource.Descriptor.SourceRowCount == 7, "captured blank removal agrees with legacy without changing raw counts");
        var page = await mixedQuery.ReadPageAsync(new(0, 8, 16384));
        Check(page.Rows.Select(row => row.RowOrdinal).SequenceEqual(new long[] { 5, 6 }) &&
            page.Rows.Select(row => row.Cells[0]).SequenceEqual(expected.Rows.Select(row => row[0])),
            "captured row padding, display values and ordinal gaps agree with legacy");
        mixedSource.SetCell(page.Rows[0], 0, "");
        await using var edited = await mixedSource.CreateQueryAsync();
        Check(await edited.Completion == 2 && (await edited.ReadPageAsync(new(0, 8, 16384))).Rows[0].Cells[0] == "",
            "editing an initially visible row to blank does not reparse and remove the row");

        static string RenderLegacy(IReadOnlyList<GridColumn> headers, IReadOnlyList<JsonElement> rows)
        {
            var csv = new StringBuilder().AppendJoin(',', headers.Select(column => Escape(column.Header))).AppendLine();
            foreach (var row in rows)
            {
                var cells = CapturedGridValues.Cells(row, headers);
                if (cells != null) csv.AppendJoin(',', cells.Select(Escape)).AppendLine();
            }
            return csv.ToString();
        }
        static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task FilesAsync(string root)
    {
        string csv = "Name,N\r\n\"a\r\nb\",1\r\nX,2,late\r\n";
        Encoding[] encodings = [new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true),
            new UTF32Encoding(false, true), new UTF32Encoding(true, true), Encoding.Latin1];
        foreach (Encoding encoding in encodings)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".csv");
            await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes(csv)).ToArray());
            await using var source = await CsvGridSource.OpenAsync(path, new(encoding), Limits with { StagingDirectory = root });
            await using var query = await source.CreateQueryAsync();
            Check(await query.Completion == 2 && query.Descriptor.Columns.Count == 3, "physical CSV encoding and ragged width");
            var page = await query.ReadPageAsync(new(0, 8, 16384));
            Check(page.Rows[0].Cells[0] == "a\r\nb" && page.Rows[0].Cells[2] == "" && page.Rows[1].RowOrdinal == 1, "physical multiline rows, padding and positional ordinals");
            using var output = new MemoryStream();
            Check(await GridOutput.WriteDelimitedAsync(query, output, [2, 0]) == 2, "CSV export streams all matches in selected column order");
            Check(!output.ToArray().Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "CSV output has no UTF-8 BOM");
            await ThrowsAsync<InvalidOperationException>(() => GridOutput.ExportAsync(query, path, replaceExisting: true));
        }
        string emptyPath = Path.Combine(root, "empty.csv");
        await File.WriteAllTextAsync(emptyPath, "");
        await using (var source = await CsvGridSource.OpenAsync(emptyPath, limits: Limits with { StagingDirectory = root }))
        await using (var query = await source.CreateQueryAsync())
        {
            Check(await query.Completion == 0 && source.Descriptor.Columns.Single().Header == "Column 1", "empty CSV has generated header and zero rows");
            Check((await GridOutput.CopyAsync(query)).Text == "Column 1" + Environment.NewLine, "empty Copy contains header only");
        }
        string changing = Path.Combine(root, "changing.csv");
        await File.WriteAllTextAsync(changing, "Name\nAlpha\nBeta");
        await using (var source = await CsvGridSource.OpenAsync(changing, limits: Limits with { StagingDirectory = root }))
        await using (var query = await source.CreateQueryAsync())
        {
            await query.Completion;
            await query.ReadPageAsync(new(0, 8, 16384));
            await File.WriteAllTextAsync(changing, "Name\nChanged\nBeta");
            await ThrowsAsync<GridSourceChangedException>(() => query.ReadPageAsync(new(0, 8, 16384)));
            await ThrowsAsync<GridSourceChangedException>(() => GridOutput.CopyAsync(query));
        }
        string hiddenChange = Path.Combine(root, "same-metadata.csv");
        await File.WriteAllTextAsync(hiddenChange, "Name\nAlpha\nBeta");
        await using (var source = await CsvGridSource.OpenAsync(hiddenChange, limits: Limits with { StagingDirectory = root }))
        await using (var query = await source.CreateQueryAsync())
        {
            await query.Completion;
            var info = new FileInfo(hiddenChange);
            var modified = info.LastWriteTimeUtc;
            var created = info.CreationTimeUtc;
            await File.WriteAllTextAsync(hiddenChange, "Name\nOther\nBeta");
            File.SetLastWriteTimeUtc(hiddenChange, modified);
            File.SetCreationTimeUtc(hiddenChange, created);
            await ThrowsAsync<GridSourceChangedException>(() => GridOutput.CopyAsync(query));
        }
        string narrow = Path.Combine(root, "oversize.csv");
        await File.WriteAllTextAsync(narrow, "Name\n" + new string('x', 2000));
        await ThrowsAsync<GridLimitException>(() => CsvGridSource.OpenAsync(narrow, limits: Limits with { StagingDirectory = root }));
        string invalidUtf8 = Path.Combine(root, "invalid-utf8.csv");
        byte[] invalidBytes = [.. Encoding.UTF8.GetBytes("Name\n"), 0xc3, 0x28];
        await File.WriteAllBytesAsync(invalidUtf8, invalidBytes);
        await using (var source = await CsvGridSource.OpenAsync(invalidUtf8, limits: Limits with { StagingDirectory = root }))
        await using (var query = await source.CreateQueryAsync())
        {
            await query.Completion;
            Check((await query.ReadPageAsync(new(0, 8, 16384))).Rows[0].Cells[0] == Encoding.UTF8.GetString(invalidBytes.AsSpan(5)), "default CSV decoding preserves File.ReadAllText replacement fallback");
        }
        await ThrowsAsync<DecoderFallbackException>(() => CsvGridSource.OpenAsync(invalidUtf8, new(new UTF8Encoding(false, true)), Limits with { StagingDirectory = root }));
        string changingOutput = Path.Combine(root, "changing-output.csv");
        string existing = Path.Combine(root, "unchanged-target.csv");
        string original = "Name\n" + string.Join("\n", Enumerable.Range(0, 300).Select(i => "A" + i.ToString("D3")));
        await File.WriteAllTextAsync(changingOutput, original);
        await File.WriteAllTextAsync(existing, "untouched");
        await using (var source = await CsvGridSource.OpenAsync(changingOutput, limits: Limits with { StagingDirectory = root }))
        await using (var query = await source.CreateQueryAsync())
        {
            await query.Completion;
            var stamp = File.GetLastWriteTimeUtc(changingOutput);
            bool changed = false;
            var progress = new InlineProgress(_ =>
            {
                if (changed) return;
                changed = true;
                File.WriteAllText(changingOutput, original.Replace("A000", "B000", StringComparison.Ordinal));
                File.SetLastWriteTimeUtc(changingOutput, stamp);
            });
            await ThrowsAsync<GridSourceChangedException>(() => GridOutput.ExportAsync(query, existing, replaceExisting: true, progress: progress));
            Check(await File.ReadAllTextAsync(existing) == "untouched" && Directory.GetFiles(root, "*.grid-pending").Length == 0,
                "source mutation during output fails final fingerprint validation and preserves the existing destination");
            Check(source.IsInvalidated && !query.TryGetCachedPage(new(0, 8, 16384), out _), "fingerprint invalidation stops all source queries and cached paint results");
            await ThrowsAsync<GridSourceChangedException>(() => source.CreateQueryAsync());
        }
    }

    private static void QueryRules()
    {
        var descriptor = new GridDescriptor(Guid.NewGuid(), "rules", [new(0, "A", "A"), new(1, "B", "B"), new(2, "C", "C")], null, null, 1);
        Check(new FrozenGridQuery(new([new(0, "%_[")]), descriptor, 0).Matches(["%_[", "", ""]), "filter wildcard characters are literal");
        Check(!new FrozenGridQuery(new([new(0, "%_[")]), descriptor, 0).Matches(["anything", "", ""]), "no SQL wildcard prefilter can omit or add matches");
        Check(new FrozenGridQuery(new([new(0, "A"), new(1, "B")]), descriptor, 0).Matches(["a", "b", ""]), "ordinary AND filters use ordinal-ignore-case");
        Check(!new FrozenGridQuery(new([new(0, "A"), new(1, "B")]), descriptor, 0).Matches(["a", "x", ""]), "every active AND predicate must match");
        Check(new FrozenGridQuery(new([new(0, "x"), new(1, " ")], [1]), descriptor, 0).Matches(["x", "", ""]), "inactive OR group falls back to active ordinary AND filters");
        string[] edges = ["i", "I", "\u0131", "\u0130", "\u00e9", "e\u0301", "\u00df", "SS", "\U00010400", "\U00010428"];
        foreach (string value in edges)
        foreach (string literal in edges)
            Check(new FrozenGridQuery(new([new(0, literal)]), descriptor, 0).Matches([value, "", ""]) == (value.IndexOf(literal, StringComparison.OrdinalIgnoreCase) >= 0), "Unicode literal matching has exact .NET semantics");
        var tie = new FrozenGridQuery(new(Sorts: [new(0, true)], SortCultureName: "en-US"), descriptor, 0);
        Check(tie.Compare(["same"], 1, ["same"], 2) < 0, "descending string sort still has ascending ordinal tie-breaker");
        var filters = new List<GridFilter> { new(0, "before") };
        var frozen = new FrozenGridQuery(new(filters), descriptor, 0);
        filters[0] = new(0, "after");
        Check(frozen.Matches(["before", "", ""]) && !frozen.Matches(["after", "", ""]), "query specification is cloned before background work");
        Throws<ArgumentException>(() => new FrozenGridQuery(new([new(0, "x"), new(0, "y")]), descriptor, 0));
        Throws<ArgumentException>(() => new FrozenGridQuery(new(Sorts: [new(3)]), descriptor, 0));
        Throws<ArgumentException>(() => new FrozenGridQuery(new([new(0, new string('x', 4097))]), descriptor, 0));
        Check(new FrozenGridQuery(null, descriptor, 0).Fingerprint != new FrozenGridQuery(null, descriptor, 1).Fingerprint, "overlay generation binds the result identity");
    }

    private static async Task QueriesAsync(string root)
    {
        await using var source = FixtureSource.Create(120, root, Limits, i => ["same", i.ToString("D3"), i % 2 == 0 ? "even" : "odd"]);
        await using var original = await source.CreateQueryAsync();
        Check(await original.Completion == 120, "unfiltered exact count finishes asynchronously");
        var first = await original.ReadPageAsync(new(0, 8, 16384));
        Check(original.TryGetCachedPage(first.Request, out var cached) && ReferenceEquals(first.Rows, cached!.Rows), "paint getter reads an existing bounded cached page");
        source.SetCell(first.Rows[0], 0, "changed,\r\n\"quote\"");
        source.SetCell(first.Rows[0], 1, "999");
        source.SetCell(first.Rows[1], 0, "\uD800");
        Check(source.OverlayGeneration == 3 && source.OverlayBytes > 0, "sparse edits have an independent revision and budget");
        for (int start = 8; start < 120; start += 8) await original.ReadPageAsync(new(start, 8, 16384));
        Check(original.CacheStatistics.Pages <= 2 && original.CacheStatistics.Bytes <= Limits.CacheBytes && !original.TryGetCachedPage(first.Request, out _), "page eviction is row/page/byte bounded");
        Check((await original.ReadPageAsync(first.Request)).Rows[0].Cells[0] == "same", "old pinned query does not adopt newer edits");
        await using var filtered = await source.CreateQueryAsync(new([new(0, "changed")]));
        Check(await filtered.Completion == 1 && (await filtered.ReadPageAsync(new(0, 8, 16384))).Rows[0].RowOrdinal == 0, "off-page edit changes filter membership");
        await using var sorted = await source.CreateQueryAsync(new(Sorts: [new(1, true)], SortCultureName: "en-US"));
        Check(await sorted.Completion == 120 && (await sorted.ReadPageAsync(new(0, 8, 16384))).Rows[0].RowOrdinal == 0, "off-page edit changes sorted position");
        var copy = await GridOutput.CopyAsync(filtered, [1, 0]);
        Check(copy.RowCount == 1 && copy.Text == "B\tA" + Environment.NewLine + "999\t\"changed,\r\n\"\"quote\"\"\"" + Environment.NewLine,
            "all-matching Copy uses effective edits, header/display order and tab quoting");
        source.SetCell(first.Rows[0], 0, "newer");
        Check((await GridOutput.CopyAsync(filtered, [0])).Text.Contains("changed", StringComparison.Ordinal), "operation snapshot is immutable across later edits");
        await ThrowsAsync<GridLimitException>(() => GridOutput.CopyAsync(sorted, maxClipboardBytes: 100));
        await filtered.DisposeAsync();
        await sorted.DisposeAsync();
        await using var any = await source.CreateQueryAsync(new([new(0, "no-match"), new(1, "01"), new(2, "odd")], [1, 2]));
        Check(await any.Completion == 65, "active OR group ignores unrelated AND filters");
        await any.DisposeAsync();
        await using var tie = await source.CreateQueryAsync(new(Sorts: [new(2)], SortCultureName: "en-US"));
        await tie.Completion;
        long previous = -1;
        await foreach (var row in tie.StreamAsync())
        {
            if (row.Cells[2] == "even") { Check(row.RowOrdinal > previous, "culture-sensitive sort ties use stable raw row ordinal"); previous = row.RowOrdinal; }
        }
        await tie.DisposeAsync();
        source.ClearEdits();
        Check(source.OverlayBytes == 0 && source.OverlayGeneration == 5, "clearing edits invalidates subsequent queries only");
        var sentinel = Path.Combine(root, "export.csv");
        await File.WriteAllTextAsync(sentinel, "old destination");
        await ThrowsAsync<IOException>(() => GridOutput.ExportAsync(original, sentinel));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => GridOutput.ExportAsync(original, sentinel, replaceExisting: true, cancellationToken: cancelled.Token));
        Check(await File.ReadAllTextAsync(sentinel) == "old destination" && Directory.GetFiles(root, "*.grid-pending").Length == 0, "canceled export preserves existing destination and cleans only owned partial");
        var export = await GridOutput.ExportAsync(original, sentinel, [1, 2], replaceExisting: true);
        Check(export.RowCount == 120 && export.ColumnCount == 2 && (await File.ReadAllTextAsync(sentinel)).StartsWith("B,C" + Environment.NewLine, StringComparison.Ordinal), "completed export atomically publishes every matching row");
    }

    private static async Task LifetimesAsync(string root)
    {
        using var release = new SemaphoreSlim(0);
        await using var source = FixtureSource.Create(30, root, Limits, i => [i.ToString()], release);
        await using var query = await source.CreateQueryAsync();
        var first = await query.ReadPageAsync(new(0, 8, 16384));
        Check(first.Rows.Count == 1 && !query.Count.IsComplete, "initial useful page precedes complete source scan/count");
        using var farCanceled = new CancellationTokenSource();
        var far1 = query.ReadPageAsync(new(25, 8, 16384), farCanceled.Token);
        var far2 = query.ReadPageAsync(new(26, 8, 16384), farCanceled.Token);
        Check((await query.ReadPageAsync(new(0, 1, 16384)).WaitAsync(TimeSpan.FromSeconds(5))).Rows.Count == 1,
            "far-position waits do not occupy interactive page read slots");
        farCanceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => far1);
        await ThrowsAsync<OperationCanceledException>(() => far2);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (query.CacheStatistics.InFlightReads != 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Check(query.CacheStatistics.InFlightReads == 0, "last-consumer cancellation stops and releases the underlying page wait");
        var request = new GridPageRequest(20, 8, 16384);
        using var oneConsumer = new CancellationTokenSource();
        Task<GridPage> a = query.ReadPageAsync(request, oneConsumer.Token);
        Task<GridPage> b = query.ReadPageAsync(request);
        oneConsumer.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => a);
        release.Release();
        Check((await b).Rows[0].RowOrdinal == 20, "canceling one shared page consumer does not cancel the other");
        Check(await query.Completion == 30, "shared page build finishes once");
        await query.DisposeAsync();
        Check(!query.TryGetCachedPage(first.Request, out _), "closed query cannot publish cached stale pages");
        await ThrowsAsync<ObjectDisposedException>(() => query.ReadPageAsync(first.Request));
        await source.DisposeAsync();
        await ThrowsAsync<ObjectDisposedException>(() => source.CreateQueryAsync());
        var limited = Limits with { MaxEditCells = 1 };
        await using var editSource = FixtureSource.Create(2, root, limited, i => ["A", "B"]);
        await using var editQuery = await editSource.CreateQueryAsync();
        await editQuery.Completion;
        var row = (await editQuery.ReadPageAsync(new(0, 2, 16384))).Rows[0];
        editSource.SetCell(row, 0, "kept");
        Throws<GridLimitException>(() => editSource.SetCell(row, 1, "rejected"));
        Check(editSource.OverlayGeneration == 1 && editSource.OverlayBytes > 0, "overlay quota rejects additional edits without evicting old edits");
        await using var effective = await editSource.CreateQueryAsync();
        Check((await effective.ReadPageAsync(new(0, 2, 16384))).Rows[0].Cells[0] == "kept", "retained overlay survives rejection and query replacement");
        await using var diskLimited = FixtureSource.Create(100, root, Limits with { MaxDiskBytes = 16384 }, i => [new string('x', 100)]);
        await using var diskQuery = await diskLimited.CreateQueryAsync();
        await ThrowsAsync<GridLimitException>(() => diskQuery.Completion);
        await using var canceledSource = FixtureSource.Create(30, root, Limits, i => [i.ToString()], new SemaphoreSlim(0));
        await using var canceledQuery = await canceledSource.CreateQueryAsync();
        await canceledQuery.ReadPageAsync(new(0, 8, 16384));
        await canceledSource.DisposeAsync();
        await ThrowsAsync<OperationCanceledException>(() => canceledQuery.Completion);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var verifiedSource = FixtureSource.Create(2, root, Limits, i => ["A"], validate: async (strong, ct) =>
        {
            if (!strong) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { exited.TrySetResult(); }
        });
        await using var verifiedQuery = await verifiedSource.CreateQueryAsync();
        await using var verifyingStream = verifiedQuery.StreamAsync().GetAsyncEnumerator();
        var moving = verifyingStream.MoveNextAsync().AsTask();
        await entered.Task;
        await verifiedSource.DisposeAsync();
        Check(exited.Task.IsCompleted, "logical close waits for canceled active source fingerprint reads");
        await ThrowsAsync<OperationCanceledException>(() => moving);
        await using var boundedStreams = FixtureSource.Create(3, root, Limits, i => ["A"]);
        await using var streamQuery = await boundedStreams.CreateQueryAsync();
        await using var stream1 = streamQuery.StreamAsync().GetAsyncEnumerator();
        await using var stream2 = streamQuery.StreamAsync().GetAsyncEnumerator();
        await using var stream3 = streamQuery.StreamAsync().GetAsyncEnumerator();
        Check(await stream1.MoveNextAsync() && await stream2.MoveNextAsync(), "bounded concurrent streams can independently read the same query");
        await ThrowsAsync<GridLimitException>(() => stream3.MoveNextAsync().AsTask());
        await boundedStreams.DisposeAsync();
        await ThrowsAsync<OperationCanceledException>(() => stream1.MoveNextAsync().AsTask());
        using var raceRelease = new SemaphoreSlim(0);
        int validations = 0;
        IGridQuerySession? racingQuery = null;
        await using var racingSource = FixtureSource.Create(30, root, Limits, i => [i.ToString()], raceRelease, async (_, ct) =>
        {
            if (Interlocked.Increment(ref validations) == 3)
            {
                raceRelease.Release();
                await racingQuery!.Completion.WaitAsync(ct);
            }
        });
        await using (racingQuery = await racingSource.CreateQueryAsync())
        {
            var partial = await racingQuery.ReadPageAsync(new(0, 8, 16384));
            Check(partial.Rows.Count == 1 && racingQuery.Count.IsComplete && !partial.IsRangeComplete,
                "count finishing during page read cannot make an initial partial range final");
            Check(!racingQuery.TryGetCachedPage(partial.Request, out _), "construction prefix is never frozen in the page cache");
            Check((await racingQuery.ReadPageAsync(partial.Request)).Rows.Count == 8, "same range can expand after construction completes");
        }
        await using var wideSource = FixtureSource.Create(20, root, Limits, _ => [new string('x', 900)]);
        await using var wideQuery = await wideSource.CreateQueryAsync();
        await wideQuery.Completion;
        var bytePage = await wideQuery.ReadPageAsync(new(0, 8, 8192));
        Check(bytePage.Rows.Count == 2 && bytePage.EstimatedBytes < 8192 && bytePage.IsRangeComplete && bytePage.HasMore,
            "byte-limited ranges are stable even when their byte sum does not exactly fill the budget");
        Check(wideQuery.TryGetCachedPage(bytePage.Request, out _), "stable byte-limited ranges are cached without waiting for an exact byte sum");
        await using var staleSource = FixtureSource.Create(1, root, Limits, _ => [new string('x', 1000), "tiny"]);
        await using var staleBase = await staleSource.CreateQueryAsync();
        var staleBaseRow = (await staleBase.ReadPageAsync(new(0, 8, 16384))).Rows.Single();
        staleSource.SetCell(staleBaseRow, 0, "");
        await using var smaller = await staleSource.CreateQueryAsync();
        var smallerRow = (await smaller.ReadPageAsync(new(0, 8, 16384))).Rows.Single();
        staleSource.ClearEdits();
        Throws<GridLimitException>(() => staleSource.SetCell(smallerRow, 1, new string('y', 1000)));
        Check(staleSource.OverlayBytes == 0, "stale query rows cannot underestimate the restored base row when validating new edits");
        await using var gapSource = FixtureSource.Create(3, root, Limits, i => [i.ToString()], ordinal: i => new long[] { 0, 2, 5 }[i]);
        await using var gapQuery = await gapSource.CreateQueryAsync();
        Check(await gapQuery.Completion == 3 && gapSource.Descriptor.SourceRowCount == 6, "display object count is separate from raw captured source count");
        var gapRows = (await gapQuery.ReadPageAsync(new(0, 8, 16384))).Rows;
        Check(gapRows.Select(r => r.RowOrdinal).SequenceEqual(new long[] { 0, 2, 5 }), "skipped capture shapes do not renumber stable row identities");
        gapSource.SetCell(gapRows[2], 0, "last");
        await using var gapEdited = await gapSource.CreateQueryAsync(new([new(0, "last")]));
        Check(await gapEdited.Completion == 1 && (await gapEdited.ReadPageAsync(new(0, 8, 16384))).Rows.Single().RowOrdinal == 5, "sparse edit identity remains raw ordinal after shape skipping");
    }

    private static async Task ScaleAsync(string root)
    {
        await using var source = FixtureSource.Create(20000, root, Limits, i => [(20000 - i).ToString("D5"), i % 7 == 0 ? "hit" : "miss"]);
        await using var query = await source.CreateQueryAsync(new(Sorts: [new(0)], SortCultureName: "en-US"));
        Check(await query.Completion == 20000, "20k source sorts via bounded runs and fixed-size disk ordinal index");
        var deep = await query.ReadPageAsync(new(19000, 8, 16384));
        Check(deep.Rows[0].RowOrdinal == 999 && deep.Rows.Count == 8, "deep positional seek does not skip/resort the source");
        for (int position = 0; position < 20000; position += 357) await query.ReadPageAsync(new(position, 8, 16384));
        Check(query.CacheStatistics.Pages <= 2 && query.CacheStatistics.Bytes <= Limits.CacheBytes, "20k random paging cache plateaus independently of corpus size");
        using var output = new MemoryStream();
        Check(await GridOutput.WriteDelimitedAsync(query, output, [0]) == 20000, "20k all-matching output does not stop at cached rows");
        Check(query.CacheStatistics.Pages <= 2, "stream output does not accumulate every page");
    }

    private sealed class FixtureSource : GridSourceBase
    {
        private FixtureSource(GridDescriptor descriptor, GridLimits limits, GridOwnedWorkspace workspace, GridDiskStore store)
            : base(descriptor, limits, workspace, store) { }
        private FixtureSource(GridDescriptor descriptor, GridLimits limits, GridOwnedWorkspace workspace, GridDiskStore store,
            Func<bool, CancellationToken, Task>? validate) : base(descriptor, limits, workspace, store, validateSource: validate) { }
        internal static FixtureSource Create(int count, string root, GridLimits limits, Func<int, string[]> cells, SemaphoreSlim? pause = null,
            Func<bool, CancellationToken, Task>? validate = null, Func<int, long>? ordinal = null)
        {
            limits = limits with { StagingDirectory = root };
            limits.Validate();
            int columns = cells(0).Length;
            var descriptor = new GridDescriptor(Guid.NewGuid(), "fixture-v1", Enumerable.Range(0, columns).Select(i => new GridColumn(i, ((char)('A' + i)).ToString(), ((char)('A' + i)).ToString())).ToArray(), count, ordinal?.Invoke(count - 1) + 1 ?? count, 1);
            var workspace = new GridOwnedWorkspace(limits);
            var store = new GridDiskStore(workspace, limits);
            var source = new FixtureSource(descriptor, limits, workspace, store, validate);
            source.StartBuild(async ct =>
            {
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await store.AppendAsync(ordinal?.Invoke(i) ?? i, cells(i), ct);
                    if (i == 0 || (i + 1) % limits.PageRows == 0) await store.PublishAsync(ct);
                    if (i == 0 && pause != null) await pause.WaitAsync(ct);
                }
                await store.FinishAsync(ct);
            });
            return source;
        }

        internal static FixtureSource Captured(IReadOnlyList<GridColumn> columns, IReadOnlyList<JsonElement> rows,
            string root, GridLimits limits)
        {
            limits = limits with { StagingDirectory = root };
            limits.Validate();
            var prepared = CapturedGridValues.PrepareColumns(columns);
            var descriptor = new GridDescriptor(Guid.NewGuid(), "captured-fixture-v1", prepared, rows.Count, rows.Count, 1);
            var workspace = new GridOwnedWorkspace(limits);
            var store = new GridDiskStore(workspace, limits);
            var source = new FixtureSource(descriptor, limits, workspace, store);
            source.StartBuild(async ct =>
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var cells = CapturedGridValues.Cells(rows[i], prepared);
                    if (CapturedGridValues.ShouldDisplay(cells)) await store.AppendAsync(i, cells!, ct);
                }
                await store.FinishAsync(ct);
            });
            return source;
        }
    }

    private sealed class SmallReader(string value) : StringReader(value)
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }
    private sealed class InlineProgress(Action<long> action) : IProgress<long> { public void Report(long value) => action(value); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); _checks++; }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
#endif

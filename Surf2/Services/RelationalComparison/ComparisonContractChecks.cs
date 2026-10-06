using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Services.RelationalComparison;

public static class ComparisonContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var passed = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "Surf2ComparisonChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var limits = new ComparisonLimits { StagingDirectory = root, SortBufferBytes = 8192, MaximumRuns = 4096, PageSize = 3 };
        try
        {
            string[] headers = ["Id", "Text", "Bool", "Missing"];
            string[] left = ["{\"Id\":\"x\",\"Text\":\"first\"}", "{\"Id\":\"X\",\"Text\":\"dup\"}",
                "{\"Id\":\"y\",\"Text\":\"one\"}", "{\"Id\":\"Y\",\"Text\":\"two\"}",
                "{\"Id\":\"x #2\",\"Text\":\"literal collision\"}", "{\"Id\":null,\"Text\":\"blank\"}",
                "{\"Id\":\"\",\"Text\":\"blank duplicate\"}", "{\"Id\":\"p\",\"Text\":null,\"Bool\":true}",
                "{\"Id\":\"Case\",\"id\":\"ignored\",\"Text\":\"a,\\\"b\\\"\\nc\"}", "null", "{}"];
            string[] right = ["{\"Id\":\"x\",\"Text\":\"first\"}", "{\"Id\":\"X\",\"Text\":\"changed\"}",
                "{\"Id\":\"y\",\"Text\":\"one\"}", "{\"Id\":\"Y\",\"Text\":\"two\"}",
                "{\"Id\":\"x #2\",\"Text\":\"literal collision\"}", "{\"Text\":\"blank\"}",
                "{\"Id\":\"\",\"Text\":\"blank duplicate\"}", "{\"Id\":\"P\",\"Text\":\"\",\"Bool\":true}",
                "{\"Id\":\"case\",\"Text\":\"a,\\\"b\\\"\\nc\"}", "{\"Id\":\"extra\",\"Bool\":false}", "42", "{}"];
            var a = Snapshot("a", left); var b = Snapshot("b", right);
            var resourceA = TableResource("a"); var resourceB = TableResource("b");
            var library = new DatabaseSnapshotLibrary(); library.Snapshots.Add(a); library.Snapshots.Add(b);
            var expected = new ResourceComparisonService().BuildTableDataDiff(resourceA, resourceB, ["Id"], library);
            await using (var result = await ComparisonTableEngine.BuildAsync(Input(left), Input(right), ["Id"], limits, ct))
            {
                var actual = await All(result);
                Check(actual.Count == expected.Rows.Count, "Table comparison retains every legacy result");
                for (int i = 0; i < actual.Count; i++)
                {
                    var x = actual[i]; var y = expected.Rows[i];
                    Check(x.Key == y.Key && x.Status == y.Status.ToString() && x.ChangedColumns == y.ChangedColumns &&
                        x.LeftPreview == y.LeftPreview && x.RightPreview == y.RightPreview, "Legacy parity row " + i);
                }
                var differences = await result.ReadPageAsync(differencesOnly: true, ct: ct);
                Check(differences.MatchingCount == expected.Rows.Count(x => x.Status != TableDataDiffStatus.Identical), "Differences-only matches all rows");
                string csv = Path.Combine(root, "all.csv");
                await result.ExportAsync(csv, ct: ct);
                Check(CsvGridParser.Parse(File.ReadAllText(csv)).Rows.Count == expected.Rows.Count, "Export includes all pages");
                File.Delete(csv);
                string existing = Path.Combine(root, "cancel.csv"); File.WriteAllText(existing, "original");
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                try { await result.ExportAsync(existing, ct: canceled.Token); throw new Exception("Cancellation was ignored."); }
                catch (OperationCanceledException) { }
                Check(File.ReadAllText(existing) == "original", "Canceled export preserves existing output"); File.Delete(existing);
            }
            Check(!Directory.EnumerateDirectories(root).Any(), "Table spools cleaned on disposal");
            var random = new Random(711);
            for (int test = 0; test < 12; test++)
            {
                string[] one = Enumerable.Range(0, 60).Select(i => JsonSerializer.Serialize(new { Id = random.Next(12), Text = random.Next(20).ToString(), Bool = i % 2 == 0 })).ToArray();
                string[] two = Enumerable.Range(0, 58).Select(i => JsonSerializer.Serialize(new { Id = random.Next(12), Text = random.Next(20).ToString(), Bool = i % 2 == 0 })).ToArray();
                var lib = new DatabaseSnapshotLibrary(); lib.Snapshots.Add(Snapshot("a", one)); lib.Snapshots.Add(Snapshot("b", two));
                var old = new ResourceComparisonService().BuildTableDataDiff(resourceA, resourceB, ["Id"], lib);
                await using var diff = await ComparisonTableEngine.BuildAsync(Input(one), Input(two), ["Id"], limits, ct);
                var actual = await All(diff);
                Check(actual.Select(x => (x.Key, x.Status, x.ChangedColumns, x.LeftPreview, x.RightPreview))
                    .SequenceEqual(old.Rows.Select(x => (x.Key, x.Status.ToString(), x.ChangedColumns, x.LeftPreview, x.RightPreview))), "Random duplicate parity " + test);
            }
            Guid epoch = Guid.NewGuid();
            RelationalComparisonTarget Target(string name) => new(new(name, "File", name, ComparisonResourceKind.File, "File", false, true, false, name), epoch);
            var collectionLeft = new[] { E("folder", true, ""), E("folder/a.sql", false, "same"), E("folder/b.sql", false, "old"), E("folder/b.sql", false, "ignored duplicate"), E("empty", true, ""), E("removed", false, "gone") };
            var collectionRight = new[] { E("folder", true, ""), E("folder/A.sql", false, "same"), E("folder/b.sql", false, "new"), E("empty", true, ""), E("added", false, "new") };
            await using (var result = await ComparisonCollectionEngine.BuildAsync(Entries(collectionLeft), Entries(collectionRight), limits, ct))
            {
                var rows = await All(result);
                Check(rows.Single(x => x.Key == "folder").Status == "Different", "Changed descendants propagate to folder");
                Check(rows.Single(x => x.Key == "empty").Status == "Identical", "Empty collections match");
                Check(rows.Single(x => x.Key == "folder/a.sql").Status == "Identical", "Relative path matching is ordinal-ignore-case");
                Check(rows.Single(x => x.Key == "folder/b.sql").Status == "Different", "First duplicate relative path wins");
                var excluded = await result.ReadPageAsync(excluded: new HashSet<string> { "folder" }, ct: ct);
                Check(excluded.MatchingCount == 3, "Folder exclusion covers its descendants on all pages");
                var collapsed = await result.ReadPageAsync(collapsed: new HashSet<string> { "folder" }, ct: ct);
                Check(collapsed.MatchingCount == 4, "Collapsed folders retain the folder and hide descendants across pages");
                var choices = await result.ReadDocumentChoicesAsync(ct: ct);
                Check(choices.Complete && choices.Left.Count == 3 && choices.Right.Count == 3, "Compare With choices retain targets without content");
                Check(choices.Left.All(x => x.Document.Content.Length == 0), "Compare With choices never retain document text");
            }
            var nestedA = new[] { E("a", true, ""), E("a/b", true, ""), E("a/b/c", false, "old") };
            var nestedB = new[] { E("a", true, ""), E("a/b", true, ""), E("a/b/c", false, "new") };
            await using (var result = await ComparisonCollectionEngine.BuildAsync(Entries(nestedA), Entries(nestedB), limits, ct))
                Check((await All(result)).All(x => x.Status == "Different"), "Nested folder statuses propagate through every level");
            var interleaveA = new[] { E("a", true, ""), E("a.txt", false, "same"), E("a/file.sql", false, "old") };
            var interleaveB = new[] { E("a", true, ""), E("a.txt", false, "same"), E("a/file.sql", false, "new") };
            await using (var result = await ComparisonCollectionEngine.BuildAsync(Entries(interleaveA), Entries(interleaveB), limits, ct))
                Check((await All(result)).Single(x => x.Key == "a").Status == "Different", "Sibling punctuation cannot interrupt folder propagation");
            string[] many = Enumerable.Range(0, 1500).Select(i => JsonSerializer.Serialize(new { Id = i, Text = "row " + i })).ToArray();
            await using (var result = await ComparisonTableEngine.BuildAsync(Input(many), Input(many.Reverse().ToArray()), ["Id"],
                limits with { MergeFanIn = 2, PageSize = 128 }, ct))
            {
                Check(result.Count == many.Length && result.StatusCounts.GetValueOrDefault("Identical") == many.Length, "Multi-pass external merge retains every selected row");
                var page = await result.ReadPageAsync(1408, ct: ct);
                Check(page.Rows.Count == 92 && !page.HasNext && page.MatchingCount == 1500, "Late result pages retain total matching count");
                string csv = Path.Combine(root, "many.csv"); await result.ExportAsync(csv, ct: ct);
                Check(CsvGridParser.Parse(File.ReadAllText(csv)).Rows.Count == 1500, "Large export includes rows outside the viewport"); File.Delete(csv);
            }
            string[] caseValues = ["aBc", "ABC", "\u03c3\u03c2", "\u03a3\u03a3", "\U00010400", "\U00010428", "\ud800", "\ud800", "\u00df", "SS"];
            for (int i = 0; i < caseValues.Length; i += 2)
                Check((ComparisonTextHash.Digest(caseValues[i], new(IgnoreCase: true)) == ComparisonTextHash.Digest(caseValues[i + 1], new(IgnoreCase: true))) ==
                    caseValues[i].Equals(caseValues[i + 1], StringComparison.OrdinalIgnoreCase), "Ordinal case hash parity " + i);
            Check(ComparisonTextHash.Digest("a \t\r\nb\u2003c", new(true)) == ComparisonTextHash.Digest("abc", new(true)), "Whitespace hash matches legacy char.IsWhiteSpace removal");
            Check(ComparisonTextHash.Digest("\ud801 \udc28", new(true, true)) == ComparisonTextHash.Digest("\U00010400", new(true, true)),
                "Combined whitespace/case options preserve surrogate-pair folding");
            using (var scratch = new ComparisonScratch(limits))
            {
                string path = scratch.NewFile();
                var row = new ComparisonResultRow("\ud800", "Identical", "", "\udc00", "", Left: Target("\ud800"));
                using (var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write)) SpoolBinary.Append(stream, path, row, ComparisonCodecs.Result, scratch);
                using var read = File.OpenRead(path);
                Check(SpoolBinary.TryRead(read, ComparisonCodecs.Result, limits.MaximumRecordBytes, out var restored) && restored == row,
                    "Binary spill preserves unmatched UTF-16 code units and locators");
            }
            try { await ComparisonTableEngine.BuildAsync(Input(left), Input(right), ["Id"], limits with { MaximumDiskBytes = 1024 }, ct); throw new Exception("Disk quota was ignored."); }
            catch (ComparisonLimitException) { passed.Add("Disk quota rejects instead of publishing partial result"); }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await ComparisonTableEngine.BuildAsync(Input(left), Input(right), ["Id"], limits, cancellation.Token); throw new Exception("Canceled comparison published."); }
                catch (OperationCanceledException) { passed.Add("Canceled comparison publishes no partial result"); }
            }
            Check(!Directory.EnumerateFileSystemEntries(root).Any(), "All quota/cancellation/completion spools are cleaned");
            return passed;

            ComparisonCollectionEntry E(string path, bool folder, string digest) => new(path, folder, digest, Target(path));
            ComparisonTableInput Input(string[] rows) => new(headers, ["Id"], rows.Length, Rows(rows, ct));
            DatabaseMetadataSnapshot Snapshot(string id, string[] rows) => new()
            {
                SnapshotId = id,
                Tables = [new() { SchemaName = "dbo", TableName = "Test", HasFullData = true }],
                Columns = headers.Select((h, i) => new SqlColumn { SchemaName = "dbo", TableName = "Test", ColumnName = h, Ordinal = i }).ToList(),
                TableDataSets = [new() { SchemaName = "dbo", TableName = "Test", Rows = rows.Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToList() }]
            };
            void Check(bool yes, string name) { if (!yes) throw new InvalidOperationException("FAILED: " + name); passed.Add(name); }
            async Task<List<ComparisonResultRow>> All(ComparisonResultStore result)
            {
                var rows = new List<ComparisonResultRow>(); long offset = 0;
                while (true)
                {
                    var page = await result.ReadPageAsync(offset, ct: ct); rows.AddRange(page.Rows);
                    if (!page.HasNext) break; offset += page.Rows.Count;
                }
                return rows;
            }
        }
        finally
        {
            // No recursive delete: a leak remains visible as a test failure.
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }
    private static ComparisonResource TableResource(string id) => new(id, "Table Data", id,
        ComparisonResourceKind.TableData, "TableData", false, false, true, id, SnapshotId: id, TableSchemaName: "dbo", TableName: "Test");
    private static async IAsyncEnumerable<CaptureRow> Rows(IEnumerable<string> input, [EnumeratorCancellation] CancellationToken ct)
    {
        long ordinal = 0;
        foreach (string text in input)
        {
            ct.ThrowIfCancellationRequested(); using var json = JsonDocument.Parse(text);
            yield return new(ordinal++, json.RootElement.Clone(), text.Length * 2L + 128);
        }
        await Task.CompletedTask;
    }
    private static async IAsyncEnumerable<ComparisonCollectionEntry> Entries(IEnumerable<ComparisonCollectionEntry> input)
    { foreach (var entry in input) yield return entry; await Task.CompletedTask; }
}

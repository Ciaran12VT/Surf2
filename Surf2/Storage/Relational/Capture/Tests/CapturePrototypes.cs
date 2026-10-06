#if CAPTURE_PROTOTYPE
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
using System.IO;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Surf2.Storage.Relational.Capture;

internal static class CapturePrototypes
{
    private static int _checks;

    public static async Task Main(string[] args)
    {
        Codec();
        Fidelity();
        ImportColumns();
        BulkBatch();
        Query();
        Sql(args[0]);
        await SortAsync();
        await ExportAsync();
        Console.WriteLine($"PASS: {_checks} capture prototype assertions (no SQL connection or database mutations).");
    }

    private static JsonElement Json(string json, int maxDepth = 64)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
        return document.RootElement.Clone();
    }

    private static CaptureLayout Layout(params string[] names) => new(names.Select(name => new CaptureColumnDefinition(name)).ToArray());

    private static void Codec()
    {
        var limits = new CaptureLimits();
        limits.Validate();
        var layout = Layout("Name", "N", "Flag", "Null", "Empty", "Missing", "Obj", "Bin");
        var original = Json("""
{ "n":1234567890123456789012345678901234567890.12345678901234567890,
  "NAME":"\u0041\n\"\/\uD83D\uDE00", "Flag":true, "Null":null, "Empty":"",
  "Obj": { "x":1, "x":2, "X":3 }, "extra":[1, {"z":null}], "Bin":"AAEC/w==" }
""");
        var encoded = CaptureRowCodec.Encode(layout, original, limits);
        Check(encoded.ScalarTokens[3] == "null" && encoded.ScalarTokens[4] == "\"\"" && encoded.ScalarTokens[5] == null,
            "explicit null, empty and missing have distinct storage");
        Check(encoded.Exceptions.Length == 2, "only structured/undeclared values use exceptions");
        Same(original, CaptureRowCodec.Decode(layout, encoded));
        foreach (string numeric in new[] { "-0", "1.2300", "1E+0003", "1e9999", "-1.234567890123456789012345678901234567890e-999" })
        {
            var row = Json("{\"N\":" + numeric + "}");
            var decoded = CaptureRowCodec.Decode(layout, CaptureRowCodec.Encode(layout, row, limits));
            Check(decoded.GetProperty("N").GetRawText() == numeric, "numeric raw spelling is unchanged");
            Check(CaptureDisplay.Cell(decoded, "n") == numeric, "numeric display matches raw spelling");
        }
        foreach (string root in new[] { "null", "true", "false", "-0", "\"\u0041\"", "[1, 2, {\"x\":1}]", "{}" })
            Same(Json(root), CaptureRowCodec.Decode(layout, CaptureRowCodec.Encode(layout, Json(root), limits)));
        Same(Json("{\"N\":\"1\",\"Empty\":\"\",\"Name\":\"a\\r\\nb\"}"),
            CaptureRowCodec.Decode(layout, CaptureRowCodec.Encode(layout, Json("{\"N\":\"1\",\"Empty\":\"\",\"Name\":\"a\\r\\nb\"}"), limits)));
        foreach (string duplicated in new[] { "{\"N\":1,\"N\":2}", "{\"N\":1,\"n\":2}",
            "{\"N\":null,\"n\":\"\",\"N\":{\"x\":1}}", "{\"unknown\":1,\"UNKNOWN\":2,\"unknown\":null}" })
        {
            var source = Json(duplicated);
            Same(source, CaptureRowCodec.Decode(layout, CaptureRowCodec.Encode(layout, source, limits)));
        }
        var duplicate = CaptureRowCodec.Encode(layout, Json("{\"N\":1,\"n\":2}"), limits);
        Check(duplicate.ScalarTokens[1] == "1" && duplicate.Exceptions.Length == 1 && duplicate.Exceptions[0].PropertyOrdinal == 1,
            "first property is physical; case-colliding later property is a bounded exception");
        Check(CaptureDisplay.Cell(CaptureRowCodec.Decode(layout, duplicate), "n") == "1", "duplicate display lookup remains first-match");
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json("{\"N\":1,\"n\":2,\"N\":3}"), limits with { MaxExceptionsPerRow = 1 }));
        string tooManyDuplicates = "{" + string.Join(",", Enumerable.Range(0, 34).Select(i => "\"N\":" + i)) + "}";
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json(tooManyDuplicates), limits));
        var excessiveProperties = Json("{" + string.Join(",", Enumerable.Range(0, 161).Select(i => "\"N\":" + i)) + "}");
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, excessiveProperties, limits));
        Throws<CaptureLimitException>(() => CaptureColumns.FromFirstRow(excessiveProperties));
        var maximumDuplicates = Json("{" + string.Join(",", Enumerable.Range(0, 33).Select(i => "\"N\":" + i)) + "}");
        var maximumEncoded = CaptureRowCodec.Encode(layout, maximumDuplicates, limits);
        Check(maximumEncoded.Exceptions.Length == 32, "32 exceptional duplicates are supported at the exact boundary");
        Same(maximumDuplicates, CaptureRowCodec.Decode(layout, maximumEncoded));
        Throws<InvalidDataException>(() => Layout("A", "a"));
        Throws<InvalidDataException>(() => CaptureRowCodec.Encode(layout, default, limits));
        Throws<CaptureLimitException>(() => new CaptureLayout(Enumerable.Range(0, 129).Select(i => new CaptureColumnDefinition("c" + i)).ToArray()));
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json("{\"Name\":\"123456789\"}"), limits with { MaxRowUtf8Bytes = 5 }));
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json("\"123456789\""), limits with { MaxTokenUtf8Bytes = 5 }));
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json("{\"x\":{},\"y\":[]}"), limits with { MaxExceptionsPerRow = 1 }));
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json("{\"x\":[123456789]}"), limits with { MaxExceptionUtf8BytesPerRow = 5 }));
        string tooMany = "{" + string.Join(",", Enumerable.Range(0, 33).Select(i => "\"extra" + i + "\":0")) + "}";
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(layout, Json(tooMany), limits));
        string deep = "{\"Obj\":" + new string('[', 65) + "0" + new string(']', 65) + "}";
        Throws<InvalidDataException>(() => CaptureRowCodec.Encode(layout, Json(deep, 128), limits));
        Same(Json("{\"x\":1}"), CaptureRowCodec.Decode(Layout(), CaptureRowCodec.Encode(Layout(), Json("{\"x\":1}"), limits)));
        var inferred = CaptureColumns.FromFirstRow(Json("{\"b\":0,\"A\":1}"));
        Check(inferred.Select(c => c.SourceName).SequenceEqual(new[] { "b", "A" }), "first-row header inference preserves order/case");
        var inferredLayout = new CaptureLayout(inferred);
        Check(CaptureRowCodec.Encode(inferredLayout, Json("{\"b\":0,\"A\":1}"), limits).Exceptions.Length == 0, "inferred scalars are real columns, not EAV");
        Check(CaptureColumns.FromFirstRow(Json("{\"x\":1,\"X\":2,\"x\":3,\"y\":4}"))
            .Select(c => c.SourceName).SequenceEqual(new[] { "x", "y" }), "first-row inference keeps first-seen unique physical names and preserves duplicate row values separately");
        var inferredDuplicate = Json("{\"x\":1,\"X\":2,\"x\":3,\"y\":4}");
        var duplicateLayout = new CaptureLayout(CaptureColumns.FromFirstRow(inferredDuplicate));
        Same(inferredDuplicate, CaptureRowCodec.Decode(duplicateLayout, CaptureRowCodec.Encode(duplicateLayout, inferredDuplicate, limits)));
        var names = Enumerable.Range(0, 20).Select(i => "n" + i + new string('a', 3990)).ToArray();
        var wideNames = Layout(names);
        var namedRow = Json("{" + string.Join(",", names.Select(name => JsonSerializer.Serialize(name.ToUpperInvariant()) + ":0")) + "}");
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(wideNames, namedRow, limits));
        byte[] corrupt = encoded.PropertyOrder.ToArray();
        corrupt[0] = 99;
        Throws<InvalidDataException>(() => CaptureRowCodec.Decode(layout, encoded with { PropertyOrder = corrupt }));
        Check(CaptureDisplay.Value(Json("true")) == "True" && CaptureDisplay.Value(Json("false")) == "False", "boolean display casing");
        Check(CaptureDisplay.Value(Json("null")) == "" && CaptureDisplay.Cell(Json("{}"), "missing") == "", "empty display for null/missing");
        Check(CaptureDisplay.Cell(Json("{\"A\":\"first\",\"a\":\"second\"}"), "a") == "first", "formatter preserves legacy first-match lookup");
        Check(CaptureDisplay.EscapeDelimited("a,\"b\"\r\nc", ',') == "\"a,\"\"b\"\"\r\nc\"", "CSV multiline/quote escaping");
        Check(CaptureDisplay.EscapeDelimited("a\tb", '\t') == "\"a\tb\"", "clipboard tab escaping");
        var same = new CaptureLayout(layout.Columns);
        Check(layout.Hash.SequenceEqual(same.Hash), "layout hash deterministic");
        Check(!layout.Hash.SequenceEqual(Layout("name", "N", "Flag", "Null", "Empty", "Missing", "Obj", "Bin").Hash), "layout hash retains original spelling");
        var metadata = new CaptureColumnDefinition("n", "decimal", -1, 255, int.MinValue, false, int.MaxValue, true);
        var metadataLayout = new CaptureLayout([metadata]);
        Check(metadataLayout.Columns[0] == metadata, "scale, ordinal, identity and unusual metadata values are not narrowed or normalized");
        Check(!metadataLayout.Hash.SequenceEqual(new CaptureLayout([metadata with { SourceScale = 0 }]).Hash), "layout hash binds complete source metadata");
        Throws<ArgumentOutOfRangeException>(() => (limits with { ReadBatchBytes = CaptureLimits.MaximumBufferBytes + 1 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (limits with { WriteBatchBytes = 32 * 1024 }).Validate());
        (limits with { WriteBatchBytes = 128 * 1024, ReadBatchBytes = 32 * 1024, MaxPageBytes = 32 * 1024 }).Validate();
        Check(CaptureLimits.GetMinimumWriteBatchBytes(3) == 64 * 1024 &&
            CaptureLimits.GetMinimumWriteBatchBytes(128) > 128 * 1024, "write-only global minimum and wide-layout minimum include real generated column counts");
        var widest = Layout(Enumerable.Range(0, 128).Select(i => "c" + i).ToArray());
        var minimalWideRow = CaptureRowCodec.Encode(widest, Json("{}"), limits);
        long wideMinimum = CaptureLimits.GetMinimumWriteBatchBytes(128);
        using var minimalWideBatch = new CaptureBulkBatch(new(Guid.NewGuid(), 1, 1), widest, [minimalWideRow], 0,
            limits with { WriteBatchBytes = wideMinimum });
        Check(minimalWideBatch.Rows.Columns.Count == 134 && minimalWideBatch.EndOrdinal == 1,
            "published wide-layout minimum accepts exactly one minimal row with all generated bulk columns");
        Throws<CaptureLimitException>(() => new CaptureBulkBatch(new(Guid.NewGuid(), 1, 1), widest, [minimalWideRow], 0,
            limits with { WriteBatchBytes = wideMinimum - 1 }));
        Throws<ArgumentOutOfRangeException>(() => CaptureLimits.GetMinimumWriteBatchBytes(-1));
        Throws<ArgumentOutOfRangeException>(() => CaptureLimits.GetMinimumWriteBatchBytes(129));
        var exceptionalRoot = CaptureRowCodec.Encode(layout, Json("null"), limits);
        string?[] rootTokens = exceptionalRoot.ScalarTokens.ToArray();
        rootTokens[0] = "1";
        Throws<InvalidDataException>(() => CaptureRowCodec.Decode(layout, exceptionalRoot with { ScalarTokens = rootTokens }));
        var unknown = CaptureRowCodec.Encode(layout, Json("{\"extra\":1}"), limits);
        Throws<InvalidDataException>(() => CaptureRowCodec.Decode(layout, unknown with { PropertyOrder = Descriptor((-1, "N")) }));
        Throws<InvalidDataException>(() => CaptureRowCodec.Decode(layout, duplicate with { PropertyOrder = Descriptor((1, null), (1, "n")) }));
        Throws<InvalidDataException>(() => CaptureRowCodec.Decode(layout, duplicate with { PropertyOrder = Descriptor((1, null), (-1, "Name")) }));
        Check(CaptureDisplay.Cell(CaptureRowCodec.Decode(layout,
            CaptureRowCodec.Encode(layout, Json("{\"N\":null,\"n\":\"later\"}"), limits)), "n") == "", "first null duplicate retains first-match empty display");
    }

    private static byte[] Descriptor(params (int Column, string? Name)[] properties)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UnicodeEncoding(false, false, true), leaveOpen: true);
        writer.Write(1);
        writer.Write(properties.Length);
        foreach (var property in properties)
        {
            writer.Write(property.Column);
            writer.Write(property.Name != null);
            if (property.Name != null) writer.Write(property.Name);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void ImportColumns()
    {
        CaptureColumnDefinition[] metadata = [new("Last", "varchar", SourceOrdinal: 8),
            new("Second", "decimal", SourceScale: -1, SourceOrdinal: 2, SourceIdentity: true),
            new("First", "bit", SourceOrdinal: 1), new("Tie", SourceOrdinal: 2), new("UnknownOrdinal")];
        Check(CaptureImportColumns.Select([], metadata).Select(column => column.SourceName)
            .SequenceEqual(new[] { "First", "Second", "Tie", "Last", "UnknownOrdinal" }), "supplied source columns use ordinal order and stable input ties");
        var explicitHeader = CaptureImportColumns.Select(["Last", "FIRST", "New", "second"], metadata);
        Check(explicitHeader.Select(column => column.SourceName).SequenceEqual(new[] { "Last", "FIRST", "New", "second" }), "explicit dataset headers preserve their own order/case");
        Check(explicitHeader[1].SourceDataType == "bit" && explicitHeader[2] == new CaptureColumnDefinition("New") &&
            explicitHeader[3].SourceScale == -1 && explicitHeader[3].SourceIdentity == true, "exact/case-safe header metadata merging preserves all flags");
        Check(CaptureImportColumns.Select([], []).Length == 0, "no metadata leaves first-row inference to the stream");
        CaptureColumnDefinition[] collisions = [new("Ab", "first"), new("aB", "second")];
        Check(CaptureImportColumns.Select(["Ab"], collisions)[0].SourceDataType == "first", "exact source name resolves a case-safe metadata match");
        Throws<InvalidDataException>(() => CaptureImportColumns.Select(["AB"], collisions));
        Throws<InvalidDataException>(() => CaptureImportColumns.Select([], collisions));
        Throws<InvalidDataException>(() => CaptureImportColumns.Select(["A", "a"], []));
        Throws<InvalidDataException>(() => CaptureImportColumns.Select([], [new("A"), new("A")]));
        var first = Json("{\"a\":1,\"A\":2}");
        var firstLayout = new CaptureLayout(CaptureColumns.FromFirstRow(first));
        var later = Json("{\"B\":3,\"a\":4,\"b\":5}");
        var laterEncoded = CaptureRowCodec.Encode(firstLayout, later, new());
        Check(firstLayout.Columns.Select(column => column.SourceName).SequenceEqual(new[] { "a" }) && laterEncoded.Exceptions.Length == 2,
            "later unknown properties are exceptions, never implicitly unioned into visible headers");
        Same(later, CaptureRowCodec.Decode(firstLayout, laterEncoded));
        var emptyFirstLayout = new CaptureLayout(CaptureColumns.FromFirstRow(Json("{}")));
        var fieldsAfterEmpty = Json("{\"Later\":1,\"later\":2}");
        var fieldsEncoded = CaptureRowCodec.Encode(emptyFirstLayout, fieldsAfterEmpty, new());
        Check(emptyFirstLayout.Columns.Length == 0 && fieldsEncoded.Exceptions.Length == 2, "empty first-row headers remain empty and later fields stay bounded exceptions");
        Same(fieldsAfterEmpty, CaptureRowCodec.Decode(emptyFirstLayout, fieldsEncoded));
        var tooManyFields = Json("{" + string.Join(",", Enumerable.Range(0, 33).Select(i => "\"later" + i + "\":1")) + "}");
        Throws<CaptureLimitException>(() => CaptureRowCodec.Encode(emptyFirstLayout, tooManyFields, new()));
    }

    private static void BulkBatch()
    {
        var limits = new CaptureLimits();
        var handle = new CaptureWriteHandle(Guid.NewGuid(), 41, 42);
        var layout = Layout("Name", "N", "Missing");
        JsonElement[] source = [Json("{\"Name\":\"\",\"N\":null}"),
            Json("{\"N\":1.2300,\"n\":2,\"Name\":\"\\u0041\",\"unknown\":[1, 2]}"), Json("null")];
        var encoded = source.Select(row => CaptureRowCodec.Encode(layout, row, limits)).ToArray();
        using var batch = new CaptureBulkBatch(handle, layout, encoded, 7, limits);
        Check(batch.EndOrdinal == 10 && batch.Rows.Rows.Count == 3 && batch.Exceptions.Rows.Count == 3, "bulk batch retains row/exception counts and contiguous ordinals");
        Check(batch.Rows.Columns.Cast<DataColumn>().Select(column => column.ColumnName)
            .SequenceEqual(new[] { "DataSetKey", "LayoutKey", "RowOrdinal", "RowKind", "EstimatedBytes", "PropertyOrder", "C0001", "C0002", "C0003" }), "bulk mappings contain only fixed/generated identifiers");
        Check(batch.Rows.Columns["C0002"]!.DataType == typeof(string) && batch.Rows.Columns["PropertyOrder"]!.DataType == typeof(byte[]) &&
            batch.Rows.Columns["RowKind"]!.DataType == typeof(byte) && batch.Exceptions.Columns["PropertyOrdinal"]!.DataType == typeof(int), "bulk types match the SQL schema without scalar coercion");
        Check((string)batch.Rows.Rows[0]["C0002"] == "null" && batch.Rows.Rows[0].IsNull("C0003") &&
            (string)batch.Rows.Rows[0]["C0001"] == "\"\"", "bulk table distinguishes explicit JSON null, missing SQL null and empty-string token");
        Check((string)batch.Rows.Rows[1]["C0002"] == "1.2300" && (string)batch.Rows.Rows[1]["C0001"] == "\"\\u0041\"", "bulk tables retain raw number and string token spellings");
        for (int i = 0; i < source.Length; i++)
        {
            DataRow row = batch.Rows.Rows[i];
            long ordinal = row.Field<long>("RowOrdinal");
            Check(row.Field<long>("DataSetKey") == 41 && row.Field<long>("LayoutKey") == 42 && ordinal == 7 + i, "bulk owner keys and row ordinals are frozen");
            var scalar = Enumerable.Range(0, layout.Columns.Length).Select(column => row.Field<string?>(CaptureLayout.ColumnName(column))).ToArray();
            var exceptions = batch.Exceptions.Rows.Cast<DataRow>().Where(value => value.Field<long>("RowOrdinal") == ordinal)
                .Select(value => new CaptureValueException(value.Field<int>("PropertyOrdinal"), (JsonValueKind)value.Field<byte>("ValueKind"), value.Field<string>("RawToken")!)).ToArray();
            var decoded = CaptureRowCodec.Decode(layout, new((JsonValueKind)row.Field<byte>("RowKind"), row.Field<byte[]>("PropertyOrder")!,
                scalar, exceptions, row.Field<long>("EstimatedBytes")));
            Same(source[i], decoded);
            Check(CaptureRowFidelity.Matches(source[i], decoded), "bulk-shaped data passes migration row-fidelity comparison");
        }
        Throws<CaptureLimitException>(() => new CaptureBulkBatch(handle, layout, encoded, 7, limits with { WriteBatchRows = 2 }));
        long bytes = encoded.Sum(row => row.EstimatedBytes) + CaptureBulkBatch.OverheadBytes(layout);
        Throws<CaptureLimitException>(() => new CaptureBulkBatch(handle, layout, encoded, 7, limits with { WriteBatchBytes = bytes - 1 }));
        using var exact = new CaptureBulkBatch(handle, layout, encoded, 7, limits with { WriteBatchBytes = bytes, WriteBatchRows = 3 });
        Check(exact.EndOrdinal == 10, "bulk copy preparation accepts exact row and byte boundaries");
        Throws<CaptureLimitException>(() => new CaptureBulkBatch(handle, layout, [], 0, limits));
        Throws<OverflowException>(() => new CaptureBulkBatch(handle, layout, encoded, long.MaxValue, limits));
        Throws<InvalidDataException>(() => new CaptureBulkBatch(handle, layout, [encoded[0] with { ScalarTokens = [] }], 0, limits));
        Throws<InvalidDataException>(() => new CaptureBulkBatch(handle, layout, encoded, 0, limits with { MaxExceptionsPerRow = 1 }));
    }

    private static void Fidelity()
    {
        var layout = Layout("a", "b", "missing", "obj");
        var source = Json("{ \"a\":1.2300, \"b\":\"\\u0041\", \"obj\":{\"x\":1, \"X\":2}, \"A\":null }");
        var stored = CaptureRowCodec.Decode(layout, CaptureRowCodec.Encode(layout, source, new()));
        Check(CaptureRowFidelity.Matches(source, stored), "decoded row fidelity accepts only containing-row formatting changes");
        foreach (string mismatch in new[] { "{\"a\":1.23,\"b\":\"\\u0041\",\"obj\":{\"x\":1, \"X\":2},\"A\":null}",
            "{\"a\":1.2300,\"b\":\"A\",\"obj\":{\"x\":1, \"X\":2},\"A\":null}",
            "{\"b\":\"\\u0041\",\"a\":1.2300,\"obj\":{\"x\":1, \"X\":2},\"A\":null}",
            "{\"a\":1.2300,\"b\":\"\\u0041\",\"obj\":{\"x\":1, \"X\":2}}",
            "{\"a\":1.2300,\"b\":\"\\u0041\",\"obj\":{\"x\":1, \"X\":2},\"A\":\"\"}",
            "{\"a\":1.2300,\"b\":\"\\u0041\",\"obj\":{\"x\":1, \"X\":2},\"a\":null}",
            "{\"a\":1.2300,\"b\":\"\\u0041\",\"obj\":{\"x\":1, \"X\":2},\"A\":null,\"missing\":null}" })
            Check(!CaptureRowFidelity.Matches(source, Json(mismatch)), "fidelity rejects number/string raw forms, order, case, duplicate, missing/null/empty changes");
        Check(!CaptureRowFidelity.Matches(Json("{\"x\":1}"), Json("{\"x\":\"1\"}")), "fidelity rejects value-kind changes");
        Check(!CaptureRowFidelity.Matches(Json("{\"x\":1,\"x\":2}"), Json("{\"x\":1,\"x\":3}")), "fidelity compares every duplicated token");
        Check(!CaptureRowFidelity.Matches(Json("{\"x\":1,\"X\":2}"), Json("{\"X\":2,\"x\":1}")), "fidelity compares duplicate property order/case");
        Check(CaptureRowFidelity.Matches(Json("[1, 2]"), Json("[1, 2]")) &&
            !CaptureRowFidelity.Matches(Json("[1, 2]"), Json("[1,2]")), "exceptional roots retain exact raw tokens");
        Check(!CaptureRowFidelity.Matches(Json("null"), Json("\"\"")) && !CaptureRowFidelity.Matches(default, default), "null/empty/undefined fidelity is explicit");
    }

    private static void Query()
    {
        var columns = Layout("A", "B", "C").Columns;
        var row = Json("{\"a\":\"%_[ literal\",\"B\":\"Stra\u00dfe\",\"C\":\"YES\"}");
        var and = new FrozenCaptureQuery(new(Filters: [new(0, "%_["), new(2, "yes")]), 3);
        Check(and.Matches(row, columns), "AND matches display strings and literal SQL wildcard characters");
        Check(!new FrozenCaptureQuery(new(Filters: [new(0, "%_["), new(1, "not present")]), 3).Matches(row, columns), "AND failure");
        var any = new FrozenCaptureQuery(new(Filters: [new(0, "not present"), new(1, "not present"), new(2, "yes")], AnyMatchColumnOrdinals: [1, 2]), 3);
        Check(any.Matches(row, columns), "selected OR group ignores unrelated AND filters like the legacy grid");
        Check(!new FrozenCaptureQuery(new(Filters: [new(0, "%_["), new(1, "no")], AnyMatchColumnOrdinals: [1]), 3).Matches(row, columns), "active OR has precedence over otherwise matching AND");
        Check(!new FrozenCaptureQuery(new(Filters: [new(0, "no"), new(1, " ")], AnyMatchColumnOrdinals: [1]), 3).Matches(row, columns), "inactive OR falls back to AND");
        foreach (string search in new[] { "STRASSE", "\u0130", "\u0131", "\u212a", "SS", "%", "[" })
            Check(new FrozenCaptureQuery(new(Filters: [new(1, search)]), 3).Matches(row, columns) ==
                CaptureDisplay.Cell(row, "B").Contains(search, StringComparison.OrdinalIgnoreCase), "Unicode filter agrees with .NET, not SQL collation");
        Throws<ArgumentOutOfRangeException>(() => new FrozenCaptureQuery(new(Filters: [new(3, "x")]), 3));
        Throws<ArgumentException>(() => new FrozenCaptureQuery(new(Filters: [new(0, "x"), new(0, "y")]), 3));
        Throws<ArgumentException>(() => new FrozenCaptureQuery(new(Sorts: [new(0), new(0)]), 3));
        var filters = new List<CaptureFilter> { new(0, "x") };
        var sorts = new List<CaptureSort> { new(0) };
        var snapshot = FrozenCaptureQuery.Snapshot(new(filters, Sorts: sorts));
        filters.Clear();
        sorts.Clear();
        Check(snapshot.Filters!.Count == 1 && snapshot.Sorts!.Count == 1, "operation snapshots do not share caller-owned arrays");
        Check(and.Fingerprint != any.Fingerprint, "cursor fingerprint binds filtering semantics");
        Check(new FrozenCaptureQuery(new(Filters: [new(0, "\ud800")]), 3).Fingerprint !=
            new FrozenCaptureQuery(new(Filters: [new(0, "\ud801")]), 3).Fingerprint, "query fingerprint does not replace or alias unpaired UTF-16 code units");
        var q = new FrozenCaptureQuery(new(Sorts: [new(0)], SortCultureName: "en-US"), 3);
        Check(q.Compare(["equal"], 2, ["equal"], 3) < 0, "RowOrdinal is final stable tie-breaker");
        var descending = new FrozenCaptureQuery(new(Sorts: [new(0, true)], SortCultureName: "en-US"), 3);
        Check(descending.Compare(["equal"], 2, ["equal"], 3) < 0, "descending sort does not reverse stable row ties");
    }

    private static void Sql(string schemaPath)
    {
        var sourceNames = Enumerable.Range(0, 128).Select(i => new CaptureColumnDefinition(i == 0 ? "x]; DROP TABLE surf.DataSet;--" : "c" + i, "dbo.[unsafe type];--")).ToArray();
        var layout = new CaptureLayout(sourceNames);
        string ddl = layout.CreateTableSql(long.MaxValue);
        Check(!ddl.Contains("unsafe", StringComparison.Ordinal) && !ddl.Contains("DROP TABLE", StringComparison.Ordinal), "source names/types never become executable SQL");
        Check(ddl.Contains("C0128", StringComparison.Ordinal), "128-column boundary is supported");
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var tree = parser.Parse(new StringReader(ddl), out var errors);
        Check(errors.Count == 0, "generated DDL parses: " + string.Join(";", errors.Select(e => e.Message)));
        var visitor = new TableVisitor();
        tree.Accept(visitor);
        Check(visitor.Columns == 134 && visitor.Columns < 1024, "physical column count leaves SQL headroom");
        Check(6 + layout.Columns.Length < 2100, "one-row writes are below SQL parameter limit");
        Check(128 * 24 + 24 + 6 * 8 + 134 * 2 + 64 < 8060, "conservative off-row root/offset budget below 8060 bytes");
        string schema = File.ReadAllText(schemaPath);
        parser.Parse(new StringReader(schema), out errors);
        Check(errors.Count == 0, "Capture schema parses: " + string.Join(";", errors.Select(e => e.Message)));
        Check(!schema.Split('\n').Any(line => line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)), "schema is one executable batch without GO");
        Check(!schema.Contains("CREATE TABLE surf.SnapshotResourceRevision", StringComparison.OrdinalIgnoreCase), "snapshot revision is only referenced, never redefined");
        Throws<ArgumentOutOfRangeException>(() => CaptureLayout.TableName(0));
        Throws<ArgumentOutOfRangeException>(() => CaptureLayout.ColumnName(128));
    }

    private sealed class TableVisitor : TSqlFragmentVisitor
    {
        internal int Columns;
        public override void ExplicitVisit(CreateTableStatement node) => Columns += node.Definition.ColumnDefinitions.Count;
    }

    private static async Task SortAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Surf2-capture-prototype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sentinel = Path.Combine(directory, "keep.txt");
        File.WriteAllText(sentinel, "owned prototype sentinel");
        try
        {
            var columns = Layout("A", "B").Columns;
            var query = new FrozenCaptureQuery(new(Sorts: [new(0), new(1, true)], SortCultureName: "en-US"), 2);
            var rows = Enumerable.Range(0, 101).Select(i => new CaptureRow(i,
                Json(JsonSerializer.Serialize(new { A = new[] { "z", "a", "A", "\u00e4", "", "10", "2" }[i % 7], B = i % 5 })), 8192)).ToArray();
            var expected = rows.OrderBy(row => row, Comparer<CaptureRow>.Create((a, b) =>
                query.Compare(query.SortKeys(a, columns), a.RowOrdinal, query.SortKeys(b, columns), b.RowOrdinal))).Select(r => r.RowOrdinal).ToArray();
            var limits = new CaptureLimits { StagingDirectory = directory, SortRunRows = 3, SortRunBytes = 32768, MergeFanIn = 2 };
            var actual = new List<long>();
            await foreach (var row in CaptureExternalSort.SortAsync(Stream(rows), query, columns, limits, CancellationToken.None))
                actual.Add(row.RowOrdinal);
            Check(actual.SequenceEqual(expected), "multi-pass external sort exactly matches .NET culture/multi-sort/stable ties");
            Check(Directory.GetDirectories(directory).Length == 0 && File.Exists(sentinel), "sort success removes only operation-owned files");
            await foreach (var row in CaptureExternalSort.SortAsync(Stream(rows), query, columns, limits, CancellationToken.None)) break;
            Check(Directory.GetDirectories(directory).Length == 0, "early stream exit cleans spool");
            await ThrowsAsync<CaptureLimitException>(async () =>
            {
                await foreach (var row in CaptureExternalSort.SortAsync(Stream(rows), query, columns, limits with { MaxSpoolBytes = 50 }, CancellationToken.None)) { }
            });
            Check(Directory.GetDirectories(directory).Length == 0 && File.Exists(sentinel), "quota failure preserves unrelated files");
            await ThrowsAsync<CaptureLimitException>(async () =>
            {
                await foreach (var row in CaptureExternalSort.SortAsync(Stream(rows), query, columns, limits with { MaxSortRuns = 2 }, CancellationToken.None)) { }
            });
            await ThrowsAsync<CaptureLimitException>(async () =>
            {
                await foreach (var row in CaptureExternalSort.SortAsync(Stream(rows), query, columns, limits with { SortRunBytes = 1024 }, CancellationToken.None)) { }
            });
            using var cts = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var row in CaptureExternalSort.SortAsync(Stream(rows, cts), query, columns, limits, cts.Token)) { }
            });
            Check(Directory.GetDirectories(directory).Length == 0 && File.Exists(sentinel), "cancelled sort removes only generated spool");
            int emptyCount = 0;
            await foreach (var row in CaptureExternalSort.SortAsync(Stream([]), query, columns, limits, CancellationToken.None)) emptyCount++;
            Check(emptyCount == 0 && Directory.GetDirectories(directory).Length == 0, "empty stream is bounded and leaves no spool");
        }
        finally
        {
            File.Delete(sentinel);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static async IAsyncEnumerable<CaptureRow> Stream(CaptureRow[] rows, CancellationTokenSource? cancel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
            if (row.RowOrdinal == 10 && cancel != null) cancel.Cancel();
            await Task.Yield();
        }
    }

    private static async Task ExportAsync()
    {
        var columns = Layout("Text", "N", "Flag", "Empty").Columns;
        CaptureRow[] rows = [new(0, Json("{\"Text\":\"a,b\\r\\n\\\"c\\\"\",\"N\":1.2300,\"Flag\":true,\"Empty\":null}"), 8192),
            new(1, Json("{\"Text\":\"x\\ty\",\"N\":\"2\",\"Flag\":false,\"Empty\":\"\"}"), 8192)];
        using var destination = new MemoryStream();
        long count = await CaptureDelimitedWriter.WriteAsync(Stream(rows), destination, columns, [1, 0, 2, 3], ',', CancellationToken.None);
        byte[] bytes = destination.ToArray();
        Check(count == 2 && destination.CanWrite, "export streams all rows and leaves the caller's stream open");
        Check(!bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "UTF-8 export has no BOM");
        string newline = Environment.NewLine;
        Check(Encoding.UTF8.GetString(bytes) == "N,Text,Flag,Empty" + newline +
            "1.2300,\"a,b\r\n\"\"c\"\"\",True," + newline + "2,x\ty,False," + newline,
            "export preserves projected column order, raw numeric display, booleans, null/empty and CSV quoting");
        using var tabs = new MemoryStream();
        await CaptureDelimitedWriter.WriteAsync(Stream(rows), tabs, columns, [0], '\t', CancellationToken.None);
        Check(Encoding.UTF8.GetString(tabs.ToArray()).Contains("\"x\ty\"", StringComparison.Ordinal), "tab export uses Excel-compatible quoting");

        CaptureRow[] mixedRows = [new(0, Json("null"), 8192), new(1, Json("1.2300"), 8192),
            rows[0] with { RowOrdinal = 2 }, new(3, Json("\"text\""), 8192), new(4, Json("true"), 8192),
            new(5, Json("false"), 8192), new(6, Json("[1,null]"), 8192), rows[1] with { RowOrdinal = 7 },
            new(8, Json("{}"), 8192)];
        using var legacyExport = new MemoryStream();
        long legacyCount = await CaptureDelimitedWriter.WriteAsync(Stream(mixedRows), legacyExport, columns,
            [1, 0, 2, 3], ',', CancellationToken.None);
        Check(legacyCount == 3, "legacy display export counts only objects, including empty objects");
        Check(Encoding.UTF8.GetString(legacyExport.ToArray()) == Encoding.UTF8.GetString(bytes) + ",,," + newline,
            "legacy display export skips leading/interleaved null, scalar and array rows without blank records");
        using var legacyTabs = new MemoryStream();
        long tabCount = await CaptureDelimitedWriter.WriteAsync(Stream(mixedRows), legacyTabs, columns,
            [0], '\t', CancellationToken.None);
        Check(tabCount == 3 && Encoding.UTF8.GetString(legacyTabs.ToArray()) == Encoding.UTF8.GetString(tabs.ToArray()) + newline,
            "tab display export also skips non-object rows by default");

        CaptureRow[] nonObjects = mixedRows.Where(row => row.Value.ValueKind != JsonValueKind.Object).ToArray();
        using var headerOnly = new MemoryStream();
        long headerCount = await CaptureDelimitedWriter.WriteAsync(Stream(nonObjects), headerOnly, columns,
            [0], ',', CancellationToken.None);
        Check(headerCount == 0 && Encoding.UTF8.GetString(headerOnly.ToArray()) == "Text" + newline,
            "all-non-object legacy export emits only the declared header");
        using var zeroColumns = new MemoryStream();
        long zeroColumnCount = await CaptureDelimitedWriter.WriteAsync(Stream(mixedRows), zeroColumns, [],
            [], ',', CancellationToken.None);
        Check(zeroColumnCount == 3 && Encoding.UTF8.GetString(zeroColumns.ToArray()) == string.Concat(Enumerable.Repeat(newline, 4)),
            "zero-column export emits a header and one empty record per object, not per source row");
        using var includeNonObjects = new MemoryStream();
        long inclusiveCount = await CaptureDelimitedWriter.WriteAsync(Stream(nonObjects), includeNonObjects, columns,
            [0], ',', CancellationToken.None, skipNonObjectRows: false);
        Check(inclusiveCount == nonObjects.Length && Encoding.UTF8.GetString(includeNonObjects.ToArray()) ==
            "Text" + newline + string.Concat(Enumerable.Repeat(newline, nonObjects.Length)),
            "explicit display opt-out retains the previous blank-record behavior");
        var retainedRows = new List<CaptureRow>();
        await foreach (var row in Stream(mixedRows)) retainedRows.Add(row);
        Check(retainedRows.Count == mixedRows.Length && retainedRows.Select(row => row.RowOrdinal).SequenceEqual(Enumerable.Range(0, mixedRows.Length).Select(i => (long)i)),
            "display skipping leaves the raw source stream and original row ordinals intact");
        Check(mixedRows[0].Value.ValueKind == JsonValueKind.Null && mixedRows[1].Value.GetRawText() == "1.2300",
            "display export does not replace or normalize non-object source values");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelled = new MemoryStream();
        await ThrowsAsync<OperationCanceledException>(() => CaptureDelimitedWriter.WriteAsync(Stream(rows), cancelled, columns, [0], ',', cancellation.Token));
        Check(cancelled.CanWrite, "export cancellation leaves caller-owned destination open");
    }

    private static void Same(JsonElement original, JsonElement actual)
    {
        Check(original.ValueKind == actual.ValueKind, "root kind round-trip");
        if (original.ValueKind != JsonValueKind.Object) { Check(original.GetRawText() == actual.GetRawText(), "non-object raw token round-trip"); return; }
        var left = original.EnumerateObject().ToArray();
        var right = actual.EnumerateObject().ToArray();
        Check(left.Length == right.Length, "property count round-trip");
        for (int i = 0; i < left.Length; i++)
        {
            Check(left[i].Name == right[i].Name, "property order/spelling round-trip");
            Check(left[i].Value.GetRawText() == right[i].Value.GetRawText(), "property raw token round-trip");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _checks++;
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
#endif

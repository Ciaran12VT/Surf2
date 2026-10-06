#if PACKAGE_PROTOTYPE
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
using System.Data.SqlTypes;
using IsolationLevel = System.Data.IsolationLevel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Surf2.Storage.Relational.Packages;

internal static class PackagePrototypes
{
    private static int _checks;
    public static async Task Main(string[] args)
    {
        Scalars(); Paths(); Catalogue(args[0]); Hashes();
        await ChunksAsync();
        await ArchiveAsync();
        Console.WriteLine($"PASS: {_checks} package prototype assertions (no SQL connection or database writes).");
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
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
    private static JsonElement Scalar(string type, object value)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) PackageRowEncoding.WriteScalar(writer, type, value);
        using var json = JsonDocument.Parse(output.ToArray());
        return json.RootElement.Clone();
    }

    private static void Scalars()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (long value in new[] { long.MinValue, -1, 0, 1, long.MaxValue })
                Check(Scalar("bigint", value).GetString() == value.ToString(CultureInfo.InvariantCulture), "integer strings preserve exact range/culture");
            Check(Scalar("bit", true).GetBoolean() && !Scalar("bit", false).GetBoolean(), "bit is JSON boolean");
            foreach (string text in new[] { "99999999999999999999999999999999999999", "-99999999999999999999999999999999999999",
                "0.0000000000000000000000000000000000001", "-12345678901234567890.123456789012345678", "1.2300" })
            {
                SqlDecimal value = SqlDecimal.Parse(text);
                var json = Scalar("decimal", value);
                int[] words = json.GetProperty("words").EnumerateArray().Select(word => unchecked((int)uint.Parse(word.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToArray();
                var restored = new SqlDecimal(json.GetProperty("precision").GetByte(), json.GetProperty("scale").GetByte(), json.GetProperty("positive").GetBoolean(), words);
                Check(restored.Data.SequenceEqual(value.Data) && restored.Precision == value.Precision && restored.Scale == value.Scale && restored.IsPositive == value.IsPositive,
                    "SQL precision-38 decimal restores words/sign/scale without CLR decimal");
            }
            foreach (double value in new[] { -0d, 0d, double.Epsilon, double.MaxValue, -1.25 })
                Check(Scalar("float", value).GetString() == unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("X16", CultureInfo.InvariantCulture), "float64 bit pattern is exact");
            foreach (float value in new[] { -0f, 0f, float.Epsilon, float.MaxValue })
                Check(Scalar("real", value).GetString() == unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("X8", CultureInfo.InvariantCulture), "float32 bit pattern is exact");
            Check(PackageRowEncoding.ForType("float", 24) == "float32-bits-v1" && PackageRowEncoding.ForType("float", 53) == "float64-bits-v1", "float precision controls wire width");
            Check(Scalar("float", -0f).GetString() == "80000000", "float24 negative zero retains the correct width");
            Check(Scalar("money", new SqlMoney(-1234.25m)).GetString() == "-1234.2500", "money fixed four-digit invariant scale");
            var offset = new DateTimeOffset(2020, 2, 29, 23, 59, 59, TimeSpan.FromMinutes(345)).AddTicks(1234567);
            Check(DateTimeOffset.ParseExact(Scalar("datetimeoffset", offset).GetString()!, "O", CultureInfo.InvariantCulture).EqualsExact(offset), "offset and fractional ticks survive");
            Check(Scalar("datetime2", offset.DateTime).GetString() == "2020-02-29T23:59:59.1234567", "datetime has no invented timezone");
            Check(Scalar("date", offset.DateTime).GetString() == "2020-02-29", "SQL date has no time component");
            Check(Scalar("time", TimeSpan.FromTicks(123456789)).GetString() == "00:00:12.3456789", "SQL time retains ticks");
            Guid id = Guid.NewGuid();
            Check(Guid.Parse(Scalar("uniqueidentifier", id).GetString()!) == id, "GUID identity is exact");
        }
        finally { CultureInfo.CurrentCulture = old; }
        Throws<InvalidDataException>(() => PackageRowEncoding.ForType("sql_variant"));
        Throws<InvalidDataException>(() => PackageRowEncoding.ForType("xml"));
        new RelationalPackageLimits().Validate();
        Throws<ArgumentOutOfRangeException>(() => (new RelationalPackageLimits { ChunkBytes = 0 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (new RelationalPackageLimits { MaxCellBytes = long.MaxValue }).Validate());
        Check(!new RelationalPackageExportOptions().AllowBlockingConsistentFallback && !new RelationalPackageExportOptions().ReplaceExisting,
            "blocking isolation and target replacement require explicit opt-in");
        Check(PackageIsolation.Select(1, false) == IsolationLevel.Snapshot && PackageIsolation.Select(1, true) == IsolationLevel.Snapshot, "snapshot always wins when available");
        foreach (int state in new[] { 0, 2, 3 })
        {
            Throws<InvalidOperationException>(() => PackageIsolation.Select(state, false));
            Check(PackageIsolation.Select(state, true) == IsolationLevel.Serializable, "blocking consistency is explicit for unavailable/transitioning snapshot states");
        }
        Throws<InvalidOperationException>(() => PackageIsolation.Select(null, true));
        Throws<InvalidOperationException>(() => PackageIsolation.Select(4, true));
    }

    private static void Paths()
    {
        Check(PackageLocalFiles.Entry("images\\paste.png") == "local-files/images/paste.png", "local files use explicit normalized relative entries");
        foreach (string path in new[] { "../secret", "a/../secret", "/absolute", "C:\\secret", "a:stream", "a//b", "a/", "a/./b",
            "a/NUL.txt", "a/COM1.png", "a/LPT\u00b9.txt", "a/name. ", "a/name.", "a/name?", "connection-settings.json", "a/CONNECTION-SETTINGS.JSON",
            "connection-settings.json.bak", "a/connection-settings.backup.json", "a/\ud800" })
            Throws<InvalidDataException>(() => PackageLocalFiles.Entry(path));
        var budget = new PackageBudget(new());
        budget.AddEntry("local-files/A.txt");
        Throws<InvalidDataException>(() => budget.AddEntry("LOCAL-FILES/a.TXT"));
        var tiny = new PackageBudget(new() { MaxEntries = 1 });
        tiny.AddEntry("one");
        Throws<RelationalPackageLimitException>(() => tiny.AddEntry("two"));
    }

    private static RelationalPackageColumn Column(string name, string type, int? ordinal) => new(1, name, type, -1, 0, 0, true,
        null, null, false, null, null, null, false, null, type == "timestamp", ordinal,
        ordinal.HasValue ? PackageRowEncoding.ForType(type) : null, ordinal.HasValue ? "PreserveValue" : "RestampRowVersion");

    private static void Catalogue(string schemaPath)
    {
        var parser = new TSql160Parser(true);
        var tables = new TableNames();
        foreach (string file in new[] { "001.Core.sql", "001.Snapshots.sql", "001.Capture.sql", "001.State.sql" })
        {
            using var source = File.OpenText(Path.Combine(schemaPath, file));
            var tree = parser.Parse(source, out var errors);
            Check(errors.Count == 0, "owned schema input parses");
            tree.Accept(tables);
        }
        string[] excluded = ["surf.StorageFormatInfo", "surf.SchemaMigration", "surf.MigrationRun", "surf.MigrationCheckpoint", "surf.MigrationIssue",
            "surf.MigrationIdentityMap", "surf.SourceProvenance", "surf.SnapshotCatalogueHead", "surf.StateCatalogueGeneration"];
        var specs = PackageTableCatalogue.Authoritative;
        Check(specs.Select(spec => spec.Key).Distinct(StringComparer.Ordinal).Count() == specs.Length, "table whitelist is unique");
        Check(tables.Names.SetEquals(specs.Select(spec => spec.Key).Concat(excluded)), "every schema table has an explicit authoritative/exclusion policy");
        foreach (var spec in specs)
        {
            Check(!excluded.Contains(spec.Key) && tables.Names.Contains(spec.Key), "whitelist cannot include excluded/missing tables");
            string sql = $"SELECT COUNT_BIG(*) FROM {spec.Qualified} t WHERE ({spec.Predicate("t")});";
            parser.Parse(new StringReader(sql), out var errors);
            Check(errors.Count == 0, "filtered authoritative selection parses: " + spec.Key);
        }
        Check(specs.Single(spec => spec.Name == "TextContent").Predicate("t").Contains("DefinitionContentKey", StringComparison.Ordinal) &&
            !specs.Single(spec => spec.Name == "TextContent").Predicate("t").Contains("DocumentRevision", StringComparison.Ordinal), "derived index-only text is excluded");
        Check(specs.Single(spec => spec.Name == "Asset").Predicate("t").Contains("PastedFallbackAssetKey", StringComparison.Ordinal), "pasted fallback assets are included losslessly");
        Check(specs.Single(spec => spec.Name == "DataSet").Predicate("t").Contains("State='Ready'", StringComparison.Ordinal), "Writing datasets are excluded");
        var capture = PackageTableCatalogue.Capture(long.MaxValue, 128);
        Check(capture.Name == "Data_9223372036854775807" && capture.Schema == "capture", "only generated internal captured table names are used");
        var metadata = new RelationalPackageTable("capture", capture.Name,
            [Column("DataSetKey", "bigint", 0), Column("C0001", "nvarchar", 1), Column("Version", "timestamp", null),
                Column("unsafe]; DROP TABLE x;--", "varbinary", 2)], ["DataSetKey"], [], capture.Selection);
        string select = PackageTableCatalogue.SelectSql(capture, metadata);
        Check(!select.Contains("t.[Version]", StringComparison.Ordinal) && select.Contains("DATALENGTH(t.[C0001])", StringComparison.Ordinal) &&
            select.Contains("CONVERT(varbinary(max),t.[C0001])", StringComparison.Ordinal) && select.EndsWith("ORDER BY t.[DataSetKey];", StringComparison.Ordinal),
            "stream projection excludes rowversions and puts length before exact bytes with stable PK sort");
        parser.Parse(new StringReader(select), out var selectErrors);
        Check(selectErrors.Count == 0, "generated query with hostile metadata identifier remains valid quoted SQL");
        Throws<ArgumentOutOfRangeException>(() => PackageTableCatalogue.Capture(0, 1));
        Throws<ArgumentOutOfRangeException>(() => PackageTableCatalogue.Capture(1, 129));
    }

    private sealed class TableNames : TSqlFragmentVisitor
    {
        internal HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public override void ExplicitVisit(CreateTableStatement node) => Names.Add(node.SchemaObjectName.SchemaIdentifier.Value + "." + node.SchemaObjectName.BaseIdentifier.Value);
    }

    private static void Hashes()
    {
        byte[] bytes = "one\nsecond\n"u8.ToArray();
        using var output = new MemoryStream();
        var budget = new PackageBudget(new());
        using var hash = new PackageHashingStream(output, budget, bytes.Length);
        hash.Write(bytes, 0, 3); hash.Write(bytes.AsSpan(3));
        Check(hash.Bytes == bytes.Length && budget.Bytes == bytes.Length && output.ToArray().SequenceEqual(bytes), "entry and aggregate byte counts cover exact bytes");
        Check(hash.Finish() == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "entry SHA256 equals exact uncompressed content");
        Throws<InvalidOperationException>(() => hash.WriteByte(1));
        using var limited = new PackageHashingStream(Stream.Null, new(new()), 2);
        Throws<RelationalPackageLimitException>(() => limited.Write(bytes));
        var aggregate = new PackageBudget(new() { MaxPackageBytes = 2 });
        using var total = new PackageHashingStream(Stream.Null, aggregate, 100);
        Throws<RelationalPackageLimitException>(() => total.Write(bytes));
        var columns = new PackageBudget(new() { MaxTotalColumns = 2 });
        columns.AddColumns(2);
        Throws<RelationalPackageLimitException>(() => columns.AddColumns(1));
    }

    private static async Task ChunksAsync()
    {
        byte[] original = [0, 0, 0, 0xD8, 0x41, 0, 0, 0xDC, 0xFF, 0xFE, 1];
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            await PackageRowEncoding.WriteChunksAsync(writer, new MemoryStream(original, writable: false), original.Length, new byte[3], default);
            await writer.FlushAsync();
        }
        using var json = JsonDocument.Parse(memory.ToArray());
        byte[] restored = json.RootElement.GetProperty("chunks").EnumerateArray().SelectMany(chunk => Convert.FromBase64String(chunk.GetString()!)).ToArray();
        Check(restored.SequenceEqual(original) && json.RootElement.GetProperty("byteLength").GetString() == "11", "raw bytes including unpaired UTF16 code units preserve exact chunk-independent content");
        using var empty = new MemoryStream();
        using (var writer = new Utf8JsonWriter(empty))
        {
            writer.WriteStartArray(); writer.WriteNullValue();
            await PackageRowEncoding.WriteChunksAsync(writer, Stream.Null, 0, new byte[4], default);
            writer.WriteEndArray(); await writer.FlushAsync();
            empty.WriteByte((byte)'\n'); writer.Reset(empty);
            writer.WriteStartArray(); writer.WriteBooleanValue(true); writer.WriteEndArray(); await writer.FlushAsync();
        }
        Check(Encoding.UTF8.GetString(empty.ToArray()) == "[null,{\"byteLength\":\"0\",\"chunks\":[]}]\n[true]", "NULL and empty distinguish; reset supports independent JSONL rows without commas/BOM");
        using var discard = new CountingSink();
        using (var writer = new Utf8JsonWriter(discard))
        {
            using var input = new PatternStream(20L * 1024 * 1024);
            await PackageRowEncoding.WriteChunksAsync(writer, input, input.Total, new byte[16 * 1024], default);
            await writer.FlushAsync();
            Check(input.MaxRequest <= 16 * 1024 && discard.MaxWrite <= 32 * 1024 && discard.Count > input.Total,
                "20MiB source streams through bounded chunks/writer buffers without a full cell allocation");
        }
        using (var writer = new Utf8JsonWriter(Stream.Null))
            await ThrowsAsync<InvalidDataException>(() => PackageRowEncoding.WriteChunksAsync(writer, new PatternStream(2), 3, new byte[8], default));
        using (var writer = new Utf8JsonWriter(Stream.Null))
            await ThrowsAsync<InvalidDataException>(() => PackageRowEncoding.WriteChunksAsync(writer, new PatternStream(3), 2, new byte[8], default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using (var writer = new Utf8JsonWriter(Stream.Null))
            await ThrowsAsync<OperationCanceledException>(() => PackageRowEncoding.WriteChunksAsync(writer, new PatternStream(20), 20, new byte[8], canceled.Token));
    }

    private static async Task ArchiveAsync()
    {
        using var file = new MemoryStream();
        var budget = new PackageBudget(new());
        string rowsPath = "database/surf/Example.rows.ndjson";
        byte[] raw = [0, 0xD8, 0x41, 0, 0xFF, 0xFE];
        string tableHash, manifestHash;
        long tableBytes;
        var table = new RelationalPackageTable("surf", "Example",
            [Column("Key", "bigint", 0) with { Identity = true, IdentitySeed = "1", IdentityIncrement = "1", IdentityLastValue = long.MaxValue.ToString(CultureInfo.InvariantCulture) },
                Column("Value", "nvarchar", 1), Column("Version", "timestamp", null)], ["Key"], [], "Prototype-v1");
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            budget.AddEntry(rowsPath);
            await using (var output = archive.CreateEntry(rowsPath, CompressionLevel.Fastest).Open())
            {
                using var hash = new PackageHashingStream(output, budget, 1024 * 1024);
                using (var writer = new Utf8JsonWriter(hash))
                {
                    writer.WriteStartArray(); PackageRowEncoding.WriteScalar(writer, "bigint", 1L);
                    await PackageRowEncoding.WriteChunksAsync(writer, new MemoryStream(raw, writable: false), raw.Length, new byte[3], default);
                    writer.WriteEndArray(); await writer.FlushAsync(); await hash.WriteAsync("\n"u8.ToArray()); writer.Reset(hash);
                    writer.WriteStartArray(); PackageRowEncoding.WriteScalar(writer, "bigint", 2L); writer.WriteNullValue(); writer.WriteEndArray();
                    await writer.FlushAsync(); await hash.WriteAsync("\n"u8.ToArray());
                }
                tableBytes = hash.Bytes; tableHash = hash.Finish();
            }
            var manifest = new RelationalPackageManifest(RelationalPackageFormat.Identifier, 2, 1, 1, "Surf2.Relational", 1, 1, 1,
                Guid.NewGuid(), DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, "SqlSnapshot-v1", "ExplicitCallerSelection-v1",
                [new(rowsPath, table, 2, tableBytes, tableHash)], []);
            budget.AddEntry(RelationalPackageFormat.ManifestEntry);
            await using var manifestOutput = archive.CreateEntry(RelationalPackageFormat.ManifestEntry).Open();
            using var manifestStream = new PackageHashingStream(manifestOutput, budget, 1024 * 1024);
            await JsonSerializer.SerializeAsync(manifestStream, manifest);
            await manifestStream.FlushAsync(); manifestHash = manifestStream.Finish();
        }
        file.Position = 0;
        using var read = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        using var manifestBytes = new MemoryStream();
        await read.GetEntry("manifest.json")!.Open().CopyToAsync(manifestBytes);
        Check(Convert.ToHexString(SHA256.HashData(manifestBytes.ToArray())).ToLowerInvariant() == manifestHash, "ZIP manifest checksum matches uncompressed serialized contract");
        var decoded = JsonSerializer.Deserialize<RelationalPackageManifest>(manifestBytes.ToArray())!;
        Check(decoded.PackageIdentifier == RelationalPackageFormat.Identifier && decoded.PackageVersion == 2 && decoded.DatabaseFormatIdentifier == "Surf2.Relational" && decoded.ExporterVersion == 1,
            "versioned manifest round-trips exact format/exporter/schema markers");
        Check(decoded.Tables[0].Table.Columns[0].IdentityLastValue == long.MaxValue.ToString(CultureInfo.InvariantCulture) &&
            decoded.Tables[0].Table.Columns[2].StreamOrdinal == null && decoded.Tables[0].Table.Columns[2].RestorePolicy == "RestampRowVersion",
            "identity counters and omitted rowversion policy survive manifest serialization");
        using var rowsBytes = new MemoryStream();
        await read.GetEntry(rowsPath)!.Open().CopyToAsync(rowsBytes);
        Check(rowsBytes.Length == decoded.Tables[0].ByteCount && decoded.Tables[0].RowCount == 2 &&
            Convert.ToHexString(SHA256.HashData(rowsBytes.ToArray())).ToLowerInvariant() == decoded.Tables[0].Sha256, "ZIP table count and SHA256 describe exact stream bytes");
        string[] lines = Encoding.UTF8.GetString(rowsBytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var first = JsonDocument.Parse(lines[0]); using var second = JsonDocument.Parse(lines[1]);
        byte[] restored = first.RootElement[1].GetProperty("chunks").EnumerateArray().SelectMany(chunk => Convert.FromBase64String(chunk.GetString()!)).ToArray();
        Check(lines.Length == 2 && first.RootElement.GetArrayLength() == 2 && restored.SequenceEqual(raw) && second.RootElement[1].ValueKind == JsonValueKind.Null,
            "ZIP rows preserve raw bytes/null and have no owner revision token cells");
        Check(budget.Bytes == rowsBytes.Length + manifestBytes.Length, "aggregate budget includes both rows and manifest");
    }

    private sealed class CountingSink : Stream
    {
        internal long Count; internal int MaxWrite;
        public override void Write(byte[] buffer, int offset, int count) { Count += count; MaxWrite = Math.Max(MaxWrite, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count += buffer.Length; MaxWrite = Math.Max(MaxWrite, buffer.Length); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => Count; public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class PatternStream(long length) : Stream
    {
        internal long Total => length; internal int MaxRequest; private long _position;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            MaxRequest = Math.Max(MaxRequest, buffer.Length);
            int count = (int)Math.Min(buffer.Length, length - _position);
            buffer[..count].Fill(0xDA); _position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
#endif

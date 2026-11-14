using System.Data.SqlTypes;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Packages;

/// <summary>Opt-in, database-free hooks for the parent regression harness.</summary>
public static class PackageRowDecodingChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var passed = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Package decoding contract failed: " + name);
            passed.Add(name);
        }
        var limits = new RelationalPackageLimits { MaxCellBytes = 1024, MaxRowBytes = 2048 };
        RelationalPackageColumn Column(string type, bool nullable = false, byte precision = 0, byte scale = 0) =>
            new(1, "Value", type, type is "binary" ? 32 : -1, precision, scale, nullable, null, null, false, null, null, null,
                false, null, false, 0, PackageRowEncoding.ForType(type, precision), "PreserveValue");
        async Task<object> DecodeAsync(string row, RelationalPackageColumn column)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(row));
            var decoder = new PackageRowDecoding(input, limits);
            var values = await decoder.ReadAsync(new[] { column }, ct) ?? throw new InvalidOperationException("Missing test row.");
            Check(await decoder.ReadAsync(new[] { column }, ct) == null, "Exact one-row EOF " + passed.Count);
            return values[0];
        }
        async Task RejectAsync(string row, RelationalPackageColumn column, string name)
        {
            try { await DecodeAsync(row, column); }
            catch (Exception error) when (error is InvalidDataException or RelationalPackageLimitException) { passed.Add(name); return; }
            throw new InvalidOperationException("Package decoding should reject: " + name);
        }
        Check((long)await DecodeAsync("[\"-9223372036854775808\"]\n", Column("bigint")) == long.MinValue, "Bigint minimum");
        Check((long)await DecodeAsync("[\"9223372036854775807\"]\n", Column("bigint")) == long.MaxValue, "Bigint maximum");
        Check(BitConverter.DoubleToInt64Bits((double)await DecodeAsync("[\"8000000000000000\"]\n", Column("float", precision: 53))) == long.MinValue, "Negative-zero IEEE bits");
        Check(await DecodeAsync("[null]\n", Column("nvarchar", nullable: true)) == DBNull.Value, "NULL distinct from empty");
        Check(((byte[])await DecodeAsync("[{\"byteLength\":\"0\",\"chunks\":[]}]\n", Column("nvarchar"))).Length == 0, "Non-null empty UTF-16");
        byte[] exact = [0, 0xD8, 65, 0, 0, 0xDC];
        string blob = "[{\"byteLength\":\"6\",\"chunks\":[\"AA==\",\"2EE=\",\"AA==\",\"ANw=\"]}]\n";
        Check(((byte[])await DecodeAsync(blob, Column("nvarchar"))).AsSpan().SequenceEqual(exact), "Split code units/unpaired surrogates retain exact bytes");
        byte[] captureBytes = Encoding.Unicode.GetBytes("[{\"text\":\"" + new string('x', 70000) + "😀\"}]");
        using (var encodedCapture = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(encodedCapture))
            {
                writer.WriteStartArray();
                writer.WriteStringValue("17");
                using var payload = new MemoryStream(captureBytes);
                await PackageRowEncoding.WriteChunksAsync(writer, payload, captureBytes.Length, new byte[32767], ct);
                writer.WriteEndArray();
                await writer.FlushAsync(ct);
            }
            encodedCapture.WriteByte((byte)'\n');
            encodedCapture.Position = 0;
            var decoder = new PackageRowDecoding(encodedCapture, limits);
            using var restored = new MemoryStream();
            long observedKey = 0, observedLength = 0;
            Task Begin(long key, long length, CancellationToken token)
            {
                observedKey = key;
                observedLength = length;
                return Task.CompletedTask;
            }
            Task Append(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                Check(bytes.Length <= 65536, "Captured payload decoder bounds each chunk");
                return restored.WriteAsync(bytes, token).AsTask();
            }
            Check(await decoder.ReadFullTableDataAsync(Begin, Append, ct) == 17 && observedKey == 17 &&
                observedLength == captureBytes.Length && restored.ToArray().AsSpan().SequenceEqual(captureBytes),
                "Captured JSON exceeds generic cell limits and preserves split UTF-16 exactly");
            Check(await decoder.ReadFullTableDataAsync(Begin, Append, ct) == null, "Captured payload exact EOF");
        }
        foreach (string malformed in new[]
        {
            "[\"1\",{\"byteLength\":\"5\",\"chunks\":[]}]\n",
            "[\"1\",{\"byteLength\":\"4\",\"chunks\":[\"AA==\"]}]\n",
            "[\"1\",{\"byteLength\":\"4\",\"chunks\":[\"AAAAAAA=\"]}]\n"
        })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(malformed));
            var decoder = new PackageRowDecoding(input, limits);
            try
            {
                await decoder.ReadFullTableDataAsync((_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, ct);
                throw new InvalidOperationException("Malformed captured JSON package was accepted.");
            }
            catch (InvalidDataException) { passed.Add("Captured JSON rejects malformed declared length/chunks"); }
        }
        Check(PackageTableCatalogue.ForSchema(1).All(table => table.Name != "FullTableData") &&
            PackageTableCatalogue.ForSchema(2).Count(table => table.Name == "FullTableData") == 1,
            "Legacy package catalogue and JSON package catalogue remain explicit");
        SqlDecimal number = SqlDecimal.Parse("12345678901234567890123456789012345678");
        using (var output = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(output))
            { writer.WriteStartArray(); PackageRowEncoding.WriteScalar(writer, "decimal", number); writer.WriteEndArray(); writer.Flush(); }
            var decoded = (SqlDecimal)await DecodeAsync(Encoding.UTF8.GetString(output.ToArray()) + "\n", Column("decimal", precision: number.Precision, scale: number.Scale));
            Check(decoded.Data.SequenceEqual(number.Data) && decoded.Precision == 38, "SqlDecimal precision 38 without CLR decimal");
        }
        await RejectAsync("[\"01\"]\n", Column("bigint"), "Noncanonical integer");
        await RejectAsync("[\"9223372036854775808\"]\n", Column("bigint"), "Integer overflow");
        await RejectAsync("[null]\n", Column("bigint"), "Non-nullable NULL");
        await RejectAsync("[\"1\"]", Column("bigint"), "Missing trailing LF");
        await RejectAsync(" [\"1\"]\n", Column("bigint"), "Leading row whitespace");
        await RejectAsync("[\"1\",\"2\"]\n", Column("bigint"), "Extra cells");
        await RejectAsync("[{\"byteLength\":\"1\",\"chunks\":[\"AA==\"]}]\n", Column("nvarchar"), "Odd UTF-16 byte length");
        await RejectAsync("[{\"byteLength\":\"2\",\"chunks\":[\"AA==\"]}]\n", Column("varbinary"), "Short chunk payload");
        await RejectAsync("[{\"byteLength\":\"1\",\"chunks\":[\"AA==\",\"AA==\"]}]\n", Column("varbinary"), "Extra chunk payload");
        await RejectAsync("[{\"byteLength\":\"1025\",\"chunks\":[]}]\n", Column("varbinary"), "Cell allocation budget");
        Check(RelationalPackageImporter.BulkRowPayloadBudget(7, new RelationalPackageImportOptions()) > 64L * 1024 * 1024,
            "Default batch supports a 64 MiB TextContent cell plus fixed/row overhead");
        long reservedPayload = -1;
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("[{\"byteLength\":\"1024\",\"chunks\":[]}]\n")))
        {
            var decoder = new PackageRowDecoding(input, limits, 512, (bytes, token) =>
            { reservedPayload = bytes; return Task.CompletedTask; });
            try
            {
                await decoder.ReadAsync(new[] { Column("varbinary") }, ct);
                throw new InvalidOperationException("Oversized batch cell was accepted.");
            }
            catch (RelationalPackageLimitException)
            { Check(reservedPayload == 0, "Row/batch budget rejects declared LOB before allocating/reserving its payload"); }
        }
        object[]? previous = null;
        PackageRowDecoding.OrderedKey(new[] { Column("bigint") }, new[] { "Value" }, new object[] { 1L }, ref previous);
        try
        {
            PackageRowDecoding.OrderedKey(new[] { Column("bigint") }, new[] { "Value" }, new object[] { 1L }, ref previous);
            throw new InvalidOperationException("Duplicate PK was accepted.");
        }
        catch (InvalidDataException) { passed.Add("Duplicate/increasing PK contract"); }
        var fk = new RelationalPackageForeignKey("GeneratedSourceName", "surf", "UserProfile", new[] { "Value" }, new[] { "ProfileKey" });
        var metadata = new RelationalPackageTable("surf", "Fixture", new[] { Column("bigint") }, new[] { "Value" }, new[] { fk }, "AllAuthoritativeRows-v1");
        RelationalPackageImporter.CompareMetadata(metadata, metadata with { ForeignKeys = new[] { fk with { Name = "GeneratedTargetName" } } });
        passed.Add("FK constraint names ignored; ordered references compared");
        try
        {
            RelationalPackageImporter.CompareMetadata(metadata, metadata with { ForeignKeys = new[] { fk with { ReferencedColumns = new[] { "WrongColumn" } } } });
            throw new InvalidOperationException("Changed FK was accepted.");
        }
        catch (InvalidDataException) { passed.Add("Changed FK target rejected"); }
        using (var source = new MemoryStream(new byte[] { 1, 2, 3 }))
        using (var hash = new PackageEntryReadStream(source, 3, SHA256.HashData(new byte[] { 1, 2, 3 })))
        {
            byte[] buffer = new byte[8];
            Check(await hash.ReadAsync(buffer, ct) == 3 && await hash.ReadAsync(buffer, ct) == 0, "Exact entry EOF");
            hash.Finish(); passed.Add("Entry SHA-256 verified");
        }
        return passed.AsReadOnly();
    }
}

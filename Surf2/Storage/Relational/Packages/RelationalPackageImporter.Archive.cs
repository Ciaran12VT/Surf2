using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageImporter
{
    private sealed record PackageInput(RelationalPackageManifest Manifest, string ManifestHash,
        IReadOnlyDictionary<string, ZipArchiveEntry> Entries, IReadOnlyDictionary<string, RelationalPackageTableStream> Tables,
        long Bytes);

    private async Task<PackageInput> ReadPackageAsync(ZipArchive archive, RelationalPackageImportOptions options, CancellationToken ct)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var caseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entries.Count >= _limits.MaxEntries || entry.Length < 0 || entry.Length > _limits.MaxEntryBytes ||
                entry.Length > _limits.MaxPackageBytes - bytes) throw new RelationalPackageLimitException("ZIP entry/package budget exceeded.");
            string path = entry.FullName;
            if (path.Length > 4096 || path.Contains('\\') || path.EndsWith('/') || !caseNames.Add(path))
                throw PackageRowDecoding.Bad("Unsafe, duplicate, or case-colliding ZIP entry");
            entries.Add(path, entry); bytes += entry.Length;
        }
        if (!entries.TryGetValue(RelationalPackageFormat.ManifestEntry, out var manifestEntry) ||
            manifestEntry.Length <= 0 || manifestEntry.Length > _limits.MaxManifestBytes)
            throw PackageRowDecoding.Bad("Missing or oversized manifest");
        byte[] data = new byte[checked((int)manifestEntry.Length)];
        await using (var stream = manifestEntry.Open())
        {
            await stream.ReadExactlyAsync(data, ct);
            if (await stream.ReadAsync(new byte[1], ct) != 0) throw PackageRowDecoding.Bad("Manifest exceeds its declared size");
        }
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (options.ExpectedManifestSha256 != null && hash != options.ExpectedManifestSha256)
            throw PackageRowDecoding.Bad("Manifest differs from the caller's pinned checksum");
        RelationalPackageManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
            UniqueProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<RelationalPackageManifest>(data, new JsonSerializerOptions
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 16 })
                ?? throw PackageRowDecoding.Bad("Null manifest");
        }
        catch (JsonException) { throw PackageRowDecoding.Bad("Malformed or unsupported manifest fields"); }
        if (manifest.PackageIdentifier != RelationalPackageFormat.Identifier || manifest.PackageVersion != 2 ||
            manifest.ExporterVersion != 1 || manifest.RowEncodingVersion != 1 ||
            manifest.DatabaseFormatIdentifier != PersistenceFormatProbe.FormatIdentifier || manifest.DatabaseSchemaVersion != 1 ||
            manifest.MinimumReaderVersion != 1 || manifest.MinimumWriterVersion != 1 || manifest.SourceDatabaseIdentity == Guid.Empty ||
            manifest.SourceCreatedAtUtc == default || manifest.ExportedAtUtc == default ||
            manifest.Consistency is not ("SqlSnapshot-v1" or "SqlSerializableBlocking-v1") ||
            manifest.LocalFilePolicy != "ExplicitCallerSelection-v1" || manifest.Tables == null || manifest.LocalFiles == null ||
            manifest.Tables.Count < PackageTableCatalogue.Authoritative.Length || manifest.Tables.Count > _limits.MaxTables)
            throw PackageRowDecoding.Bad("Unsupported package/format/consistency contract");
        var tables = new Dictionary<string, RelationalPackageTableStream>(StringComparer.Ordinal);
        var usedEntries = new HashSet<string>(StringComparer.Ordinal) { RelationalPackageFormat.ManifestEntry };
        int totalColumns = 0;
        foreach (var stream in manifest.Tables)
        {
            if (stream?.Table == null || stream.Table.Columns == null || stream.Table.PrimaryKey == null || stream.Table.ForeignKeys == null)
                throw PackageRowDecoding.Bad("Incomplete table metadata");
            var table = stream.Table;
            PackageTableSpec spec;
            if (table.Schema == "surf")
                spec = PackageTableCatalogue.Authoritative.SingleOrDefault(candidate => candidate.Name == table.Name)
                    ?? throw PackageRowDecoding.Bad("Excluded authoritative table");
            else if (table.Schema == "capture" && table.Name != null && table.Name.StartsWith("Data_", StringComparison.Ordinal))
            {
                long key = PackageRowDecoding.Integer(table.Name[5..], 1, long.MaxValue);
                int count = table.Columns.Count - 6;
                if (count is < 0 or > 128) throw PackageRowDecoding.Bad("Unsupported capture layout width");
                spec = PackageTableCatalogue.Capture(key, count);
            }
            else throw PackageRowDecoding.Bad("Excluded schema/table identifier");
            if (stream.Entry != spec.Entry || table.RowSelection != spec.Selection || !tables.TryAdd(spec.Key, stream) ||
                !usedEntries.Add(spec.Entry)) throw PackageRowDecoding.Bad("Table entry/predicate contract mismatch");
            CheckEntry(entries, stream.Entry, stream.ByteCount, stream.Sha256, _limits.MaxEntryBytes);
            if (stream.RowCount < 0 || stream.RowCount > stream.ByteCount / 3 || table.Columns.Count == 0 ||
                table.Columns.Count > _limits.MaxColumnsPerTable || table.PrimaryKey.Count == 0 || table.PrimaryKey.Count > table.Columns.Count ||
                table.ForeignKeys.Count > 1024) throw PackageRowDecoding.Bad("Table row/column/relationship bounds");
            totalColumns = checked(totalColumns + table.Columns.Count);
            if (totalColumns > _limits.MaxTotalColumns) throw new RelationalPackageLimitException("Package column catalogue budget exceeded.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                if (column == null || string.IsNullOrEmpty(column.Name) || column.Name.Length > 128 || !names.Add(column.Name) ||
                    column.SqlType == null || column.SqlType.Length > 32 || column.ComputedDefinition?.Length > 16384 ||
                    column.Collation?.Length > 128 || column.RestorePolicy?.Length > 64)
                    throw PackageRowDecoding.Bad("Invalid bounded column metadata");
            }
            foreach (var fk in table.ForeignKeys)
                if (fk == null || fk.Columns == null || fk.ReferencedColumns == null || fk.Columns.Count == 0 ||
                    fk.Columns.Count > table.Columns.Count || fk.Columns.Count != fk.ReferencedColumns.Count ||
                    fk.Name?.Length > 128 || fk.ReferencedSchema?.Length > 128 || fk.ReferencedTable?.Length > 128)
                    throw PackageRowDecoding.Bad("Invalid bounded FK metadata");
        }
        foreach (var spec in PackageTableCatalogue.Authoritative)
            if (!tables.ContainsKey(spec.Key)) throw PackageRowDecoding.Bad("Required authoritative table omitted");
        foreach (var local in manifest.LocalFiles)
        {
            if (local == null || local.Entry == null || !local.Entry.StartsWith("local-files/", StringComparison.Ordinal) ||
                PackageLocalFiles.Entry(local.Entry[12..]) != local.Entry || !usedEntries.Add(local.Entry))
                throw PackageRowDecoding.Bad("Unsafe or duplicate optional local entry");
            CheckEntry(entries, local.Entry, local.ByteCount, local.Sha256, _limits.MaxLocalFileBytes);
        }
        if (usedEntries.Count != entries.Count) throw PackageRowDecoding.Bad("ZIP contains undeclared/excluded entries");
        return new(manifest, hash, entries, tables, bytes);
    }

    private static void CheckEntry(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string name, long bytes, string hash, long limit)
    {
        _ = PackageRowDecoding.Hash(hash);
        if (bytes < 0 || bytes > limit || !entries.TryGetValue(name, out var entry) || entry.Length != bytes)
            throw PackageRowDecoding.Bad("Manifest and ZIP sizes/entry names differ");
    }

    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw PackageRowDecoding.Bad("Duplicate manifest JSON property");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }

    // Bound central-directory allocation BEFORE ZipArchive materializes its entry objects.
    private async Task PreflightZipAsync(FileStream file, CancellationToken ct)
    {
        if (file.Length < 22 || file.Length > _limits.MaxPackageBytes) throw PackageRowDecoding.Bad("ZIP physical-size budget");
        int tailLength = (int)Math.Min(file.Length, 65557);
        var tail = new byte[tailLength]; file.Position = file.Length - tailLength;
        await file.ReadExactlyAsync(tail, ct);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50 &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { end = i; break; }
        if (end < 0 || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4, 2)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6, 2)) != 0)
            throw PackageRowDecoding.Bad("Missing/split ZIP central directory");
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        ulong diskCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8, 2));
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12, 4));
        ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16, 4));
        long absoluteEnd = file.Length - tailLength + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (absoluteEnd < 76) throw PackageRowDecoding.Bad("Missing ZIP64 locator");
            byte[] locator = new byte[20]; file.Position = absoluteEnd - 20; await file.ReadExactlyAsync(locator, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw PackageRowDecoding.Bad("Unsupported split ZIP64");
            ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (zip64Offset > (ulong)(absoluteEnd - 76)) throw PackageRowDecoding.Bad("Invalid ZIP64 offset");
            byte[] header = new byte[56]; file.Position = (long)zip64Offset; await file.ReadExactlyAsync(header, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x06064b50 ||
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(4)) != 44 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != 0)
                throw PackageRowDecoding.Bad("Unsupported ZIP64 directory header");
            diskCount = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
            count = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
            size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40));
            offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));
        }
        if (count == 0 || count != diskCount || count > (ulong)_limits.MaxEntries || size > 32 * 1024 * 1024 ||
            offset > (ulong)absoluteEnd || size > (ulong)absoluteEnd - offset)
            throw new RelationalPackageLimitException("ZIP central directory exceeds the supported bounded single-file contract.");
        file.Position = 0;
    }

    private async Task VerifyLocalFilesAsync(PackageInput package, CancellationToken ct)
    {
        byte[] buffer = new byte[_limits.ChunkBytes];
        foreach (var local in package.Manifest.LocalFiles)
        {
            await using var input = package.Entries[local.Entry].Open();
            using var verified = new PackageEntryReadStream(input, local.ByteCount, PackageRowDecoding.Hash(local.Sha256));
            while (await verified.ReadAsync(buffer, ct) != 0) { }
            verified.Finish();
        }
    }
}

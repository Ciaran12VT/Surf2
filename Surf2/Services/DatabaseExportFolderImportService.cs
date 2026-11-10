using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Surf2.Models;

namespace Surf2.Services;

public sealed record DatabaseExportFolderImportResult(DatabaseMetadataSnapshot Snapshot, IReadOnlyList<string> Warnings, string Format);

/// <summary>Reads extracted Surf display exports without executing SQL or modifying the source folder.</summary>
public sealed partial class DatabaseExportFolderImportService
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;
    private const long MaximumTotalBytes = 128L * 1024 * 1024;
    private const int MaximumFiles = 100_000;
    private static readonly string[] LegacyFolders = ["Stored Procedures", "Views", "Functions", "Triggers", "Tables", "Table Data"];
    private sealed record SourceFile(string Path, string Relative, long Length);
    private sealed record Identity(string Schema, string Name);
    private sealed record Resource(SourceFile File, Identity? Identity, SqlDatabaseObjectKind? Kind, bool Table, bool Data, string Encoding);
    private sealed class ImportState
    {
        public required DatabaseMetadataSnapshot Snapshot { get; init; }
        public List<string> Warnings { get; } = [];
        public Dictionary<string, byte[]> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SqlTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Identity> LegacyTableFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DataNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Rows { get; set; }
        public void Warn(string text) { if (!Warnings.Contains(text, StringComparer.Ordinal)) Warnings.Add(text); }
    }

    public async Task<DatabaseExportFolderImportResult> ImportAsync(string folderPath, string? displayName = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        string root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        RejectLink(root);
        var children = Directory.EnumerateFileSystemEntries(root).ToArray();
        bool relational = children.Any(p => Directory.Exists(p) && Path.GetFileName(p).Equals("resources", StringComparison.OrdinalIgnoreCase));
        var files = Inventory(root, relational, ct);
        if (files.Count == 0) throw Bad("The folder contains no exported database resources.");
        var state = new ImportState { Snapshot = new() { DisplayName = string.IsNullOrWhiteSpace(displayName) ? new DirectoryInfo(root).Name : displayName.Trim(),
            DatabaseName = string.Empty, ImportedAtUtc = DateTimeOffset.UtcNow } };
        state.Warn("This is a display export, not a database backup. The source database name, capture timestamps, original snapshot/history identities and TOP/WHERE selections are not restored. Imported content is dated with the current time.");
        var resources = relational ? await ReadRelationalResourcesAsync(files, state, ct).ConfigureAwait(false) : ReadLegacyResources(files);
        foreach (var resource in resources.Where(r => !r.Data))
        {
            ct.ThrowIfCancellationRequested();
            string text = Decode(await ReadAsync(resource.File, state, ct).ConfigureAwait(false), resource.Encoding, resource.File.Relative);
            if (resource.Table) ReadTable(resource, text, state);
            else ReadObject(resource, text, state);
        }
        foreach (var resource in resources.Where(r => r.Data))
        {
            ct.ThrowIfCancellationRequested();
            Identity identity = resource.Identity ?? ResolveLegacyData(resource.File, state);
            string key = NameKey(identity);
            if (!state.DataNames.Add(key)) throw Bad("Repeated table data identity: " + Display(identity));
            string csv = Decode(await ReadAsync(resource.File, state, ct).ConfigureAwait(false), "utf-8", resource.File.Relative);
            var rows = ReadCsv(csv, resource.File.Relative);
            if (rows.Count == 0) throw Bad("The CSV has no header: " + resource.File.Relative);
            string[] headers = rows[0];
            if (headers.Length > 128) throw Bad("The CSV exceeds the 128-column import budget: " + resource.File.Relative);
            bool zeroColumns = headers.Length == 1 && headers[0].Length == 0;
            if (!zeroColumns && (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length))
                throw Bad("CSV headers must be nonempty and unique: " + resource.File.Relative);
            var data = new SqlTableDataSet { SchemaName = identity.Schema, TableName = identity.Name, ImportedAtUtc = state.Snapshot.ImportedAtUtc };
            foreach (var cells in rows.Skip(1))
            {
                if (cells.Length != headers.Length) throw Bad("CSV row width differs from its header: " + resource.File.Relative);
                if (++state.Rows > 1_000_000) throw Bad("The export exceeds the 1,000,000-row import budget.");
                if (zeroColumns && cells[0].Length != 0) throw Bad("A CSV without columns contains a value: " + resource.File.Relative);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!zeroColumns) for (int i = 0; i < headers.Length; i++) values.Add(headers[i], cells[i]);
                data.Rows.Add(JsonSerializer.SerializeToElement(values));
            }
            data.RowCount = data.Rows.Count;
            if (state.Tables.TryGetValue(key, out var table))
            {
                if (table.HasFullData) data.RowCount = table.FullDataRowCount;
                table.HasFullData = true; table.FullDataRowCount = Math.Max(data.RowCount, data.Rows.Count); table.FullDataImportedAtUtc = data.ImportedAtUtc;
            }
            else state.Warn("Some CSV datasets have no table metadata. Their headers and rows are retained without inventing column types or primary keys.");
            state.Snapshot.TableDataSets.Add(data);
            state.Snapshot.FullDataTableNames.Add(Display(identity));
        }
        if (state.Snapshot.TableDataSets.Count != 0)
            state.Warn("CSV preserves displayed cell text only. Imported cells are strings: original JSON/SQL types, NULL versus empty/missing values, non-object rows and properties omitted by the exporter cannot be recovered.");
        if (state.Snapshot.Tables.Any(t => t.HasFullData && !state.DataNames.Contains(NameKey(new(t.SchemaName, t.TableName)))))
            throw Bad("A table is marked as having exported data, but its CSV file is missing.");
        // No partially read result is returned if the folder changed during import.
        var after = Inventory(root, relational, ct);
        if (!files.Select(f => (f.Relative, f.Length)).SequenceEqual(after.Select(f => (f.Relative, f.Length))))
            throw Bad("The export folder changed while it was being imported.");
        foreach (var file in files)
        {
            var bytes = await ReadBytesAsync(file, ct).ConfigureAwait(false);
            if (!state.Hashes.TryGetValue(file.Path, out var hash) || !SHA256.HashData(bytes).AsSpan().SequenceEqual(hash))
                throw Bad("An export file changed while it was being imported: " + file.Relative);
        }
        return new(state.Snapshot, state.Warnings.AsReadOnly(), relational ? "Surf relational resource export" : "Surf named-folder export");
    }

    private static List<SourceFile> Inventory(string root, bool relational, CancellationToken ct)
    {
        var result = new List<SourceFile>(); long bytes = 0;
        foreach (string directory in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); RejectLink(directory);
            string category = Path.GetFileName(directory);
            if (!Directory.Exists(directory) || (relational ? !category.Equals("resources", StringComparison.OrdinalIgnoreCase) : !LegacyFolders.Contains(category, StringComparer.OrdinalIgnoreCase)))
                throw Bad("Unexpected entry in the export root: " + category + ". Select the extracted database export folder.");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); RejectLink(path);
                if (Directory.Exists(path)) throw Bad("Nested resource directories are not part of a Surf export: " + Path.GetRelativePath(root, path));
                var info = new FileInfo(path);
                if (info.Length > MaximumFileBytes || info.Length > MaximumTotalBytes - bytes || result.Count >= MaximumFiles)
                    throw Bad("The export exceeds the import budget (32 MiB per file, 128 MiB total, 100,000 files).");
                bytes += info.Length; result.Add(new(path, category + "/" + info.Name, info.Length));
            }
        }
        return result;
    }
    private static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Bad("Export folders cannot contain symbolic links or reparse points: " + path); }
    private static async Task<byte[]> ReadBytesAsync(SourceFile file, CancellationToken ct)
    {
        RejectLink(file.Path);
        await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length != file.Length) throw Bad("An export file changed: " + file.Relative);
        byte[] bytes = new byte[checked((int)file.Length)]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); return bytes;
    }
    private static async Task<byte[]> ReadAsync(SourceFile file, ImportState state, CancellationToken ct)
    { byte[] bytes = await ReadBytesAsync(file, ct).ConfigureAwait(false); state.Hashes.Add(file.Path, SHA256.HashData(bytes)); return bytes; }
    private static string Decode(byte[] bytes, string encoding, string path)
    {
        if (encoding == "utf-16le-raw")
        {
            if (bytes.Length < 2 || bytes[0] != 255 || bytes[1] != 254 || bytes.Length % 2 != 0) throw Bad("Invalid raw UTF-16 resource: " + path);
            var chars = new char[(bytes.Length - 2) / 2];
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 + i * 2, 2));
            return new(chars);
        }
        try { return new UTF8Encoding(false, true).GetString(bytes.AsSpan(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 resource: " + path, error); }
    }
    private static List<Resource> ReadLegacyResources(List<SourceFile> files) => files.Select(file =>
    {
        string category = file.Relative.Split('/')[0]; bool data = category.Equals("Table Data", StringComparison.OrdinalIgnoreCase);
        if (!Path.GetExtension(file.Path).Equals(data ? ".csv" : ".txt", StringComparison.OrdinalIgnoreCase)) throw Bad("Unexpected resource extension: " + file.Relative);
        SqlDatabaseObjectKind? kind = category.ToUpperInvariant() switch {
            "STORED PROCEDURES" => SqlDatabaseObjectKind.StoredProcedure, "VIEWS" => SqlDatabaseObjectKind.View,
            "FUNCTIONS" => SqlDatabaseObjectKind.Function, "TRIGGERS" => SqlDatabaseObjectKind.Trigger, _ => null };
        return new Resource(file, null, kind, category.Equals("Tables", StringComparison.OrdinalIgnoreCase), data, "utf-8");
    }).ToList();

    private static async Task<List<Resource>> ReadRelationalResourcesAsync(List<SourceFile> files, ImportState state, CancellationToken ct)
    {
        var result = new List<Resource>(); var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = files.ToDictionary(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase);
        (long Snapshot, long? Version, bool Current)? origin = null;
        foreach (var file in files.Where(f => Path.GetExtension(f.Path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            string stem = Path.GetFileNameWithoutExtension(file.Path);
            if (stem.Length != 8 || !stem.All(char.IsAsciiDigit)) throw Bad("Invalid generated resource name: " + file.Relative);
            using var json = ParseMetadata(await ReadAsync(file, state, ct).ConfigureAwait(false), file.Relative);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
                throw Bad("Invalid or repeated resource metadata properties: " + file.Relative);
            bool current = value.TryGetProperty("Kind", out _);
            if (current && value.TryGetProperty("Collection", out _)) throw Bad("Resource metadata mixes current and historical formats: " + file.Relative);
            long? version = current && value.TryGetProperty("VersionKey", out var versionValue) && versionValue.ValueKind == JsonValueKind.Null
                ? null : PositiveMetadataKey(value, "VersionKey", file.Relative);
            var resourceOrigin = (PositiveMetadataKey(value, "SnapshotKey", file.Relative), version, current);
            if (origin is { } original && original != resourceOrigin) throw Bad("The export folder mixes different snapshot versions or export formats.");
            origin = resourceOrigin;
            var identity = new Identity(RawName(value, "SchemaUtf16Le"), RawName(value, "NameUtf16Le"));
            bool table = false, data = false; SqlDatabaseObjectKind? kind = null;
            if (value.TryGetProperty("Kind", out var kindValue))
            {
                if (kindValue.ValueKind != JsonValueKind.Number || !kindValue.TryGetInt32(out int number) || number is < 0 or > 5)
                    throw Bad("Unknown resource kind: " + file.Relative);
                table = number == 4; data = number == 5;
                if (!table && !data) kind = (SqlDatabaseObjectKind)(number + 1);
            }
            else if (value.TryGetProperty("Collection", out var collection) && collection.ValueKind == JsonValueKind.Number && collection.TryGetInt32(out int number) && number is 0 or 1 or 4)
            {
                table = number == 1; data = number == 4;
                if (number == 0 && value.TryGetProperty("ObjectKind", out var objectKind))
                {
                    if (objectKind.ValueKind != JsonValueKind.Number || !objectKind.TryGetInt32(out int objectKindNumber) || objectKindNumber is < 1 or > 4)
                        throw Bad("Unknown historical object kind: " + file.Relative);
                    kind = (SqlDatabaseObjectKind)objectKindNumber;
                }
            }
            else throw Bad("Missing resource kind/collection: " + file.Relative);
            string encoding = value.TryGetProperty("TextEncoding", out var encodingValue) && encodingValue.ValueKind == JsonValueKind.String ? encodingValue.GetString() ?? "" : "";
            if (encoding != (data ? "utf-8" : "utf-16le-raw")) throw Bad("Unsupported resource encoding: " + file.Relative);
            string bodyName = stem + (data ? ".csv" : ".sql");
            if (!byName.TryGetValue(bodyName, out var body)) throw Bad("Missing resource body: " + bodyName);
            consumed.Add(file.Path); consumed.Add(body.Path); result.Add(new(body, identity, kind, table, data, encoding));
        }
        if (consumed.Count != files.Count) throw Bad("A resource body is missing its JSON sidecar, or the folder contains an unexpected file.");
        return result;
    }
    private static JsonDocument ParseMetadata(byte[] bytes, string path)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException error) { throw new InvalidDataException("Invalid JSON resource sidecar: " + path, error); }
    }
    private static long PositiveMetadataKey(JsonElement value, string property, string path)
    {
        if (!value.TryGetProperty(property, out var key) || key.ValueKind != JsonValueKind.Number || !key.TryGetInt64(out long number) || number <= 0)
            throw Bad("Missing or invalid " + property + " in " + path);
        return number;
    }
    private static string RawName(JsonElement value, string property)
    {
        byte[] bytes;
        try { bytes = value.GetProperty(property).GetBytesFromBase64(); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Invalid raw resource name: " + property, error); }
        if (bytes.Length == 0 || bytes.Length % 2 != 0 || bytes.Length > 65536) throw Bad("Invalid resource name length.");
        var chars = new char[bytes.Length / 2];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return new(chars);
    }
    private static Identity ResolveLegacyData(SourceFile file, ImportState state)
    {
        string filename = Path.GetFileName(file.Path);
        var candidates = state.LegacyTableFiles.Values.Where(identity =>
        {
            return IsLegacyFileName(filename, LegacyFileName(identity, ".csv"));
        }).ToList();
        if (candidates.Count == 1) return candidates[0];
        throw Bad("Cannot recover an unambiguous identity for CSV '" + file.Relative + "' from its companion Tables files. Exported filenames may have been shortened, sanitized or given collision suffixes.");
    }
    private static bool IsLegacyFileName(string actual, string expected)
    {
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(actual, "^" + Regex.Escape(Path.GetFileNameWithoutExtension(expected)) + @" \((?:[2-9]|[1-9][0-9]+)\)" + Regex.Escape(Path.GetExtension(expected)) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }
    private static string LegacyFileName(Identity identity, string extension)
    {
        string name = (identity.Schema + "." + identity.Name).Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim('.', ' '); if (name.Length == 0) name = "resource";
        if (name.Length > 120) name = name[..120].Trim('.', ' ');
        return name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;
    }
    private static string NameKey(Identity identity) => identity.Schema.Length.ToString(CultureInfo.InvariantCulture) + ":" + identity.Schema + identity.Name;
    private static string Display(Identity identity) => SqlName.FormatMultipartName(identity.Schema, identity.Name);
    private static InvalidDataException Bad(string message) => new(message);
}

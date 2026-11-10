using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Migration;

public sealed partial class LegacySourceStage
{
    internal const long PackageMaximumFileBytes = 8L * 1024 * 1024 * 1024;
    internal const long PackageMaximumEntryBytes = 8L * 1024 * 1024 * 1024;
    internal const long PackageMaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    internal const long PackageMaximumDocumentBytes = 4L * 1024 * 1024 * 1024;
    internal const int PackageMaximumMetadataBytes = 16 * 1024 * 1024;
    internal const int PackageMaximumEntries = 10_000;
    internal const int PackageMaximumImageBytes = 16 * 1024 * 1024;
    private const string PackageDocumentsEntry = "database/surf2-documents.json";
    private const string PackageLocalPrefix = "local-files/";

    /// <summary>Freezes a format-1 ZIP without SQL access or extraction into application data.</summary>
    public static async Task<LegacySourceStage> CapturePackageAsync(string packagePath, string stagingRoot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ct.ThrowIfCancellationRequested();
        string original = Path.GetFullPath(packagePath);
        RejectLocalRedirects(Path.GetFullPath(stagingRoot), directory: true);
        await using var source = OpenLocalRead(original);
        await PreflightPackageDirectoryAsync(source, ct);
        byte[] packageHash = await SHA256.HashDataAsync(source, ct);
        source.Position = 0;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = PreflightPackageEntries(archive, ct);
        var manifest = await ReadPackageExportManifestAsync(entries["manifest.json"], ct);
        int localCount = entries.Keys.Count(name => name.StartsWith(PackageLocalPrefix, StringComparison.Ordinal));
        if (manifest.LocalFileCount != localCount)
            throw new InvalidDataException("The format-1 local-file count does not match the ZIP inventory.");

        var stage = new LegacySourceStage(stagingRoot)
            { SourceOrigin = "Package", PackagePath = original, PackageHash = packageHash };
        try
        {
            Directory.CreateDirectory(stage.SourceImageDirectory!);
            Directory.CreateDirectory(Path.Combine(stage.DirectoryPath, "assets"));
            int documentCount = 0;
            if (entries.TryGetValue(PackageDocumentsEntry, out var databaseDocuments))
                documentCount = await stage.ReadPackageDocumentsAsync(databaseDocuments, ct);
            if (documentCount != manifest.DocumentCount)
                throw new InvalidDataException("The format-1 document count does not match its document array.");
            foreach (var (key, definition) in Documents)
            {
                if (stage._documents.ContainsKey(key)) continue;
                string path = stage.DocumentPath(key);
                if (entries.TryGetValue(PackageLocalPrefix + definition.FileName, out var local))
                {
                    await CopyPackageEntryAsync(local, path, PackageMaximumDocumentBytes, ct);
                    await ValidatePackageFileAsync(path, ct);
                    byte[] hash = await HashFileAsync(path, ct);
                    stage._documents.Add(key, await stage.DescribeAsync(key, path, "Package", hash, null, null, ct));
                }
                else
                {
                    await using (var output = CreatePackageOutput(path))
                    {
                        object value = Activator.CreateInstance(definition.Type)
                            ?? throw new InvalidOperationException("Default legacy state could not be constructed.");
                        await JsonSerializer.SerializeAsync(output, value, definition.Type, JsonOptions, ct);
                        await FlushPackageOutputAsync(output, ct);
                    }
                    byte[] hash = await HashFileAsync(path, ct);
                    stage._documents.Add(key, await stage.DescribeAsync(key, path, "Default", hash, null, null, ct));
                }
            }
            foreach (var (name, entry) in entries)
            {
                const string images = PackageLocalPrefix + "PastedDiagramImages/";
                if (!name.StartsWith(images, StringComparison.OrdinalIgnoreCase)) continue;
                string leaf = name[images.Length..];
                if (leaf.Contains('/') || !leaf.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(stage.SourceImageDirectory!, leaf);
                await CopyPackageEntryAsync(entry, path, PackageMaximumImageBytes, ct);
                await RequirePackagePngAsync(path, ct);
                byte[] hash = await HashFileAsync(path, ct);
                string frozen = Path.Combine(stage.DirectoryPath, "assets", Convert.ToHexString(hash).ToLowerInvariant() + ".png");
                if (!File.Exists(frozen)) File.Copy(path, frozen, overwrite: false);
                stage._assets.Add(new(path, frozen, hash));
            }
            stage.ValidateSourceOrigin();
            stage.Fingerprint = stage.ComputeSourceFingerprint();
            await stage.SaveManifestAsync(ct);
            await stage.CheckPackageManifestAsync(ct);
            // The original ZIP stays pinned throughout preflight, decoding, image staging, and manifest publication.
            return stage;
        }
        catch
        {
            await stage.DisposeAsync();
            throw;
        }
    }

    private async Task<int> ReadPackageDocumentsAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using Stream input = entry.Open();
        var reader = new PackageJsonReader(input, Math.Min(entry.Length, PackageMaximumEntryBytes));
        await reader.StartAsync(ct);
        await reader.ExpectAsync('[', ct);
        int ordinal = 0;
        if (await reader.TakeIfAsync(']', ct))
        {
            await reader.EndAsync(ct);
            if (reader.BytesRead != entry.Length) throw new InvalidDataException("The package document entry length is inconsistent.");
            return 0;
        }
        while (true)
        {
            if (ordinal >= Documents.Count) throw new InvalidDataException("A format-1 package has more than six documents.");
            await reader.ExpectAsync('{', ct);
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? key = null;
            DateTimeOffset? timestamp = null;
            string pending = Path.Combine(DirectoryPath, "package-document-" + ordinal.ToString(CultureInfo.InvariantCulture) + ".pending");
            if (await reader.TakeIfAsync('}', ct)) throw new InvalidDataException("An empty package document is unsupported.");
            while (true)
            {
                string name = await reader.MetadataStringAsync(64, ct);
                if (!names.Add(name)) throw new InvalidDataException("A package document has duplicate fields.");
                await reader.ExpectAsync(':', ct);
                switch (name)
                {
                    case "DocumentKey":
                        key = await reader.MetadataStringAsync(64, ct);
                        if (!Documents.ContainsKey(key) || _documents.ContainsKey(key))
                            throw new InvalidDataException("A package document key is unknown or duplicated.");
                        break;
                    case "UpdatedAtUtc":
                        timestamp = ParsePackageTimestamp(await reader.MetadataStringAsync(64, ct));
                        break;
                    case "PayloadJson":
                        await using (var output = CreatePackageOutput(pending))
                        {
                            await reader.StringAsync(output, PackageMaximumDocumentBytes, ct);
                            await FlushPackageOutputAsync(output, ct);
                        }
                        break;
                    default: throw new InvalidDataException("A package document has an unsupported field.");
                }
                if (await reader.TakeIfAsync('}', ct)) break;
                await reader.ExpectAsync(',', ct);
            }
            if (names.Count != 3 || key == null || timestamp == null)
                throw new InvalidDataException("A package document requires its key, payload, and UTC timestamp.");
            await ValidatePackageFileAsync(pending, ct);
            string path = DocumentPath(key);
            File.Move(pending, path, overwrite: false);
            byte[] hash = await HashFileAsync(path, ct);
            _documents.Add(key, await DescribeAsync(key, path, "Package", hash, timestamp, null, ct));
            ordinal++;
            if (await reader.TakeIfAsync(']', ct)) break;
            await reader.ExpectAsync(',', ct);
        }
        await reader.EndAsync(ct);
        if (reader.BytesRead != entry.Length) throw new InvalidDataException("The package document entry length is inconsistent.");
        return ordinal;
    }

    private void ValidatePackageOrigin()
    {
        if (PackagePath == null || !Path.IsPathFullyQualified(PackagePath) || PackageHash is not { Length: 32 } ||
            _documents.Count != Documents.Count || !Documents.Keys.All(_documents.ContainsKey) ||
            string.Equals(Path.GetFullPath(PackagePath), Path.Combine(DirectoryPath, "manifest.json"), StringComparison.OrdinalIgnoreCase) ||
            IsWithin(DirectoryPath, PackagePath) || _assets.Count > PackageMaximumEntries)
            throw new InvalidDataException("Invalid or mixed package origin metadata.");
        foreach (var (key, document) in _documents)
        {
            if (document.SourceKind is not ("Package" or "Default") || document.LocalSourcePath != null ||
                document.SourceHash is not { Length: 32 } || document.StagingHash is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(document.SourceHash, document.StagingHash) ||
                document.ByteCount <= 0 || document.ByteCount > PackageMaximumDocumentBytes ||
                document.OriginalTimestamp?.Offset is { } offset && offset != TimeSpan.Zero ||
                document.SourceKind == "Default" && document.OriginalTimestamp != null ||
                !string.Equals(Path.GetFullPath(document.FilePath), DocumentPath(key), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid package document descriptor.");
        }
        var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _assets)
        {
            string leaf = Path.GetFileName(asset.OriginalPath);
            RequirePackageLeaf(leaf);
            if (!leaf.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !leaves.Add(leaf) || asset.Hash is not { Length: 32 } ||
                !string.Equals(Path.GetFullPath(asset.OriginalPath), Path.Combine(SourceImageDirectory!, leaf), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(asset.FilePath), Path.Combine(DirectoryPath, "assets", Convert.ToHexString(asset.Hash).ToLowerInvariant() + ".png"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A package image must be an owned safe PNG leaf with a content-addressed frozen copy.");
        }
    }

    private byte[] ComputePackageFingerprint()
    {
        if (PackagePath == null || PackageHash is not { Length: 32 }) throw new InvalidDataException("The package has no source fingerprint.");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Utf8, leaveOpen: true))
        {
            writer.Write("Surf2.LegacySourceStage/Package/v1");
            writer.Write(PackagePath);
            writer.Write(PackageHash);
            foreach (var document in _documents.Values.OrderBy(document => document.Key, StringComparer.Ordinal))
            {
                writer.Write(document.Key);
                writer.Write(document.SourceKind);
                writer.Write(document.SourceHash);
                writer.Write(document.OriginalTimestamp?.Ticks ?? 0);
            }
            writer.Write(_assets.Count);
            foreach (var asset in _assets.OrderBy(asset => Path.GetFileName(asset.OriginalPath), StringComparer.Ordinal))
            {
                writer.Write(Path.GetFileName(asset.OriginalPath));
                writer.Write(asset.Hash);
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    private async Task WithVerifiedPackageSourceAsync(Func<Task> publish, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ValidateSourceOrigin();
        if (!CryptographicOperations.FixedTimeEquals(Fingerprint, ComputeSourceFingerprint()))
            throw new InvalidDataException("The package source fingerprint is inconsistent.");
        var locked = new List<FileStream>();
        using var assetPaths = PinPastedImageDirectories();
        try
        {
            await LockAsync(PackagePath!, PackageHash!, PackageMaximumFileBytes, null);
            var manifest = OpenLocalRead(Path.Combine(DirectoryPath, "manifest.json"));
            locked.Add(manifest);
            await CheckPackageManifestAsync(ct);
            foreach (var document in _documents.Values)
                await LockAsync(document.FilePath, document.StagingHash, PackageMaximumDocumentBytes, document.ByteCount);
            foreach (var asset in _assets)
            {
                await LockAsync(asset.OriginalPath, asset.Hash, PackageMaximumImageBytes, null);
                await LockAsync(asset.FilePath, asset.Hash, PackageMaximumImageBytes, null);
            }
            CheckPastedImageAbsences();
            ct.ThrowIfCancellationRequested();
            await publish();
        }
        finally { foreach (var file in locked) await file.DisposeAsync(); }

        async Task LockAsync(string path, byte[] expected, long maximum, long? length)
        {
            var file = OpenLocalRead(path);
            locked.Add(file);
            if (file.Length > maximum || length.HasValue && file.Length != length.Value ||
                !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, ct), expected))
                throw new InvalidDataException("The package or its frozen stage changed after capture: " + Path.GetFileName(path));
        }
    }

    private async Task CheckPackageManifestAsync(CancellationToken ct)
    {
        await using var input = OpenLocalRead(Path.Combine(DirectoryPath, "manifest.json"));
        if (input.Length > PackageMaximumMetadataBytes) throw new InvalidDataException("Package stage metadata exceeds its budget.");
        await ValidatePackageRootJsonAsync(input, ct);
        input.Position = 0;
        using var json = await JsonDocument.ParseAsync(input, cancellationToken: ct);
        bool pastedFreeze = json.RootElement.TryGetProperty("PastedImageFreeze", out _);
        RequirePackageProperties(json.RootElement, pastedFreeze
            ? ["MigrationIdentity", "Fingerprint", "Documents", "Assets", "SourceOrigin", "PackagePath", "PackageHash", "PastedImageFreeze"]
            : ["MigrationIdentity", "Fingerprint", "Documents", "Assets", "SourceOrigin", "PackagePath", "PackageHash"]);
        if (_pastedFreezeStarted && !pastedFreeze) throw new InvalidDataException("The package PNG freeze metadata disappeared.");
        if (pastedFreeze)
        {
            await using var cursor = OpenAt(input.Name, 0);
            if (!await cursor.MoveNextAsync(ct)) throw new InvalidDataException("The package stage manifest is empty.");
            await cursor.ReadObjectAsync(async (name, value, token) =>
            {
                if (name == "PastedImageFreeze") await CheckPastedFreezeManifestAsync(value, token);
                else await value.SkipValueAsync(token);
            }, ct);
        }
        var root = json.RootElement;
        if (root.GetProperty("MigrationIdentity").GetGuid() != MigrationIdentity || root.GetProperty("SourceOrigin").GetString() != "Package" ||
            root.GetProperty("PackagePath").GetString() != PackagePath ||
            !CryptographicOperations.FixedTimeEquals(root.GetProperty("PackageHash").GetBytesFromBase64(), PackageHash!) ||
            !CryptographicOperations.FixedTimeEquals(root.GetProperty("Fingerprint").GetBytesFromBase64(), Fingerprint))
            throw new InvalidDataException("The frozen package identity changed.");
        var documents = root.GetProperty("Documents");
        if (documents.GetArrayLength() != Documents.Count) throw new InvalidDataException("The frozen package document inventory changed.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in documents.EnumerateArray())
        {
            RequirePackageProperties(item, "Key", "FilePath", "SourceKind", "SourceHash", "StagingHash", "ByteCount", "OriginalTimestamp", "LocalSourcePath");
            var actual = item.Deserialize<StagedLegacyDocument>() ?? throw new InvalidDataException("Invalid frozen package document.");
            if (!seen.Add(actual.Key) || !_documents.TryGetValue(actual.Key, out var expected) ||
                actual.FilePath != expected.FilePath || actual.SourceKind != expected.SourceKind || actual.ByteCount != expected.ByteCount ||
                actual.OriginalTimestamp != expected.OriginalTimestamp || actual.LocalSourcePath != expected.LocalSourcePath ||
                actual.SourceHash is not { Length: 32 } || actual.StagingHash is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(actual.SourceHash, expected.SourceHash) ||
                !CryptographicOperations.FixedTimeEquals(actual.StagingHash, expected.StagingHash))
                throw new InvalidDataException("A frozen package document descriptor changed.");
        }
        var assets = root.GetProperty("Assets");
        if (assets.GetArrayLength() != _assets.Count) throw new InvalidDataException("The frozen package asset inventory changed.");
        int ordinal = 0;
        foreach (var item in assets.EnumerateArray())
        {
            RequirePackageProperties(item, "OriginalPath", "FilePath", "Hash");
            var actual = item.Deserialize<StagedLegacyAsset>() ?? throw new InvalidDataException("Invalid frozen package asset.");
            var expected = _assets[ordinal++];
            if (actual.OriginalPath != expected.OriginalPath || actual.FilePath != expected.FilePath || actual.Hash is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(actual.Hash, expected.Hash))
                throw new InvalidDataException("A frozen package asset descriptor changed.");
        }
    }

    private sealed record PackageExportManifest(int DocumentCount, int LocalFileCount);

    private static async Task<PackageExportManifest> ReadPackageExportManifestAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        if (entry.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("The format-1 manifest exceeds its metadata budget.");
        await using Stream stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await stream.ReadExactlyAsync(bytes, ct);
        if (await stream.ReadAsync(new byte[1], ct) != 0) throw new InvalidDataException("The format-1 manifest length is inconsistent.");
        int start = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        using (var grammar = new MemoryStream(bytes, writable: false)) await ValidatePackageRootJsonAsync(grammar, ct);
        using var json = JsonDocument.Parse(bytes.AsMemory(start), new JsonDocumentOptions { MaxDepth = 8 });
        RequirePackageProperties(json.RootElement, "FormatVersion", "ExportedAtUtc", "AppName", "AppVersion", "DatabaseName", "DocumentCount", "LocalFileCount");
        var root = json.RootElement;
        if (root.GetProperty("FormatVersion").GetInt32() != 1 || root.GetProperty("AppName").GetString() != "Surf2")
            throw new InvalidDataException("Only Surf2 format-1 persistence packages are supported.");
        _ = ParsePackageTimestamp(root.GetProperty("ExportedAtUtc").GetString() ?? "");
        foreach (string name in new[] { "AppVersion", "DatabaseName" })
            if ((root.GetProperty(name).GetString() ?? throw new InvalidDataException("Invalid manifest string.")).Length > 1024)
                throw new InvalidDataException("Format-1 manifest strings exceed their metadata budget.");
        int documents = root.GetProperty("DocumentCount").GetInt32(), locals = root.GetProperty("LocalFileCount").GetInt32();
        if (documents is < 0 or > 6 || locals is < 0 or > PackageMaximumEntries)
            throw new InvalidDataException("Format-1 manifest counts exceed their bounds.");
        return new(documents, locals);
    }

    private static void RequirePackageProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Package metadata requires an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Package metadata contains an unknown or duplicate property.");
        if (seen.Count != names.Length) throw new InvalidDataException("Package metadata is missing a required property.");
    }

    private static DateTimeOffset ParsePackageTimestamp(string text)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        // SQL's DateTime was exported with Kind=Unspecified. Its no-suffix representation is still UTC.
        if (text.Length > 64 || !DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
            throw new InvalidDataException("A package timestamp must be an original UTC SQL timestamp.");
        return timestamp;
    }

    private static Dictionary<string, ZipArchiveEntry> PreflightPackageEntries(ZipArchive archive, CancellationToken ct)
    {
        if (archive.Entries.Count > PackageMaximumEntries) throw new InvalidDataException("The package has too many ZIP entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (name.Length > 4096 || name.Contains('\\') || name.EndsWith('/') || name.StartsWith('/') ||
                name != "manifest.json" && name != PackageDocumentsEntry && !name.StartsWith(PackageLocalPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("An unsupported or unsafe ZIP entry path was found.");
            foreach (string segment in name.Split('/')) RequirePackageLeaf(segment);
            if (!aliases.Add(name) || !entries.TryAdd(name, entry)) throw new InvalidDataException("The package contains aliased or duplicate ZIP paths.");
            int type = (int)((uint)entry.ExternalAttributes >> 16) & 0xf000;
            if (type is not (0 or 0x8000) || (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                entry.Length < 0 || entry.Length > PackageMaximumEntryBytes || entry.CompressedLength < 0 || entry.CompressedLength > PackageMaximumFileBytes)
                throw new InvalidDataException("Redirected ZIP entries or entries beyond the size budget are unsupported.");
            expanded = checked(expanded + entry.Length);
            if (expanded > PackageMaximumExpandedBytes) throw new InvalidDataException("The package exceeds its aggregate expanded-size budget.");
        }
        foreach (string name in entries.Keys)
        {
            for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                if (aliases.Contains(name[..slash])) throw new InvalidDataException("The ZIP contains a file/directory path collision.");
        }
        if (!entries.ContainsKey("manifest.json")) throw new InvalidDataException("The package has no format-1 manifest.");
        // Other safe local files, including connection-settings.json, are counted but never opened or extracted.
        return entries;
    }

    private static void RequirePackageLeaf(string leaf)
    {
        if (leaf.Length is 0 or > 255 || leaf is "." or ".." || leaf.EndsWith('.') || leaf.EndsWith(' ') ||
            leaf.Any(c => c < 32 || c is '/' or '\\' or ':' or '"' or '<' or '>' or '|' or '?' or '*'))
            throw new InvalidDataException("A ZIP path contains an unsafe leaf name.");
        string device = leaf.Split('.')[0].TrimEnd(' ');
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (device[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))
            throw new InvalidDataException("A ZIP path contains a reserved device name.");
    }

    private static FileStream CreatePackageOutput(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task FlushPackageOutputAsync(FileStream file, CancellationToken ct)
    { await file.FlushAsync(ct); file.Flush(flushToDisk: true); }

    private static async Task CopyPackageEntryAsync(ZipArchiveEntry entry, string path, long maximum, CancellationToken ct)
    {
        if (entry.Length <= 0 || entry.Length > maximum) throw new InvalidDataException("A selected ZIP entry exceeds its size budget or is empty.");
        await using Stream input = entry.Open();
        await using var output = CreatePackageOutput(path);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            bytes = checked(bytes + count);
            if (bytes > entry.Length || bytes > maximum) throw new InvalidDataException("A ZIP entry exceeded its declared length.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (bytes != entry.Length) throw new InvalidDataException("A ZIP entry is shorter than its declared length.");
        await FlushPackageOutputAsync(output, ct);
    }

    private static async Task RequirePackagePngAsync(string path, CancellationToken ct)
    {
        await using var file = OpenLocalRead(path);
        var signature = new byte[8];
        await file.ReadExactlyAsync(signature, ct);
        if (!signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("A packaged pasted image is not a PNG.");
    }

    private static async Task ValidatePackageFileAsync(string path, CancellationToken ct)
    {
        await using var input = OpenLocalRead(path);
        await ValidatePackageRootJsonAsync(input, ct);
    }

    /// <summary>Regression hook: decode one quoted JSON string, including arbitrary stream chunk boundaries.</summary>
    internal static async Task DecodePackageJsonStringAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        var reader = new PackageJsonReader(input, PackageMaximumEntryBytes);
        await reader.StartAsync(ct);
        await reader.StringAsync(output, PackageMaximumDocumentBytes, ct);
        await reader.EndAsync(ct);
    }

    /// <summary>Regression hook: bounded grammar/UTF-8 validation of one legacy root object, without hydration.</summary>
    internal static async Task ValidatePackageRootJsonAsync(Stream input, CancellationToken ct = default)
    {
        var reader = new PackageJsonReader(input, PackageMaximumDocumentBytes);
        await reader.StartAsync(ct);
        if (await reader.PeekNonWhitespaceAsync(ct) != '{') throw new InvalidDataException("A legacy persistence root must be a JSON object.");
        await reader.ValueAsync(0, ct);
        await reader.EndAsync(ct);
    }

    // Check the central directory's count/size before ZipArchive materializes its entry catalogue.
    private static async Task PreflightPackageDirectoryAsync(FileStream file, CancellationToken ct)
    {
        if (file.Length is < 22 or > PackageMaximumFileBytes) throw new InvalidDataException("The package file exceeds its size budget or is not a ZIP.");
        int tailLength = (int)Math.Min(file.Length, 22 + ushort.MaxValue);
        var tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        await file.ReadExactlyAsync(tail, ct);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            { end = i; break; }
        if (end < 0 || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6)) != 0)
            throw new InvalidDataException("Missing or multi-disk ZIP directory.");
        long endOffset = file.Length - tailLength + end;
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (count != BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8))) throw new InvalidDataException("Multi-disk ZIP is unsupported.");
        long directoryEnd = endOffset;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (endOffset < 76) throw new InvalidDataException("Missing ZIP64 locator.");
            var locator = new byte[20];
            file.Position = endOffset - 20;
            await file.ReadExactlyAsync(locator, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw new InvalidDataException("Invalid or multi-disk ZIP64 locator.");
            ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (zip64Offset != (ulong)(endOffset - 20 - 56)) throw new InvalidDataException("Invalid ZIP64 directory offset.");
            file.Position = (long)zip64Offset;
            var header = new byte[56];
            await file.ReadExactlyAsync(header, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x06064b50 ||
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(4)) != 44 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != 0 ||
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24)) != BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)))
                throw new InvalidDataException("Unsupported ZIP64 directory record.");
            count = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
            size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40));
            offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));
            directoryEnd = (long)zip64Offset;
        }
        if (count > PackageMaximumEntries || size > PackageMaximumMetadataBytes || offset > (ulong)directoryEnd || size != (ulong)directoryEnd - offset)
            throw new InvalidDataException("The ZIP directory exceeds its metadata budget or has invalid bounds.");
        file.Position = (long)offset;
        var record = new byte[46];
        for (ulong i = 0; i < count; i++)
        {
            await file.ReadExactlyAsync(record, ct);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(8));
            ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(10));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(28));
            long rest = nameLength + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(32));
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x02014b50 || (flags & 0x2041) != 0 || compression is not (0 or 8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(34)) != 0 || nameLength is <= 0 or > 4096 || file.Position + rest > directoryEnd)
                throw new InvalidDataException("An encrypted, malformed, or unsupported ZIP directory entry was found.");
            file.Position += rest;
        }
        if (file.Position != directoryEnd) throw new InvalidDataException("The ZIP directory entry count is inconsistent.");
        file.Position = 0;
    }

    private sealed class PackageJsonReader
    {
        private readonly Stream _input;
        private readonly long _maximum;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _position, _length;
        private bool _eof;
        public long BytesRead { get; private set; }

        internal PackageJsonReader(Stream input, long maximum) { _input = input; _maximum = maximum; }

        internal async Task StartAsync(CancellationToken ct)
        {
            if (await PeekByteAsync(ct) == 0xef)
            {
                if (await ByteAsync(ct) != 0xef || await ByteAsync(ct) != 0xbb || await ByteAsync(ct) != 0xbf)
                    throw new InvalidDataException("Invalid UTF-8 BOM.");
            }
        }

        internal async Task EndAsync(CancellationToken ct)
        {
            if (await PeekNonWhitespaceAsync(ct) != -1) throw new InvalidDataException("Trailing JSON content is unsupported.");
        }

        internal async ValueTask<int> PeekNonWhitespaceAsync(CancellationToken ct)
        {
            int value;
            while ((value = await PeekByteAsync(ct)) is 0x20 or 0x09 or 0x0a or 0x0d) _position++;
            return value;
        }

        internal async Task ExpectAsync(char expected, CancellationToken ct)
        {
            if (await PeekNonWhitespaceAsync(ct) != expected) throw new InvalidDataException("Invalid JSON grammar; expected " + expected + ".");
            _position++;
        }

        internal async Task<bool> TakeIfAsync(char expected, CancellationToken ct)
        {
            if (await PeekNonWhitespaceAsync(ct) != expected) return false;
            _position++;
            return true;
        }

        private async ValueTask<int> PeekByteAsync(CancellationToken ct)
        {
            if (_position != _length) return _buffer[_position];
            if (_eof) return -1;
            ct.ThrowIfCancellationRequested();
            _length = await _input.ReadAsync(_buffer.AsMemory(), ct);
            _position = 0;
            BytesRead = checked(BytesRead + _length);
            if (BytesRead > _maximum) throw new InvalidDataException("JSON input exceeds its byte budget.");
            if (_length == 0) { _eof = true; return -1; }
            return _buffer[0];
        }

        private async ValueTask<int> ByteAsync(CancellationToken ct)
        {
            int value = await PeekByteAsync(ct);
            if (value != -1) _position++;
            return value;
        }

        internal async Task<string> MetadataStringAsync(int maximum, CancellationToken ct)
        {
            using var output = new MemoryStream();
            await StringAsync(output, maximum, ct);
            return Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }

        internal async Task StringAsync(Stream? output, long maximum, CancellationToken ct)
        {
            await ExpectAsync('"', ct);
            byte[]? decoded = output == null ? null : new byte[64 * 1024];
            int written = 0, continuation = 0, scalar = 0, minimum = 0;
            long total = 0;
            while (true)
            {
                if (_position == _length && await PeekByteAsync(ct) == -1)
                    throw new InvalidDataException("Unterminated JSON string.");
                int value = _buffer[_position++];
                if (decoded != null && written + 4 > decoded.Length)
                {
                    await output!.WriteAsync(decoded.AsMemory(0, written), ct);
                    written = 0;
                }
                if (continuation != 0)
                {
                    if (value is < 0x80 or > 0xbf) throw new InvalidDataException("Invalid UTF-8 continuation in a JSON string.");
                    scalar = (scalar << 6) | (value & 0x3f);
                    continuation--;
                    if (continuation == 0 && (scalar < minimum || scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff))
                        throw new InvalidDataException("Overlong or invalid UTF-8 scalar in a JSON string.");
                    if (decoded != null) decoded[written++] = (byte)value;
                    total++;
                }
                else if (value >= 0x80)
                {
                    if (value is >= 0xc2 and <= 0xdf) { continuation = 1; scalar = value & 0x1f; minimum = 0x80; }
                    else if (value is >= 0xe0 and <= 0xef) { continuation = 2; scalar = value & 0x0f; minimum = 0x800; }
                    else if (value is >= 0xf0 and <= 0xf4) { continuation = 3; scalar = value & 7; minimum = 0x10000; }
                    else throw new InvalidDataException("Invalid UTF-8 lead byte in a JSON string.");
                    if (decoded != null) decoded[written++] = (byte)value;
                    total++;
                }
                else if (value == '"')
                {
                    if (decoded != null && written != 0) await output!.WriteAsync(decoded.AsMemory(0, written), ct);
                    return;
                }
                else if (value == '\\')
                {
                    int escape = await ByteAsync(ct);
                    int character = escape switch
                    {
                        '"' => '"', '\\' => '\\', '/' => '/', 'b' => 8, 'f' => 12,
                        'n' => 10, 'r' => 13, 't' => 9, 'u' => await HexScalarAsync(ct),
                        _ => throw new InvalidDataException("Invalid JSON string escape.")
                    };
                    if (character is >= 0xd800 and <= 0xdbff)
                    {
                        if (await ByteAsync(ct) != '\\' || await ByteAsync(ct) != 'u')
                            throw new InvalidDataException("A high surrogate requires an escaped low surrogate.");
                        int low = await HexScalarAsync(ct);
                        if (low is < 0xdc00 or > 0xdfff) throw new InvalidDataException("Invalid JSON surrogate pair.");
                        character = 0x10000 + ((character - 0xd800) << 10) + low - 0xdc00;
                    }
                    else if (character is >= 0xdc00 and <= 0xdfff) throw new InvalidDataException("Unpaired low surrogate in a JSON string.");
                    int bytes = character < 0x80 ? 1 : character < 0x800 ? 2 : character < 0x10000 ? 3 : 4;
                    if (decoded != null) written = EncodeScalar(character, decoded, written);
                    total += bytes;
                }
                else
                {
                    if (value < 0x20) throw new InvalidDataException("An unescaped control byte occurred in a JSON string.");
                    if (decoded != null) decoded[written++] = (byte)value;
                    total++;
                }
                if (total > maximum) throw new InvalidDataException("A decoded JSON string exceeds its byte budget.");
            }
        }

        private async Task<int> HexScalarAsync(CancellationToken ct)
        {
            int scalar = 0;
            for (int i = 0; i < 4; i++)
            {
                int value = await ByteAsync(ct);
                int digit = value switch { >= '0' and <= '9' => value - '0', >= 'A' and <= 'F' => value - 'A' + 10,
                    >= 'a' and <= 'f' => value - 'a' + 10, _ => -1 };
                if (digit < 0) throw new InvalidDataException("Invalid or truncated JSON Unicode escape.");
                scalar = (scalar << 4) | digit;
            }
            return scalar;
        }

        private static int EncodeScalar(int scalar, byte[] target, int position)
        {
            if (scalar < 0x80) target[position++] = (byte)scalar;
            else if (scalar < 0x800)
            {
                target[position++] = (byte)(0xc0 | (scalar >> 6));
                target[position++] = (byte)(0x80 | (scalar & 0x3f));
            }
            else if (scalar < 0x10000)
            {
                target[position++] = (byte)(0xe0 | (scalar >> 12));
                target[position++] = (byte)(0x80 | ((scalar >> 6) & 0x3f));
                target[position++] = (byte)(0x80 | (scalar & 0x3f));
            }
            else
            {
                target[position++] = (byte)(0xf0 | (scalar >> 18));
                target[position++] = (byte)(0x80 | ((scalar >> 12) & 0x3f));
                target[position++] = (byte)(0x80 | ((scalar >> 6) & 0x3f));
                target[position++] = (byte)(0x80 | (scalar & 0x3f));
            }
            return position;
        }

        internal async Task ValueAsync(int depth, CancellationToken ct)
        {
            int value = await PeekNonWhitespaceAsync(ct);
            if (depth >= 128 && value is '{' or '[') throw new InvalidDataException("The JSON root exceeds its nesting budget.");
            if (value == '{')
            {
                _position++;
                if (await TakeIfAsync('}', ct)) return;
                while (true)
                {
                    await StringAsync(null, _maximum, ct);
                    await ExpectAsync(':', ct);
                    await ValueAsync(depth + 1, ct);
                    if (await TakeIfAsync('}', ct)) return;
                    await ExpectAsync(',', ct);
                }
            }
            if (value == '[')
            {
                _position++;
                if (await TakeIfAsync(']', ct)) return;
                while (true)
                {
                    await ValueAsync(depth + 1, ct);
                    if (await TakeIfAsync(']', ct)) return;
                    await ExpectAsync(',', ct);
                }
            }
            if (value == '"') { await StringAsync(null, _maximum, ct); return; }
            string? literal = value switch { 't' => "true", 'f' => "false", 'n' => "null", _ => null };
            if (literal != null)
            {
                foreach (char expected in literal)
                    if (await ByteAsync(ct) != expected) throw new InvalidDataException("Invalid JSON literal.");
                return;
            }
            await NumberAsync(ct);
        }

        private async Task NumberAsync(CancellationToken ct)
        {
            if (await PeekByteAsync(ct) == '-') _position++;
            int value = await PeekByteAsync(ct);
            if (value == '0') _position++;
            else if (value is >= '1' and <= '9') await DigitsAsync(ct);
            else throw new InvalidDataException("Invalid JSON value or number.");
            if (await PeekByteAsync(ct) == '.') { _position++; await DigitsAsync(ct); }
            if (await PeekByteAsync(ct) is 'e' or 'E')
            {
                _position++;
                if (await PeekByteAsync(ct) is '+' or '-') _position++;
                await DigitsAsync(ct);
            }
        }

        private async Task DigitsAsync(CancellationToken ct)
        {
            if (await PeekByteAsync(ct) is < '0' or > '9') throw new InvalidDataException("A JSON number is missing digits.");
            while (await PeekByteAsync(ct) is >= '0' and <= '9') _position++;
        }
    }
}

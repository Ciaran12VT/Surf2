using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.Migration;

public sealed record StagedLegacyDocument(string Key, string FilePath, string SourceKind,
    byte[] SourceHash, byte[] StagingHash, long ByteCount, DateTimeOffset? OriginalTimestamp,
    string? LocalSourcePath);
public sealed record StagedLegacyAsset(string OriginalPath, string FilePath, byte[] Hash);
public sealed record StagedPastedImageReference(string OriginalPath, bool IsMissing);
public sealed record StagedPastedImageFreeze(int Version, bool Completed, IReadOnlyList<StagedPastedImageReference> References);
public sealed record LegacyStageManifest(Guid MigrationIdentity, byte[] Fingerprint,
    IReadOnlyList<StagedLegacyDocument> Documents, IReadOnlyList<StagedLegacyAsset> Assets,
    string SourceOrigin = "Sql",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackagePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? PackageHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StagedPastedImageFreeze? PastedImageFreeze = null);

public sealed partial class LegacySourceStage : IAsyncDisposable
{
    public static readonly IReadOnlyDictionary<string, (string FileName, Type Type)> Documents =
        new Dictionary<string, (string, Type)>(StringComparer.Ordinal)
        {
            ["scope-library"] = ("scopes.json", typeof(ScopeLibrary)),
            ["database-snapshots"] = ("database-snapshots.json", typeof(DatabaseSnapshotLibrary)),
            ["diagram-library"] = ("diagrams.json", typeof(DiagramLibrary)),
            ["workbench-library"] = ("workbenches.json", typeof(WorkbenchLibrary)),
            ["workspace-state"] = ("workspace-state.json", typeof(WorkspaceState)),
            ["app-settings"] = ("settings.json", typeof(AppSettings))
        };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly Dictionary<string, StagedLegacyDocument> _documents = new(StringComparer.Ordinal);
    private readonly string _ownedRoot;
    private readonly List<StagedLegacyAsset> _assets = [];

    private LegacySourceStage(string stagingRoot, Guid? identity = null)
    {
        MigrationIdentity = identity ?? Guid.NewGuid();
        _ownedRoot = Path.GetFullPath(stagingRoot);
        DirectoryPath = Path.Combine(_ownedRoot, MigrationIdentity.ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public Guid MigrationIdentity { get; }
    public string DirectoryPath { get; }
    public IReadOnlyDictionary<string, StagedLegacyDocument> StagedDocuments => _documents;
    public string SourceOrigin { get; private set; } = "Sql";
    public string? PackagePath { get; private set; }
    public byte[]? PackageHash { get; private set; }
    public string? SourceImageDirectory => DeriveSourceImageDirectory();
    public byte[] Fingerprint { get; private set; } = [];
    public bool RetainForRecovery { get; set; }

    public async Task SaveManifestAsync(CancellationToken ct = default)
    {
        using var paths = new DirectoryPins();
        paths.Add(DirectoryPath);
        string manifest = Path.Combine(DirectoryPath, "manifest.json");
        RejectLocalRedirects(manifest, directory: false);
        string temporary = Path.Combine(DirectoryPath, "manifest." + Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, new LegacyStageManifest(MigrationIdentity, Fingerprint,
                    _documents.Values.ToArray(), _assets.ToArray(), SourceOrigin, PackagePath, PackageHash, PastedFreezeDescriptor()), cancellationToken: ct);
                if (output.Length > (SourceOrigin == "Package" ? PackageMaximumMetadataBytes : 64L * 1024 * 1024))
                    throw new InvalidDataException("The stage manifest exceeds its metadata byte budget.");
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            RejectLocalRedirects(manifest, directory: false);
            File.Move(temporary, manifest, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<LegacySourceStage> ReopenAsync(string stageDirectory, CancellationToken ct = default)
    {
        string directory = Path.GetFullPath(stageDirectory);
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid identity)) throw new InvalidDataException("Not a migration staging directory.");
        await using var input = OpenLocalRead(Path.Combine(directory, "manifest.json"));
        if (input.Length > 64L * 1024 * 1024) throw new InvalidDataException("Migration manifest exceeds its metadata budget.");
        var manifest = await JsonSerializer.DeserializeAsync<LegacyStageManifest>(input, cancellationToken: ct)
            ?? throw new InvalidDataException("Migration manifest is empty.");
        if (manifest.MigrationIdentity != identity || manifest.Fingerprint is not { Length: 32 } ||
            manifest.Documents == null || manifest.Documents.Count != Documents.Count || manifest.Assets == null ||
            manifest.SourceOrigin is not ("Sql" or "Local" or "Package"))
            throw new InvalidDataException("Migration manifest identity is invalid.");
        var stage = new LegacySourceStage(Path.GetDirectoryName(directory)!, identity)
            { Fingerprint = manifest.Fingerprint, RetainForRecovery = true, SourceOrigin = manifest.SourceOrigin,
                PackagePath = manifest.PackagePath, PackageHash = manifest.PackageHash };
        foreach (var document in manifest.Documents)
        {
            if (!Documents.ContainsKey(document.Key) || !IsWithin(directory, document.FilePath) ||
                document.SourceKind is not ("Sql" or "Local" or "Package" or "Default") || document.SourceHash is not { Length: 32 } ||
                document.StagingHash is not { Length: 32 } || document.ByteCount < 0)
                throw new InvalidDataException("Migration manifest contains an invalid document descriptor.");
            stage._documents.Add(document.Key, document);
        }
        if (manifest.Assets.Count > MaximumPastedImageReferences) throw new InvalidDataException("Migration asset metadata exceeds its count budget.");
        foreach (var asset in manifest.Assets)
        {
            if (asset == null || !IsWithin(directory, asset.FilePath) || asset.Hash is not { Length: 32 }) throw new InvalidDataException("Migration manifest contains an invalid asset path.");
            stage._assets.Add(asset);
        }
        stage.RestorePastedFreeze(manifest.PastedImageFreeze);
        stage.ValidateSourceOrigin();
        if (!CryptographicOperations.FixedTimeEquals(stage.Fingerprint, stage.ComputeSourceFingerprint()))
            throw new InvalidDataException("Migration manifest source fingerprint is inconsistent.");
        if (stage.SourceOrigin == "Package") await stage.CheckPackageManifestAsync(ct);
        else await stage.CheckNonPackageManifestAsync(Path.Combine(directory, "manifest.json"), ct);
        if (stage.PastedImagesFrozen) await stage.VerifyFrozenPastedImagesAsync(ct);
        return stage;
    }

    public async Task RegisterAssetAsync(string originalPath, string stagedPath, CancellationToken ct = default)
    {
        if (PastedImagesFrozen) throw new InvalidOperationException("The completed pasted-image freeze cannot be changed.");
        if (!IsWithin(DirectoryPath, stagedPath)) throw new InvalidDataException("An asset must be inside the migration staging directory.");
        originalPath = Path.GetFullPath(originalPath);
        string? imageDirectory = SourceImageDirectory;
        if (imageDirectory != null && !string.Equals(Path.GetDirectoryName(originalPath), imageDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A pasted image cannot come from a different source directory than the frozen stage.");
        await using var staged = OpenLocalRead(stagedPath);
        if (staged.Length is <= 0 or > MaximumPastedImageBytes) throw new InvalidDataException("A pasted PNG exceeds its byte budget.");
        byte[] hash = await SHA256.HashDataAsync(staged, ct);
        ValidateFrozenAssetDescriptor(new(originalPath, Path.GetFullPath(stagedPath), hash));
        var existing = _assets.FirstOrDefault(asset => string.Equals(asset.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.Hash, hash)) throw new InvalidDataException("A pasted image changed during conversion.");
            return;
        }
        if (_assets.Count >= MaximumPastedImageReferences) throw new InvalidDataException("Migration asset metadata exceeds its count budget.");
        _assets.Add(new(Path.GetFullPath(originalPath), Path.GetFullPath(stagedPath), hash));
        ValidatePastedMetadata();
        await SaveManifestAsync(ct);
    }

    private static bool IsWithin(string directory, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static async Task<LegacySourceStage> CaptureAsync(string sourceConnectionString,
        string localAppDataDirectory, string stagingRoot,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = await new PersistenceFormatProbe().ProbeAsync(sourceConnectionString, cancellationToken);
        if (source.Format != PersistenceFormat.Legacy)
        {
            throw new InvalidOperationException("The source is not a readable supported legacy database.");
        }

        string localRoot = Path.GetFullPath(localAppDataDirectory);
        var stage = new LegacySourceStage(stagingRoot);
        try
        {
            var session = new RelationalSession(sourceConnectionString);
            await using var connection = await session.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            command.CommandText = """
SELECT DocumentKey, UpdatedAtUtc,
       HASHBYTES('SHA2_256', CONVERT(varbinary(max), PayloadJson)),
       DATALENGTH(PayloadJson), PayloadJson
FROM app.Surf2Documents ORDER BY DocumentKey;
""";
            using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    string key = reader.GetString(0);
                    if (!Documents.ContainsKey(key))
                    {
                        throw new InvalidDataException("The source contains an unsupported persistence document key.");
                    }

                    var timestamp = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
                    byte[] hash = (byte[])reader.GetValue(2);
                    long sourceBytes = reader.GetInt64(3);
                    if (sourceBytes == 0 || hash.Length != 32)
                    {
                        throw new InvalidDataException("A legacy SQL document is empty or has no fingerprint.");
                    }

                    progress?.Report("Staging " + key);
                    string path = stage.DocumentPath(key);
                    using TextReader input = reader.GetTextReader(4);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var writer = new StreamWriter(output, Utf8, 16 * 1024, leaveOpen: false))
                    {
                        char[] buffer = new char[16 * 1024];
                        int count;
                        while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                        {
                            await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        }
                    }

                    // Persist the image-origin root even when no document needs local fallback; it is not part of the SQL hash preimage.
                    stage._documents.Add(key, await stage.DescribeAsync(key, path, "Sql", hash, timestamp,
                        Path.Combine(localRoot, Documents[key].FileName), cancellationToken));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            foreach (var (key, definition) in Documents)
            {
                if (stage._documents.ContainsKey(key))
                {
                    continue;
                }

                string localPath = Path.Combine(localRoot, definition.FileName);
                string path = stage.DocumentPath(key);
                string kind;
                FileStream? local = null;
                try { local = OpenLocalRead(localPath); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    RequireLocalAbsent(localPath);
                }
                await using var localInput = local;
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (localInput != null)
                    {
                        // SQL absence, not a SQL read failure, is what permits local fallback.
                        await localInput.CopyToAsync(output, cancellationToken);
                        kind = "Local";
                    }
                    else
                    {
                        object value = Activator.CreateInstance(definition.Type)
                            ?? throw new InvalidOperationException("Default legacy state could not be constructed.");
                        await JsonSerializer.SerializeAsync(output, value, definition.Type, JsonOptions, cancellationToken);
                        kind = "Default";
                    }
                }

                byte[] hash = await HashFileAsync(path, cancellationToken);
                stage._documents.Add(key, await stage.DescribeAsync(key, path, kind, hash, null,
                    localPath, cancellationToken));
            }

            stage.ValidateSourceOrigin();
            stage.Fingerprint = ComputeFingerprint(stage._documents.Values);
            await stage.SaveManifestAsync(cancellationToken);
            return stage;
        }
        catch
        {
            await stage.DisposeAsync();
            throw;
        }
    }

    public Task VerifyUnchangedAsync(string? sourceConnectionString, CancellationToken cancellationToken = default) =>
        WithVerifiedSourceAsync(sourceConnectionString, () => Task.CompletedTask, cancellationToken);

    public async Task WithVerifiedSourceAsync(string? sourceConnectionString, Func<Task> publish,
        CancellationToken cancellationToken = default)
    {
        if (SourceOrigin == "Local")
        {
            await WithVerifiedLocalSourceAsync(publish, cancellationToken);
            return;
        }
        if (SourceOrigin == "Package")
        {
            await WithVerifiedPackageSourceAsync(publish, cancellationToken);
            return;
        }
        if (SourceOrigin != "Sql") throw new InvalidDataException("Unsupported legacy source origin.");
        ValidateSourceOrigin();
        using var assetPaths = PinPastedImageDirectories();
        var expected = _documents.Values.Where(document => document.SourceKind == "Sql")
            .ToDictionary(document => document.Key, StringComparer.Ordinal);
        var session = new RelationalSession(sourceConnectionString!);
        await using var connection = await session.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT DocumentKey, HASHBYTES('SHA2_256', CONVERT(varbinary(max), PayloadJson)), UpdatedAtUtc
FROM app.Surf2Documents ORDER BY DocumentKey;
""";
        command.CommandTimeout = 120;
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                string key = reader.GetString(0);
                if (!expected.Remove(key, out var original) ||
                    !CryptographicOperations.FixedTimeEquals(original.SourceHash, (byte[])reader.GetValue(1)) ||
                    original.OriginalTimestamp?.UtcDateTime != DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc))
                {
                    throw new InvalidDataException("The legacy SQL source changed after staging. Conversion cannot be activated.");
                }
            }
        }

        if (expected.Count != 0)
        {
            throw new InvalidDataException("A legacy SQL source document disappeared after staging.");
        }

        var lockedFiles = new List<FileStream>();
        try
        {
            var manifest = OpenLocalRead(Path.Combine(DirectoryPath, "manifest.json"));
            lockedFiles.Add(manifest);
            if (manifest.Length > 64L * 1024 * 1024) throw new InvalidDataException("The source manifest exceeds its metadata budget.");
            await CheckNonPackageManifestAsync(manifest.Name, cancellationToken);
            foreach (var document in _documents.Values)
            {
                if (document.SourceKind == "Default" && document.LocalSourcePath != null)
                    RequireLocalAbsent(document.LocalSourcePath);
                if (document.SourceKind == "Local" && document.LocalSourcePath != null)
                {
                    await LockAndCheckAsync(document.LocalSourcePath, document.SourceHash);
                }
                await LockAndCheckAsync(document.FilePath, document.StagingHash);
                if (lockedFiles[^1].Length != document.ByteCount) throw new InvalidDataException("Staged document byte count changed.");
            }
            foreach (var asset in _assets)
            {
                await LockAndCheckAsync(asset.OriginalPath, asset.Hash);
                await LockAndCheckAsync(asset.FilePath, asset.Hash);
            }
            foreach (var document in _documents.Values)
                if (document.SourceKind == "Default" && document.LocalSourcePath != null) RequireLocalAbsent(document.LocalSourcePath);
            CheckPastedImageAbsences();
            // Source SQL range locks and file read handles remain held until the destination is published.
            await publish();
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            foreach (var file in lockedFiles) await file.DisposeAsync();
        }

        async Task LockAndCheckAsync(string path, byte[] expectedHash)
        {
            var file = OpenLocalRead(path);
            lockedFiles.Add(file);
            byte[] hash = await SHA256.HashDataAsync(file, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(hash, expectedHash)) throw new InvalidDataException("Source or staging content changed after capture: " + Path.GetFileName(path));
        }
    }

    public StreamingJsonCursor OpenDocument(string key) =>
        OpenAt(_documents[key].FilePath, 0);

    public static StreamingJsonCursor OpenAt(string path, long offset)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek(offset, SeekOrigin.Begin);
        return new StreamingJsonCursor(stream, baseOffset: offset);
    }

    public static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private string DocumentPath(string key) => Path.Combine(DirectoryPath, key + ".json");

    private static byte[] ComputeFingerprint(IEnumerable<StagedLegacyDocument> documents, string sourceOrigin = "Sql")
    {
        if (sourceOrigin is not ("Sql" or "Local")) throw new InvalidDataException("Unsupported legacy source origin.");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Utf8, leaveOpen: true))
        {
            // Keep the original SQL preimage byte-for-byte; local stages use a disjoint, versioned prefix.
            if (sourceOrigin == "Local") writer.Write("Surf2.LegacySourceStage/Local/v1");
            foreach (var document in documents.OrderBy(document => document.Key, StringComparer.Ordinal))
            {
                writer.Write(document.Key);
                writer.Write(document.SourceKind);
                writer.Write(document.SourceHash);
                writer.Write(document.OriginalTimestamp?.Ticks ?? 0);
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    private async Task<StagedLegacyDocument> DescribeAsync(string key, string path, string kind,
        byte[] sourceHash, DateTimeOffset? originalTimestamp, string? localSourcePath, CancellationToken ct) =>
        new(key, path, kind, sourceHash, await HashFileAsync(path, ct), new FileInfo(path).Length,
            originalTimestamp, localSourcePath);

    public ValueTask DisposeAsync()
    {
        if (!RetainForRecovery && Directory.Exists(DirectoryPath))
        {
            string expected = Path.Combine(_ownedRoot, MigrationIdentity.ToString("N"));
            if (!string.Equals(Path.GetFullPath(DirectoryPath), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to clean a directory not owned by this migration.");
            }

            Directory.Delete(DirectoryPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

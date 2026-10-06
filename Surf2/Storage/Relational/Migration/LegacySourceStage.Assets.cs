using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Migration;

public sealed partial class LegacySourceStage
{
    internal const int MaximumPastedImageReferences = 100_000;
    internal const int MaximumPastedImageBytes = 16 * 1024 * 1024;
    private const long MaximumPastedMetadataBytes = 64L * 1024 * 1024;
    private readonly Dictionary<string, StagedPastedImageReference> _pastedReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pastedFreezeGate = new(1, 1);
    private bool _pastedFreezeStarted;
    public bool PastedImagesFrozen { get; private set; }
    public IReadOnlyList<StagedPastedImageReference> StagedPastedImageReferences => Array.AsReadOnly(_pastedReferences.Values.ToArray());
    public IReadOnlyList<StagedLegacyAsset> StagedAssets => Array.AsReadOnly(_assets.Select(a => a with { Hash = a.Hash.ToArray() }).ToArray());

    /// <summary>Call after six-root schema preflight, before creating/installing/writing the destination.</summary>
    public async Task FreezeReferencedPastedImagesAsync(CancellationToken ct = default)
    {
        await _pastedFreezeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ValidateSourceOrigin();
            using var paths = PinPastedImageDirectories();
            _pastedFreezeStarted = true;
            string? sourceImageDirectory = SourceImageDirectory;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await ScanPastedImageReferencesAsync(async (leaf, token) =>
            {
                string directory = sourceImageDirectory ?? throw new InvalidDataException(
                    "The old stage does not record its pasted-image source directory. Capture a new stage before converting referenced images.");
                string original = StateImages.LeafPath(directory, leaf);
                if (!seen.Add(original)) return;
                if (seen.Count > MaximumPastedImageReferences) throw new InvalidDataException("Referenced PNG count exceeds the staging metadata budget.");
                if (_pastedReferences.TryGetValue(original, out var previous))
                {
                    if (previous.IsMissing) RequireLocalAbsent(original);
                    else await VerifyFrozenAssetAsync(FindFrozenAsset(original), verifyOriginal: true, token, validateBytes: true);
                    return;
                }
                if (PastedImagesFrozen) throw new InvalidDataException("The completed stage gained a referenced pasted PNG.");
                var existing = _assets.FirstOrDefault(a => string.Equals(a.OriginalPath, original, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    await VerifyFrozenAssetAsync(existing, verifyOriginal: true, token, validateBytes: true);
                    _pastedReferences.Add(original, new(original, false));
                    ValidatePastedMetadata();
                    await SaveManifestAsync(token);
                    return;
                }
                // ZIP inventory, not ambient files added to its review staging directory, defines package presence.
                if (SourceOrigin == "Package")
                {
                    RequireLocalAbsent(original);
                    _pastedReferences.Add(original, new(original, true));
                    ValidatePastedMetadata();
                    await SaveManifestAsync(token);
                    return;
                }
                FileStream? source = null;
                try { source = OpenLocalRead(original); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    RequireLocalAbsent(original);
                    _pastedReferences.Add(original, new(original, true));
                    ValidatePastedMetadata();
                    await SaveManifestAsync(token);
                }
                if (source == null) return;
                await using (source)
                {
                    if (source.Length is <= 0 or > MaximumPastedImageBytes) throw new InvalidDataException("A referenced pasted PNG is empty or exceeds 16 MiB.");
                    var bytes = new byte[checked((int)source.Length)];
                    await source.ReadExactlyAsync(bytes, token);
                    if (await source.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("A referenced pasted PNG changed while freezing.");
                    StateImages.Validate(bytes, new StateLimits(), requirePng: true, cancellationToken: token);
                    byte[] hash = SHA256.HashData(bytes);
                    string staged = await WriteFrozenPngAsync(bytes, hash, token);
                    _pastedReferences.Add(original, new(original, false));
                    await RegisterAssetAsync(original, staged, token);
                }
            }, ct);
            if (_pastedReferences.Count != seen.Count) throw new InvalidDataException("The frozen PNG reference inventory differs from the staged diagrams/workbenches.");
            if (SourceOrigin != "Package" && _assets.Count != _pastedReferences.Values.Count(reference => !reference.IsMissing))
                throw new InvalidDataException("The non-package PNG stage contains assets without an authoritative diagram/workbench reference.");
            ValidatePastedMetadata();
            CheckPastedImageAbsences();
            PastedImagesFrozen = true;
            await SaveManifestAsync(ct);
            await VerifyFrozenPastedImagesAsync(ct);
        }
        finally { _pastedFreezeGate.Release(); }
    }

    public void RequirePastedImagesFrozen()
    {
        if (!PastedImagesFrozen) throw new InvalidOperationException("Freeze referenced pasted images before any destination creation, installation or migration writes.");
    }

    /// <summary>A frozen absence deliberately throws into State's existing inline/definition fallback, never live-file lookup.</summary>
    public async Task<byte[]?> ReadFrozenPngAsync(string originalPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequirePastedImagesFrozen();
        originalPath = Path.GetFullPath(originalPath);
        if (!_pastedReferences.TryGetValue(originalPath, out var reference))
            throw new InvalidDataException("A pasted filename is not covered by the completed source freeze.");
        if (reference.IsMissing) throw new FileNotFoundException("This pasted PNG was absent in the frozen source; use preserved inline/definition bytes.");
        var asset = FindFrozenAsset(originalPath);
        try
        {
            await using var file = OpenLocalRead(asset.FilePath);
            if (file.Length is <= 0 or > MaximumPastedImageBytes) throw new InvalidDataException("A frozen PNG is empty or oversized.");
            var bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes, ct);
            if (await file.ReadAsync(new byte[1], ct) != 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), asset.Hash))
                throw new InvalidDataException("A frozen PNG changed; the original cannot replace it on resume.");
            StateImages.Validate(bytes, new StateLimits(), requirePng: true, cancellationToken: ct);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("A frozen present PNG is missing, inaccessible or corrupt; live-source fallback is forbidden.", error); }
    }

    public async Task VerifyFrozenPastedImagesAsync(CancellationToken ct = default)
    {
        RequirePastedImagesFrozen();
        using var paths = PinPastedImageDirectories();
        string? directory = SourceImageDirectory;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await ScanPastedImageReferencesAsync((leaf, _) =>
        {
            string original = StateImages.LeafPath(directory ?? throw new InvalidDataException("The frozen source has no image directory."), leaf);
            if (!_pastedReferences.ContainsKey(original)) throw new InvalidDataException("A staged PNG reference has no frozen present/absent descriptor.");
            seen.Add(original);
            return Task.CompletedTask;
        }, ct);
        if (seen.Count != _pastedReferences.Count) throw new InvalidDataException("The frozen PNG reference coverage changed.");
        foreach (var asset in _assets) await VerifyFrozenAssetAsync(asset, verifyOriginal: true, ct);
        CheckPastedImageAbsences();
    }

    internal bool IsFrozenPastedImageMissing(string originalPath) => _pastedReferences.TryGetValue(Path.GetFullPath(originalPath), out var value) && value.IsMissing;
    private StagedLegacyAsset FindFrozenAsset(string original) => _assets.SingleOrDefault(a => string.Equals(a.OriginalPath, original, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException("A frozen present PNG has no asset descriptor.");
    private StagedPastedImageFreeze? PastedFreezeDescriptor() => !_pastedFreezeStarted ? null : new(1, PastedImagesFrozen, _pastedReferences.Values.ToArray());

    private void RestorePastedFreeze(StagedPastedImageFreeze? descriptor)
    {
        if (descriptor == null) return; // Old manifests remain readable; they must be frozen before import.
        if (descriptor.Version != 1 || descriptor.References == null || descriptor.References.Count > MaximumPastedImageReferences)
            throw new InvalidDataException("Unsupported or oversized pasted-image freeze metadata.");
        _pastedFreezeStarted = true; PastedImagesFrozen = descriptor.Completed;
        foreach (var reference in descriptor.References)
            if (reference == null || !_pastedReferences.TryAdd(reference.OriginalPath, reference))
                throw new InvalidDataException("Duplicate or invalid frozen PNG reference metadata.");
        ValidatePastedMetadata();
    }

    private void ValidateFrozenAssetDescriptor(StagedLegacyAsset asset)
    {
        if (asset.Hash is not { Length: 32 } || asset.OriginalPath is not { Length: > 0 and <= 32768 } ||
            asset.FilePath is not { Length: > 0 and <= 32768 } || !Path.IsPathFullyQualified(asset.OriginalPath) || !Path.IsPathFullyQualified(asset.FilePath))
            throw new InvalidDataException("Invalid frozen PNG descriptor.");
        string original = Path.GetFullPath(asset.OriginalPath);
        string directory = SourceImageDirectory ?? Path.GetDirectoryName(original)!;
        RequirePackageLeaf(Path.GetFileName(original));
        if (!string.Equals(original, StateImages.LeafPath(directory, Path.GetFileName(original)), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(asset.FilePath), Path.Combine(DirectoryPath, "assets", Convert.ToHexString(asset.Hash).ToLowerInvariant() + ".png"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen PNG paths must address the protected source leaf and content-addressed owned staging copy.");
    }

    private void ValidatePastedMetadata()
    {
        if (_assets.Count > MaximumPastedImageReferences || _pastedReferences.Count > MaximumPastedImageReferences)
            throw new InvalidDataException("Frozen PNG metadata exceeds its count budget.");
        long bytes = 0;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _assets)
        {
            ValidateFrozenAssetDescriptor(asset);
            if (!present.Add(asset.OriginalPath)) throw new InvalidDataException("Duplicate frozen PNG source path.");
            // Six bytes per UTF-16 code unit covers JSON's escaped path representation, including non-ASCII names.
            bytes = checked(bytes + 256 + ((long)asset.OriginalPath.Length + asset.FilePath.Length) * 6);
        }
        string? directory = SourceImageDirectory;
        foreach (var reference in _pastedReferences.Values)
        {
            RequirePackageLeaf(Path.GetFileName(reference.OriginalPath));
            if (reference.OriginalPath.Length > 32768 || !Path.IsPathFullyQualified(reference.OriginalPath) || directory == null ||
                !string.Equals(reference.OriginalPath, StateImages.LeafPath(directory, Path.GetFileName(reference.OriginalPath)), StringComparison.OrdinalIgnoreCase) ||
                reference.IsMissing == present.Contains(reference.OriginalPath))
                throw new InvalidDataException("Frozen present/absent PNG descriptors disagree with their source root or asset inventory.");
            bytes = checked(bytes + 128 + (long)reference.OriginalPath.Length * 6);
        }
        if (bytes > (SourceOrigin == "Package" ? PackageMaximumMetadataBytes : MaximumPastedMetadataBytes))
            throw new InvalidDataException("Frozen PNG metadata exceeds its byte budget.");
    }

    internal async Task CheckPastedFreezeManifestAsync(StreamingJsonCursor value, CancellationToken ct)
    {
        if (value.TokenType == JsonTokenType.Null)
        {
            if (_pastedFreezeStarted) throw new InvalidDataException("Frozen PNG metadata disappeared.");
            return;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        int references = 0;
        await value.ReadObjectAsync(async (name, cursor, token) =>
        {
            names.Add(name);
            switch (name)
            {
                case "Version":
                    if (!_pastedFreezeStarted || (await cursor.ReadValueAsync(token)).GetInt32() != 1) throw new InvalidDataException("The PNG freeze version changed.");
                    break;
                case "Completed":
                    if ((await cursor.ReadValueAsync(token)).GetBoolean() != PastedImagesFrozen) throw new InvalidDataException("The PNG freeze completion state changed.");
                    break;
                case "References":
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await cursor.ReadArrayAsync(async (ordinal, item, inner) =>
                    {
                        if (ordinal >= _pastedReferences.Count) throw new InvalidDataException("The PNG manifest gained references.");
                        JsonElement json = await item.ReadValueAsync(inner);
                        RequirePackageProperties(json, "OriginalPath", "IsMissing");
                        var actual = json.Deserialize<StagedPastedImageReference>() ?? throw new InvalidDataException("Invalid PNG reference descriptor.");
                        if (!seen.Add(actual.OriginalPath) || !_pastedReferences.TryGetValue(actual.OriginalPath, out var expected) || actual != expected)
                            throw new InvalidDataException("A PNG present/absent reference descriptor changed.");
                        references++;
                    }, token);
                    break;
                default: throw new InvalidDataException("Unsupported PNG freeze metadata property.");
            }
        }, ct);
        if (names.Count != 3 || references != _pastedReferences.Count) throw new InvalidDataException("PNG freeze descriptor coverage changed.");
    }

    internal void CheckPastedImageAbsences()
    {
        ValidatePastedMetadata();
        foreach (var reference in _pastedReferences.Values)
            if (reference.IsMissing) RequireLocalAbsent(reference.OriginalPath);
    }

    private async Task ScanPastedImageReferencesAsync(Func<string, CancellationToken, Task> read, CancellationToken ct)
    {
        foreach (string key in new[] { "diagram-library", "workbench-library" })
        {
            var descriptor = _documents[key];
            await using var file = OpenLocalRead(descriptor.FilePath);
            if (file.Length != descriptor.ByteCount || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, ct), descriptor.StagingHash))
                throw new InvalidDataException("A staged diagram/workbench root changed before PNG freezing.");
            file.Position = 0;
            await using var cursor = new StreamingJsonCursor(file, leaveOpen: true);
            if (!await cursor.MoveNextAsync(ct)) throw new InvalidDataException("A staged diagram/workbench root is empty.");
            string array = key == "diagram-library" ? "Diagrams" : "Workbenches";
            await cursor.ReadObjectAsync(async (name, value, token) =>
            {
                if (name != array) { await value.SkipValueAsync(token); return; }
                await value.ReadArrayAsync(async (ordinal, item, inner) =>
                {
                    if (ordinal >= 1_000_000) throw new InvalidDataException("PNG discovery exceeds its aggregate-count budget.");
                    if (key == "diagram-library") await DiagramAsync(item, inner);
                    else await item.ReadObjectAsync(async (property, nested, cancellation) =>
                    {
                        if (property == "ActiveDiagramSnapshot" && nested.TokenType != JsonTokenType.Null) await DiagramAsync(nested, cancellation);
                        else await nested.SkipValueAsync(cancellation);
                    }, inner);
                }, token);
            }, ct);
            if (await cursor.MoveNextAsync(ct)) throw new InvalidDataException("A staged diagram/workbench root has trailing values.");
        }

        Task DiagramAsync(StreamingJsonCursor cursor, CancellationToken token) => cursor.ReadObjectAsync(async (name, value, inner) =>
        {
            if (name != "Objects") { await value.SkipValueAsync(inner); return; }
            await value.ReadArrayAsync(async (ordinal, item, cancellation) =>
            {
                if (ordinal >= 100_000) throw new InvalidDataException("A selected diagram exceeds PNG discovery's object-count budget.");
                await item.ReadObjectAsync(async (property, scalar, next) =>
                {
                    if (property != "PastedImageFileName") { await scalar.SkipValueAsync(next); return; }
                    scalar.Require(JsonTokenType.String);
                    if (!string.IsNullOrWhiteSpace(scalar.Value)) { RequirePackageLeaf(scalar.Value!); await read(scalar.Value!, next); }
                }, cancellation);
            }, inner);
        }, token);
    }

    private async Task<string> WriteFrozenPngAsync(byte[] bytes, byte[] hash, CancellationToken ct)
    {
        string directory = Path.Combine(DirectoryPath, "assets");
        RejectLocalRedirects(directory, directory: true);
        Directory.CreateDirectory(directory);
        RejectLocalRedirects(directory, directory: true);
        string path = Path.Combine(directory, Convert.ToHexString(hash).ToLowerInvariant() + ".png");
        FileStream? existing = null;
        try { existing = OpenLocalRead(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        if (existing != null)
        {
            await using (existing)
                if (existing.Length != bytes.LongLength || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(existing, ct), hash))
                    throw new InvalidDataException("A content-addressed frozen PNG is corrupt.");
            return path;
        }
        CheckAvailableDisk(directory, bytes.LongLength);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(flushToDisk: true); }
            File.Move(pending, path, overwrite: false);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return path;
    }

    private static void CheckAvailableDisk(string directory, long bytes)
    {
        string? root = Path.GetPathRoot(directory);
        if (root == null || root.StartsWith("\\\\", StringComparison.Ordinal)) return;
        long? available = null;
        try { var drive = new DriveInfo(root); if (drive.IsReady) available = drive.AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (available.HasValue && available.Value < bytes) throw new IOException("Insufficient staging disk space for a referenced pasted PNG.");
    }

    private async Task VerifyFrozenAssetAsync(StagedLegacyAsset asset, bool verifyOriginal, CancellationToken ct, bool validateBytes = false)
    {
        ValidateFrozenAssetDescriptor(asset);
        foreach (string path in verifyOriginal ? new[] { asset.OriginalPath, asset.FilePath } : new[] { asset.FilePath })
        {
            await using var file = OpenLocalRead(path);
            if (file.Length is <= 0 or > MaximumPastedImageBytes || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, ct), asset.Hash))
                throw new InvalidDataException("A frozen pasted PNG or its original source changed.");
            if (validateBytes && path == asset.FilePath)
            {
                file.Position = 0;
                var bytes = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(bytes, ct);
                StateImages.Validate(bytes, new StateLimits(), requirePng: true, cancellationToken: ct);
            }
        }
    }

    // Directory handles prevent path replacement/rename while present-file hashes and absent-leaf checks run.
    // A missing leaf has no read handle: its frozen absence is immutable, and is rechecked immediately before publication.
    internal IDisposable PinPastedImageDirectories()
    {
        var pins = new DirectoryPins();
        try
        {
            foreach (string directory in new[] { DirectoryPath, Path.Combine(DirectoryPath, "assets"), SourceImageDirectory ?? DirectoryPath })
                pins.Add(directory);
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string directory)
        {
            RejectLocalRedirects(directory, directory: true);
            if (!OperatingSystem.IsWindows()) return;
            for (string? path = Path.GetFullPath(directory); path != null; path = Path.GetDirectoryName(path))
            {
                if (!_paths.Add(path)) continue;
                var handle = OpenPastedDirectory(path, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError(); handle.Dispose();
                    if (error is 2 or 3) continue;
                    throw new IOException("Could not safely pin a pasted-image/staging directory.", error);
                }
                _handles.Add(handle);
                var name = new StringBuilder(32768);
                uint length = GetLocalFinalPath(handle, name, (uint)name.Capacity, 0);
                string final = name.ToString();
                if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) final = "\\\\" + final[8..];
                else if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final[4..];
                if (length == 0 || length >= name.Capacity || !string.Equals(Path.TrimEndingDirectorySeparator(final), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A pinned image/staging directory was redirected.");
            }
        }
        public void Dispose() { foreach (var handle in _handles) handle.Dispose(); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenPastedDirectory(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
}

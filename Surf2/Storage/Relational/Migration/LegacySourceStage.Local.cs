using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Surf2.Storage.Relational.Migration;

public sealed partial class LegacySourceStage
{
    /// <summary>Stages the six local/default roots without probing or opening a SQL source.</summary>
    public static async Task<LegacySourceStage> CaptureLocalAsync(string localAppDataRoot, string stagingRoot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ct.ThrowIfCancellationRequested();
        string sourceRoot = Path.GetFullPath(localAppDataRoot);
        RejectLocalRedirects(sourceRoot, directory: true);
        RejectLocalRedirects(Path.GetFullPath(stagingRoot), directory: true);
        var stage = new LegacySourceStage(stagingRoot) { SourceOrigin = "Local" };
        var sources = new List<FileStream>(Documents.Count);
        try
        {
            foreach (var (key, definition) in Documents)
            {
                ct.ThrowIfCancellationRequested();
                string original = Path.Combine(sourceRoot, definition.FileName);
                string staged = stage.DocumentPath(key);
                FileStream? input = null;
                try { input = OpenLocalRead(original); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Only confirmed absence permits defaults. Access errors and redirected paths fail closed.
                    RequireLocalAbsent(original);
                }
                if (input != null) sources.Add(input);
                byte[] sourceHash;
                string kind;
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (input != null)
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[64 * 1024];
                        long copied = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, count), ct);
                            hash.AppendData(buffer, 0, count);
                            copied = checked(copied + count);
                        }
                        if (copied != input.Length) throw new InvalidDataException("A local source changed during capture.");
                        sourceHash = hash.GetHashAndReset();
                        kind = "Local";
                    }
                    else
                    {
                        object value = Activator.CreateInstance(definition.Type)
                            ?? throw new InvalidOperationException("Default legacy state could not be constructed.");
                        await JsonSerializer.SerializeAsync(output, value, definition.Type, JsonOptions, ct);
                        sourceHash = []; // Defaults are fingerprinted from their frozen bytes, never regenerated on resume.
                        kind = "Default";
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                }
                if (kind == "Default") sourceHash = await HashFileAsync(staged, ct);
                var document = await stage.DescribeAsync(key, staged, kind, sourceHash, null, original, ct);
                if (!CryptographicOperations.FixedTimeEquals(document.SourceHash, document.StagingHash))
                    throw new InvalidDataException("The staged local copy differs from its pinned source bytes.");
                stage._documents.Add(key, document);
            }
            stage.ValidateSourceOrigin();
            stage.CheckLocalAbsences();
            stage.Fingerprint = stage.ComputeSourceFingerprint();
            await stage.SaveManifestAsync(ct);
            stage.CheckLocalAbsences();
            return stage;
        }
        catch
        {
            await stage.DisposeAsync();
            throw;
        }
        finally
        {
            foreach (var file in sources) await file.DisposeAsync();
        }
    }

    public Task VerifyUnchangedAsync(CancellationToken cancellationToken = default) =>
        WithVerifiedSourceAsync(null, () => Task.CompletedTask, cancellationToken);

    public Task WithVerifiedSourceAsync(Func<Task> publish, CancellationToken cancellationToken = default) =>
        WithVerifiedSourceAsync(null, publish, cancellationToken);

    internal byte[] ComputeSourceFingerprint() => SourceOrigin == "Package"
        ? ComputePackageFingerprint() : ComputeFingerprint(_documents.Values, SourceOrigin);

    private string? DeriveSourceImageDirectory()
    {
        if (SourceOrigin == "Package") return Path.Combine(DirectoryPath, "PastedDiagramImages");
        if (SourceOrigin is not ("Sql" or "Local")) throw new InvalidDataException("Unsupported legacy source origin.");
        string? root = null;
        foreach (var document in _documents.Values)
        {
            if (document.LocalSourcePath == null) continue;
            if (!Path.IsPathFullyQualified(document.LocalSourcePath) || !Documents.TryGetValue(document.Key, out var definition))
                throw new InvalidDataException("A frozen source root has an invalid local path.");
            string source = Path.GetFullPath(document.LocalSourcePath);
            string parent = Path.GetDirectoryName(source)!;
            if (!string.Equals(Path.GetFileName(source), definition.FileName, StringComparison.OrdinalIgnoreCase) ||
                root != null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Frozen source paths must address the known root files in one app-data directory.");
            root ??= parent;
        }
        string? images = root == null ? null : Path.Combine(root, "PastedDiagramImages");
        foreach (string original in _assets.Select(asset => asset.OriginalPath).Concat(_pastedReferences.Keys))
        {
            if (!Path.IsPathFullyQualified(original)) throw new InvalidDataException("A frozen source image has an invalid path.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(original))!;
            if (!string.Equals(Path.GetFileName(directory), "PastedDiagramImages", StringComparison.OrdinalIgnoreCase) ||
                images != null && !string.Equals(directory, images, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Frozen image pins and document roots must share one source image directory.");
            images ??= directory;
        }
        // An older all-SQL manifest with no image pins did not record a local root. Do not invent one from a resume caller.
        return images;
    }

    internal void ValidateSourceOrigin()
    {
        ValidatePastedMetadata();
        if (SourceOrigin == "Package") { ValidatePackageOrigin(); return; }
        if (SourceOrigin is not ("Sql" or "Local") || PackagePath != null || PackageHash != null ||
            _documents.Values.Any(document => document.SourceKind == "Package"))
            throw new InvalidDataException("Unsupported or mixed legacy source origin.");
        // SQL origins intentionally retain the existing SQL -> local -> default fallback rules.
        if (SourceOrigin == "Sql") { _ = SourceImageDirectory; return; }
        if (_documents.Count != Documents.Count || !Documents.Keys.All(_documents.ContainsKey))
            throw new InvalidDataException("A local source stage must contain exactly six known roots.");
        string? root = null;
        foreach (var (key, document) in _documents)
        {
            if (document.SourceKind is not ("Local" or "Default") || document.LocalSourcePath == null ||
                !Path.IsPathFullyQualified(document.LocalSourcePath) || document.OriginalTimestamp != null ||
                document.SourceHash is not { Length: 32 } || document.StagingHash is not { Length: 32 } ||
                !CryptographicOperations.FixedTimeEquals(document.SourceHash, document.StagingHash) || document.ByteCount < 0 ||
                !string.Equals(Path.GetFullPath(document.FilePath), DocumentPath(key), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A local source stage contains mixed or invalid document descriptors.");
            string source = Path.GetFullPath(document.LocalSourcePath);
            string parent = Path.GetDirectoryName(source)!;
            root ??= parent;
            if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(source), Documents[key].FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local source roots must address the six fixed files in one app-data directory.");
        }
        foreach (var asset in _assets)
        {
            string original = Path.GetFullPath(asset.OriginalPath);
            if (asset.Hash is not { Length: 32 } || !IsWithin(DirectoryPath, asset.FilePath) ||
                !string.Equals(Path.GetDirectoryName(original), Path.Combine(root!, "PastedDiagramImages"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A local source stage contains an asset from another origin or directory.");
        }
    }

    private async Task WithVerifiedLocalSourceAsync(Func<Task> publish, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ct.ThrowIfCancellationRequested();
        ValidateSourceOrigin();
        if (!CryptographicOperations.FixedTimeEquals(Fingerprint, ComputeSourceFingerprint()))
            throw new InvalidDataException("The local source fingerprint is inconsistent.");
        var locked = new List<FileStream>();
        using var assetPaths = PinPastedImageDirectories();
        try
        {
            // Pin the manifest too; resume uses precisely these frozen descriptors, not a fresh default model.
            var manifestFile = OpenLocalRead(Path.Combine(DirectoryPath, "manifest.json"));
            locked.Add(manifestFile);
            if (manifestFile.Length > 64L * 1024 * 1024) throw new InvalidDataException("The local manifest exceeds its metadata budget.");
            await CheckNonPackageManifestAsync(manifestFile.Name, ct);
            foreach (var document in _documents.Values)
            {
                if (document.SourceKind == "Default") RequireLocalAbsent(document.LocalSourcePath!);
                else await LockAndCheckAsync(document.LocalSourcePath!, document.SourceHash, document.ByteCount);
                await LockAndCheckAsync(document.FilePath, document.StagingHash, document.ByteCount);
            }
            foreach (var asset in _assets)
            {
                await LockAndCheckAsync(asset.OriginalPath, asset.Hash, null);
                await LockAndCheckAsync(asset.FilePath, asset.Hash, null);
            }
            // Missing files cannot have read handles. Repeat the appeared-file guard after all potentially long hashes.
            CheckLocalAbsences();
            CheckPastedImageAbsences();
            ct.ThrowIfCancellationRequested();
            await publish();
        }
        finally
        {
            foreach (var file in locked) await file.DisposeAsync();
        }

        async Task LockAndCheckAsync(string path, byte[] expected, long? bytes)
        {
            var file = OpenLocalRead(path);
            locked.Add(file);
            if (bytes.HasValue && file.Length != bytes.Value)
                throw new InvalidDataException("A local source or staged byte count changed after capture.");
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, ct), expected))
                throw new InvalidDataException("Local source or staging content changed after capture: " + Path.GetFileName(path));
        }
    }

    private async Task CheckNonPackageManifestAsync(string path, CancellationToken ct)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long assets = 0;
        bool pastedFreeze = false;
        await using var cursor = OpenAt(path, 0);
        if (!await cursor.MoveNextAsync(ct)) throw new InvalidDataException("The source manifest is empty.");
        await cursor.ReadObjectAsync(async (name, value, token) =>
        {
            if (!properties.Add(name)) throw new InvalidDataException("The source manifest has duplicate properties.");
            switch (name)
            {
                case "SourceOrigin":
                    if ((await value.ReadValueAsync(token)).GetString() != SourceOrigin)
                        throw new InvalidDataException("The frozen source origin changed.");
                    break;
                case "MigrationIdentity":
                    if ((await value.ReadValueAsync(token)).GetGuid() != MigrationIdentity)
                        throw new InvalidDataException("The frozen migration identity changed.");
                    break;
                case "Fingerprint":
                    if (!CryptographicOperations.FixedTimeEquals((await value.ReadValueAsync(token)).GetBytesFromBase64(), Fingerprint))
                        throw new InvalidDataException("The frozen source fingerprint changed.");
                    break;
                case "Documents":
                    await value.ReadArrayAsync(async (i, item, inner) =>
                    {
                        if (i >= _documents.Count) throw new InvalidDataException("The frozen manifest has extra documents.");
                        var document = JsonSerializer.Deserialize<StagedLegacyDocument>(await item.ReadValueAsync(inner))
                            ?? throw new InvalidDataException("Invalid document descriptor.");
                        if (!seen.Add(document.Key) || !_documents.TryGetValue(document.Key, out var expected) ||
                            document.FilePath != expected.FilePath || document.SourceKind != expected.SourceKind || document.ByteCount != expected.ByteCount ||
                            document.LocalSourcePath != expected.LocalSourcePath || document.OriginalTimestamp != expected.OriginalTimestamp ||
                            document.SourceHash is not { Length: 32 } || document.StagingHash is not { Length: 32 } ||
                            !CryptographicOperations.FixedTimeEquals(document.SourceHash, expected.SourceHash) ||
                            !CryptographicOperations.FixedTimeEquals(document.StagingHash, expected.StagingHash))
                            throw new InvalidDataException("A frozen document descriptor changed.");
                    }, token);
                    break;
                case "Assets":
                    await value.ReadArrayAsync(async (i, item, inner) =>
                    {
                        if (i >= _assets.Count) throw new InvalidDataException("The frozen manifest has extra assets.");
                        var asset = JsonSerializer.Deserialize<StagedLegacyAsset>(await item.ReadValueAsync(inner))
                            ?? throw new InvalidDataException("Invalid asset descriptor.");
                        var expected = _assets[(int)i];
                        if (asset.OriginalPath != expected.OriginalPath || asset.FilePath != expected.FilePath || asset.Hash is not { Length: 32 } ||
                            !CryptographicOperations.FixedTimeEquals(asset.Hash, expected.Hash))
                            throw new InvalidDataException("A frozen asset descriptor changed.");
                        assets++;
                    }, token);
                    break;
                case "PastedImageFreeze":
                    await CheckPastedFreezeManifestAsync(value, token);
                    pastedFreeze = true;
                    break;
                default: throw new InvalidDataException("The source manifest has an unsupported property.");
            }
        }, ct);
        bool originCovered = properties.Contains("SourceOrigin") || SourceOrigin == "Sql";
        if (!originCovered || (_pastedFreezeStarted && !pastedFreeze) || properties.Count != (properties.Contains("SourceOrigin") ? 5 : 4) + (pastedFreeze ? 1 : 0) ||
            seen.Count != _documents.Count || assets != _assets.Count || await cursor.MoveNextAsync(ct))
            throw new InvalidDataException("The frozen manifest root or descriptor coverage changed.");
    }

    private void CheckLocalAbsences()
    {
        foreach (var document in _documents.Values)
            if (document.SourceKind == "Default") RequireLocalAbsent(document.LocalSourcePath!);
    }

    private static void RequireLocalAbsent(string path)
    {
        RejectLocalRedirects(Path.GetDirectoryName(Path.GetFullPath(path))!, directory: true);
        try { _ = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw new InvalidDataException("A previously absent local source appeared after staging.");
    }

    private static FileStream OpenLocalRead(string path)
    {
        path = Path.GetFullPath(path);
        RejectLocalRedirects(Path.GetDirectoryName(path)!, directory: true);
        RejectLocalRedirects(path, directory: false);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var buffer = new StringBuilder(32768);
                uint length = GetLocalFinalPath(file.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new InvalidDataException("The opened local source path could not be verified.");
                string final = buffer.ToString();
                if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) final = "\\\\" + final[8..];
                else if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final[4..];
                if (!string.Equals(final, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An opened local source or staged file was redirected.");
            }
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static void RejectLocalRedirects(string path, bool directory)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current), directory = true)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
                throw new InvalidDataException("Local source and staging paths cannot be redirected or have the wrong file type.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLocalFinalPath(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}

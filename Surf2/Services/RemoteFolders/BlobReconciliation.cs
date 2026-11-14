using System.IO;
using System.Security.Cryptography;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

public enum BlobReconciliationChoice { None, KeepLocal, KeepBlob, DeleteLocal, DeleteBlob }
public sealed record LocalFolderFile(string Path, long ByteCount, DateTimeOffset LastModified, string Sha256);
public sealed record BlobReconciliationEntry(string Path, LocalFolderFile? Local, BlobSourceObject? Remote,
    string? RemoteSha256, string RemoteCopyPath, bool Different, bool PreviouslyMatched, BlobReconciliationChoice SuggestedChoice)
{
    public IReadOnlyList<BlobReconciliationChoice> Choices => !Different ? [BlobReconciliationChoice.None] : Local == null
        ? PreviouslyMatched ? [BlobReconciliationChoice.KeepBlob, BlobReconciliationChoice.DeleteBlob] : [BlobReconciliationChoice.KeepBlob]
        : Remote == null ? PreviouslyMatched ? [BlobReconciliationChoice.KeepLocal, BlobReconciliationChoice.DeleteLocal] : [BlobReconciliationChoice.KeepLocal]
        : [BlobReconciliationChoice.KeepLocal, BlobReconciliationChoice.KeepBlob];
}
public sealed record BlobReconciliationResult(bool Succeeded, IReadOnlyList<string> AppliedPaths, string Message);
internal sealed record PairBaseline(string ConfigurationKey, string LocalFolder, DateTimeOffset VerifiedAtUtc, IReadOnlyList<PairBaselineItem> Items);
internal sealed record PairBaselineItem(string Path, string Sha256, string RemoteETag);

public sealed class BlobReconciliationPlan : IAsyncDisposable
{
    internal BlobReconciliationPlan(ResourceSourceDefinition source, string localFolder, string stage, IReadOnlyList<BlobReconciliationEntry> entries, FolderCacheStore cache)
    { _source = source.Copy(); LocalFolder = localFolder; Stage = stage; Entries = entries; _cache = cache;
      _lease = new FileStream(System.IO.Path.Combine(stage, ".usage"), FileMode.Open, FileAccess.Read, FileShare.Read); }
    private readonly ResourceSourceDefinition _source;
    public ResourceSourceDefinition Source => _source.Copy();
    public string LocalFolder { get; }
    public IReadOnlyList<BlobReconciliationEntry> Entries { get; internal set; }
    public bool HasDifferences => Entries.Any(e => e.Different);
    public List<string> AppliedPaths { get; } = [];
    internal string Stage { get; }
    private readonly FolderCacheStore _cache;
    private readonly FileStream _lease;
    private bool _disposed;
    internal void RequireAvailable() { if (_disposed) throw new ObjectDisposedException(nameof(BlobReconciliationPlan)); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _lease.Dispose();
        using var operation = await _cache.AcquireOperationAsync(CancellationToken.None);
        _cache.DeleteOwnedDirectory(Stage);
    }
}

public sealed partial class BlobSourceService
{
    public async Task<BlobReconciliationPlan> CompareAsync(ResourceSourceDefinition source, string localFolder,
        IProgress<FolderSourceProgress>? progress = null, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source); localFolder = Path.GetFullPath(localFolder);
        if (!Directory.Exists(localFolder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        FolderSourcePaths.RequireNoReparseAncestors(localFolder, localFolder);
        using var operation = await _cache.AcquireOperationAsync(ct);
        string stage = _cache.CreateStage(source);
        try
        {
            var transport = Transport(source);
            var remote = await ListBoundedAsync(source, transport, progress, ct);
            _cache.CheckCapacity(source, remote.Sum(i => i.ByteCount));
            var local = await ScanLocalAsync(localFolder, ct);
            var baseline = ReadBaseline(source, localFolder)?.Items.ToDictionary(i => i.Path, StringComparer.Ordinal) ?? [];
            string prefix = FolderSourcePaths.NormalizePrefix(source.Prefix);
            var byName = remote.ToDictionary(i => i.Name[prefix.Length..], StringComparer.Ordinal);
            RequireRepresentableTree(byName.Keys.Concat(local.Keys));
            var entries = new List<BlobReconciliationEntry>();
            long completed = 0;
            foreach (string name in byName.Keys.Union(local.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); local.TryGetValue(name, out var localFile); byName.TryGetValue(name, out var blob);
                baseline.TryGetValue(name, out var old);
                string copy = FolderSourcePaths.SafeCombine(Path.Combine(stage, "content"), FolderSourcePaths.CacheName(name));
                string? hash = null;
                if (blob != null)
                {
                    if (old != null && old.RemoteETag == blob.ETag) hash = old.Sha256;
                    else { await DownloadAsync(source, transport, blob, copy, ct); hash = await FolderSourcePaths.HashFileAsync(copy, ct); }
                }
                bool different = localFile == null || blob == null || localFile.Sha256 != hash;
                BlobReconciliationChoice suggested = BlobReconciliationChoice.None;
                if (different)
                {
                    if (old == null && blob == null) suggested = BlobReconciliationChoice.KeepLocal;
                    else if (old == null && localFile == null) suggested = BlobReconciliationChoice.KeepBlob;
                    else if (localFile != null && blob != null && old != null)
                    {
                        if (localFile.Sha256 == old.Sha256) suggested = BlobReconciliationChoice.KeepBlob;
                        else if (hash == old.Sha256) suggested = BlobReconciliationChoice.KeepLocal;
                    }
                }
                entries.Add(new(name, localFile, blob, hash, copy, different, old != null, suggested));
                completed += blob?.ByteCount ?? 0;
                progress?.Report(new("Comparing local folder and Blob", completed, Files: entries.Count));
            }
            return new(source, localFolder, stage, entries, _cache);
        }
        catch { _cache.DeleteOwnedDirectory(stage); throw; }
    }

    public async Task<(string Local, string Blob)> ReadComparisonAsync(BlobReconciliationPlan plan, BlobReconciliationEntry entry, CancellationToken ct = default)
    {
        plan.RequireAvailable();
        if ((entry.Local?.ByteCount ?? 0) + (entry.Remote?.ByteCount ?? 0) > 256 * 1024)
            throw new FolderCapacityException("These files exceed the interactive text-diff limit. Compare their sizes and dates, then choose the version to keep.");
        using var operation = await _cache.AcquireOperationAsync(ct);
        await RequireLocalMatchesAsync(plan.LocalFolder, entry.Path, entry.Local, ct);
        if (entry.Remote != null) await EnsureRemoteCopyAsync(plan, entry, Transport(plan.Source), ct);
        string local = entry.Local == null ? "" : await File.ReadAllTextAsync(FolderSourcePaths.SafeCombine(plan.LocalFolder, entry.Path), ct);
        string blob = entry.Remote == null ? "" : await File.ReadAllTextAsync(entry.RemoteCopyPath, ct);
        if (local.Contains('\0') || blob.Contains('\0') || local.Length + (long)blob.Length > 256 * 1024 || local.Count(c => c == '\n') + blob.Count(c => c == '\n') > 20_000)
            throw new FolderCapacityException("Binary or oversized files use metadata comparison. Choose the version to keep in the review list.");
        return (local, blob);
    }

    public async Task<BlobReconciliationResult> ApplyAsync(BlobReconciliationPlan plan,
        IReadOnlyDictionary<string, BlobReconciliationChoice> decisions, IProgress<FolderSourceProgress>? progress = null, CancellationToken ct = default)
    {
        plan.RequireAvailable();
        foreach (var entry in plan.Entries.Where(e => e.Different))
            if (!decisions.TryGetValue(entry.Path, out var choice) || !entry.Choices.Contains(choice))
                throw new ArgumentException("Choose a valid action for every differing file before applying the plan.");
        using var operation = await _cache.AcquireOperationAsync(ct);
        var transport = Transport(plan.Source);
        var expectedRemote = plan.Entries.Where(e => e.Remote != null).ToDictionary(e => e.Path, e => e.Remote!, StringComparer.Ordinal);
        var expectedHashes = plan.Entries.Where(e => e.RemoteSha256 != null).ToDictionary(e => e.Path, e => e.RemoteSha256!, StringComparer.Ordinal);
        string prefix = FolderSourcePaths.NormalizePrefix(plan.Source.Prefix);
        WriteJournal(plan, decisions, "Applying");
        try
        {
            foreach (var entry in plan.Entries.Where(e => e.Different))
            {
                ct.ThrowIfCancellationRequested();
                await RequireLocalMatchesAsync(plan.LocalFolder, entry.Path, entry.Local, ct);
                string path = FolderSourcePaths.SafeCombine(plan.LocalFolder, entry.Path);
                var action = decisions[entry.Path];
                progress?.Report(new("Applying " + action + ": " + entry.Path, Files: plan.AppliedPaths.Count));
                switch (action)
                {
                    case BlobReconciliationChoice.KeepLocal:
                        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                        {
                            string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
                            if (hash != entry.Local!.Sha256) throw new FolderConflictException("A local file changed after review: " + entry.Path);
                            file.Position = 0;
                            var uploaded = await transport.UploadAsync(prefix + entry.Path, file, entry.Remote?.ETag, ct);
                            expectedRemote[entry.Path] = uploaded; expectedHashes[entry.Path] = hash;
                        }
                        break;
                    case BlobReconciliationChoice.KeepBlob:
                        await EnsureRemoteCopyAsync(plan, entry, transport, ct);
                        FolderCacheStore.RequireDriveSpace(plan.LocalFolder, entry.Remote!.ByteCount, _cache.ReadSettings().FreeSpaceReserveBytes);
                        await ReplaceLocalAsync(plan.LocalFolder, entry, ct);
                        expectedHashes[entry.Path] = entry.RemoteSha256!;
                        break;
                    case BlobReconciliationChoice.DeleteBlob:
                        await transport.DeleteAsync(entry.Remote!, ct); expectedRemote.Remove(entry.Path); expectedHashes.Remove(entry.Path);
                        break;
                    case BlobReconciliationChoice.DeleteLocal:
                        await DeleteLocalAsync(plan.LocalFolder, entry, ct); expectedRemote.Remove(entry.Path); expectedHashes.Remove(entry.Path);
                        break;
                }
                plan.AppliedPaths.Add(entry.Path); WriteJournal(plan, decisions, "Applying");
            }
            var freshRemote = (await ListBoundedAsync(plan.Source, transport, progress, ct)).ToDictionary(i => i.Name[prefix.Length..], StringComparer.Ordinal);
            var freshLocal = await ScanLocalAsync(plan.LocalFolder, ct);
            if (freshRemote.Count != expectedRemote.Count || freshLocal.Count != expectedRemote.Count || expectedRemote.Any(pair =>
                !freshRemote.TryGetValue(pair.Key, out var actual) || actual.ETag != pair.Value.ETag ||
                !freshLocal.TryGetValue(pair.Key, out var file) || file.Sha256 != expectedHashes[pair.Key]))
                throw new FolderConflictException("One of the locations changed during reconciliation. The resource remains unloaded; load it again to review the current differences.");
            var verified = freshLocal.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new BlobReconciliationEntry(p.Key, p.Value,
                freshRemote[p.Key], p.Value.Sha256, FolderSourcePaths.SafeCombine(Path.Combine(plan.Stage, "content"), FolderSourcePaths.CacheName(p.Key)), false, true, BlobReconciliationChoice.None)).ToArray();
            SaveBaseline(plan.Source, plan.LocalFolder, verified);
            plan.Entries = verified;
            WriteJournal(plan, decisions, "Verified");
            return new(true, plan.AppliedPaths.ToArray(), "Both locations agree. The resource can now be loaded.");
        }
        catch (Exception ex) when (ex is IOException or Azure.RequestFailedException or OperationCanceledException or UnauthorizedAccessException)
        {
            WriteJournal(plan, decisions, "Incomplete");
            if (ex is OperationCanceledException) throw;
            return new(false, plan.AppliedPaths.ToArray(), "Reconciliation did not finish. " + plan.AppliedPaths.Count + " file actions completed. Load again to rescan; the resource remains unloaded. " + RemoteFolderServices.DescribeError(ex));
        }
    }

    public async Task<PreparedFolderSource> OpenVerifiedPairAsync(BlobReconciliationPlan plan, CancellationToken ct = default)
    {
        plan.RequireAvailable();
        if (plan.HasDifferences) throw new FolderConflictException("Reconcile all differences before loading this resource.");
        using var operation = await _cache.AcquireOperationAsync(ct);
        var local = await ScanLocalAsync(plan.LocalFolder, ct);
        if (local.Count != plan.Entries.Count || plan.Entries.Any(e => !local.TryGetValue(e.Path, out var actual) || actual.Sha256 != e.RemoteSha256))
            throw new FolderConflictException("The local folder changed after verification. Load and review it again.");
        SaveBaseline(plan.Source, plan.LocalFolder, plan.Entries);
        var items = plan.Entries.Select(e => new FolderSourceItem(e.Path, e.Path, e.Local!.ByteCount, e.Local.LastModified,
            Revision(e.Remote!), e.Local.Sha256)).ToArray();
        return new(plan.LocalFolder, new(FolderSourcePaths.ConfigurationKey(plan.Source),
            FolderSourcePaths.Hash(string.Join("\n", items.Select(i => i.LogicalPath + "\0" + i.Revision))), DateTimeOffset.UtcNow, items));
    }

    private async Task EnsureRemoteCopyAsync(BlobReconciliationPlan plan, BlobReconciliationEntry entry, IBlobFolderTransport transport, CancellationToken ct)
    {
        if (entry.Remote == null) throw new ArgumentException("There is no Blob version for this entry.");
        if (!File.Exists(entry.RemoteCopyPath)) await DownloadAsync(plan.Source, transport, entry.Remote, entry.RemoteCopyPath, ct);
        if (await FolderSourcePaths.HashFileAsync(entry.RemoteCopyPath, ct) != entry.RemoteSha256)
            throw new FolderConflictException("The downloaded Blob no longer matches the reviewed content: " + entry.Path);
    }
    private static async Task<Dictionary<string, LocalFolderFile>> ScanLocalAsync(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, LocalFolderFile>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested(); FolderSourcePaths.RequireNoReparseAncestors(root, directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A paired folder contains a symlink or junction: " + Path.GetFileName(path));
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                FolderSourcePaths.RequireWindowsRelativePath(relative);
                var info = new FileInfo(path); long length = info.Length; DateTimeOffset modified = info.LastWriteTimeUtc;
                string hash = await FolderSourcePaths.HashFileAsync(path, ct); info.Refresh();
                if (info.Length != length || info.LastWriteTimeUtc != modified.UtcDateTime) throw new FolderConflictException("A local file changed while being compared: " + relative);
                result.Add(relative, new(relative, length, modified, hash));
                if (result.Count > 100_000) throw new FolderCapacityException("The paired folder exceeds the supported 100,000 file limit.");
            }
        }
        return result;
    }
    private static void RequireRepresentableTree(IEnumerable<string> names)
    {
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            FolderSourcePaths.RequireWindowsRelativePath(name); files.Add(name);
            string current = "";
            foreach (var part in name.Split('/'))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                if (spellings.TryGetValue(current, out var spelling) && spelling != current)
                    throw new IOException("These Blob/local paths differ only by case and cannot be paired on Windows: " + current + " / " + spelling);
                spellings[current] = current;
            }
        }
        foreach (string name in files)
        {
            int slash = name.LastIndexOf('/');
            while (slash > 0)
            {
                if (files.Contains(name[..slash])) throw new IOException("A Blob name is both a file and a folder and cannot be paired on Windows: " + name[..slash]);
                slash = name.LastIndexOf('/', slash - 1);
            }
        }
    }
    private static async Task RequireLocalMatchesAsync(string root, string name, LocalFolderFile? expected, CancellationToken ct)
    {
        string path = FolderSourcePaths.SafeCombine(root, name); FolderSourcePaths.RequireNoReparseAncestors(root, path);
        if (expected == null)
        { if (File.Exists(path) || Directory.Exists(path)) throw new FolderConflictException("A new local item appeared after review: " + name); }
        else if (!File.Exists(path) || await FolderSourcePaths.HashFileAsync(path, ct) != expected.Sha256)
            throw new FolderConflictException("A local file changed after review: " + name);
    }
    private static async Task ReplaceLocalAsync(string root, BlobReconciliationEntry entry, CancellationToken ct)
    {
        string path = FolderSourcePaths.SafeCombine(root, entry.Path); FolderSourcePaths.RequireNoReparseAncestors(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".surf2-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(entry.RemoteCopyPath, pending);
            await RequireLocalMatchesAsync(root, entry.Path, entry.Local, ct);
            if (entry.Local == null) File.Move(pending, path); // No overwrite if a new file races the reviewed absence.
            else
            {
                string backup = path + ".surf2-" + Guid.NewGuid().ToString("N") + ".recovery";
                File.Move(path, backup);
                try
                {
                    if (await FolderSourcePaths.HashFileAsync(backup, ct) != entry.Local.Sha256)
                        throw new FolderConflictException("A local file changed during replacement. Its recovery copy is: " + backup);
                    File.Move(pending, path); File.Delete(backup);
                }
                catch { if (!File.Exists(path)) File.Move(backup, path); throw; }
            }
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private static async Task DeleteLocalAsync(string root, BlobReconciliationEntry entry, CancellationToken ct)
    {
        string path = FolderSourcePaths.SafeCombine(root, entry.Path), backup = path + ".surf2-" + Guid.NewGuid().ToString("N") + ".recovery";
        await RequireLocalMatchesAsync(root, entry.Path, entry.Local, ct); File.Move(path, backup);
        try
        {
            if (await FolderSourcePaths.HashFileAsync(backup, ct) != entry.Local!.Sha256)
                throw new FolderConflictException("A local file changed during deletion. Its recovery copy is: " + backup);
            File.Delete(backup);
        }
        catch { if (!File.Exists(path)) File.Move(backup, path); throw; }
    }
    private string BaselinePath(ResourceSourceDefinition source, string root) => Path.Combine(Path.GetDirectoryName(_cache.Root)!, "SourcePairs",
        FolderSourcePaths.ConfigurationKey(source) + "-" + FolderSourcePaths.Hash(Path.GetFullPath(root).ToUpperInvariant()) + ".json");
    private PairBaseline? ReadBaseline(ResourceSourceDefinition source, string root)
    {
        string path = BaselinePath(source, root);
        if (!File.Exists(path)) return null;
        var value = _cache.ReadJson<PairBaseline>(path);
        return value.ConfigurationKey == FolderSourcePaths.ConfigurationKey(source) && value.LocalFolder.Equals(root, StringComparison.OrdinalIgnoreCase) ? value : null;
    }
    private void SaveBaseline(ResourceSourceDefinition source, string root, IReadOnlyList<BlobReconciliationEntry> entries) =>
        _cache.WriteJson(BaselinePath(source, root), new PairBaseline(FolderSourcePaths.ConfigurationKey(source), root, DateTimeOffset.UtcNow,
            entries.Select(e => new PairBaselineItem(e.Path, e.Local!.Sha256, e.Remote!.ETag)).ToArray()));
    private void WriteJournal(BlobReconciliationPlan plan, IReadOnlyDictionary<string, BlobReconciliationChoice> choices, string status) =>
        _cache.WriteJson(BaselinePath(plan.Source, plan.LocalFolder) + ".operation.json", new { Configuration = FolderSourcePaths.ConfigurationKey(plan.Source), plan.LocalFolder,
            Status = status, ReviewedAtUtc = DateTimeOffset.UtcNow, Choices = choices, Applied = plan.AppliedPaths.ToArray() });
}

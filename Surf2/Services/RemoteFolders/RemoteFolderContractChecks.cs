using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

/// <summary>Private temporary files and an in-memory Blob transport only; no cloud access or user credentials.</summary>
public static class RemoteFolderContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var passed = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Remote folder contract: " + message); passed.Add(message); }
        string fixture = Path.Combine(Path.GetTempPath(), "Surf2-source-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var cache = new FolderCacheStore(Path.Combine(fixture, "cache"));
            await cache.SaveSettingsAsync(new(8 * 1024 * 1024, 32 * 1024 * 1024, 0), ct);
            var fake = new MemoryBlobTransport();
            var blob = new BlobSourceService(cache, new FolderCredentialStore(Path.Combine(fixture, "credentials")), _ => fake);
            var source = new ResourceSourceDefinition { SourceId = Guid.NewGuid(), Provider = SourceProvider.AzureBlob, ConfigurationRevision = 1,
                Endpoint = "https://fixture.blob.core.windows.net", Container = "fixture", AuthenticationKind = "Anonymous" };
            fake.Put("A.cs", "class Upper { }"); fake.Put("a.cs", "class Lower { }"); fake.Put("folder/item.txt", "hello");
            using (var first = await blob.PrepareAsync(source, "Fixture", ct: ct))
            {
                Check(first.Manifest.Items.Count == 3 && first.ResolveItem("A.cs") != first.ResolveItem("a.cs"), "Blob exact-case names map to distinct safe local files");
                Check(await File.ReadAllTextAsync(first.ResolveItem("folder/item.txt"), ct) == "hello", "Logical Blob hierarchy resolves its downloaded content");
                try { await cache.RemoveAsync(FolderSourcePaths.ConfigurationKey(source), ct); throw new InvalidOperationException("Expected active checkout protection."); }
                catch (IOException) { Check(File.Exists(first.ResolveItem("folder/item.txt")), "Cache cleanup preserves every file in an actively leased generation"); }
                int downloaded = fake.Downloads;
                using var unchanged = await blob.PrepareAsync(source, "Fixture", true, ct: ct);
                Check(fake.Downloads == downloaded, "Unchanged Blob ETags reuse verified local bytes without downloading");
                Check(unchanged.PhysicalRoot == first.PhysicalRoot && unchanged.Revision == first.Revision,
                    "An unchanged Blob refresh retains the published generation without staging duplicate files");
            }
            fake.Remove("a.cs"); fake.Put("folder/item.txt", "updated");
            using (var changed = await blob.PrepareAsync(source, "Fixture", true, ct: ct))
                Check(changed.Manifest.Items.Count == 2 && await File.ReadAllTextAsync(changed.ResolveItem("folder/item.txt"), ct) == "updated", "Changed and deleted Blob entries publish one complete new manifest");
            fake.Put("folder/item.txt", "not published"); fake.FailDownload = "folder/item.txt";
            try { using var unexpected = await blob.PrepareAsync(source, "Fixture", true, ct: ct); throw new InvalidOperationException("Expected a failed download."); }
            catch (FolderSourceException) { }
            using (var retained = cache.TryOpenCurrent(source)!)
                Check(await File.ReadAllTextAsync(retained.ResolveItem("folder/item.txt"), ct) == "updated", "Failed refresh retains the previously published copy");
            fake.FailDownload = null;
            var paired = source.Copy(); paired.SourceId = Guid.NewGuid();
            fake.Clear(); fake.Put("remote.txt", "remote"); fake.Put("shared.txt", "remote shared");
            string local = Path.Combine(fixture, "user-folder"); Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, "local.txt"), "local", ct);
            await File.WriteAllTextAsync(Path.Combine(local, "shared.txt"), "local shared", ct);
            await using (var plan = await blob.CompareAsync(paired, local, ct: ct))
            {
                Check(plan.HasDifferences && plan.Entries.Single(e => e.Path == "shared.txt").SuggestedChoice == BlobReconciliationChoice.None,
                    "Initial divergent files require an explicit version choice");
                var choices = plan.Entries.Where(e => e.Different).ToDictionary(e => e.Path,
                    e => e.Local == null ? BlobReconciliationChoice.KeepBlob : BlobReconciliationChoice.KeepLocal, StringComparer.Ordinal);
                var result = await blob.ApplyAsync(plan, choices, ct: ct);
                Check(result.Succeeded && !plan.HasDifferences && File.Exists(Path.Combine(local, "remote.txt")) && fake.Read("local.txt") == "local" && fake.Read("shared.txt") == "local shared",
                    "Reviewed initial pairing uploads, downloads and resolves conflicting contents");
                using var prepared = await blob.OpenVerifiedPairAsync(plan, ct);
                Check(prepared.PhysicalRoot == local && prepared.Manifest.Items.All(i => i.LogicalPath == i.CacheRelativePath), "A verified pair uses the chosen user folder and exact relative identities");
            }
            fake.Remove("shared.txt");
            await using (var missing = await blob.CompareAsync(paired, local, ct: ct))
            {
                var entry = missing.Entries.Single(e => e.Path == "shared.txt");
                Check(entry.PreviouslyMatched && entry.SuggestedChoice == BlobReconciliationChoice.None && entry.Choices.Contains(BlobReconciliationChoice.DeleteLocal) && entry.Choices.Contains(BlobReconciliationChoice.KeepLocal),
                    "A previously matched missing file requires explicit restore or delete");
                try { await cache.RemoveAsync(FolderSourcePaths.ConfigurationKey(paired), ct); throw new InvalidOperationException("Expected review copy protection."); }
                catch (IOException) { passed.Add("Cache cleanup refuses a copy held by an active reconciliation review"); }
                await File.WriteAllTextAsync(Path.Combine(local, "shared.txt"), "edited during review", ct);
                var result = await blob.ApplyAsync(missing, new Dictionary<string, BlobReconciliationChoice> { ["shared.txt"] = BlobReconciliationChoice.DeleteLocal }, ct: ct);
                Check(!result.Succeeded && await File.ReadAllTextAsync(Path.Combine(local, "shared.txt"), ct) == "edited during review", "A local edit after review is preserved and keeps reconciliation incomplete");
            }
            fake.Clear(); fake.Put("A/file.txt", "a"); fake.Put("a/other.txt", "b");
            try { await using var invalid = await blob.CompareAsync(paired, local, ct: ct); throw new InvalidOperationException("Expected Windows collision rejection."); }
            catch (IOException) { passed.Add("Paired folders reject case-colliding directory names before writes"); }
            foreach (string ambiguous in new[] { "folder/", "folder//item.txt" })
            {
                fake.Clear(); fake.Put(ambiguous, "ambiguous"); int priorDownloads = fake.Downloads;
                try { using var invalid = await blob.PrepareAsync(source, "Ambiguous name", true, ct: ct); throw new InvalidOperationException("Expected ambiguous Blob name rejection."); }
                catch (FolderSourceException) { Check(fake.Downloads == priorDownloads, "Ambiguous Blob path is rejected before download: " + ambiguous); }
            }
            await cache.SaveSettingsAsync(new(1024 * 1024, 32 * 1024 * 1024, 0), ct);
            fake.Clear(); fake.Put("huge.bin", new string('x', 2 * 1024 * 1024)); int beforeQuota = fake.Downloads;
            var oversized = source.Copy(); oversized.SourceId = Guid.NewGuid();
            try { using var invalid = await blob.PrepareAsync(oversized, "Too large", ct: ct); throw new InvalidOperationException("Expected quota rejection."); }
            catch (FolderCapacityException) { Check(fake.Downloads == beforeQuota, "Capacity preflight rejects an oversized source before downloading"); }
            fake.Clear(); fake.Put("large.txt", new string('x', 700 * 1024));
            var nearLimit = source.Copy(); nearLimit.SourceId = Guid.NewGuid();
            string nearLimitRoot;
            using (var initial = await blob.PrepareAsync(nearLimit, "Near limit", ct: ct))
            {
                nearLimitRoot = initial.PhysicalRoot; int downloaded = fake.Downloads;
                using var unchanged = await blob.PrepareAsync(nearLimit, "Near limit", true, ct: ct);
                Check(unchanged.PhysicalRoot == initial.PhysicalRoot && fake.Downloads == downloaded,
                    "An unchanged 700 KiB Blob copy refreshes within a 1 MiB quota without requiring duplicate storage");
                await File.WriteAllTextAsync(initial.ResolveItem("large.txt"), "damaged", ct);
            }
            int beforeRepair = fake.Downloads;
            using (var repaired = await blob.PrepareAsync(nearLimit, "Near limit", true, ct: ct))
                Check(repaired.PhysicalRoot != nearLimitRoot && fake.Downloads == beforeRepair + 1 &&
                    new FileInfo(repaired.ResolveItem("large.txt")).Length == 700 * 1024,
                    "An unchanged remote ETag still repairs corrupted local content in a new complete generation");
            var retirement = source.Copy(); retirement.SourceId = Guid.NewGuid();
            fake.Clear(); fake.Put("file.txt", "old");
            using (var oldGeneration = await blob.PrepareAsync(retirement, "Retirement", ct: ct))
            {
                fake.Put("file.txt", "new");
                using var current = await blob.PrepareAsync(retirement, "Retirement", true, ct: ct);
                Check(Directory.Exists(oldGeneration.PhysicalRoot), "Publishing a replacement preserves the retired generation while another reader holds its lease");
                oldGeneration.Dispose();
                using var unchanged = await blob.PrepareAsync(retirement, "Retirement", true, ct: ct);
                Check(!Directory.Exists(oldGeneration.PhysicalRoot) && unchanged.PhysicalRoot == current.PhysicalRoot && File.Exists(current.ResolveItem("file.txt")),
                    "An unchanged refresh reclaims released retired generations while preserving the active current generation");
            }
            Check(GitSourceService.RepositoryIdentity("https://example.com/team/repo.git") == GitSourceService.RepositoryIdentity("git@example.com:team/repo.git"), "HTTPS and SSH spelling can identify the same repository");
            Check(GitSourceService.SanitizeEndpoint("ssh://git:password@example.com/team/repo.git?token=secret#fragment") == "ssh://git@example.com/team/repo.git",
                "SSH repository metadata excludes embedded passwords, tokens and fragments");
            Check(FolderSourcePaths.ConfigurationKey(source) != FolderSourcePaths.ConfigurationKey(source.CopyWithEndpoint("https://other.blob.core.windows.net")), "Changing trusted origin invalidates local source/cache identity");
            await CheckGitInspectorAsync(fixture, cache, passed, ct);
            return passed;
        }
        finally
        {
            // Only this unique operation-owned fixture is deleted; no checkout supplied by a user is touched.
            string full = Path.GetFullPath(fixture), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("Surf2-source-check-", StringComparison.Ordinal) && Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(full, true);
            }
        }
    }
    private static async Task CheckGitInspectorAsync(string fixture, FolderCacheStore cache, List<string> passed, CancellationToken ct)
    {
        string root = Path.Combine(fixture, "git"), nested = Path.Combine(root, "src"); Directory.CreateDirectory(nested);
        await Git(root, ["init", "--quiet"], ct);
        await Git(root, ["config", "remote.origin.url", "https://example.com/team/repo.git"], ct);
        var service = new GitSourceService(cache); var unborn = await service.InspectAsync(nested, ct);
        if (unborn == null || unborn.ScopedSubfolder != "src" || unborn.Commit.Length != 0 || !unborn.Matches("git@example.com:team/repo.git"))
            throw new InvalidOperationException("Nested/unborn Git detection failed.");
        passed.Add("Local Git detection handles nested folders and unborn HEAD without modifying the checkout");
        await Git(root, ["-c", "user.name=Surf2 Test", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "--quiet", "-m", "fixture"], ct);
        string linked = Path.Combine(fixture, "worktree"); await Git(root, ["worktree", "add", "--quiet", "--detach", linked], ct);
        var detached = await service.InspectAsync(linked, ct);
        if (detached == null || detached.Commit.Length < 40 || detached.Branch != "(detached HEAD)" || !File.Exists(Path.Combine(linked, ".git")))
            throw new InvalidOperationException("Linked Git worktree detection failed.");
        passed.Add("Local Git detection resolves gitdir worktrees and detached HEAD");
        await File.WriteAllTextAsync(Path.Combine(nested, "sample.cs"), "class First { }", ct);
        await Git(root, ["add", "--", "src/sample.cs"], ct);
        await Git(root, ["-c", "user.name=Surf2 Test", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "content"], ct);
        var managed = new GitSourceService(cache, allowLocalFixture: true);
        var source = new ResourceSourceDefinition { SourceId = Guid.NewGuid(), Provider = SourceProvider.Git, Endpoint = root, Subfolder = "src", Ref = "HEAD", ConfigurationRevision = 1 };
        using (var prepared = await managed.PrepareAsync(source, "Managed fixture", ct: ct))
        {
            if (prepared.Manifest.Items.Count != 1 || prepared.Manifest.Items[0].LogicalPath != "sample.cs" ||
                await File.ReadAllTextAsync(prepared.ResolveItem("sample.cs"), ct) != "class First { }")
                throw new InvalidOperationException("Managed Git checkout did not preserve selected subfolder identity/content.");
        }
        passed.Add("A managed Git checkout prepares real committed files under the selected subfolder");
        await File.WriteAllTextAsync(Path.Combine(nested, "sample.cs"), "class Second { }", ct);
        await Git(root, ["add", "--", "src/sample.cs"], ct);
        await Git(root, ["-c", "user.name=Surf2 Test", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "changed"], ct);
        using (var reused = await managed.PrepareAsync(source, "Managed fixture", ct: ct))
            if (await File.ReadAllTextAsync(reused.ResolveItem("sample.cs"), ct) != "class First { }")
                throw new InvalidOperationException("Managed Git reuse unexpectedly fetched a new revision.");
        passed.Add("Reopening a managed Git source reuses its prepared checkout until explicit refresh");
        using (var refreshed = await managed.PrepareAsync(source, "Managed fixture", refresh: true, ct: ct))
            if (await File.ReadAllTextAsync(refreshed.ResolveItem("sample.cs"), ct) != "class Second { }")
                throw new InvalidOperationException("Managed Git refresh did not advance to the new commit.");
        passed.Add("Explicit Git refresh reuses objects and publishes a newly prepared checkout");
        await cache.RemoveAsync(FolderSourcePaths.ConfigurationKey(source), ct);
        if (!File.Exists(Path.Combine(nested, "sample.cs"))) throw new InvalidOperationException("Cache eviction touched the user checkout.");
        passed.Add("Managed Git cache cleanup handles read-only objects and preserves the source checkout");
    }
    private static async Task Git(string directory, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=NUL"); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); await output; await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Private Git fixture setup failed.");
    }
    private static ResourceSourceDefinition CopyWithEndpoint(this ResourceSourceDefinition value, string endpoint) { var copy = value.Copy(); copy.Endpoint = endpoint; return copy; }

    private sealed class MemoryBlobTransport : IBlobFolderTransport
    {
        private readonly Dictionary<string, (byte[] Bytes, int Version)> _data = new(StringComparer.Ordinal);
        private int _version;
        public int Downloads { get; private set; }
        public string? FailDownload { get; set; }
        public void Put(string name, string contents) => _data[name] = (Encoding.UTF8.GetBytes(contents), ++_version);
        public string Read(string name) => Encoding.UTF8.GetString(_data[name].Bytes);
        public void Remove(string name) => _data.Remove(name);
        public void Clear() => _data.Clear();
        private static BlobSourceObject Describe(string name, (byte[] Bytes, int Version) data) => new(name, data.Bytes.Length, DateTimeOffset.UnixEpoch, "\"" + data.Version + "\"");
        public async IAsyncEnumerable<BlobSourceObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var pair in _data.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
            { ct.ThrowIfCancellationRequested(); yield return Describe(pair.Key, pair.Value); }
        }
        public async Task DownloadAsync(BlobSourceObject expected, Stream destination, CancellationToken ct)
        {
            if (FailDownload == expected.Name) throw new FolderSourceException("Injected download failure.");
            var current = _data[expected.Name];
            if (Describe(expected.Name, current).ETag != expected.ETag) throw new FolderConflictException("Injected remote change.");
            Downloads++; await destination.WriteAsync(current.Bytes, ct);
        }
        public async Task<BlobSourceObject> UploadAsync(string name, Stream content, string? expectedETag, CancellationToken ct)
        {
            bool exists = _data.TryGetValue(name, out var current);
            if (expectedETag == null ? exists : !exists || Describe(name, current).ETag != expectedETag) throw new FolderConflictException("Conditional upload failed.");
            using var copy = new MemoryStream(); await content.CopyToAsync(copy, ct); _data[name] = (copy.ToArray(), ++_version); return Describe(name, _data[name]);
        }
        public Task DeleteAsync(BlobSourceObject expected, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_data.TryGetValue(expected.Name, out var current) || Describe(expected.Name, current).ETag != expected.ETag) throw new FolderConflictException("Conditional delete failed.");
            _data.Remove(expected.Name); return Task.CompletedTask;
        }
    }
}

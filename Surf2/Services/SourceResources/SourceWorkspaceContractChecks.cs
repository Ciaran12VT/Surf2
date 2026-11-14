using System.Diagnostics;
using System.IO;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RemoteFolders;

namespace Surf2.Services.SourceResources;

public static class SourceWorkspaceContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var passed = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Source workspace: " + name); passed.Add(name); }
        string fixture = Path.Combine(Path.GetTempPath(), "Surf2-workspace-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture); var registered = new List<ScopedResource>();
        try
        {
            var member = new ScopedResource { Kind = ResourceKind.Folder, Path = "https://fixture.blob.core.windows.net/container", Source = new() { Provider = SourceProvider.AzureBlob, Endpoint = "https://fixture.blob.core.windows.net", Container = "container", AuthenticationKind = "Anonymous" } };
            var scope = new Scope(); scope.Resources.Add(member); scope.EnsureResourceContainers(); ResourceWorkspace.Register(scope); registered.Add(member);
            string root1 = Path.Combine(fixture, "first"), root2 = Path.Combine(fixture, "second"); Directory.CreateDirectory(root1); Directory.CreateDirectory(root2);
            string[] names = ["A.cs", "a.cs", "folder/space name %.sql", "node", "node/child.txt"];
            PreparedFolderSource Prepared(string root)
            {
                var items = names.Select(name => new FolderSourceItem(name, name == "A.cs" ? "objects/" + FolderSourcePaths.Hash(name) : FolderSourcePaths.CacheName(name), 1, DateTimeOffset.UnixEpoch, "etag", "hash")).ToArray();
                foreach (var item in items) { string path = FolderSourcePaths.SafeCombine(root, item.CacheRelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, item.LogicalPath == "A.cs" ? "public class BlobUpper { }" : item.LogicalPath == "a.cs" ? "public class BlobLower { }" : "x"); }
                return new(root, new(FolderSourcePaths.ConfigurationKey(member.Source!), "generation", DateTimeOffset.UtcNow, items));
            }
            ResourceWorkspace.Admit(member, Prepared(root1));
            var query = new SourcePhysicalExplorerQueries(); var children = await query.ReadDirectoryAsync(root1, "en-IE", new ExplorerLimits(), ct);
            Check(children.Any(e => e.DisplayName == "A.cs") && children.Any(e => e.DisplayName == "a.cs") && children.Any(e => e.DisplayName == "folder"), "Blob explorer preserves exact file case and logical folders over encoded cache paths");
            var colliding = children.Where(e => e.DisplayName == "node").ToArray();
            string nodeFile = ResourceWorkspace.Locator(member.PublicId, "node"), nodeFolder = ResourceWorkspace.DirectoryLocator(member.PublicId, "node");
            Check(colliding.Length == 2 && colliding.Select(e => e.LogicalKey).Distinct(StringComparer.Ordinal).Count() == 2 &&
                File.Exists(ResourceWorkspace.Resolve(nodeFile)) && ResourceWorkspace.Resolve(nodeFile) != ResourceWorkspace.Resolve(nodeFolder),
                "A Blob object and virtual folder with the same name have distinct stable locators");
            string rootLocator = ResourceWorkspace.Locator(member.PublicId, ""), fileLocator = ResourceWorkspace.Locator(member.PublicId, "folder/space name %.sql");
            string firstFile = ResourceWorkspace.Resolve(fileLocator);
            Check(ResourceWorkspace.Resolve(rootLocator) == root1 && ResourceWorkspace.ToPersistentPath(firstFile, member.PublicId) == fileLocator,
                "Source root and encoded child addresses round-trip through stable member identity");
            ResourceWorkspace.Admit(member, Prepared(root2));
            Check(ResourceWorkspace.Resolve(rootLocator) == root2 && ResourceWorkspace.Resolve(fileLocator) != firstFile && ResourceWorkspace.ToPersistentPath(ResourceWorkspace.Resolve(fileLocator), member.PublicId) == fileLocator,
                "A saved source/item address resolves the current prepared generation after refresh");
            var blobReferences = await BuildMemoryReferences(member, root2, ct);
            Check(blobReferences.Entities.Any(e => e.Name == "BlobUpper" && e.FilePath == ResourceWorkspace.Locator(member.PublicId, "A.cs")) &&
                blobReferences.Entities.Any(e => e.Name == "BlobLower" && e.FilePath == ResourceWorkspace.Locator(member.PublicId, "a.cs")),
                "Default memory references parse Blob logical extensions and preserve exact source/item identity even without a cache-file extension");
            ResourceWorkspace.Block(member, "fixture unload");
            try { _ = ResourceWorkspace.Resolve(fileLocator); throw new InvalidOperationException("Blocked member resolved."); }
            catch (IOException) { passed.Add("An unloaded resource cannot resolve its saved item address"); }

            string gitRoot = Path.Combine(fixture, "managed-git"); Directory.CreateDirectory(Path.Combine(gitRoot, ".git")); Directory.CreateDirectory(Path.Combine(gitRoot, "src"));
            File.WriteAllText(Path.Combine(gitRoot, ".git", "config"), "private metadata"); File.WriteAllText(Path.Combine(gitRoot, "src", "file.cs"), "public class GitType { }");
            var gitMember = new ScopedResource { Kind = ResourceKind.Folder, Source = new() { Provider = SourceProvider.Git, Endpoint = "https://example.invalid/repo.git" } };
            scope.Resources.Add(gitMember); ResourceWorkspace.Register(scope); registered.Add(gitMember);
            ResourceWorkspace.Admit(gitMember, new(gitRoot, new("fixture", "commit", DateTimeOffset.UtcNow,
                [new("src/file.cs", "src/file.cs", 1, DateTimeOffset.UnixEpoch, "commit", "hash")])));
            var gitChildren = await query.ReadDirectoryAsync(gitRoot, "en-IE", new ExplorerLimits(), ct);
            Check(gitChildren.Count() == 1 && gitChildren[0].DisplayName == "src" && !gitChildren.Any(e => e.DisplayName == ".git"), "Managed Git enumeration excludes private Git metadata and lists only manifest content");
            var nested = await query.ReadDirectoryAsync(gitChildren[0].Path, "en-IE", new ExplorerLimits(), ct);
            Check(nested.Single().DisplayName == "file.cs" && nested.Single().Path == Path.Combine(gitRoot, "src", "file.cs"), "Managed Git logical directories resolve normal checkout files");
            var gitReferences = await BuildMemoryReferences(gitMember, gitRoot, ct);
            Check(gitReferences.IndexedFileCount == 1 && gitReferences.Entities.Any(e => e.Name == "GitType" && e.FilePath == ResourceWorkspace.Locator(gitMember.PublicId, "src/file.cs")),
                "Default memory references parse managed Git contents using logical locators and exclude Git metadata");
            string pairedRoot = Path.Combine(fixture, "paired-watch"); Directory.CreateDirectory(pairedRoot);
            var pairedMember = new ScopedResource { Kind = ResourceKind.Folder, Path = pairedRoot, ContainerId = member.ContainerId, PairedResourceId = member.PublicId };
            member.PairedResourceId = pairedMember.PublicId;
            scope.Resources.Add(pairedMember); ResourceWorkspace.Register(scope); registered.Add(pairedMember); ResourceWorkspace.Admit(pairedMember);
            var notification = new TaskCompletionSource<PhysicalReferenceInvalidation>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var watch = new PhysicalReferenceWatch(ExplorerView(pairedMember, pairedRoot), change => notification.TrySetResult(change)))
            {
                watch.RecordChange(WatcherChangeTypes.Changed, Path.Combine(pairedRoot, "bin", "generated.txt"));
                var observed = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Check(observed.ScopeResourceKeys.Contains(1) && (observed.Reason & PhysicalReferenceInvalidationReason.Changed) != 0,
                    "Paired folders invalidate on generated-file changes excluded from ordinary reference parsing");
            }
            await CheckLargeManifestAsync(fixture, scope, registered, Check, ct);
            await CheckGroupingAsync(fixture, Check, ct);
            return passed;
        }
        finally
        {
            foreach (var member in registered) ResourceWorkspace.Block(member, release: true);
            string full = Path.GetFullPath(fixture), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("Surf2-workspace-check-", StringComparison.Ordinal) && Directory.Exists(full))
            {
                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(full, true);
            }
        }
    }
    private static Task<Surf2.Services.ScopeReferenceIndex> BuildMemoryReferences(ScopedResource member, string root, CancellationToken ct)
        => MemoryReferenceIndex.BuildAsync(ExplorerView(member, root), ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings()), _ => Task.CompletedTask, ct);
    private static async Task CheckLargeManifestAsync(string fixture, Scope scope, List<ScopedResource> registered,
        Action<bool, string> check, CancellationToken ct)
    {
        const int count = 10_000;
        var member = new ScopedResource { Kind = ResourceKind.Folder, Source = new() { Provider = SourceProvider.AzureBlob,
            Endpoint = "https://fixture.blob.core.windows.net", Container = "large", AuthenticationKind = "Anonymous" } };
        string root = Path.Combine(fixture, "large-manifest"); Directory.CreateDirectory(root);
        var items = new CountingManifestItems(Enumerable.Range(0, count).Select(i =>
        {
            string logical = $"area{i / 100}/module{i / 10}/file{i}.cs";
            return new FolderSourceItem(logical, FolderSourcePaths.CacheName(logical), 1, DateTimeOffset.UnixEpoch, "etag", "hash");
        }).ToArray());
        var prepared = new PreparedFolderSource(root, new("fixture", "large", DateTimeOffset.UtcNow, items));
        scope.Resources.Add(member); ResourceWorkspace.Register(scope); registered.Add(member); ResourceWorkspace.Admit(member, prepared);
        items.ResetReads();
        var pending = new Stack<string>(); pending.Push(root); var query = new SourcePhysicalExplorerQueries();
        int files = 0, directories = 0; bool roundTrips = true;
        while (pending.TryPop(out var directory))
        {
            directories++;
            foreach (var child in await query.ReadDirectoryAsync(directory, "en-IE", new ExplorerLimits(), ct))
            {
                roundTrips &= ResourceWorkspace.Resolve(child.LogicalKey!) == child.Path &&
                    ResourceWorkspace.ToPersistentPath(child.Path, member.PublicId) == child.LogicalKey;
                if (child.IsDirectory) pending.Push(child.Path);
                else { files++; roundTrips &= prepared.ResolveItem(ResourceWorkspace.TryParse(child.LogicalKey!, out _, out var logical) ? logical : "") == child.Path; }
            }
        }
        check(files == count && directories == 1_101 && roundTrips,
            "A 10,000-file nested Blob manifest traverses every child once with stable directory and file round-trips");
        check(items.Reads == 0,
            "Warm nested browsing, item resolution and reverse lookup perform zero manifest scans after the immutable index is built");
    }
    private sealed class CountingManifestItems(IReadOnlyList<FolderSourceItem> items) : IReadOnlyList<FolderSourceItem>
    {
        internal long Reads { get; private set; }
        internal void ResetReads() => Reads = 0;
        public int Count => items.Count;
        public FolderSourceItem this[int index] { get { Reads++; return items[index]; } }
        public IEnumerator<FolderSourceItem> GetEnumerator()
        {
            foreach (var item in items) { Reads++; yield return item; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static ExplorerScope ExplorerView(ScopedResource member, string root)
    {
        var resource = new ExplorerResource(1, 0, member.ResourceId, member.Kind, root, "Fixture", true, true, null, null, null, null, false)
            { PublicId = member.PublicId, ContainerId = member.ContainerId, Source = member.Source };
        return new ExplorerScope(new(Guid.NewGuid(), 1, 1, "scope", "snapshots", "diagrams", true, [], [], false),
            "fixture", "Fixture", [resource], [], "en-IE");
    }
    private static async Task CheckGroupingAsync(string fixture, Action<bool, string> check, CancellationToken ct)
    {
        var scope = new Scope();
        string first = Path.Combine(fixture, "checkout1"), second = Path.Combine(fixture, "checkout2");
        foreach (string path in new[] { first, second })
        {
            Directory.CreateDirectory(path); await Git(path, ["init", "--quiet"], ct);
            await Git(path, ["config", "remote.origin.url", "https://example.invalid/team/repo.git"], ct);
        }
        var a = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Path = first, DisplayNameOverride = "Kept alias", Source = new() { Provider = SourceProvider.LocalFileSystem } }, ct: ct);
        var b = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Path = second, Source = new() { Provider = SourceProvider.LocalFileSystem } }, ct: ct);
        check(a.ContainerId == b.ContainerId && scope.ResourceContainers.Count == 1, "Explicit LocalFileSystem checkouts of one repository join one container");
        var duplicate = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Path = first + Path.DirectorySeparatorChar }, ct: ct);
        check(ReferenceEquals(duplicate, a) && scope.Resources.Count == 2 && a.DisplayNameOverride == "Kept alias", "Adding an existing local path reuses its concrete resource and preserves its alias");
        var remote = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Source = new() { Provider = SourceProvider.Git, Endpoint = "https://example.invalid/team/repo.git", Ref = "main" } }, ct: ct);
        check(remote.ContainerId == a.ContainerId && scope.Resources.Count == 3, "A direct Git source joins matching local checkout alternatives");
        var again = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Source = remote.Source!.Copy() }, ct: ct);
        check(ReferenceEquals(again, remote) && scope.Resources.Count == 3, "An identical direct Git location is not added twice");
        string plain = Path.Combine(fixture, "ordinary"); Directory.CreateDirectory(plain); Directory.CreateDirectory(Path.Combine(plain, ".git"));
        var ordinary = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Path = plain }, ct: ct);
        check(scope.Resources.Contains(ordinary), "Unreadable or unrecognized Git metadata does not prevent ordinary folder registration");
        var blob = new ScopedResource { Kind = ResourceKind.Folder, Source = new() { Provider = SourceProvider.AzureBlob, Endpoint = "https://fixture.blob.core.windows.net", Container = "fixture", AuthenticationKind = "Anonymous" } };
        await ScopeSourceActions.AddMemberAsync(scope, blob, ct: ct); int before = scope.Resources.Count;
        var linked = await ScopeSourceActions.AddMemberAsync(scope, new() { Kind = ResourceKind.Folder, Path = plain }, blob, ct);
        check(ReferenceEquals(linked, ordinary) && scope.Resources.Count == before && linked.ContainerId == blob.ContainerId, "Linking an already registered folder moves its existing resource into the selected container");
    }
    private static async Task Git(string directory, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=NUL"); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Git did not start for a private fixture.");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); await output; await error;
        if (process.ExitCode != 0) throw new IOException("Private Git grouping fixture setup failed.");
    }
}

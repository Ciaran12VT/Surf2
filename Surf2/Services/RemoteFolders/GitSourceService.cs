using System.Diagnostics;
using System.IO;
using System.Text;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

public sealed record GitCheckoutInfo(string RepositoryRoot, string ScopedSubfolder, string Branch, string Commit,
    IReadOnlyDictionary<string, string> Remotes)
{
    public bool Matches(string endpoint) => Remotes.Values.Any(v => GitSourceService.RepositoryIdentity(v) == GitSourceService.RepositoryIdentity(endpoint));
}

public sealed class GitSourceService
{
    private readonly FolderCacheStore cache;
    private readonly bool _allowLocalFixture;
    public GitSourceService(FolderCacheStore cache) : this(cache, false) { }
    // Private-fixture transport for executable checks; normal application construction only permits HTTPS/SSH.
    internal GitSourceService(FolderCacheStore cache, bool allowLocalFixture) { this.cache = cache; _allowLocalFixture = allowLocalFixture; }
    public async Task<GitCheckoutInfo?> InspectAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return null;
        // rev-parse handles .git directories, gitdir files, linked worktrees, and nested scoped folders.
        var root = await GitAsync(folder, ["rev-parse", "--show-toplevel"], ct, allowFailure: true);
        if (root.Code != 0) return null;
        string repository = Path.GetFullPath(root.Output.Trim());
        string relative = Path.GetRelativePath(repository, Path.GetFullPath(folder)).Replace('\\', '/');
        if (relative == ".") relative = "";
        var branch = await GitAsync(folder, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, allowFailure: true);
        var commit = await GitAsync(folder, ["rev-parse", "--verify", "HEAD"], ct, allowFailure: true);
        var remotes = await GitAsync(folder, ["config", "--get-regexp", "^remote\\..*\\.url$"], ct, allowFailure: true);
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf(' ');
            if (split < 0) continue;
            string name = line[..split];
            if (name.StartsWith("remote.") && name.EndsWith(".url")) urls[name[7..^4]] = SanitizeEndpoint(line[(split + 1)..].Trim());
        }
        return new(repository, relative, branch.Code == 0 ? branch.Output.Trim() : "(detached HEAD)", commit.Code == 0 ? commit.Output.Trim() : "", urls);
    }

    public async Task<FolderAccessResult> TestAsync(ResourceSourceDefinition source, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source);
        string selected = string.IsNullOrWhiteSpace(source.Ref) ? "HEAD" : source.Ref;
        var result = await GitAsync(null, ["ls-remote", "--symref", "--", source.Endpoint, selected, "refs/heads/" + selected, "refs/tags/" + selected], ct, allowLocalFixture: _allowLocalFixture);
        if (string.IsNullOrWhiteSpace(result.Output) && !IsCommit(selected)) throw new FolderSourceException("The repository is accessible, but the selected branch or tag was not found.");
        return new("Repository access verified. " + (selected == "HEAD" ? "The remote default branch will be resolved on preparation." : "Selected revision: " + selected));
    }

    public async Task<PreparedFolderSource> PrepareAsync(ResourceSourceDefinition source, string name, bool refresh = false,
        IProgress<FolderSourceProgress>? progress = null, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source);
        using var operation = await cache.AcquireOperationAsync(ct);
        var previous = cache.TryOpenCurrent(source);
        if (!refresh && previous != null) return previous;
        string stage = cache.CreateStage(source), checkout = Path.Combine(stage, "content");
        try
        {
            string? previousCheckout = previous?.PhysicalRoot;
            if (previous != null && previous.Manifest.RootRelativePath.Length != 0)
                foreach (string _ in previous.Manifest.RootRelativePath.Split('/')) previousCheckout = Path.GetDirectoryName(previousCheckout!);
            if (previousCheckout != null && Directory.Exists(Path.Combine(previousCheckout, ".git")))
            {
                long copyBytes = FolderCacheStore.DirectoryBytes(Path.Combine(previousCheckout, ".git"));
                cache.CheckCapacity(source, copyBytes);
                CopyDirectory(Path.Combine(previousCheckout, ".git"), Path.Combine(checkout, ".git"), ct);
            }
            else await GitAsync(checkout, ["init", "--quiet", "--template="], ct, isolated: true);
            progress?.Report(new("Fetching repository"));
            string requested = string.IsNullOrWhiteSpace(source.Ref) ? "HEAD" : source.Ref;
            await GitAsync(checkout, ["fetch", "--no-tags", "--depth=1", "--recurse-submodules=no", "--no-auto-maintenance", "--", source.Endpoint, requested],
                ct, monitor: () => cache.CheckCapacity(source), allowLocalFixture: _allowLocalFixture);
            string commit = (await GitAsync(checkout, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"], ct, isolated: true)).Output.Trim();
            if (!IsCommit(commit)) throw new FolderSourceException("Git did not resolve a commit for this source.");
            var tree = await GitAsync(checkout, ["ls-tree", "-r", "-z", "--full-tree", commit], ct, isolated: true);
            var paths = new List<string>(); var windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in tree.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = entry.IndexOf('\t');
                if (tab < 0) throw new FolderSourceException("Git returned an invalid tree entry.");
                string path = entry[(tab + 1)..];
                FolderSourcePaths.RequireWindowsRelativePath(path);
                if (path.Split('/').Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase))) throw new FolderSourceException("A Git tree contains a reserved metadata path.");
                string partial = "";
                foreach (string part in path.Split('/'))
                {
                    partial = partial.Length == 0 ? part : partial + "/" + part;
                    if (windows.TryGetValue(partial, out var spelling) && spelling != partial)
                        throw new FolderSourceException("This Git tree contains paths that differ only by case and cannot be checked out on Windows: " + path);
                    windows[partial] = partial;
                }
                if (entry.StartsWith("120000 ") || entry.StartsWith("160000 "))
                    throw new FolderSourceException("This Git tree contains a symlink or submodule that this version cannot materialize: " + path);
                paths.Add(path);
                if (paths.Count > 100_000) throw new FolderCapacityException("The repository exceeds the supported 100,000 file limit.");
            }
            progress?.Report(new("Preparing working files", Files: paths.Count));
            // Isolated configuration means a repository attribute cannot activate a user/system smudge filter.
            await GitAsync(checkout, ["-c", "core.autocrlf=false", "-c", "core.symlinks=false", "checkout", "--quiet", "--detach", "--force", commit],
                ct, isolated: true, monitor: () => cache.CheckCapacity(source));
            string subfolder = source.Subfolder.Trim('/').Replace('\\', '/');
            if (subfolder.Length != 0) FolderSourcePaths.RequireWindowsRelativePath(subfolder);
            string root = subfolder.Length == 0 ? checkout : FolderSourcePaths.SafeCombine(checkout, subfolder);
            if (!Directory.Exists(root)) throw new FolderSourceException("The selected repository subfolder does not exist at this commit.");
            var items = new List<FolderSourceItem>(); long completed = 0;
            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (subfolder.Length != 0 && !path.StartsWith(subfolder + "/", StringComparison.Ordinal)) continue;
                string local = FolderSourcePaths.SafeCombine(checkout, path);
                if (new FileInfo(local).Length < 2048)
                {
                    using var reader = new StreamReader(local);
                    if ((await reader.ReadLineAsync(ct)) == "version https://git-lfs.github.com/spec/v1")
                        throw new FolderSourceException("This repository contains a Git LFS pointer. Fetching LFS content is not supported yet: " + path);
                }
                string logical = subfolder.Length == 0 ? path : path[(subfolder.Length + 1)..];
                var info = new FileInfo(local);
                items.Add(new(logical, logical, info.Length, info.LastWriteTimeUtc, commit, await FolderSourcePaths.HashFileAsync(local, ct)));
                completed += info.Length;
                if (items.Count % 100 == 0) { cache.CheckCapacity(source); progress?.Report(new("Verifying checkout", completed, Files: items.Count)); }
            }
            cache.CheckCapacity(source);
            var manifest = new FolderSourceManifest(FolderSourcePaths.ConfigurationKey(source), commit, DateTimeOffset.UtcNow, items, subfolder);
            previous?.Dispose(); previous = null;
            return cache.Publish(source, name, stage, manifest);
        }
        catch { cache.DeleteOwnedDirectory(stage); throw; }
        finally { previous?.Dispose(); }
    }

    public static string RepositoryIdentity(string endpoint)
    {
        endpoint = SanitizeEndpoint(endpoint).TrimEnd('/');
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh")
        {
            string port = uri.IsDefaultPort || uri.Port is 22 or 443 ? "" : ":" + uri.Port;
            return uri.Host.ToLowerInvariant() + port + "/" + uri.AbsolutePath.Trim('/').TrimGitSuffix();
        }
        int colon = endpoint.IndexOf(':');
        if (colon > 0 && !endpoint[..colon].Contains('/') && !endpoint[..colon].Contains('\\'))
        {
            string host = endpoint[..colon]; int at = host.LastIndexOf('@'); if (at >= 0) host = host[(at + 1)..];
            return host.ToLowerInvariant() + "/" + endpoint[(colon + 1)..].Trim('/').TrimGitSuffix();
        }
        return endpoint.TrimGitSuffix();
    }
    public static string SanitizeEndpoint(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh")
            return new UriBuilder(uri) { UserName = uri.Scheme == "ssh" ? uri.UserInfo.Split(':')[0] : "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return endpoint.Trim();
    }
    private void Validate(ResourceSourceDefinition source)
    {
        if (source.Provider != SourceProvider.Git) throw new ArgumentException("This is not a Git source.");
        string endpoint = source.Endpoint;
        if (endpoint.Any(char.IsControl) || endpoint.StartsWith('-') || endpoint != SanitizeEndpoint(endpoint))
            throw new ArgumentException("Use a repository URL without an embedded password, token or query. Sign in through Git Credential Manager or your SSH agent.");
        bool url = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh";
        bool ssh = !url && endpoint.Contains('@') && endpoint.Contains(':') && !endpoint.Contains(' ');
        bool fixture = _allowLocalFixture && Path.IsPathFullyQualified(endpoint) && Directory.Exists(endpoint);
        if (!url && !ssh && !fixture) throw new ArgumentException("Use an HTTPS or SSH Git repository URL.");
        if (source.Ref.StartsWith('-') || source.Ref.Any(char.IsControl) || source.Ref.Any(char.IsWhiteSpace)) throw new ArgumentException("The Git revision is invalid.");
    }
    private static bool IsCommit(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("A managed Git copy contains an unexpected link.");
            string target = Path.Combine(destination, Path.GetFileName(path));
            if ((attrs & FileAttributes.Directory) != 0) CopyDirectory(path, target, ct); else File.Copy(path, target);
        }
    }
    private sealed record GitResult(int Code, string Output);
    private static async Task<GitResult> GitAsync(string? directory, IEnumerable<string> arguments, CancellationToken ct,
        bool isolated = false, bool allowFailure = false, Action? monitor = null, bool allowLocalFixture = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (directory != null) start.WorkingDirectory = directory;
        foreach (string option in new[] { "-c", "core.hooksPath=NUL", "-c", "core.longpaths=true", "-c", "protocol.ext.allow=never", "-c", "protocol.file.allow=" + (allowLocalFixture ? "always" : "never"), "-c", "submodule.recurse=false" }) start.ArgumentList.Add(option);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "auto";
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        if (isolated) { start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "NUL"; }
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new FolderSourceException("Git for Windows is required. Install Git and ensure git.exe is on PATH.", ex); }
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = ReadBoundedAsync(process.StandardOutput, 32 * 1024 * 1024, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, 256 * 1024, timeout.Token);
        try
        {
            var exit = process.WaitForExitAsync(timeout.Token);
            while (!exit.IsCompleted)
            {
                if (await Task.WhenAny(exit, Task.Delay(1000, timeout.Token)) == exit) break;
                timeout.Token.ThrowIfCancellationRequested();
                monitor?.Invoke();
                if (output.IsFaulted) await output;
                if (error.IsFaulted) await error;
            }
            await exit;
            string text = await output; string diagnostic = await error;
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 && !allowFailure)
            {
                if (allowLocalFixture) throw new FolderSourceException("Private Git fixture failed: " + diagnostic);
                throw new FolderSourceException("Git could not complete this operation. Check repository access, the selected revision, Git Credential Manager, and SSH host/key configuration.");
            }
            return new(process.ExitCode, text);
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken ct)
    {
        var text = new StringBuilder(); char[] buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        { if (text.Length > maximum - count) throw new FolderCapacityException("Git output exceeded the supported discovery limit."); text.Append(buffer, 0, count); }
        return text.ToString();
    }
}

internal static class GitEndpointExtensions
{
    internal static string TrimGitSuffix(this string value) => value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}

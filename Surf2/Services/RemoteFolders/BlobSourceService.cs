using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Surf2.Models;

namespace Surf2.Services.RemoteFolders;

public sealed record BlobSourceObject(string Name, long ByteCount, DateTimeOffset? LastModified, string ETag, string? VersionId = null);

/// <summary>Small transport boundary permits deterministic reconciliation tests without cloud writes.</summary>
public interface IBlobFolderTransport
{
    IAsyncEnumerable<BlobSourceObject> ListAsync(string prefix, CancellationToken ct);
    Task TestReadAsync(BlobSourceObject expected, CancellationToken ct) => Task.CompletedTask;
    Task DownloadAsync(BlobSourceObject expected, Stream destination, CancellationToken ct);
    Task<BlobSourceObject> UploadAsync(string name, Stream content, string? expectedETag, CancellationToken ct);
    Task DeleteAsync(BlobSourceObject expected, CancellationToken ct);
}

public sealed partial class BlobSourceService
{
    private readonly FolderCacheStore _cache;
    private readonly FolderCredentialStore _credentials;
    private readonly Func<ResourceSourceDefinition, IBlobFolderTransport>? _transport;
    private readonly ConcurrentDictionary<string, InteractiveBrowserCredential> _interactive = new();
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    public BlobSourceService(FolderCacheStore cache, FolderCredentialStore credentials,
        Func<ResourceSourceDefinition, IBlobFolderTransport>? transport = null)
    { _cache = cache; _credentials = credentials; _transport = transport; }

    public void SaveSas(ResourceSourceDefinition source, string sas)
    {
        source = source.Copy();
        Validate(source);
        sas = sas.Trim().TrimStart('?');
        if (!sas.Contains("sig=", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Enter a SAS token containing a signature.");
        _credentials.Save(source, sas);
    }
    public async Task SignInAsync(ResourceSourceDefinition source, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source);
        var credential = Interactive(source);
        var record = await credential.AuthenticateAsync(new TokenRequestContext(["https://storage.azure.com/.default"]), ct);
        using var stream = new MemoryStream(); await record.SerializeAsync(stream, ct);
        _credentials.Save(source, Convert.ToBase64String(stream.ToArray()));
    }
    public void Disconnect(ResourceSourceDefinition source)
    {
        _interactive.TryRemove(FolderSourcePaths.ConfigurationKey(source), out _);
        _credentials.Remove(source);
    }
    public async Task CreateContainerAsync(ResourceSourceDefinition source, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source);
        await Client(source).CreateAsync(PublicAccessType.None, cancellationToken: ct);
    }
    public async Task<FolderAccessResult> TestAsync(ResourceSourceDefinition source, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source);
        int count = 0; long bytes = 0;
        var transport = Transport(source);
        await foreach (var item in transport.ListAsync(FolderSourcePaths.NormalizePrefix(source.Prefix), ct))
        {
            if (count == 0) await transport.TestReadAsync(item, ct);
            bytes = checked(bytes + item.ByteCount); count++;
            if (count >= 10) break;
        }
        return new("Container listing verified" + (count == 0 ? " (empty selection)." : " (sample only; download size is checked before preparation)."), bytes, count);
    }
    public async Task<PreparedFolderSource> PrepareAsync(ResourceSourceDefinition source, string name, bool refresh = false,
        IProgress<FolderSourceProgress>? progress = null, CancellationToken ct = default)
    {
        source = source.Copy();
        Validate(source);
        using var operation = await _cache.AcquireOperationAsync(ct);
        var previous = _cache.TryOpenCurrent(source);
        if (!refresh && previous != null) return previous;
        string? stage = null;
        try
        {
            var transport = Transport(source);
            var listed = await ListBoundedAsync(source, transport, progress, ct);
            var old = previous?.Manifest.Items.ToDictionary(i => i.LogicalPath, StringComparer.Ordinal) ?? [];
            string prefix = FolderSourcePaths.NormalizePrefix(source.Prefix);
            bool unchanged = previous != null && old.Count == listed.Count;
            if (unchanged)
                foreach (var item in listed)
                {
                    ct.ThrowIfCancellationRequested();
                    string logical = item.Name[prefix.Length..];
                    if (!old.TryGetValue(logical, out var cached) || !await CachedItemMatchesAsync(previous!, cached, item, ct))
                    { unchanged = false; break; }
                }
            if (unchanged)
            {
                ct.ThrowIfCancellationRequested();
                _cache.MarkCurrentChecked(source);
                progress?.Report(new("Blob files are unchanged", Files: listed.Count));
                var retained = previous!; previous = null; return retained;
            }
            stage = _cache.CreateStage(source);
            string root = Path.Combine(stage, "content");
            long bytes = listed.Sum(i => i.ByteCount);
            _cache.CheckCapacity(source, bytes);
            long driveReserve = _cache.ReadSettings().FreeSpaceReserveBytes;
            var items = new List<FolderSourceItem>(); long done = 0;
            foreach (var item in listed)
            {
                ct.ThrowIfCancellationRequested();
                string logical = item.Name[prefix.Length..], relative = FolderSourcePaths.CacheName(logical), path = FolderSourcePaths.SafeCombine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string hash;
                if (old.TryGetValue(logical, out var cached) && previous != null && await CachedItemMatchesAsync(previous, cached, item, ct))
                { File.Copy(previous.ResolveItem(logical), path); hash = cached.Sha256; }
                else
                {
                    await DownloadAsync(source, transport, item, path, ct);
                    hash = await FolderSourcePaths.HashFileAsync(path, ct);
                }
                items.Add(new(logical, relative, item.ByteCount, item.LastModified, Revision(item), hash));
                // The locked preflight reserves the complete staged byte count. Publication checks
                // the final cache total; per-file full directory scans would make a warm refresh quadratic.
                done += item.ByteCount; FolderCacheStore.RequireDriveSpace(_cache.Root, 0, driveReserve);
                progress?.Report(new("Preparing Blob files", done, bytes, items.Count));
            }
            string revision = FolderSourcePaths.Hash(string.Join("\n", items.Select(i => i.LogicalPath + "\0" + i.Revision)));
            var manifest = new FolderSourceManifest(FolderSourcePaths.ConfigurationKey(source), revision, DateTimeOffset.UtcNow, items);
            previous?.Dispose(); previous = null;
            return _cache.Publish(source, name, stage, manifest);
        }
        catch { if (stage != null) _cache.DeleteOwnedDirectory(stage); throw; }
        finally { previous?.Dispose(); }
    }
    private static async Task<bool> CachedItemMatchesAsync(PreparedFolderSource previous, FolderSourceItem cached, BlobSourceObject remote, CancellationToken ct)
    {
        if (cached.Revision != Revision(remote) || cached.ByteCount != remote.ByteCount) return false;
        try
        {
            string path = previous.ResolveItem(cached.LogicalPath);
            return File.Exists(path) && new FileInfo(path).Length == cached.ByteCount && await FolderSourcePaths.HashFileAsync(path, ct) == cached.Sha256;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    internal async Task<List<BlobSourceObject>> ListBoundedAsync(ResourceSourceDefinition source, IBlobFolderTransport transport,
        IProgress<FolderSourceProgress>? progress, CancellationToken ct)
    {
        string prefix = FolderSourcePaths.NormalizePrefix(source.Prefix);
        var result = new List<BlobSourceObject>(); long total = 0;
        long perSourceLimit = _cache.ReadSettings().PerSourceBytes;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in transport.ListAsync(prefix, ct))
        {
            if (!item.Name.StartsWith(prefix, StringComparison.Ordinal) || item.Name.Length == prefix.Length)
                throw new FolderSourceException("The listing contains an item outside the selected prefix or an empty relative name.");
            if (item.Name[prefix.Length..].Split('/').Any(segment => segment.Length == 0))
                throw new FolderSourceException("This Blob selection contains an object with an empty path segment or trailing slash, which cannot be distinguished safely from an explorer folder. Choose a prefix without that object or rename it in Blob Storage.");
            if (item.ByteCount < 0 || !names.Add(item.Name)) throw new FolderSourceException("The Blob listing is inconsistent. Refresh and retry.");
            if (result.Count >= 100_000) throw new FolderCapacityException("The selected Blob prefix exceeds 100,000 items. Choose a smaller prefix.");
            total = checked(total + item.ByteCount);
            if (total > perSourceLimit) throw new FolderCapacityException("The selected Blob prefix exceeds the per-source limit. Choose a smaller prefix or adjust Local Cache settings.");
            result.Add(item);
            if (result.Count % 100 == 0) progress?.Report(new("Estimating Blob storage", TotalBytes: total, Files: result.Count));
        }
        return result.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }
    private async Task DownloadAsync(ResourceSourceDefinition source, IBlobFolderTransport transport, BlobSourceObject item, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
        await using var bounded = new CapacityStream(file, item.ByteCount, () => FolderCacheStore.RequireDriveSpace(_cache.Root, 1024 * 1024, _cache.ReadSettings().FreeSpaceReserveBytes));
        await transport.DownloadAsync(item, bounded, ct);
        if (file.Length != item.ByteCount) throw new FolderConflictException("A Blob changed size during download. Refresh and review again.");
        await file.FlushAsync(ct);
    }
    private IBlobFolderTransport Transport(ResourceSourceDefinition source) => _transport?.Invoke(source) ?? new AzureBlobTransport(Client(source));
    private BlobContainerClient Client(ResourceSourceDefinition source)
    {
        Validate(source);
        var endpoint = new UriBuilder(source.Endpoint) { Path = "/" + source.Container, Query = "", Fragment = "" }.Uri;
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(45);
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Transport = new HttpClientTransport(Http);
        return source.AuthenticationKind.ToUpperInvariant() switch
        {
            "SAS" => new(endpoint, new AzureSasCredential(_credentials.Read(source) ?? throw new FolderSourceException("Reconnect this Blob source: its SAS credential is not available on this desktop.")), options),
            "ENTRA" => new(endpoint, Interactive(source), options),
            "ANONYMOUS" => new(endpoint, options),
            _ => throw new ArgumentException("Choose Entra, SAS, or Anonymous authentication.")
        };
    }
    private InteractiveBrowserCredential Interactive(ResourceSourceDefinition source) => _interactive.GetOrAdd(FolderSourcePaths.ConfigurationKey(source), _ =>
    {
        AuthenticationRecord? record = null;
        string? stored = _credentials.Read(source);
        if (stored != null) { using var stream = new MemoryStream(Convert.FromBase64String(stored)); record = AuthenticationRecord.Deserialize(stream); }
        return new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            DisableAutomaticAuthentication = true, AuthenticationRecord = record,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "Surf2.RemoteSources", UnsafeAllowUnencryptedStorage = false }
        });
    });
    public static void Validate(ResourceSourceDefinition source)
    {
        if (source.Provider != SourceProvider.AzureBlob) throw new ArgumentException("This is not an Azure Blob source.");
        if (!Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.Trim('/').Length != 0)
            throw new ArgumentException("Enter an HTTPS Blob service endpoint without a container path, credentials or query string.");
        if (source.Container.Length is < 3 or > 63 || source.Container[0] == '-' || source.Container[^1] == '-' || source.Container.Contains("--") ||
            source.Container.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("Enter a valid lowercase Azure container name (3–63 letters, digits or single hyphens).");
        _ = FolderSourcePaths.NormalizePrefix(source.Prefix);
    }
    internal static string Revision(BlobSourceObject item) => item.ETag + (item.VersionId == null ? "" : "|" + item.VersionId);

    private sealed class AzureBlobTransport(BlobContainerClient container) : IBlobFolderTransport
    {
        public async IAsyncEnumerable<BlobSourceObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var page in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).AsPages(pageSizeHint: 500))
                foreach (var item in page.Values)
                    yield return new(item.Name, item.Properties.ContentLength ?? 0, item.Properties.LastModified, item.Properties.ETag?.ToString() ?? throw new IOException("A Blob listing omitted its ETag."), item.VersionId);
        }
        public async Task TestReadAsync(BlobSourceObject expected, CancellationToken ct) =>
            await container.GetBlobClient(expected.Name).GetPropertiesAsync(new BlobRequestConditions { IfMatch = new ETag(expected.ETag) }, ct);
        public async Task DownloadAsync(BlobSourceObject expected, Stream destination, CancellationToken ct)
        {
            var blob = container.GetBlobClient(expected.Name);
            if (expected.VersionId != null) blob = blob.WithVersion(expected.VersionId);
            await blob.DownloadToAsync(destination, new BlobDownloadToOptions { Conditions = new BlobRequestConditions { IfMatch = new ETag(expected.ETag) },
                TransferOptions = new Azure.Storage.StorageTransferOptions { MaximumConcurrency = 2, MaximumTransferSize = 1024 * 1024 } }, ct);
        }
        public async Task<BlobSourceObject> UploadAsync(string name, Stream content, string? expectedETag, CancellationToken ct)
        {
            long length = content.Length;
            var result = await container.GetBlobClient(name).UploadAsync(content, new BlobUploadOptions
            {
                Conditions = expectedETag == null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(expectedETag) },
                TransferOptions = new Azure.Storage.StorageTransferOptions { MaximumConcurrency = 2, MaximumTransferSize = 1024 * 1024 }
            }, ct);
            return new(name, length, result.Value.LastModified, result.Value.ETag.ToString(), result.Value.VersionId);
        }
        public async Task DeleteAsync(BlobSourceObject expected, CancellationToken ct) =>
            await container.GetBlobClient(expected.Name).DeleteAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = new ETag(expected.ETag) }, ct);
    }

    private sealed class CapacityStream(Stream inner, long maximum, Action check) : Stream
    {
        private long _checkedAt;
        public override bool CanRead => false; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { if (value > maximum) throw new FolderCapacityException("A download exceeded its advertised size."); inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); await inner.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        private void Check(int count)
        {
            if (inner.Position > maximum - count) throw new FolderCapacityException("A download exceeded its advertised size.");
            if (inner.Position - _checkedAt >= 1024 * 1024 || _checkedAt == 0) { check(); _checkedAt = inner.Position; }
        }
        protected override void Dispose(bool disposing) { /* The caller owns the file stream. */ }
    }
}

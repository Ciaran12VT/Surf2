using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Surf2.Services.RelationalGrid;

public sealed class CsvGridSource : GridSourceBase
{
    private readonly string _sourcePath;
    private CsvGridSource(GridDescriptor descriptor, GridLimits limits, GridOwnedWorkspace workspace,
        GridDiskStore store, long skip, CsvFileFingerprint fingerprint)
        : base(descriptor, limits, workspace, store, skip, fingerprint.ValidateAsync) { _sourcePath = fingerprint.SourcePath; }

    internal override void ValidateDestination(string path)
    {
        if (string.Equals(CanonicalFile(path), CanonicalFile(_sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Session grid edits cannot be exported over the original CSV source.");
    }

    private static string CanonicalFile(string path)
    {
        string full = Path.GetFullPath(path);
        var target = File.Exists(full) ? new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true) : null;
        full = target?.FullName ?? full;
        return Path.Combine(CanonicalDirectory(Path.GetDirectoryName(full)!, 0), Path.GetFileName(full));
    }

    private static string CanonicalDirectory(string path, int depth)
    {
        if (depth > 128) throw new IOException("Export directory links are too deep to validate safely.");
        var directory = new DirectoryInfo(path);
        if (directory.Parent == null) return directory.FullName;
        var target = directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0 ? directory.ResolveLinkTarget(returnFinalTarget: true) : null;
        if (target != null) return CanonicalDirectory(target.FullName, depth + 1);
        return Path.Combine(CanonicalDirectory(directory.Parent.FullName, depth + 1), directory.Name);
    }

    // Width/header inference matches CsvGridParser and requires a complete
    // bounded-memory index pass. No source string or row list is retained.
    public static Task<CsvGridSource> OpenAsync(string path, CsvGridOpenOptions? options = null,
        GridLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string absolute = Path.GetFullPath(path);
        options ??= new();
        limits ??= new();
        limits.Validate();
        return Task.Run(() => OpenCoreAsync(absolute, options, limits, cancellationToken), cancellationToken);
    }

    private static async Task<CsvGridSource> OpenCoreAsync(string path, CsvGridOpenOptions options, GridLimits limits, CancellationToken ct)
    {
        var workspace = new GridOwnedWorkspace(limits);
        GridDiskStore? store = null;
        try
        {
            store = new(workspace, limits);
            var before = CsvFileFingerprint.Stat(path);
            string[]? first = null;
            long count = 0;
            int width = 1;
            byte[] hash;
            string encoding;
            await using (var file = CsvFileFingerprint.Open(path))
            {
                using var hashing = new CsvHashingReadStream(file);
                using (var reader = new StreamReader(hashing, options.DefaultEncoding ?? new UTF8Encoding(false, false),
                    options.DetectByteOrderMarks, 16 * 1024, leaveOpen: true))
                {
                    await foreach (var cells in new CsvRecordReader(reader, limits).RecordsAsync(ct).ConfigureAwait(false))
                    {
                        first ??= cells;
                        width = Math.Max(width, cells.Length);
                        await store.AppendAsync(count++, cells, ct).ConfigureAwait(false);
                    }
                    encoding = reader.CurrentEncoding.WebName;
                }
                hash = hashing.Finish();
                // Recheck bytes while this read-only handle still denies writers
                // on platforms implementing FileShare. Do not trust timestamps alone.
                file.Position = 0;
                byte[] verification = await CsvFileFingerprint.HashAsync(file, ct).ConfigureAwait(false);
                if (!hash.AsSpan().SequenceEqual(verification) || before != CsvFileFingerprint.Stat(path))
                    throw new GridSourceChangedException("CSV changed while its grid index was being built; reopen it.");
            }
            await store.FinishAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var headers = CsvHeaderPolicy.Infer(first, width);
            long skip = headers.HasHeader ? 1 : 0;
            var columns = headers.Headers.Select((h, i) => new GridColumn(i, h, h)).ToArray();
            var fingerprint = new CsvFileFingerprint(path, before, hash);
            string revision = $"csv-v1:{encoding}:{options.DetectByteOrderMarks}:{Convert.ToHexString(hash)}";
            var descriptor = new GridDescriptor(Guid.NewGuid(), revision, Array.AsReadOnly(columns), count - skip, count - skip, 1);
            return new(descriptor, limits, workspace, store, skip, fingerprint);
        }
        catch (Exception error)
        {
            if (store != null)
                try { await store.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { error.Data["GridStoreCleanupFailed"] = cleanup; }
            try { workspace.Dispose(); } catch (Exception cleanup) { error.Data["GridWorkspaceCleanupFailed"] = cleanup; }
            throw;
        }
    }
}

internal sealed class CsvFileFingerprint(string path, CsvFileStat stat, byte[] hash)
{
    internal string SourcePath => path;
    internal static CsvFileStat Stat(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new GridSourceChangedException("CSV is missing; reopen the source instead of using stale grid rows.");
        return new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal async Task ValidateAsync(bool strong, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Stat(path) != stat) throw new GridSourceChangedException("CSV changed after opening; close and reopen its grid index.");
        if (!strong) return;
        await using var file = Open(path);
        byte[] current = await HashAsync(file, ct).ConfigureAwait(false);
        if (Stat(path) != stat || !hash.AsSpan().SequenceEqual(current))
            throw new GridSourceChangedException("CSV bytes changed after opening; the operation was stopped instead of mixing source versions.");
    }

    internal static async Task<byte[]> HashAsync(Stream file, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0) sha.AppendData(buffer, 0, read);
        return sha.GetHashAndReset();
    }
}

internal readonly record struct CsvFileStat(long Length, long ModifiedTicks, long CreatedTicks);

internal sealed class CsvHashingReadStream(Stream source) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal byte[] Finish() => _hash.GetHashAndReset();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        _hash.AppendData(buffer, offset, read);
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        int read = source.Read(buffer);
        _hash.AppendData(buffer[..read]);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
        _hash.AppendData(buffer.Span[..read]);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

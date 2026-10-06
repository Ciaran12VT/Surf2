using System.IO;
using System.Security.Cryptography;

namespace Surf2.Storage.Relational.Packages;

internal sealed class PackageBudget(RelationalPackageLimits limits)
{
    internal long Bytes { get; private set; }
    private int _columns;
    private readonly HashSet<string> _entries = new(StringComparer.OrdinalIgnoreCase);
    internal void AddEntry(string path)
    {
        if (_entries.Count >= limits.MaxEntries) throw new RelationalPackageLimitException("Package entry limit exceeded.");
        if (!_entries.Add(path)) throw new InvalidDataException("Duplicate or case-colliding package entry.");
    }
    internal void Reserve(int count)
    {
        if (count < 0 || count > limits.MaxPackageBytes - Bytes)
            throw new RelationalPackageLimitException("Package uncompressed-byte limit exceeded.");
        Bytes += count;
    }
    internal void AddColumns(int count)
    {
        _columns = checked(_columns + count);
        if (_columns > limits.MaxTotalColumns) throw new RelationalPackageLimitException("Package catalogue column budget exceeded.");
    }
}

internal sealed class PackageHashingStream(Stream destination, PackageBudget budget, long limit) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;
    internal long Bytes { get; private set; }
    internal string Finish()
    {
        if (_finished) throw new InvalidOperationException("Entry checksum was already finalized.");
        _finished = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }
    private void Reserve(int count)
    {
        if (_finished) throw new InvalidOperationException("Entry is complete.");
        if (count > limit - Bytes) throw new RelationalPackageLimitException("Package entry byte limit exceeded.");
        budget.Reserve(count);
        Bytes += count;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(buffer.Length);
        destination.Write(buffer);
        _hash.AppendData(buffer);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Reserve(buffer.Length);
        await destination.WriteAsync(buffer, ct);
        _hash.AppendData(buffer.Span);
    }
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken ct) => destination.FlushAsync(ct);
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Bytes;
    public override long Position { get => Bytes; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

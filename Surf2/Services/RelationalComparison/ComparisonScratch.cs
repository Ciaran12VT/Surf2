using System.IO;
using System.Text;

namespace Surf2.Services.RelationalComparison;

internal sealed class ComparisonScratch : IDisposable
{
    private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly OwnedScratchLease _lease;
    private long _bytes;
    private bool _closing, _cleanupCompleted;
    public ComparisonLimits Limits { get; }
    public string DirectoryPath { get; }
    public ComparisonScratch(ComparisonLimits limits)
    {
        limits.Validate(); Limits = limits;
        string root = Path.GetFullPath(limits.StagingDirectory ?? Path.Combine(Path.GetTempPath(), "Surf2", "Comparisons"));
        _lease = new OwnedScratchLease(root);
        DirectoryPath = _lease.DirectoryPath;
    }
    public string NewFile()
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".spool");
        _lease.RegisterFile(path); // Persist ownership before a crash can leave the payload behind.
        _files.Add(path, 0); return path;
    }
    public void Reserve(string path, long count)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (count < 0 || count > Limits.MaximumDiskBytes - _bytes)
            throw new ComparisonLimitException("Comparison exceeds its temporary-disk budget. No partial result was published.");
        _files[path] = checked(_files[path] + count); _bytes += count;
    }
    public void Delete(string path)
    {
        if (!_files.TryGetValue(path, out long bytes)) return;
        _lease.ValidateOwnedPath(path);
        File.Delete(path); _files.Remove(path); _bytes -= bytes;
    }
    public void Dispose()
    {
        if (_cleanupCompleted) return;
        _closing = true;
        // Only exact files created by this operation, never an enumerated or computed recursive delete.
        int failures = 0; Exception? firstFailure = null;
        try
        {
            foreach (string path in _files.Keys.ToArray())
            {
                try { Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { failures++; firstFailure ??= ex; }
            }
        }
        finally
        {
            // A failed payload deletion leaves its ownership manifest recoverable,
            // but must release the live handle instead of blocking expiry forever.
            try { _lease.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failures++; firstFailure ??= ex; }
        }
        if (failures != 0) throw new IOException($"Could not remove {failures} owned comparison scratch entries.", firstFailure);
        _cleanupCompleted = true;
    }
}

internal sealed record SpoolCodec<T>(Action<BinaryWriter, T> Write, Func<BinaryReader, T> Read);
internal static class SpoolBinary
{
    public static void WriteString(BinaryWriter writer, string text)
    {
        writer.Write(text.Length);
        foreach (char c in text) writer.Write((ushort)c);
    }
    public static string ReadString(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 2)
            throw new InvalidDataException("Invalid comparison string frame.");
        char[] chars = new char[count];
        for (int i = 0; i < count; i++) chars[i] = (char)reader.ReadUInt16();
        return new(chars);
    }
    public static byte[] Encode<T>(T value, SpoolCodec<T> codec, int maximumBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(new LimitedWriteStream(stream, maximumBytes), Encoding.UTF8, true);
        codec.Write(writer, value); writer.Flush(); return stream.ToArray();
    }
    public static void Append<T>(FileStream stream, string path, T value, SpoolCodec<T> codec, ComparisonScratch scratch)
    {
        byte[] bytes = Encode(value, codec, scratch.Limits.MaximumRecordBytes);
        scratch.Reserve(path, checked(4L + bytes.Length));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    public static bool TryRead<T>(Stream stream, SpoolCodec<T> codec, int maximumBytes, out T value)
    {
        if (stream.Position == stream.Length) { value = default!; return false; }
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        int size = reader.ReadInt32();
        if (size < 0 || size > maximumBytes || size > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid comparison record frame.");
        byte[] bytes = reader.ReadBytes(size);
        using var frame = new MemoryStream(bytes, false);
        using var fields = new BinaryReader(frame);
        value = codec.Read(fields);
        if (frame.Position != frame.Length) throw new InvalidDataException("Trailing comparison record bytes.");
        return true;
    }

    private sealed class LimitedWriteStream(Stream inner, int limit) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - inner.Length) throw new ComparisonLimitException("Comparison record exceeds its byte budget.");
            inner.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (inner.Length == limit) throw new ComparisonLimitException("Comparison record exceeds its byte budget.");
            inner.WriteByte(value);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

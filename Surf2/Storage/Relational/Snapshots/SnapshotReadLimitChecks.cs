using System.IO;

namespace Surf2.Storage.Relational.Snapshots;

internal static class SnapshotReadLimitChecks
{
    internal static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var passed = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Snapshot read-limit check failed: " + label);
            passed.Add(label);
        }
        new SnapshotReadLimits().Validate();
        new SnapshotReadLimits { SelectedDefinitionBytes = SnapshotReadLimits.MaximumDefinitionBytes }.Validate();
        Expect<ArgumentOutOfRangeException>(() => new SnapshotReadLimits { SelectedDefinitionBytes = 0 }.Validate());
        Expect<ArgumentOutOfRangeException>(() => new SnapshotReadLimits { SelectedDefinitionBytes = SnapshotReadLimits.MaximumDefinitionBytes + 1 }.Validate());
        Check(SnapshotReadLimits.DefaultDefinitionBytes == 16L * 1024 * 1024 && SnapshotReadLimits.MaximumDefinitionBytes == 128L * 1024 * 1024,
            "Selected definition policy is distinct from the absolute 128 MiB content ceiling");

        long metadataLimit = RelationalSnapshotStore.MaximumMetadataPageBytes;
        SnapshotReadGuard.RequireBytes(metadataLimit, metadataLimit);
        Expect<SnapshotReadLimitException>(() => SnapshotReadGuard.RequireBytes(metadataLimit + 1, metadataLimit));
        Expect<InvalidDataException>(() => SnapshotReadGuard.RequireBytes(-1, metadataLimit));
        Check(metadataLimit == 8L * 1024 * 1024, "Metadata boundary accepts exact bytes and rejects oversize or corrupt lengths");
        var aggregate = new SnapshotMetadataBudget();
        aggregate.Add(256);
        aggregate.Add(metadataLimit - 256);
        Expect<SnapshotReadLimitException>(() => aggregate.Add(1));
        Check(aggregate.Bytes == metadataLimit, "Selected table header and children share one atomic cumulative byte budget");
        var manyChildren = new SnapshotMetadataBudget();
        for (int i = 0; i < metadataLimit / 256; i++) manyChildren.Add(256);
        Expect<SnapshotReadLimitException>(() => manyChildren.Add(256));
        Check(manyChildren.Bytes == metadataLimit, "Fixed per-child charge bounds even empty-name child collections");

        using var untouched = new CountingStream("not read");
        await ExpectAsync<SnapshotReadLimitException>(() => SnapshotReadGuard.ReadUtf16Async(untouched,
            SnapshotReadLimits.MaximumDefinitionBytes + 2, SnapshotReadLimits.MaximumDefinitionBytes, ct)).ConfigureAwait(false);
        await ExpectAsync<SnapshotReadLimitException>(() => SnapshotReadGuard.ReadUtf16Async(untouched,
            SnapshotReadLimits.DefaultDefinitionBytes + 2, SnapshotReadLimits.DefaultDefinitionBytes, ct)).ConfigureAwait(false);
        Check(untouched.ReadCalls == 0, "SQL-declared oversized definitions are rejected before binary consumption/allocation");
        await ExpectAsync<InvalidDataException>(() => SnapshotReadGuard.ReadUtf16Async(untouched, 3, 10, ct)).ConfigureAwait(false);
        Check(untouched.ReadCalls == 0, "Invalid odd SQL Unicode byte lengths fail before reading bytes");
        const string exact = " A\r\n\ud83d\udca7\ud800 \udc00\0\uffff ";
        using var fragmented = new CountingStream(exact);
        string value = await SnapshotReadGuard.ReadUtf16Async(fragmented, exact.Length * 2L, exact.Length * 2L, ct).ConfigureAwait(false);
        Check(value == exact && fragmented.ReadCalls == exact.Length * 2 + 1,
            "One-byte fragmented raw reads preserve paired/unpaired code units, NUL, noncharacters and trailing spaces");
        using var literalBytes = new CountingStream(new byte[] { 0, 0xd8, 0x20, 0, 0, 0xdc, 0, 0, 0xff, 0xff });
        Check(await SnapshotReadGuard.ReadUtf16Async(literalBytes, 10, 10, ct).ConfigureAwait(false) == "\ud800 \udc00\0\uffff",
            "Literal SQL UTF-16LE bytes decode without encoding fallback or byte-order changes");
        string everyUnit = new(Enumerable.Range(0, 65536).Select(i => (char)i).ToArray());
        using var allUnits = new CountingStream(everyUnit, 7);
        Check(await SnapshotReadGuard.ReadUtf16Async(allUnits, everyUnit.Length * 2L, everyUnit.Length * 2L, ct).ConfigureAwait(false) == everyUnit,
            "Every possible UTF-16 code unit survives the raw binary reader unchanged");
        string boundary = new string('x', 8191) + "\ud800\udc00\ud800 \udc00\0\uffff ";
        foreach (int chunk in new[] { 1, 7, 16 * 1024 })
        {
            using var chunked = new CountingStream(boundary, chunk);
            string actual = await SnapshotReadGuard.ReadUtf16Async(chunked, boundary.Length * 2L, boundary.Length * 2L, ct).ConfigureAwait(false);
            Check(actual == boundary, "Raw definition code units survive buffer boundaries and byte chunk size " + chunk);
        }
        using var shortText = new CountingStream("a");
        await ExpectAsync<InvalidDataException>(() => SnapshotReadGuard.ReadUtf16Async(shortText, 4, 10, ct)).ConfigureAwait(false);
        using var extraText = new CountingStream("ab");
        await ExpectAsync<InvalidDataException>(() => SnapshotReadGuard.ReadUtf16Async(extraText, 2, 10, ct)).ConfigureAwait(false);
        Check(shortText.ReadCalls == 3 && extraText.ReadCalls == 3, "Short/long streamed definitions fail rather than return truncated content");
        using var halfUnit = new CountingStream(new byte[] { 0x61, 0, 0xff });
        await ExpectAsync<InvalidDataException>(() => SnapshotReadGuard.ReadUtf16Async(halfUnit, 4, 10, ct)).ConfigureAwait(false);
        using var extraByte = new CountingStream(new byte[] { 0x61, 0, 0xff });
        await ExpectAsync<InvalidDataException>(() => SnapshotReadGuard.ReadUtf16Async(extraByte, 2, 10, ct)).ConfigureAwait(false);
        Check(halfUnit.ReadCalls == 4 && extraByte.ReadCalls == 3, "Partial or extra raw code-unit bytes fail exact declared-length validation");
        using var empty = new CountingStream("");
        Check(await SnapshotReadGuard.ReadUtf16Async(empty, 0, 10, ct).ConfigureAwait(false) == "", "Empty definitions remain valid and complete");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var cancellationReader = new CountingStream(exact);
        await ExpectAsync<OperationCanceledException>(() => SnapshotReadGuard.ReadUtf16Async(cancellationReader, exact.Length * 2L, 100, cancelled.Token)).ConfigureAwait(false);
        Check(cancellationReader.ReadCalls == 0, "Cancelled selected reads allocate/read no definition buffer");
        return passed.AsReadOnly();
    }

    private sealed class CountingStream : MemoryStream
    {
        private readonly int _chunkSize;
        internal CountingStream(string text, int chunkSize = 1) : this(RawBytes(text), chunkSize) { }
        internal CountingStream(byte[] bytes, int chunkSize = 1) : base(bytes, writable: false) => _chunkSize = chunkSize;
        internal int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return base.ReadAsync(buffer[..Math.Min(_chunkSize, buffer.Length)], cancellationToken);
        }
    }

    private static byte[] RawBytes(string text)
    {
        byte[] bytes = new byte[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            bytes[i * 2] = (byte)text[i];
            bytes[i * 2 + 1] = (byte)(text[i] >> 8);
        }
        return bytes;
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected read-limit rejection: " + typeof(T).Name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); } catch (T) { return; }
        throw new InvalidOperationException("Expected async read-limit rejection: " + typeof(T).Name);
    }
}

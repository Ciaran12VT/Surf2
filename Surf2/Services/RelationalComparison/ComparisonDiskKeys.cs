using System.Buffers.Binary;
using System.IO;

namespace Surf2.Services.RelationalComparison;

// Disk-backed open addressing preserves the legacy GLOBAL duplicate suffix
// counter and collisions with literal names such as "key #2", without a RAM set.
internal sealed class ComparisonDiskKeys : IDisposable
{
    private readonly ComparisonScratch _scratch;
    private readonly FileStream _slots;
    private readonly FileStream _keys;
    private readonly string _slotPath;
    private readonly string _keyPath;
    private readonly long _capacity;
    private long _suffix = 2;
    public ComparisonDiskKeys(ComparisonScratch scratch, long maximumRows)
    {
        _scratch = scratch;
        if (maximumRows < 0 || maximumRows > long.MaxValue / 16 - 1)
            throw new ComparisonLimitException("Table row count exceeds the comparison key-index budget.");
        _capacity = Math.Max(16, checked(maximumRows * 2 + 1));
        _slotPath = scratch.NewFile(); _keyPath = scratch.NewFile();
        scratch.Reserve(_slotPath, checked(_capacity * 8));
        _slots = File.Open(_slotPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _slots.SetLength(_capacity * 8);
            _keys = File.Open(_keyPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch { _slots.Dispose(); throw; }
    }
    public string Add(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "(blank key)";
        string unique = key;
        while (!TryAdd(unique, ct)) unique = key + " #" + _suffix++;
        return unique;
    }
    private bool TryAdd(string key, CancellationToken ct)
    {
        long slot = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(key) % _capacity;
        Span<byte> value = stackalloc byte[8];
        for (long probe = 0; probe < _capacity; probe++)
        {
            ct.ThrowIfCancellationRequested();
            _slots.Position = slot * 8; _slots.ReadExactly(value);
            long address = BinaryPrimitives.ReadInt64LittleEndian(value);
            if (address == 0)
            {
                _keys.Position = _keys.Length;
                long position = _keys.Position;
                SpoolBinary.Append(_keys, _keyPath, key, KeyCodec, _scratch);
                BinaryPrimitives.WriteInt64LittleEndian(value, checked(position + 1));
                _slots.Position = slot * 8; _slots.Write(value); return true;
            }
            _keys.Position = address - 1;
            if (!SpoolBinary.TryRead(_keys, KeyCodec, _scratch.Limits.MaximumRecordBytes, out string existing))
                throw new InvalidDataException("Comparison key-index entry is missing.");
            if (StringComparer.OrdinalIgnoreCase.Equals(existing, key)) return false;
            slot = (slot + 1) % _capacity;
        }
        throw new ComparisonLimitException("Comparison key-index capacity was exceeded.");
    }
    private static readonly SpoolCodec<string> KeyCodec = new(SpoolBinary.WriteString, SpoolBinary.ReadString);
    public void Dispose()
    {
        _slots.Dispose(); _keys.Dispose(); _scratch.Delete(_slotPath); _scratch.Delete(_keyPath);
    }
}

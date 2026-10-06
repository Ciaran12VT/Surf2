using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Surf2.Services.RelationalComparison;

internal sealed class ComparisonTextHash(ComparisonOptions options) : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer = new byte[16384];
    private int _used;
    private char? _high;
    public void Append(string text)
    {
        Span<char> chars = stackalloc char[2];
        foreach (char c in text)
        {
            if (options.IgnoreWhitespace && char.IsWhiteSpace(c)) continue;
            if (_high.HasValue)
            {
                char high = _high.Value; _high = null;
                if (char.IsLowSurrogate(c) && options.IgnoreCase)
                {
                    var rune = Rune.ToUpperInvariant(new Rune(high, c));
                    int count = rune.EncodeToUtf16(chars);
                    for (int i = 0; i < count; i++) Emit(chars[i]);
                    continue;
                }
                Emit(high);
            }
            if (options.IgnoreCase && char.IsHighSurrogate(c)) { _high = c; continue; }
            Emit(options.IgnoreCase ? char.ToUpperInvariant(c) : c);
        }
    }
    private void Emit(char c)
    {
        if (_used == _buffer.Length) Flush();
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_used, 2), c); _used += 2;
    }
    private void Flush() { _hash.AppendData(_buffer.AsSpan(0, _used)); _used = 0; }
    public string Finish()
    {
        if (_high.HasValue) { Emit(_high.Value); _high = null; }
        Flush(); return Convert.ToHexString(_hash.GetHashAndReset());
    }
    public static string Digest(string text, ComparisonOptions options)
    { using var hash = new ComparisonTextHash(options); hash.Append(text); return hash.Finish(); }
    public void Dispose() => _hash.Dispose();
}

using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Surf2.Storage.Relational.Packages;

// Reads bounded JSON tokens, not JSONL lines or library-sized JsonDocuments.
// Chunked cells remain binary until SQL converts them in the installed column's context.
internal sealed class PackageRowDecoding(Stream input, RelationalPackageLimits limits, long? rowByteBudget = null,
    Func<long, CancellationToken, Task>? reserveRowAsync = null)
{
    private readonly TokenReader _tokens = new(input);
    private long _rowBytes;
    private readonly long _maximumRowBytes = Math.Min(limits.MaxRowBytes, rowByteBudget ?? limits.MaxRowBytes);
    internal long RowBytes => _rowBytes;

    internal async Task<object[]?> ReadAsync(IReadOnlyList<RelationalPackageColumn> columns, CancellationToken ct)
    {
        if (!await _tokens.BeginRowAsync(ct)) return null;
        _rowBytes = 0;
        if (_maximumRowBytes < 0) throw new RelationalPackageLimitException("The configured batch has no room for one row.");
        if (reserveRowAsync != null) await reserveRowAsync(0, ct);
        var values = new object[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            await _tokens.NextAsync(ct);
            var column = columns[i];
            if (_tokens.Kind == JsonTokenType.Null)
            {
                if (!column.Nullable) throw Bad("NULL in a non-nullable column");
                values[i] = DBNull.Value;
            }
            else if (PackageRowEncoding.IsChunked(column.Encoding!))
                values[i] = await BlobAsync(column, ct);
            else
            {
                await ReserveAsync(256, ct);
                values[i] = await ScalarAsync(column, ct);
            }
        }
        await _tokens.NextAsync(ct);
        _tokens.Require(JsonTokenType.EndArray);
        await _tokens.EndRowAsync(ct);
        return values;
    }

    private async Task<byte[]> BlobAsync(RelationalPackageColumn column, CancellationToken ct)
    {
        _tokens.Require(JsonTokenType.StartObject);
        await _tokens.PropertyAsync("byteLength", ct);
        long length = Integer(_tokens.String(), 0, limits.MaxCellBytes);
        if ((column.Encoding == "utf16le-chunks-v1" && (length & 1) != 0) ||
            (column.MaxLengthBytes >= 0 && length > column.MaxLengthBytes) ||
            (column.SqlType is "binary" or "char" or "nchar" && length != column.MaxLengthBytes))
            throw Bad("Invalid cell byte length for the installed SQL column");
        await ReserveAsync(length, ct);
        var bytes = new byte[checked((int)length)];
        await _tokens.PropertyAsync("chunks", ct);
        _tokens.Require(JsonTokenType.StartArray);
        int offset = 0;
        while (true)
        {
            await _tokens.NextAsync(ct);
            if (_tokens.Kind == JsonTokenType.EndArray) break;
            string chunk = _tokens.String();
            if (chunk.Length == 0 || chunk.Length > 87384 || chunk.Length % 4 != 0)
                throw Bad("Invalid bounded base64 chunk");
            int capacity = chunk.Length / 4 * 3;
            byte[] decoded = new byte[capacity];
            if (!Convert.TryFromBase64String(chunk, decoded, out int count) || count == 0 || count > 65536 ||
                count > bytes.Length - offset || Convert.ToBase64String(decoded, 0, count) != chunk)
                throw Bad("Chunk bytes do not match the declared cell length or canonical base64");
            decoded.AsSpan(0, count).CopyTo(bytes.AsSpan(offset));
            offset += count;
        }
        if (offset != bytes.Length) throw Bad("Chunked cell ended before its declared byte length");
        await _tokens.NextAsync(ct);
        _tokens.Require(JsonTokenType.EndObject);
        return bytes;
    }

    private async Task<object> ScalarAsync(RelationalPackageColumn column, CancellationToken ct)
    {
        var culture = CultureInfo.InvariantCulture;
        if (column.Encoding == "boolean-v1")
            return _tokens.Kind switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => throw Bad("Expected boolean") };
        if (column.Encoding == "sql-decimal-words-v1")
        {
            _tokens.Require(JsonTokenType.StartObject);
            await _tokens.PropertyAsync("precision", ct);
            byte precision = checked((byte)_tokens.Number(1, 38));
            await _tokens.PropertyAsync("scale", ct);
            byte scale = checked((byte)_tokens.Number(0, precision));
            await _tokens.PropertyAsync("positive", ct);
            bool positive = _tokens.Kind switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => throw Bad("Expected decimal sign") };
            await _tokens.PropertyAsync("words", ct);
            _tokens.Require(JsonTokenType.StartArray);
            int[] words = new int[4];
            for (int i = 0; i < 4; i++)
            {
                await _tokens.NextAsync(ct);
                words[i] = unchecked((int)Hex(_tokens.String(), 8));
            }
            await _tokens.NextAsync(ct); _tokens.Require(JsonTokenType.EndArray);
            await _tokens.NextAsync(ct); _tokens.Require(JsonTokenType.EndObject);
            if (precision != column.Precision || scale != column.Scale) throw Bad("Decimal metadata differs from the installed column");
            try { return new SqlDecimal(precision, scale, positive, words); }
            catch (Exception ex) when (ex is ArgumentException or SqlTypeException or OverflowException) { throw Bad("Invalid SQL decimal magnitude"); }
        }
        string value = _tokens.String();
        if (value.Length > 128) throw Bad("Oversized scalar");
        switch (column.SqlType)
        {
            case "bigint": return Integer(value, long.MinValue, long.MaxValue);
            case "int": return checked((int)Integer(value, int.MinValue, int.MaxValue));
            case "smallint": return checked((short)Integer(value, short.MinValue, short.MaxValue));
            case "tinyint": return checked((byte)Integer(value, 0, byte.MaxValue));
            case "float" when column.Encoding == "float64-bits-v1":
                double number = BitConverter.Int64BitsToDouble(unchecked((long)Hex(value, 16)));
                return double.IsFinite(number) ? number : throw Bad("SQL cannot store non-finite float values");
            case "float": case "real":
                float single = BitConverter.Int32BitsToSingle(unchecked((int)Hex(value, 8)));
                return float.IsFinite(single) ? single : throw Bad("SQL cannot store non-finite real values");
            case "uniqueidentifier":
                if (!Guid.TryParseExact(value, "D", out var guid)) throw Bad("Invalid GUID");
                return guid;
            case "money": case "smallmoney":
                if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out var money) ||
                    money.ToString("F4", culture) != value) throw Bad("Invalid canonical SQL money");
                if (column.SqlType == "smallmoney" && (money < -214748.3648m || money > 214748.3647m)) throw Bad("Smallmoney overflow");
                try { return new SqlMoney(money); } catch (OverflowException) { throw Bad("Money overflow"); }
            case "date":
                if (!DateTime.TryParseExact(value, "yyyy-MM-dd", culture, DateTimeStyles.None, out var date)) throw Bad("Invalid date");
                return date;
            case "datetime": case "datetime2": case "smalldatetime":
                if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff", culture, DateTimeStyles.None, out var timestamp)) throw Bad("Invalid timestamp");
                return timestamp;
            case "datetimeoffset":
                if (!DateTimeOffset.TryParseExact(value, "O", culture, DateTimeStyles.None, out var offset)) throw Bad("Invalid offset timestamp");
                return offset;
            case "time":
                if (!TimeSpan.TryParseExact(value, "c", culture, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) throw Bad("Invalid SQL time");
                return time;
            default: throw Bad("Unsupported scalar encoding");
        }
    }

    private async Task ReserveAsync(long count, CancellationToken ct)
    {
        if (count > _maximumRowBytes - _rowBytes)
            throw new RelationalPackageLimitException("Package row payload exceeds the configured row/batch budget. Increase BatchBytes within the supported bound or use a smaller package row; an installed destination cannot be published or resumed after this failure.");
        _rowBytes += count;
        // A previous batch may need flushing BEFORE this cell's byte[] is allocated.
        if (reserveRowAsync != null) await reserveRowAsync(_rowBytes, ct);
    }

    internal static long Integer(string value, long minimum, long maximum)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result) ||
            result < minimum || result > maximum || result.ToString(CultureInfo.InvariantCulture) != value)
            throw Bad("Invalid canonical integer or numeric bound");
        return result;
    }

    internal static byte[] Hash(string value)
    {
        if (value == null || value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Bad("Expected lowercase SHA-256");
        return Convert.FromHexString(value);
    }

    private static ulong Hex(string value, int length)
    {
        if (value.Length != length || value.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F')) ||
            !ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)) throw Bad("Invalid IEEE/decimal word");
        return bits;
    }

    internal static void OrderedKey(IReadOnlyList<RelationalPackageColumn> columns, IReadOnlyList<string> primaryKey,
        object[] row, ref long[]? previous)
    {
        var key = new long[primaryKey.Count];
        for (int i = 0; i < key.Length; i++)
        {
            int ordinal = -1;
            for (int j = 0; j < columns.Count; j++) if (columns[j].Name == primaryKey[i]) ordinal = j;
            if (ordinal < 0 || row[ordinal] is not (long or int or short or byte)) throw Bad("Unsupported authoritative PK type");
            key[i] = Convert.ToInt64(row[ordinal], CultureInfo.InvariantCulture);
        }
        if (previous != null)
        {
            int comparison = 0;
            for (int i = 0; i < key.Length && comparison == 0; i++) comparison = key[i].CompareTo(previous[i]);
            if (comparison <= 0) throw Bad("Rows must have strictly increasing SQL primary keys");
        }
        previous = key;
    }

    internal static InvalidDataException Bad(string message) => new("Invalid relational package: " + message + ".");

    private sealed class TokenReader(Stream source)
    {
        private const int MaximumTokenBytes = 96 * 1024;
        private byte[] _buffer = new byte[MaximumTokenBytes];
        private int _position, _length;
        private bool _final;
        private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 8 });
        internal JsonTokenType Kind { get; private set; }
        private string? _value;

        internal async Task<bool> BeginRowAsync(CancellationToken ct)
        {
            if (!await AvailableAsync(ct)) return false;
            if (_buffer[_position] != (byte)'[') throw Bad("A row must start with '[' with no BOM or inter-row whitespace");
            await NextAsync(ct); Require(JsonTokenType.StartArray);
            return true;
        }

        internal async Task EndRowAsync(CancellationToken ct)
        {
            if (!await AvailableAsync(ct) || _buffer[_position++] != (byte)'\n') throw Bad("Each complete row must end with LF");
            _state = new(new JsonReaderOptions { MaxDepth = 8 });
        }

        internal async Task NextAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadAvailable()) return;
                if (_final) throw Bad("Truncated JSON row");
                await RefillAsync(ct);
            }
        }

        private bool ReadAvailable()
        {
            try
            {
                var reader = new Utf8JsonReader(_buffer.AsSpan(_position, _length - _position), _final, _state);
                if (!reader.Read())
                {
                    _position += checked((int)reader.BytesConsumed); _state = reader.CurrentState; return false;
                }
                Kind = reader.TokenType;
                _value = Kind switch
                {
                    JsonTokenType.String or JsonTokenType.PropertyName => reader.GetString(),
                    JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
                    _ => null
                };
                _position += checked((int)reader.BytesConsumed); _state = reader.CurrentState;
                return true;
            }
            catch (JsonException) { throw Bad("Malformed bounded JSON token"); }
        }

        private async Task<bool> AvailableAsync(CancellationToken ct)
        {
            if (_position == _length && !_final) await RefillAsync(ct);
            return _position < _length;
        }

        private async Task RefillAsync(CancellationToken ct)
        {
            int remaining = _length - _position;
            if (remaining == _buffer.Length) throw new RelationalPackageLimitException("A package JSON token exceeds 96 KiB.");
            if (remaining > 0 && _position > 0) Buffer.BlockCopy(_buffer, _position, _buffer, 0, remaining);
            _position = 0; _length = remaining;
            int read = await source.ReadAsync(_buffer.AsMemory(remaining), ct);
            _length += read; _final = read == 0;
        }

        internal async Task PropertyAsync(string name, CancellationToken ct)
        {
            await NextAsync(ct); Require(JsonTokenType.PropertyName);
            if (_value != name) throw Bad("Unexpected, reordered, or duplicate cell property");
            await NextAsync(ct);
        }

        internal string String() { Require(JsonTokenType.String); return _value!; }
        internal long Number(long min, long max) { Require(JsonTokenType.Number); return Integer(_value!, min, max); }
        internal void Require(JsonTokenType kind) { if (Kind != kind) throw Bad("Unexpected JSON token"); }
    }
}

internal sealed class PackageEntryReadStream(Stream source, long expectedBytes, byte[] expectedHash) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes;
    private bool _complete;
    internal void Finish()
    {
        if (_complete || _bytes != expectedBytes || !CryptographicOperations.FixedTimeEquals(_hash.GetHashAndReset(), expectedHash))
            throw PackageRowDecoding.Bad("Entry length or SHA-256 mismatch");
        _complete = true;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_complete) throw new InvalidOperationException("The package entry is complete.");
        long remaining = expectedBytes - _bytes;
        int take = remaining >= buffer.Length ? buffer.Length : checked((int)remaining + 1);
        int count = await source.ReadAsync(buffer[..take], ct);
        if (count > expectedBytes - _bytes) throw PackageRowDecoding.Bad("Entry exceeds its declared byte count");
        _bytes += count; _hash.AppendData(buffer.Span[..count]); return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expectedBytes;
    public override long Position { get => _bytes; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

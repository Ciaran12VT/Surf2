using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

/// <summary>Reads one bounded row at a time from a potentially large JSON array.</summary>
internal sealed class CaptureJsonArray
{
    private readonly int _maximumRowBytes;
    private byte[] _buffer;
    private int _position;
    private int _length;
    private bool _final;
    private bool _started;
    private bool _ended;
    private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 65 });

    private CaptureJsonArray(int maximumRowBytes)
    {
        if (maximumRowBytes <= 0 || maximumRowBytes > CaptureLimits.MaximumRowUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRowBytes));
        _maximumRowBytes = maximumRowBytes;
        _buffer = new byte[Math.Min(64 * 1024, maximumRowBytes + 1024)];
    }

    internal static async IAsyncEnumerable<JsonElement> ReadAsync(Stream source, int maximumRowBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = new CaptureJsonArray(maximumRowBytes);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = parser.ReadAvailable(out var value);
            if (result == ReadResult.Complete) yield break;
            if (result == ReadResult.Value) yield return value;
            if (result == ReadResult.NeedMore) await parser.RefillAsync(source, cancellationToken);
        }
    }

    private enum ReadResult { Progress, Value, NeedMore, Complete }

    private ReadResult ReadAvailable(out JsonElement value)
    {
        value = default;
        var reader = new Utf8JsonReader(_buffer.AsSpan(_position, _length - _position), _final, _state);
        if (!reader.Read())
        {
            Consume(ref reader);
            if (!_final) return ReadResult.NeedMore;
            if (!_ended) throw new InvalidDataException("Captured JSON array is incomplete.");
            return ReadResult.Complete;
        }
        if (!_started)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new InvalidDataException("Captured JSON must be an array.");
            _started = true;
            Consume(ref reader);
            return ReadResult.Progress;
        }
        if (_ended) throw new InvalidDataException("Captured JSON contains trailing values.");
        if (reader.TokenType == JsonTokenType.EndArray)
        {
            _ended = true;
            Consume(ref reader);
            return ReadResult.Progress;
        }
        long start = reader.TokenStartIndex;
        if (!JsonDocument.TryParseValue(ref reader, out var document))
            return ReadResult.NeedMore;
        using (document)
        {
            if (reader.BytesConsumed - start > _maximumRowBytes)
                throw new CaptureLimitException("A captured JSON row exceeds the row byte budget.");
            value = document.RootElement.Clone();
        }
        Consume(ref reader);
        return ReadResult.Value;
    }

    private void Consume(ref Utf8JsonReader reader)
    {
        _position += checked((int)reader.BytesConsumed);
        _state = reader.CurrentState;
    }

    private async Task RefillAsync(Stream source, CancellationToken cancellationToken)
    {
        if (_final) throw new InvalidDataException("Captured JSON array is incomplete.");
        int remaining = _length - _position;
        if (remaining > 0 && _position > 0) Buffer.BlockCopy(_buffer, _position, _buffer, 0, remaining);
        _position = 0;
        _length = remaining;
        if (remaining == _buffer.Length)
        {
            int maximum = checked(_maximumRowBytes + 64 * 1024);
            if (_buffer.Length >= maximum)
                throw new CaptureLimitException("A captured JSON row exceeds the row byte budget.");
            Array.Resize(ref _buffer, Math.Min(maximum, checked(_buffer.Length * 2)));
        }
        int read = await source.ReadAsync(_buffer.AsMemory(_length), cancellationToken);
        _length += read;
        _final = read == 0;
    }
}

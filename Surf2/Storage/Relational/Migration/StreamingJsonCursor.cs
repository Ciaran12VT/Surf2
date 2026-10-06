using System.IO;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Migration;

/// <summary>A bounded token reader for nested legacy arrays, not a library-sized JsonDocument.</summary>
public sealed class StreamingJsonCursor : IAsyncDisposable
{
    public const int MaximumValueBytes = 32 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private byte[] _buffer = new byte[64 * 1024];
    private int _position;
    private int _length;
    private long _bufferOffset;
    private bool _final;
    private bool _started;
    private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 128 });

    public StreamingJsonCursor(Stream stream, bool leaveOpen = false, long baseOffset = 0)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _bufferOffset = baseOffset;
    }

    public JsonTokenType TokenType { get; private set; }
    public long TokenOffset { get; private set; }
    public string? Value { get; private set; }
    public int Depth { get; private set; }
    private byte[] _raw = [];

    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadAvailable())
            {
                return true;
            }

            if (_final)
            {
                TokenType = JsonTokenType.None;
                Value = null;
                _raw = [];
                return false;
            }

            await RefillAsync(cancellationToken);
        }
    }

    private bool ReadAvailable()
    {
        if (!_started)
        {
            if (_length < 3 && !_final)
            {
                return false;
            }

            if (_bufferOffset == 0 && _length >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
            {
                _position = 3;
            }

            _started = true;
        }

        var reader = new Utf8JsonReader(_buffer.AsSpan(_position, _length - _position), _final, _state);
        if (!reader.Read())
        {
            _position += checked((int)reader.BytesConsumed);
            _state = reader.CurrentState;
            return false;
        }

        int start = checked((int)reader.TokenStartIndex);
        int consumed = checked((int)reader.BytesConsumed);
        TokenType = reader.TokenType;
        TokenOffset = _bufferOffset + _position + start;
        Depth = reader.CurrentDepth;
        Value = TokenType switch
        {
            JsonTokenType.String or JsonTokenType.PropertyName => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            _ => null
        };
        _raw = _buffer.AsSpan(_position + start, consumed - start).ToArray();
        _position += consumed;
        _state = reader.CurrentState;
        return true;
    }

    private async Task RefillAsync(CancellationToken cancellationToken)
    {
        int remaining = _length - _position;
        _bufferOffset += _position;
        if (remaining != 0 && _position != 0)
        {
            Buffer.BlockCopy(_buffer, _position, _buffer, 0, remaining);
        }

        _position = 0;
        _length = remaining;
        if (remaining == _buffer.Length)
        {
            if (_buffer.Length >= MaximumValueBytes)
            {
                throw new InvalidDataException("A legacy JSON token exceeds the supported conversion limit.");
            }

            Array.Resize(ref _buffer, Math.Min(MaximumValueBytes, checked(_buffer.Length * 2)));
        }

        int count = await _stream.ReadAsync(_buffer.AsMemory(_length), cancellationToken);
        _length += count;
        _final = count == 0;
    }

    public async Task ReadObjectAsync(
        Func<string, StreamingJsonCursor, CancellationToken, Task> readProperty,
        CancellationToken cancellationToken = default)
    {
        Require(JsonTokenType.StartObject);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await MoveNextAsync(cancellationToken))
        {
            if (TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            Require(JsonTokenType.PropertyName);
            string name = Value ?? throw new InvalidDataException("A property name is missing.");
            if (!names.Add(name))
            {
                throw new InvalidDataException("A legacy model contains a duplicate property name.");
            }

            if (!await MoveNextAsync(cancellationToken))
            {
                throw new InvalidDataException("A JSON property has no value.");
            }

            await readProperty(name, this, cancellationToken);
        }

        throw new InvalidDataException("A JSON object is incomplete.");
    }

    public async Task ReadArrayAsync(
        Func<long, StreamingJsonCursor, CancellationToken, Task> readItem,
        CancellationToken cancellationToken = default)
    {
        Require(JsonTokenType.StartArray);
        long ordinal = 0;
        while (await MoveNextAsync(cancellationToken))
        {
            if (TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            await readItem(ordinal++, this, cancellationToken);
        }

        throw new InvalidDataException("A JSON array is incomplete.");
    }

    public async Task SkipValueAsync(CancellationToken cancellationToken = default)
    {
        if (TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            RequireValue();
            return;
        }

        int initialDepth = Depth;
        while (await MoveNextAsync(cancellationToken))
        {
            if (Depth == initialDepth && TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
            {
                return;
            }
        }

        throw new InvalidDataException("A JSON value is incomplete.");
    }

    public async Task<JsonElement> ReadValueAsync(CancellationToken cancellationToken = default)
    {
        using var output = new BoundedJsonBuffer(MaximumValueBytes);
        using (var writer = new Utf8JsonWriter(output))
        {
            await CopyValueAsync(writer, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        using var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)));
        return document.RootElement.Clone();
    }

    public async Task<JsonElement> ReadHeaderAsync(IReadOnlySet<string> excludedCollections,
        CancellationToken cancellationToken = default)
    {
        using var output = new BoundedJsonBuffer(MaximumValueBytes);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            await ReadObjectAsync(async (name, value, ct) =>
            {
                if (excludedCollections.Contains(name))
                {
                    await value.SkipValueAsync(ct);
                }
                else
                {
                    writer.WritePropertyName(name);
                    await value.CopyValueAsync(writer, ct);
                }
            }, cancellationToken);
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }

        using var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)));
        return document.RootElement.Clone();
    }

    public async Task CopyValueAsync(Utf8JsonWriter writer, CancellationToken cancellationToken = default)
    {
        RequireValue();
        int initialDepth = Depth;
        bool container = TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray;
        while (true)
        {
            switch (TokenType)
            {
                case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                case JsonTokenType.PropertyName: writer.WritePropertyName(Value!); break;
                default: writer.WriteRawValue(_raw, skipInputValidation: false); break;
            }

            // Flush per token to keep a streamed container from accumulating in the writer.
            await writer.FlushAsync(cancellationToken);
            if (!container || (Depth == initialDepth && TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray))
            {
                return;
            }

            if (!await MoveNextAsync(cancellationToken))
            {
                throw new InvalidDataException("A JSON value is incomplete.");
            }
        }
    }

    public void Require(JsonTokenType expected)
    {
        if (TokenType != expected)
        {
            throw new InvalidDataException("Unexpected token in a legacy document.");
        }
    }

    private void RequireValue()
    {
        if (TokenType is JsonTokenType.None or JsonTokenType.PropertyName or JsonTokenType.EndArray or JsonTokenType.EndObject)
        {
            throw new InvalidDataException("A JSON value was expected.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _raw = [];
        _buffer = [];
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync();
        }
    }

    private sealed class BoundedJsonBuffer(int limit) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Verify(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Verify(count);
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        private void Verify(int count)
        {
            if (Length + count > limit)
            {
                throw new InvalidDataException("A single legacy entity exceeds the supported conversion limit.");
            }
        }
    }
}

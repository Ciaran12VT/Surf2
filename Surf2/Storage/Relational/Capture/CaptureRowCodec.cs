using System.IO;
using System.Text;
using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

internal sealed record CaptureValueException(int PropertyOrdinal, JsonValueKind Kind, string RawToken);
internal sealed record EncodedCaptureRow(JsonValueKind Kind, byte[] PropertyOrder,
    string?[] ScalarTokens, CaptureValueException[] Exceptions, long EstimatedBytes);

internal static class CaptureRowCodec
{
    private static readonly Encoding StrictUnicode = new UnicodeEncoding(false, false, true);

    internal static EncodedCaptureRow Encode(CaptureLayout layout, JsonElement row, CaptureLimits limits)
    {
        if (row.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Undefined is not a JSON row.");
        string raw = row.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > limits.MaxRowUtf8Bytes)
            throw new CaptureLimitException("A row exceeds the raw UTF-8 row budget.");
        // Enforce the same bounded nesting support before any committed write
        // that the decoder/spool reader will use (JsonDocument's depth 64).
        try { using var validation = JsonDocument.Parse(raw); }
        catch (JsonException ex) { throw new InvalidDataException("A captured row exceeds the supported JSON nesting depth (64).", ex); }
        var tokens = new string?[layout.Columns.Length];
        var exceptions = new List<CaptureValueException>();
        long exceptionBytes = 0;
        using var shape = new MemoryStream();
        using var writer = new BinaryWriter(shape, StrictUnicode, leaveOpen: true);
        if (row.ValueKind == JsonValueKind.Object)
        {
            var properties = row.EnumerateObject().Take(CaptureLimits.MaximumColumns + limits.MaxExceptionsPerRow + 1).ToArray();
            if (properties.Length > CaptureLimits.MaximumColumns + limits.MaxExceptionsPerRow)
                throw new CaptureLimitException("Too many properties in one row.");
            writer.Write(1);
            writer.Write(properties.Length);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                CaptureLayout.ValidateText(property.Name);
                bool firstOccurrence = names.Add(property.Name);
                int column = firstOccurrence && layout.ColumnMap.TryGetValue(property.Name, out int match) ? match : -1;
                writer.Write(column);
                bool originalName = column < 0 || !string.Equals(property.Name, layout.Columns[column].SourceName, StringComparison.Ordinal);
                writer.Write(originalName);
                if (originalName) writer.Write(property.Name);
                string token = property.Value.GetRawText();
                if (Encoding.UTF8.GetByteCount(token) > limits.MaxTokenUtf8Bytes)
                    throw new CaptureLimitException("An individual token exceeds the token budget.");
                if (column < 0 || property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    AddException(i, property.Value.ValueKind, token);
                else
                    tokens[column] = token;
            }
        }
        else
            AddException(-1, row.ValueKind, raw);
        writer.Flush();
        if (shape.Length > CaptureLimits.MaximumShapeBytes)
            throw new CaptureLimitException("The property-order descriptor exceeds 64 KiB.");
        // Conservative retained-memory accounting includes UTF-16 strings,
        // JsonDocument backing storage, descriptor, bulk DataTable records and
        // per-property/exception overhead. Bulk tables share the token strings.
        long estimate = checked(raw.Length * 8L + shape.Length * 4 + 4096 + tokens.Length * 256L + exceptions.Count * 512L);
        if (estimate > CaptureLimits.MaximumEstimatedRowBytes)
            throw new CaptureLimitException("A row exceeds the estimated-memory budget.");
        return new(row.ValueKind, shape.ToArray(), tokens, exceptions.ToArray(), estimate);

        void AddException(int ordinal, JsonValueKind kind, string token)
        {
            int tokenBytes = Encoding.UTF8.GetByteCount(token);
            if (tokenBytes > limits.MaxTokenUtf8Bytes)
                throw new CaptureLimitException("An individual token exceeds the token budget.");
            exceptionBytes += tokenBytes;
            if (exceptions.Count >= limits.MaxExceptionsPerRow || exceptionBytes > limits.MaxExceptionUtf8BytesPerRow)
                throw new CaptureLimitException("Duplicate, undeclared, or structured values exceed the per-row exception budget (at most 32 values and 1 MiB); review the layout or source shape.");
            exceptions.Add(new(ordinal, kind, token));
        }
    }

    internal static JsonElement Decode(CaptureLayout layout, EncodedCaptureRow row)
    {
        var exceptions = row.Exceptions.ToDictionary(x => x.PropertyOrdinal);
        if (row.Kind != JsonValueKind.Object)
        {
            if (row.PropertyOrder.Length != 0 || row.ScalarTokens.Any(token => token != null) ||
                exceptions.Count != 1 || !exceptions.TryGetValue(-1, out var root) || root.Kind != row.Kind)
                throw new InvalidDataException("Invalid exceptional root descriptor.");
            return Parse(root.RawToken, root.Kind);
        }
        using var shape = new MemoryStream(row.PropertyOrder, writable: false);
        using var reader = new BinaryReader(shape, StrictUnicode);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unknown property-order encoding.");
        int count = reader.ReadInt32();
        if (count is < 0 or > CaptureLimits.MaximumColumns + CaptureLimits.MaximumExceptionsPerRow)
            throw new InvalidDataException("Invalid property count.");
        var usedColumns = new HashSet<int>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var json = new StringBuilder("{");
        for (int i = 0; i < count; i++)
        {
            int column = reader.ReadInt32();
            if (column < -1 || column >= layout.Columns.Length || (column >= 0 && !usedColumns.Add(column)))
                throw new InvalidDataException("Invalid property-to-column mapping.");
            bool hasName = reader.ReadBoolean();
            if (column < 0 && !hasName) throw new InvalidDataException("Unnamed exceptional property.");
            string name = hasName ? reader.ReadString() : layout.Columns[column].SourceName;
            CaptureLayout.ValidateText(name);
            bool firstOccurrence = usedNames.Add(name);
            if (column >= 0 && (!firstOccurrence || !string.Equals(name, layout.Columns[column].SourceName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid original property spelling.");
            if (column < 0 && firstOccurrence && layout.ColumnMap.ContainsKey(name))
                throw new InvalidDataException("A first declared property cannot use the exceptional-name path.");
            string token;
            if (exceptions.Remove(i, out var exception))
            {
                if (column >= 0 && row.ScalarTokens[column] != null) throw new InvalidDataException("Scalar and exceptional value both present.");
                token = exception.RawToken;
                using var check = JsonDocument.Parse(token);
                if (check.RootElement.ValueKind != exception.Kind || (column >= 0 && exception.Kind is not (JsonValueKind.Object or JsonValueKind.Array)))
                    throw new InvalidDataException("Invalid exceptional value kind.");
            }
            else
            {
                token = column >= 0 ? row.ScalarTokens[column] ?? throw new InvalidDataException("Missing scalar token.")
                    : throw new InvalidDataException("Missing exceptional property.");
                using var check = JsonDocument.Parse(token);
                if (check.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    throw new InvalidDataException("Structured value in a scalar column.");
            }
            if (i > 0) json.Append(',');
            json.Append(JsonSerializer.Serialize(name)).Append(':').Append(token);
        }
        if (shape.Position != shape.Length || exceptions.Count != 0 ||
            row.ScalarTokens.Where((token, index) => token != null && !usedColumns.Contains(index)).Any())
            throw new InvalidDataException("Unused tokens or descriptor bytes.");
        return Parse(json.Append('}').ToString(), JsonValueKind.Object);
    }

    private static JsonElement Parse(string token, JsonValueKind kind)
    {
        using var document = JsonDocument.Parse(token);
        if (document.RootElement.ValueKind != kind) throw new InvalidDataException("Unexpected root value kind.");
        return document.RootElement.Clone();
    }
}

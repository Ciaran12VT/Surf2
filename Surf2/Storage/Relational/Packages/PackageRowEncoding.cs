using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Surf2.Storage.Relational.Packages;

internal static class PackageRowEncoding
{
    internal static string ForType(string type, byte? precision = null) => type switch
    {
        "nvarchar" or "nchar" => "utf16le-chunks-v1",
        "varchar" or "char" => "sql-codepage-chunks-v1",
        "binary" or "varbinary" => "binary-chunks-v1",
        "bigint" or "int" or "smallint" or "tinyint" => "integer-string-v1",
        "bit" => "boolean-v1",
        "decimal" or "numeric" => "sql-decimal-words-v1",
        "money" or "smallmoney" => "money-string-v1",
        "float" when precision is <= 24 => "float32-bits-v1",
        "float" => "float64-bits-v1", "real" => "float32-bits-v1",
        "uniqueidentifier" => "guid-string-v1",
        "date" => "date-string-v1",
        "datetime" or "datetime2" or "smalldatetime" => "datetime-string-v1",
        "datetimeoffset" => "datetimeoffset-string-v1", "time" => "time-string-v1",
        _ => throw new InvalidDataException("Unsupported SQL type in authoritative package schema: " + type)
    };

    internal static bool IsChunked(string encoding) => encoding is "utf16le-chunks-v1" or "sql-codepage-chunks-v1" or "binary-chunks-v1";

    internal static async Task WriteChunksAsync(Utf8JsonWriter writer, Stream source, long byteCount,
        byte[] buffer, CancellationToken ct)
    {
        writer.WriteStartObject();
        writer.WriteString("byteLength", byteCount.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartArray("chunks");
        long remaining = byteCount;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
            if (read == 0) throw new InvalidDataException("Stream ended before its declared cell/file length.");
            writer.WriteBase64StringValue(buffer.AsSpan(0, read));
            await writer.FlushAsync(ct);
            remaining -= read;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
            throw new InvalidDataException("Stream exceeds its declared cell/file length.");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static void WriteScalar(Utf8JsonWriter writer, string type, object value)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "bit": writer.WriteBooleanValue((bool)value); break;
            case "decimal": case "numeric":
                var number = (SqlDecimal)value;
                writer.WriteStartObject();
                writer.WriteNumber("precision", number.Precision);
                writer.WriteNumber("scale", number.Scale);
                writer.WriteBoolean("positive", number.IsPositive);
                writer.WriteStartArray("words");
                foreach (int word in number.Data) writer.WriteStringValue(unchecked((uint)word).ToString("X8", culture));
                writer.WriteEndArray(); writer.WriteEndObject(); break;
            case "money": case "smallmoney": writer.WriteStringValue(((SqlMoney)value).Value.ToString("F4", culture)); break;
            case "float" when value is float single: writer.WriteStringValue(unchecked((uint)BitConverter.SingleToInt32Bits(single)).ToString("X8", culture)); break;
            case "float": writer.WriteStringValue(unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value)).ToString("X16", culture)); break;
            case "real": writer.WriteStringValue(unchecked((uint)BitConverter.SingleToInt32Bits((float)value)).ToString("X8", culture)); break;
            case "uniqueidentifier": writer.WriteStringValue(((Guid)value).ToString("D")); break;
            case "date": writer.WriteStringValue(((DateTime)value).ToString("yyyy-MM-dd", culture)); break;
            case "datetime": case "datetime2": case "smalldatetime":
                writer.WriteStringValue(((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", culture)); break;
            case "datetimeoffset": writer.WriteStringValue(((DateTimeOffset)value).ToString("O", culture)); break;
            case "time": writer.WriteStringValue(((TimeSpan)value).ToString("c", culture)); break;
            case "bigint": case "int": case "smallint": case "tinyint":
                writer.WriteStringValue(((IFormattable)value).ToString(null, culture)); break;
            default: throw new InvalidDataException("Unsupported scalar package encoding.");
        }
    }
}

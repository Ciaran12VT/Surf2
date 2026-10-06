using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Surf2.Storage.Relational.Capture;

internal sealed class CaptureLayout
{
    private static readonly Encoding StrictUnicode = new UnicodeEncoding(false, false, true);
    internal const int EncodingVersion = 1;
    internal CaptureColumnDefinition[] Columns { get; }
    internal byte[] Hash { get; }
    internal Dictionary<string, int> ColumnMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal CaptureLayout(IReadOnlyList<CaptureColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count > CaptureLimits.MaximumColumns)
            throw new CaptureLimitException("Layouts over 128 columns require a separately reviewed wide-table encoding.");
        Columns = columns.ToArray();
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, StrictUnicode, leaveOpen: true);
        writer.Write(EncodingVersion);
        writer.Write(Columns.Length);
        for (int i = 0; i < Columns.Length; i++)
        {
            var column = Columns[i] ?? throw new ArgumentException("Null column definition.", nameof(columns));
            ValidateText(column.SourceName);
            if (column.SourceDataType != null) ValidateText(column.SourceDataType);
            if (!ColumnMap.TryAdd(column.SourceName, i))
                throw new InvalidDataException("Duplicate or case-colliding declared column names are unsupported.");
            writer.Write(column.SourceName);
            WriteNullable(writer, column.SourceDataType);
            WriteNullable(writer, column.SourceMaxLength?.ToString(CultureInfo.InvariantCulture));
            WriteNullable(writer, column.SourcePrecision?.ToString(CultureInfo.InvariantCulture));
            WriteNullable(writer, column.SourceScale?.ToString(CultureInfo.InvariantCulture));
            WriteNullable(writer, column.SourceNullable?.ToString());
            WriteNullable(writer, column.SourceOrdinal?.ToString(CultureInfo.InvariantCulture));
            WriteNullable(writer, column.SourceIdentity?.ToString());
        }
        writer.Flush();
        Hash = SHA256.HashData(bytes.ToArray());
    }

    internal static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 4096) throw new CaptureLimitException("A source name or datatype exceeds 4096 UTF-16 characters.");
        try { _ = StrictUnicode.GetByteCount(text); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Unpaired UTF-16 surrogate in source metadata.", ex); }
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null) writer.Write(value);
    }

    internal static string ColumnName(int ordinal)
    {
        if (ordinal is < 0 or >= CaptureLimits.MaximumColumns) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return "C" + (ordinal + 1).ToString("D4", CultureInfo.InvariantCulture);
    }

    internal static string TableName(long layoutKey)
    {
        if (layoutKey <= 0) throw new ArgumentOutOfRangeException(nameof(layoutKey));
        return "Data_" + layoutKey.ToString(CultureInfo.InvariantCulture);
    }

    internal static string QualifiedTable(long layoutKey) => "[capture].[" + TableName(layoutKey) + "]";

    internal string CreateTableSql(long layoutKey)
    {
        string table = QualifiedTable(layoutKey);
        string key = layoutKey.ToString(CultureInfo.InvariantCulture);
        var sql = new StringBuilder($"""
CREATE TABLE {table} (
    DataSetKey bigint NOT NULL,
    LayoutKey bigint NOT NULL CHECK (LayoutKey = {key}),
    RowOrdinal bigint NOT NULL CHECK (RowOrdinal >= 0),
    RowKind tinyint NOT NULL CHECK (RowKind BETWEEN 1 AND 7),
    EstimatedBytes bigint NOT NULL CHECK (EstimatedBytes BETWEEN 1 AND {CaptureLimits.MaximumEstimatedRowBytes}),
    PropertyOrder varbinary(max) NOT NULL CHECK (DATALENGTH(PropertyOrder) <= {CaptureLimits.MaximumShapeBytes}),
""");
        foreach (var ordinal in Enumerable.Range(0, Columns.Length))
        {
            string name = ColumnName(ordinal);
            // Scalar tokens are a deliberate lossless nvarchar encoding, not a
            // decimal/double/date guess from executable source datatype text.
            sql.AppendLine($"    [{name}] nvarchar(max) NULL CHECK (DATALENGTH([{name}]) <= 2097152),");
        }
        sql.AppendLine($"    CONSTRAINT [PK_Capture_{key}] PRIMARY KEY CLUSTERED (DataSetKey, RowOrdinal),");
        sql.AppendLine($"    CONSTRAINT [FK_Capture_{key}] FOREIGN KEY (DataSetKey, LayoutKey) REFERENCES surf.DataSet(DataSetKey, LayoutKey));");
        // 128 LOB roots plus fixed fields/offsets stay conservatively below 8060
        // bytes. Wider layouts are rejected, even though SQL permits 1024 columns.
        sql.AppendLine($"EXEC sys.sp_tableoption @TableNamePattern = N'capture.{TableName(layoutKey)}', @OptionName = 'large value types out of row', @OptionValue = 'ON';");
        return sql.ToString();
    }
}

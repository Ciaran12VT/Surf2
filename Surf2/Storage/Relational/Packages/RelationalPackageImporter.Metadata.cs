using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageImporter
{
    private sealed record KnownTable(PackageTableSpec Spec, RelationalPackageTable Metadata)
    {
        internal RelationalPackageColumn[] StreamColumns => Metadata.Columns.Where(c => c.StreamOrdinal.HasValue).ToArray();
    }

    private async Task<KnownTable> ReadKnownAsync(SqlConnection connection, SqlTransaction? transaction, PackageTableSpec spec, CancellationToken ct)
    {
        var columns = new List<RelationalPackageColumn>();
        await using (var command = Command(connection, transaction, """
SELECT c.column_id,c.name,TYPE_NAME(c.system_type_id),CONVERT(int,c.max_length),c.precision,c.scale,
 c.is_nullable,c.collation_name,CONVERT(int,COLLATIONPROPERTY(c.collation_name,'CodePage')),c.is_identity,
 CONVERT(nvarchar(100),ic.seed_value),CONVERT(nvarchar(100),ic.increment_value),c.is_computed,
 CASE WHEN DATALENGTH(cc.definition)<=32768 THEN cc.definition ELSE NULL END,
 CONVERT(bit,CASE WHEN c.user_type_id<>c.system_type_id OR c.is_hidden=1 OR c.encryption_type IS NOT NULL
 OR DATALENGTH(cc.definition)>32768 THEN 1 ELSE 0 END)
FROM sys.columns c
LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
WHERE c.object_id=OBJECT_ID(@Table,'U') ORDER BY c.column_id;
""", RelationalSession.Parameter("@Table", SqlDbType.NVarChar, spec.Qualified, 256)))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            int ordinal = 0;
            while (await reader.ReadAsync(ct))
            {
                if (columns.Count >= _limits.MaxColumnsPerTable || reader.GetBoolean(14)) throw PackageRowDecoding.Bad("Unsupported installed column metadata");
                string type = reader.GetString(2);
                bool rowVersion = type is "timestamp" or "rowversion", computed = reader.GetBoolean(12), identity = reader.GetBoolean(9);
                bool included = !rowVersion && !computed;
                columns.Add(new(reader.GetInt32(0), reader.GetString(1), type, reader.GetInt32(3), reader.GetByte(4), reader.GetByte(5),
                    reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    identity, reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), null,
                    computed, reader.IsDBNull(13) ? null : reader.GetString(13), rowVersion,
                    included ? ordinal++ : null, included ? PackageRowEncoding.ForType(type, reader.GetByte(4)) : null,
                    rowVersion ? "RestampRowVersion" : computed ? "RegenerateComputed" : identity ? "PreserveIdentityAndReseed" : "PreserveValue"));
            }
        }
        if (columns.Count == 0) throw PackageRowDecoding.Bad("Known installed table is missing");
        var primary = new List<string>();
        await using (var command = Command(connection, transaction, """
SELECT c.name FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id
JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
WHERE i.object_id=OBJECT_ID(@Table,'U') AND i.is_primary_key=1 AND k.key_ordinal>0 ORDER BY k.key_ordinal;
""", RelationalSession.Parameter("@Table", SqlDbType.NVarChar, spec.Qualified, 256)))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (primary.Count >= columns.Count) throw PackageRowDecoding.Bad("Installed PK bounds");
                primary.Add(reader.GetString(0));
            }
        }
        var foreign = new List<RelationalPackageForeignKey>();
        await using (var command = Command(connection, transaction, """
SELECT fk.object_id,fk.name,OBJECT_SCHEMA_NAME(fk.referenced_object_id),OBJECT_NAME(fk.referenced_object_id),pc.name,rc.name
FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=fk.object_id
JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
WHERE fk.parent_object_id=OBJECT_ID(@Table,'U') ORDER BY fk.object_id,fc.constraint_column_id;
""", RelationalSession.Parameter("@Table", SqlDbType.NVarChar, spec.Qualified, 256)))
        {
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            int last = -1, count = 0;
            List<string>? from = null, to = null;
            while (await reader.ReadAsync(ct))
            {
                if (++count > 1024) throw PackageRowDecoding.Bad("Installed FK bounds");
                if (last != reader.GetInt32(0))
                {
                    last = reader.GetInt32(0); from = []; to = [];
                    string schema = reader.GetString(2), table = reader.GetString(3);
                    if (!PackageTableCatalogue.Authoritative.Any(s => s.Schema == schema && s.Name == table))
                        throw PackageRowDecoding.Bad("Installed FK references a non-package table");
                    foreign.Add(new(reader.GetString(1), schema, table, from, to));
                }
                from!.Add(reader.GetString(4)); to!.Add(reader.GetString(5));
            }
        }
        return new(spec, new(spec.Schema, spec.Name, columns, primary, foreign, spec.Selection));
    }

    internal static void CompareMetadata(RelationalPackageTable expected, RelationalPackageTable actual)
    {
        if (expected.Schema != actual.Schema || expected.Name != actual.Name || expected.RowSelection != actual.RowSelection ||
            expected.Columns.Count != actual.Columns.Count || !expected.PrimaryKey.SequenceEqual(actual.PrimaryKey, StringComparer.Ordinal))
            throw PackageRowDecoding.Bad("Table/PK differs from the installed known DDL");
        for (int i = 0; i < expected.Columns.Count; i++)
        {
            var e = expected.Columns[i]; var a = actual.Columns[i];
            if ((e with { IdentityLastValue = null }) != (a with { IdentityLastValue = null }))
                throw PackageRowDecoding.Bad("Column/type/encoding/computed/collation differs from the installed known DDL");
            if (e.Identity)
            {
                if (e.SqlType != "bigint" || e.IdentitySeed != "1" || e.IdentityIncrement != "1")
                    throw PackageRowDecoding.Bad("Unsupported identity allocator definition");
                if (e.IdentityLastValue != null) _ = PackageRowDecoding.Integer(e.IdentityLastValue, long.MinValue, long.MaxValue);
            }
            else if (e.IdentityLastValue != null) throw PackageRowDecoding.Bad("Counter metadata on a non-identity column");
        }
        static string Normalized(RelationalPackageForeignKey fk) => JsonSerializer.Serialize(new object[]
            { fk.ReferencedSchema, fk.ReferencedTable, fk.Columns, fk.ReferencedColumns });
        if (!expected.ForeignKeys.Select(Normalized).Order(StringComparer.Ordinal).SequenceEqual(
            actual.ForeignKeys.Select(Normalized).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw PackageRowDecoding.Bad("Foreign-key column pairs differ from the installed known DDL");
    }

    private async Task<List<KnownTable>> CreateCaptureTablesAsync(SqlConnection connection, PackageInput package, CancellationToken ct)
    {
        var result = new List<KnownTable>();
        long after = 0;
        while (true)
        {
            long key; int count; byte[] hash;
            await using (var command = Command(connection, null, "SELECT TOP(1) LayoutKey,ColumnCount,EncodingVersion,LayoutHash FROM surf.DataLayout WHERE LayoutKey>@After ORDER BY LayoutKey;",
                RelationalSession.Parameter("@After", SqlDbType.BigInt, after)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) break;
                key = reader.GetInt64(0); count = reader.GetInt32(1);
                if (key <= 0 || count is < 0 or > 128 || reader.GetInt32(2) != 1) throw PackageRowDecoding.Bad("Unsupported restored capture layout");
                hash = reader.GetFieldValue<byte[]>(3);
            }
            var columns = new List<CaptureColumnDefinition>(count);
            await using (var command = Command(connection, null, """
SELECT ColumnOrdinal,SourceName,SourceDataType,SourceMaxLength,SourcePrecision,SourceScale,SourceNullable,SourceOrdinal,SourceIdentity,EncodingPolicy
FROM surf.DataColumn WHERE LayoutKey=@Key ORDER BY ColumnOrdinal;
""", RelationalSession.Parameter("@Key", SqlDbType.BigInt, key)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (columns.Count >= count || reader.GetInt32(0) != columns.Count || reader.GetString(9) != "JsonScalarToken-v1")
                        throw PackageRowDecoding.Bad("Capture column ordinal/encoding mismatch");
                    columns.Add(new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetByte(4),
                        reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetBoolean(8)));
                }
            }
            if (columns.Count != count) throw PackageRowDecoding.Bad("Incomplete capture column catalogue");
            var layout = new CaptureLayout(columns);
            if (!layout.Hash.AsSpan().SequenceEqual(hash)) throw PackageRowDecoding.Bad("Capture layout content hash mismatch");
            var spec = PackageTableCatalogue.Capture(key, count);
            if (!package.Tables.TryGetValue(spec.Key, out var stream)) throw PackageRowDecoding.Bad("Capture stream omitted for a layout");
            // Executable text comes only from the local CaptureLayout generator and a positive integer key.
            await ExecuteAsync(connection, null, layout.CreateTableSql(key), ct);
            var known = await ReadKnownAsync(connection, null, spec, ct);
            CompareMetadata(stream.Table, known.Metadata);
            result.Add(known); after = key;
        }
        if (package.Tables.Keys.Count(key => key.StartsWith("capture.", StringComparison.Ordinal)) != result.Count)
            throw PackageRowDecoding.Bad("Capture streams do not exactly match the layout catalogue");
        return result;
    }

    private async Task ConstraintsAsync(SqlConnection connection, SqlTransaction? transaction, IEnumerable<KnownTable> tables, bool enable, CancellationToken ct)
    {
        foreach (var table in tables)
            foreach (var fk in table.Metadata.ForeignKeys)
                await ExecuteAsync(connection, transaction, "ALTER TABLE " + table.Spec.Qualified +
                    (enable ? " WITH CHECK CHECK CONSTRAINT " : " NOCHECK CONSTRAINT ") + PackageTableCatalogue.Quote(fk.Name) + ";", ct);
    }

    private static string TypeSql(RelationalPackageColumn column, bool staging)
    {
        if (staging && PackageRowEncoding.IsChunked(column.Encoding!)) return "varbinary(max)";
        return column.SqlType switch
        {
            "nvarchar" or "nchar" => column.SqlType + "(" + (column.MaxLengthBytes == -1 ? "max" : (column.MaxLengthBytes / 2).ToString(CultureInfo.InvariantCulture)) + ")",
            "varchar" or "char" or "binary" or "varbinary" => column.SqlType + "(" + (column.MaxLengthBytes == -1 ? "max" : column.MaxLengthBytes.ToString(CultureInfo.InvariantCulture)) + ")",
            "decimal" or "numeric" => column.SqlType + "(" + column.Precision.ToString(CultureInfo.InvariantCulture) + "," + column.Scale.ToString(CultureInfo.InvariantCulture) + ")",
            "datetime2" or "datetimeoffset" or "time" => column.SqlType + "(" + column.Scale.ToString(CultureInfo.InvariantCulture) + ")",
            "float" => "float(" + column.Precision.ToString(CultureInfo.InvariantCulture) + ")",
            "bigint" or "int" or "smallint" or "tinyint" or "bit" or "money" or "smallmoney" or "real" or "uniqueidentifier" or "date" or "datetime" or "smalldatetime" => column.SqlType,
            _ => throw PackageRowDecoding.Bad("Unsupported installed SQL type")
        };
    }
}

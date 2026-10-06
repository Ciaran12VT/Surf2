using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageExporter
{
    private SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string text, params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _limits.CommandTimeoutSeconds;
        command.CommandText = text;
        command.Parameters.AddRange(parameters);
        return command;
    }

    private async Task<RelationalPackageTable> ReadMetadataAsync(SqlConnection connection, SqlTransaction transaction,
        PackageTableSpec spec, PackageBudget budget, CancellationToken ct)
    {
        var columns = new List<RelationalPackageColumn>();
        int streamOrdinal = 0;
        await using (var command = Command(connection, transaction, """
SELECT c.column_id, c.name, TYPE_NAME(c.system_type_id), CONVERT(int,c.max_length), c.precision, c.scale,
       c.is_nullable, c.collation_name, CONVERT(int,COLLATIONPROPERTY(c.collation_name,'CodePage')),
       c.is_identity, CONVERT(nvarchar(100),ic.seed_value), CONVERT(nvarchar(100),ic.increment_value),
       CONVERT(nvarchar(100),ic.last_value), c.is_computed,
       CASE WHEN DATALENGTH(cc.definition)<=32768 THEN cc.definition ELSE NULL END,
       CONVERT(bit,CASE WHEN DATALENGTH(cc.definition)>32768 THEN 1 ELSE 0 END),
       CONVERT(bit,CASE WHEN c.user_type_id<>c.system_type_id OR c.is_hidden=1 OR c.encryption_type IS NOT NULL THEN 1 ELSE 0 END)
FROM sys.columns c
LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
WHERE c.object_id=OBJECT_ID(@Table,'U') ORDER BY c.column_id;
""", RelationalSession.Parameter("@Table", SqlDbType.NVarChar, spec.Qualified, 256)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (columns.Count >= _limits.MaxColumnsPerTable) throw new RelationalPackageLimitException("Package table column limit exceeded.");
                if (reader.GetBoolean(15) || reader.GetBoolean(16)) throw new InvalidDataException("Unsupported authoritative SQL column metadata.");
                string type = reader.GetString(2);
                bool rowVersion = type is "timestamp" or "rowversion";
                bool computed = reader.GetBoolean(13);
                string? encoding = rowVersion ? null : PackageRowEncoding.ForType(type, reader.GetByte(4));
                bool included = !rowVersion && !computed;
                columns.Add(new(reader.GetInt32(0), reader.GetString(1), type, reader.GetInt32(3), reader.GetByte(4), reader.GetByte(5),
                    reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.GetBoolean(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12), computed, reader.IsDBNull(14) ? null : reader.GetString(14), rowVersion,
                    included ? streamOrdinal++ : null, included ? encoding : null,
                    rowVersion ? "RestampRowVersion" : computed ? "RegenerateComputed" : reader.GetBoolean(9) ? "PreserveIdentityAndReseed" : "PreserveValue"));
            }
        }
        if (columns.Count == 0) throw new InvalidDataException("An authoritative package table is absent or metadata is inaccessible: " + spec.Key);
        budget.AddColumns(columns.Count);
        if (spec.CaptureLayoutKey.HasValue) ValidateCaptureMetadata(spec, columns);

        var primaryKey = new List<string>();
        await using (var command = Command(connection, transaction, """
SELECT c.name FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id AND k.index_id=i.index_id
JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
WHERE i.object_id=OBJECT_ID(@Table,'U') AND i.is_primary_key=1 AND k.key_ordinal>0 ORDER BY k.key_ordinal;
""", RelationalSession.Parameter("@Table", SqlDbType.NVarChar, spec.Qualified, 256)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (primaryKey.Count == columns.Count) throw new InvalidDataException("Invalid package primary key metadata.");
                string name = reader.GetString(0);
                if (!columns.Any(column => column.Name == name && column.StreamOrdinal.HasValue))
                    throw new InvalidDataException("An omitted SQL column participates in the primary key.");
                primaryKey.Add(name);
            }
        }
        if (primaryKey.Count == 0) throw new InvalidDataException("A stable primary key is required for every package table.");
        var foreignKeys = new List<RelationalPackageForeignKey>();
        await using (var command = Command(connection, transaction, """
SELECT fk.object_id,fk.name,OBJECT_SCHEMA_NAME(fk.referenced_object_id),OBJECT_NAME(fk.referenced_object_id),
       pc.name,rc.name,fk.is_disabled,fk.is_not_trusted
FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=fk.object_id
JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
WHERE fk.parent_object_id=OBJECT_ID(@Table,'U') ORDER BY fk.object_id,fc.constraint_column_id;
""", RelationalSession.Parameter("@Table", SqlDbType.NVarChar, spec.Qualified, 256)))
        {
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            int last = -1, count = 0;
            List<string>? from = null, to = null;
            while (await reader.ReadAsync(ct))
            {
                if (++count > 1024) throw new RelationalPackageLimitException("Package foreign-key catalogue budget exceeded.");
                if (reader.GetBoolean(6) || reader.GetBoolean(7)) throw new InvalidDataException("An authoritative foreign key is disabled or untrusted.");
                if (last != reader.GetInt32(0))
                {
                    last = reader.GetInt32(0); from = []; to = [];
                    foreignKeys.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), from, to));
                }
                from!.Add(reader.GetString(4)); to!.Add(reader.GetString(5));
            }
        }
        return new(spec.Schema, spec.Name, columns.AsReadOnly(), primaryKey.AsReadOnly(),
            foreignKeys.Select(fk => fk with { Columns = fk.Columns.ToArray(), ReferencedColumns = fk.ReferencedColumns.ToArray() }).ToArray(), spec.Selection);
    }

    private static void ValidateCaptureMetadata(PackageTableSpec spec, List<RelationalPackageColumn> columns)
    {
        string[] fixedNames = ["DataSetKey", "LayoutKey", "RowOrdinal", "RowKind", "EstimatedBytes", "PropertyOrder"];
        string[] types = ["bigint", "bigint", "bigint", "tinyint", "bigint", "varbinary"];
        if (columns.Count != 6 + spec.CaptureColumnCount!.Value) throw new InvalidDataException("Generated capture schema does not match its layout catalogue.");
        for (int i = 0; i < columns.Count; i++)
        {
            string name = i < 6 ? fixedNames[i] : "C" + (i - 5).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            string type = i < 6 ? types[i] : "nvarchar";
            if (columns[i].Name != name || columns[i].SqlType != type || columns[i].Computed || columns[i].Identity ||
                columns[i].Nullable != (i >= 6) || (i >= 5 && columns[i].MaxLengthBytes != -1))
                throw new InvalidDataException("Unexpected generated capture column metadata.");
        }
    }

    private async Task ValidateReferencesAsync(SqlConnection connection, SqlTransaction transaction, PackageTableSpec spec,
        RelationalPackageTable table, CancellationToken ct)
    {
        foreach (var fk in table.ForeignKeys)
        {
            var target = PackageTableCatalogue.Authoritative.SingleOrDefault(candidate => candidate.Schema == fk.ReferencedSchema && candidate.Name == fk.ReferencedTable)
                ?? throw new InvalidDataException("An authoritative table references an excluded package table.");
            string nonNull = string.Join(" AND ", fk.Columns.Select(column => "t." + PackageTableCatalogue.Quote(column) + " IS NOT NULL"));
            string equality = string.Join(" AND ", fk.Columns.Zip(fk.ReferencedColumns, (from, to) =>
                "t." + PackageTableCatalogue.Quote(from) + "=r." + PackageTableCatalogue.Quote(to)));
            await using var command = Command(connection, transaction, $"""
SELECT CONVERT(bit,CASE WHEN EXISTS (SELECT 1 FROM {spec.Qualified} t WHERE ({spec.Predicate("t")}) AND {nonNull}
AND NOT EXISTS (SELECT 1 FROM {target.Qualified} r WHERE ({target.Predicate("r")}) AND {equality})) THEN 1 ELSE 0 END);
""");
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            if ((bool)(await command.ExecuteScalarAsync(ct) ?? true))
                throw new InvalidDataException("An exported authoritative relationship would reference omitted/incomplete data: " + spec.Key);
        }
    }
}

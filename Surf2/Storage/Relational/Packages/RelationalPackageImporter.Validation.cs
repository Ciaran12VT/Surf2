using System.Data;
using System.Data.SqlTypes;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational.Capture;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageImporter
{
    private async Task ValidateStoredTableAsync(SqlConnection connection, SqlTransaction transaction, KnownTable table,
        RelationalPackageTableStream stream, PackageInput package, RelationalPackageImportOptions options, CancellationToken ct)
    {
        await using (var count = Command(connection, transaction, "SELECT COUNT_BIG(*) FROM " + table.Spec.Qualified + ";"))
        {
            using var cancel = RelationalSession.CancelCommand(count, ct);
            if ((long)(await count.ExecuteScalarAsync(ct) ?? -1L) != stream.RowCount) throw PackageRowDecoding.Bad("Restored table row count mismatch");
        }
        var columns = table.StreamColumns;
        await using var input = package.Entries[stream.Entry].Open();
        using var verified = new PackageEntryReadStream(input, stream.ByteCount, PackageRowDecoding.Hash(stream.Sha256));
        var decoder = new PackageRowDecoding(verified, _limits, BulkRowPayloadBudget(columns.Length, options));
        await using var command = Command(connection, transaction, PackageTableCatalogue.SelectSql(table.Spec, table.Metadata));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, ct);
        long countRows = 0;
        long[]? previous = null;
        var buffer = new byte[_limits.ChunkBytes];
        while (await decoder.ReadAsync(columns, ct) is { } expected)
        {
            PackageRowDecoding.OrderedKey(columns, table.Metadata.PrimaryKey, expected, ref previous);
            ValidateContentRow(table.Spec, columns, expected);
            if (!await reader.ReadAsync(ct)) throw PackageRowDecoding.Bad("Restored selection has fewer rows than the pinned package");
            int ordinal = 0;
            for (int i = 0; i < columns.Length; i++)
            {
                bool chunked = PackageRowEncoding.IsChunked(columns[i].Encoding!);
                bool isNull = await reader.IsDBNullAsync(ordinal, ct);
                if ((expected[i] == DBNull.Value) != isNull) throw PackageRowDecoding.Bad("Restored SQL NULL differs from the source");
                if (!isNull && chunked)
                {
                    var bytes = (byte[])expected[i];
                    if (reader.GetInt64(ordinal) != bytes.LongLength) throw PackageRowDecoding.Bad("Restored cell byte length differs from the source");
                    await using var stored = reader.GetStream(ordinal + 1);
                    int position = 0;
                    while (position < bytes.Length)
                    {
                        int read = await stored.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, bytes.Length - position)), ct);
                        if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(bytes.AsSpan(position, read)))
                            throw PackageRowDecoding.Bad("Restored raw UTF-16/code-page/binary cell differs from the source");
                        position += read;
                    }
                    if (await stored.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw PackageRowDecoding.Bad("Restored cell exceeds its source bytes");
                }
                else if (!isNull)
                {
                    object actual = columns[i].SqlType switch
                    {
                        "decimal" or "numeric" => await reader.GetFieldValueAsync<SqlDecimal>(ordinal, ct),
                        "money" or "smallmoney" => await reader.GetFieldValueAsync<SqlMoney>(ordinal, ct),
                        _ => await reader.GetFieldValueAsync<object>(ordinal, ct)
                    };
                    if (!ScalarEqual(expected[i], actual)) throw PackageRowDecoding.Bad("Restored typed scalar differs from the source");
                }
                ordinal += chunked ? 2 : 1;
            }
            countRows++;
        }
        if (countRows != stream.RowCount || await reader.ReadAsync(ct)) throw PackageRowDecoding.Bad("Restored selection row count mismatch");
        verified.Finish();
    }

    private static bool ScalarEqual(object expected, object actual) => (expected, actual) switch
    {
        (double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
        (float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b),
        (DateTimeOffset a, DateTimeOffset b) => a.EqualsExact(b),
        (SqlDecimal a, SqlDecimal b) => a.Precision == b.Precision && a.Scale == b.Scale && a.IsPositive == b.IsPositive && a.Data.SequenceEqual(b.Data),
        _ => expected.Equals(actual)
    };

    private static void ValidateContentRow(PackageTableSpec spec, IReadOnlyList<RelationalPackageColumn> columns, object[] row)
    {
        if (spec.Schema != "surf" || spec.Name is not ("TextContent" or "Asset")) return;
        object Value(string name) => row[columns.Select((column, ordinal) => (column, ordinal)).Single(p => p.column.Name == name).ordinal];
        byte[] bytes = (byte[])Value(spec.Name == "TextContent" ? "Text" : "Bytes");
        if ((long)Value("ByteCount") != bytes.LongLength || !SHA256.HashData(bytes).AsSpan().SequenceEqual((byte[])Value("ContentHash")))
            throw PackageRowDecoding.Bad("Content bytes/hash/count mismatch");
        if (spec.Name == "TextContent")
        {
            long lines = 1;
            for (int i = 0; i < bytes.Length; i += 2) if (bytes[i] == 10 && bytes[i + 1] == 0) lines++;
            if ((int)Value("HashEncodingVersion") != 1 || (long)Value("CharacterCount") != bytes.LongLength / 2 || (long)Value("LineCount") != lines)
                throw PackageRowDecoding.Bad("Exact UTF-16 text metrics mismatch");
        }
    }

    private async Task<long> ValidateCaptureModelsAsync(SqlConnection connection, IReadOnlyList<KnownTable> captures,
        Guid import, byte[] fingerprint, Guid database, CancellationToken ct)
    {
        await RequireImportAsync(connection, null, import, fingerprint, database, ct);
        // A pinned, read-only Converting session is the existing specialized validation guard.
        // Normal runtime RequireReadyAsync and all domain write guards remain unchanged.
        var validation = RelationalSession.ForMigrationValidation(_destination, import, fingerprint);
        var capture = new RelationalCaptureStore(validation);
        long after = 0, total = 0;
        while (true)
        {
            long key, layout, actual;
            await using (var command = Command(connection, null, """
SELECT TOP(1) DataSetKey,LayoutKey,ActualRowCount,ExpectedActualRowCount,State,DisplayFormatVersion
FROM surf.DataSet WHERE DataSetKey>@After ORDER BY DataSetKey;
""", RelationalSession.Parameter("@After", SqlDbType.BigInt, after)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) break;
                key = reader.GetInt64(0); layout = reader.GetInt64(1); actual = reader.GetInt64(2);
                if (key <= 0 || actual < 0 || reader.IsDBNull(3) || reader.GetInt64(3) != actual || reader.GetString(4) != "Ready" || reader.GetInt32(5) != 1)
                    throw PackageRowDecoding.Bad("Incomplete captured dataset flags/counts");
            }
            if (!captures.Any(t => t.Spec.CaptureLayoutKey == layout)) throw PackageRowDecoding.Bad("A dataset has no generated capture stream");
            long rows = 0;
            await foreach (var row in capture.StreamRowsAsync(key, cancellationToken: ct).ConfigureAwait(false))
            {
                if (row.RowOrdinal != rows || rows >= actual) throw PackageRowDecoding.Bad("Captured row ordinals/counts mismatch");
                rows++;
            }
            if (rows != actual) throw PackageRowDecoding.Bad("Captured decoded row count mismatch");
            total = checked(total + rows); after = key;
        }
        return total;
    }

    private async Task ValidateDatabaseAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<KnownTable> tables,
        IReadOnlyList<KnownTable> captures, long capturedRows, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, """
IF EXISTS(SELECT 1 FROM surf.DatabaseSnapshot WHERE IsPublished<>1)
 OR EXISTS(SELECT 1 FROM surf.SnapshotResourceRevision WHERE IsSealed<>1)
 OR EXISTS(SELECT 1 FROM surf.DataSet WHERE State<>'Ready' OR ExpectedActualRowCount IS NULL
  OR ActualRowCount<>ExpectedActualRowCount OR DisplayFormatVersion<>1)
 OR EXISTS(SELECT 1 FROM surf.TableDataRevision t WHERE NOT EXISTS(SELECT 1 FROM surf.DataSet d WHERE d.RevisionKey=t.RevisionKey))
 THROW 51100,'The restored snapshot/capture publication flags are incomplete.',1;
IF EXISTS(SELECT 1 FROM surf.TextContent WHERE HashEncodingVersion<>1 OR ByteCount<>DATALENGTH(Text)
 OR CharacterCount<>DATALENGTH(Text)/2 OR ContentHash<>HASHBYTES('SHA2_256',CONVERT(varbinary(max),Text)))
 OR EXISTS(SELECT 1 FROM surf.Asset WHERE ByteCount<>DATALENGTH(Bytes) OR ContentHash<>HASHBYTES('SHA2_256',Bytes))
 THROW 51101,'The restored content SQL hashes or byte counts do not match.',1;
IF COALESCE((SELECT SUM(ActualRowCount) FROM surf.DataSet),0)<>@Rows
 THROW 51102,'The restored captured row total does not match decoded validation.',1;
""", ct, RelationalSession.Parameter("@Rows", SqlDbType.BigInt, capturedRows));
        foreach (var table in tables)
        {
            await ExecuteAsync(connection, transaction, """
IF EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(@Table,'U') AND (is_disabled=1 OR is_not_trusted=1))
 THROW 51103,'A restored owned foreign key remains disabled or untrusted.',1;
IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(@Table,'U') AND (is_disabled=1 OR is_not_trusted=1))
 THROW 51104,'A restored owned check constraint is disabled or untrusted.',1;
""", ct, RelationalSession.Parameter("@Table", SqlDbType.NVarChar, table.Spec.Qualified, 256));
        }
        foreach (var capture in captures)
            await ExecuteAsync(connection, transaction, $"""
IF EXISTS(SELECT 1 FROM surf.DataSet d OUTER APPLY
 (SELECT COUNT_BIG(*) [RowCount],MIN(RowOrdinal) FirstRow,MAX(RowOrdinal) LastRow FROM {capture.Spec.Qualified} r WHERE r.DataSetKey=d.DataSetKey) counts
 WHERE d.LayoutKey=@Layout AND (counts.[RowCount]<>d.ActualRowCount OR
  (counts.[RowCount]>0 AND (counts.FirstRow<>0 OR counts.LastRow<>counts.[RowCount]-1))))
 OR EXISTS(SELECT 1 FROM surf.DataValueException e JOIN surf.DataSet d ON d.DataSetKey=e.DataSetKey
 WHERE d.LayoutKey=@Layout AND NOT EXISTS(SELECT 1 FROM {capture.Spec.Qualified} r WHERE r.DataSetKey=e.DataSetKey AND r.RowOrdinal=e.RowOrdinal))
 THROW 51105,'Captured stable ordinals, actual counts, or exceptional-row ownership do not match.',1;
IF NOT EXISTS(SELECT 1 FROM surf.DataSet WHERE LayoutKey=@Layout)
 THROW 51106,'An unused captured layout was restored.',1;
""", ct, RelationalSession.Parameter("@Layout", SqlDbType.BigInt, capture.Spec.CaptureLayoutKey!.Value));
        foreach (var (table, owners) in new (string, string)[]
        {
            ("ScopeResource", "ScopeKey"), ("VirtualFolder", "ScopeKey"), ("VirtualFolderMember", "VirtualFolderKey"),
            ("DiagramObject", "DiagramRevisionKey"), ("Workflow", "DiagramRevisionKey"), ("WorkflowItem", "WorkflowKey"),
            ("QueryItem", "DiagramObjectKey,WorkflowItemKey"), ("DocumentWindowState", "WorkspaceSessionKey,WorkbenchKey"),
            ("WorkspaceUnloadedResource", "WorkspaceSessionKey,WorkbenchKey"), ("DocumentWindowFilter", "DocumentWindowKey"),
            ("ReferenceConnectionLine", "WorkbenchKey"), ("ExtensionAppearance", "ProfileKey"), ("ReferenceStyle", "ProfileKey"),
            ("DiagramImageDefinition", "ProfileKey")
        })
            await ExecuteAsync(connection, transaction, $"""
IF EXISTS(SELECT 1 FROM surf.[{table}] GROUP BY {owners}
 HAVING MIN(SortOrdinal)<>0 OR MAX(SortOrdinal)<>COUNT_BIG(*)-1 OR COUNT(DISTINCT SortOrdinal)<>COUNT_BIG(*))
 THROW 51107,'A restored state collection has non-contiguous owned ordinals.',1;
""", ct);
    }

    private Task PublishAsync(SqlConnection connection, SqlTransaction transaction, PackageInput package,
        Guid import, byte[] fingerprint, Guid database, CancellationToken ct) => ExecuteAsync(connection, transaction, """
INSERT surf.SourceProvenance(MigrationIdentity,DocumentKey,SourceKind,SourceSchemaVersion,OriginalTimestamp,ContentHash,ByteCount)
VALUES(@Import,N'package-manifest','Package',@Schema,@Exported,@Hash,@Bytes);
INSERT surf.StateCatalogueGeneration(ProfileKey,Kind,PublicationId) VALUES(1,0,NEWID()),(1,1,NEWID()),(1,2,NEWID());
UPDATE surf.SnapshotCatalogueHead SET UserKey=UserKey WHERE UserKey=1;
UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;
UPDATE surf.MigrationRun SET Status='Complete',FinishedAtUtc=SYSDATETIMEOFFSET()
WHERE MigrationIdentity=@Import AND SourceFingerprint=@Hash AND ConverterVersion=1 AND Status='Converting';
IF @@ROWCOUNT<>1 THROW 51108,'The pinned import run changed before publication.',1;
UPDATE surf.StorageFormatInfo SET State='Ready',CompletedMigrationIdentity=@Import
WHERE Singleton=1 AND State='Migrating' AND DatabaseIdentity=@Database AND SchemaVersion=1;
IF @@ROWCOUNT<>1 THROW 51109,'The pinned format marker changed before publication.',1;
""", ct, RelationalSession.Parameter("@Import", SqlDbType.UniqueIdentifier, import),
        RelationalSession.Parameter("@Schema", SqlDbType.Int, package.Manifest.DatabaseSchemaVersion),
        RelationalSession.Parameter("@Exported", SqlDbType.DateTimeOffset, package.Manifest.ExportedAtUtc),
        RelationalSession.Parameter("@Hash", SqlDbType.Binary, fingerprint, 32),
        RelationalSession.Parameter("@Bytes", SqlDbType.BigInt, package.Entries[RelationalPackageFormat.ManifestEntry].Length),
        RelationalSession.Parameter("@Database", SqlDbType.UniqueIdentifier, database));
}

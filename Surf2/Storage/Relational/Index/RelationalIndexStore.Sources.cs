using System.Collections.Immutable;
using System.Data;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    public const string TableRendererVersion = "database-table-v1";
    public const string DiagramRendererVersion = "diagram-type-label-image-v1";

    public static ImmutableArray<SymbolInput> ExtractFileSymbols(string path, string text,
        IReferenceDefinitionParser? parser, CancellationToken token = default)
    {
        string filename = Path.GetFileName(path), stem = Path.GetFileNameWithoutExtension(path);
        var result = ImmutableArray.CreateBuilder<SymbolInput>();
        result.Add(new(string.IsNullOrWhiteSpace(stem) ? filename : stem, filename, ReferenceEntityKind.File,
            path, 1, 1, 1, 1, null, null, null, "File", string.Empty));
        if (parser != null)
        {
            foreach (ReferenceEntity entity in parser.Parse(path, text))
            {
                token.ThrowIfCancellationRequested();
                if (result.Count == MaximumSymbolsPerDocument) throw new IndexDocumentTooLargeException();
                result.Add(SymbolInput.FromReference(entity));
            }
        }
        return result.ToImmutable();
    }

    public async Task<PreparedIndexSource> PrepareDefinitionAsync(long sourceRevisionKey, string locator,
        string containerName, int maximumCharacters = 8 * 1024 * 1024, CancellationToken token = default)
    {
        await using var connection = await OpenReadAsync(token);
        await using var command = IndexSql.Command(connection, null, """
SELECT LEFT(o.SchemaName,65537),LEFT(o.ObjectName,65537),o.ObjectKind,c.ContentHash,c.Text
FROM surf.DatabaseObjectRevision o JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=o.RevisionKey
JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=rr.SnapshotKey
JOIN surf.TextContent c ON c.ContentKey=o.DefinitionContentKey
WHERE o.RevisionKey=@Revision AND rr.IsSealed=1 AND s.IsPublished=1;
""");
        IndexSql.Add(command, "@Revision", SqlDbType.BigInt, sourceRevisionKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Definition revision not found.");
        string schema = MetadataString(reader, 0), name = MetadataString(reader, 1);
        var objectKind = (SqlDatabaseObjectKind)reader.GetInt32(2);
        string fingerprint = IndexSql.Hex((byte[])reader[3]);
        using var body = reader.GetTextReader(4);
        string text = await IndexTextMatcher.ReadBoundedAsync(body, maximumCharacters, token);
        var kind = objectKind switch
        {
            SqlDatabaseObjectKind.StoredProcedure => ReferenceEntityKind.StoredProcedure,
            SqlDatabaseObjectKind.View => ReferenceEntityKind.View,
            SqlDatabaseObjectKind.Function => ReferenceEntityKind.Function,
            SqlDatabaseObjectKind.Trigger => ReferenceEntityKind.Trigger,
            _ => ReferenceEntityKind.File
        };
        ImmutableArray<SymbolInput> symbols = objectKind == SqlDatabaseObjectKind.Unknown ? [] :
            [new(name, SqlName.FormatPlainMultipartName(schema, name), kind, locator, 1, 1,
                1 + text.Count(c => c == '\n'), 1, null, null, null, "SQL Server", containerName)];
        return new(text, fingerprint, "SQL Server", symbols, sourceRevisionKey);
    }

    public async Task<PreparedIndexSource> PrepareTableCodeAsync(long sourceRevisionKey, string locator,
        string containerName, CancellationToken token = default)
    {
        await using var connection = await OpenReadAsync(token);
        var selected = await ReadSelectedTableAsync(connection, null, sourceRevisionKey, token);
        var renderer = new DatabaseDocumentService();
        string text = renderer.CreateTableDocument(selected.Slice, selected.Table);
        if (text.Length > 8 * 1024 * 1024) throw new IndexDocumentTooLargeException();
        var symbols = ImmutableArray.CreateBuilder<SymbolInput>();
        string qualified = SqlName.FormatPlainMultipartName(selected.Table.SchemaName, selected.Table.TableName);
        symbols.Add(new(selected.Table.TableName, qualified, ReferenceEntityKind.Table, locator, 1, 1, 1, 1,
            null, null, null, "SQL Server", containerName));
        var columns = selected.Slice.Columns.Where(c =>
            string.Equals(c.SchemaName, selected.Table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.TableName, selected.Table.TableName, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Ordinal).ToArray();
        var firstLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int headerLines = selected.Table.HasFullData ? 3 : 2;
        for (int i = 0; i < columns.Length; i++) firstLines.TryAdd(columns[i].ColumnName, headerLines + i + 1);
        foreach (SqlColumn column in columns)
        {
            int line = firstLines[column.ColumnName];
            string columnOwner = SqlName.FormatPlainMultipartName(column.SchemaName, column.TableName);
            symbols.Add(new(column.ColumnName, $"{columnOwner}.{column.ColumnName}", ReferenceEntityKind.Field,
                locator, line, 5, line, 5 + column.ColumnName.Length, null, null, null, "SQL Server", columnOwner));
        }
        return new(text, IndexSql.Hex(IndexSql.Hash(text)), "SQL Server", symbols.ToImmutable(), sourceRevisionKey);
    }

    public async Task<PreparedIndexSource> PrepareDiagramAsync(long diagramRevisionKey, CancellationToken token = default)
    {
        await using var connection = await OpenReadAsync(token);
        string text = await ReadDiagramProjectionAsync(connection, null, diagramRevisionKey, token);
        return new(text, IndexSql.Hex(IndexSql.Hash(text)), "Diagram", []);
    }

    private static async Task ValidateSourceAsync(SqlConnection connection, SqlTransaction transaction,
        IndexedDocumentKind kind, long? resourceKey, long? diagramKey, IndexPublication publication, CancellationToken token)
    {
        if (kind == IndexedDocumentKind.File)
        {
            if (publication.SourceRevisionKey != null || publication.FileFingerprint == null)
                throw new ArgumentException("Physical publication requires a fingerprint, not a database revision.");
            return;
        }
        if (publication.FileFingerprint != null) throw new ArgumentException("Database publication cannot own a file fingerprint.");
        if (kind == IndexedDocumentKind.Diagram)
        {
            if (publication.SourceRevisionKey != null || publication.Symbols.Length != 0 ||
                publication.Lease.Policy.RendererVersion != DiagramRendererVersion)
                throw new ArgumentException("Unsupported diagram projection policy.");
            string expected = await ReadDiagramProjectionAsync(connection, transaction, diagramKey!.Value, token);
            if (!string.Equals(expected, publication.Text, StringComparison.Ordinal) ||
                publication.Lease.SourceFingerprint != IndexSql.Hex(IndexSql.Hash(expected)))
                throw new InvalidOperationException("Diagram projection is not the type/label/image-name representation.");
            return;
        }
        await using var command = IndexSql.Command(connection, transaction, """
SELECT rr.RevisionKey FROM surf.SnapshotResourceRevision rr JOIN surf.SnapshotResource r ON r.ResourceKey=rr.ResourceKey
JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey
WHERE rr.RevisionKey=@Revision AND rr.ResourceKey=@Resource AND rr.IsSealed=1
 AND s.IsPublished=1 AND r.CurrentRevisionKey=rr.RevisionKey;
""");
        IndexSql.Add(command, "@Revision", SqlDbType.BigInt, publication.SourceRevisionKey);
        IndexSql.Add(command, "@Resource", SqlDbType.BigInt, resourceKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        if (await command.ExecuteScalarAsync(token) is not long) throw new InvalidOperationException("Source revision is not current and sealed.");
        if (kind == IndexedDocumentKind.Definition)
        {
            command.CommandText = """
SELECT o.RevisionKey FROM surf.DatabaseObjectRevision o JOIN surf.TextContent c ON c.ContentKey=o.DefinitionContentKey
WHERE o.RevisionKey=@Revision AND c.ContentHash=@Fingerprint
 AND CONVERT(varbinary(max),c.Text)=CONVERT(varbinary(max),@Text);
""";
            IndexSql.Add(command, "@Fingerprint", SqlDbType.Binary, IndexSql.Bytes(publication.Lease.SourceFingerprint, 32), 32);
            IndexSql.Add(command, "@Text", SqlDbType.NVarChar, publication.Text, -1);
            if (await command.ExecuteScalarAsync(token) is not long) throw new InvalidOperationException("Definition text does not match its source revision.");
        }
        else
        {
            if (publication.Lease.Policy.RendererVersion != TableRendererVersion) throw new ArgumentException("Unknown table renderer version.");
            var selected = await ReadSelectedTableAsync(connection, transaction, publication.SourceRevisionKey!.Value, token);
            string expected = new DatabaseDocumentService().CreateTableDocument(selected.Slice, selected.Table);
            if (!string.Equals(expected, publication.Text, StringComparison.Ordinal) ||
                publication.Lease.SourceFingerprint != IndexSql.Hex(IndexSql.Hash(expected)))
                throw new InvalidOperationException("Table code differs from its selected metadata revision.");
        }
    }

    private static async Task<string> ReadDiagramProjectionAsync(SqlConnection connection, SqlTransaction? transaction,
        long revisionKey, CancellationToken token)
    {
        await using var command = IndexSql.Command(connection, transaction, """
IF NOT EXISTS(SELECT 1 FROM surf.DiagramRevision WHERE DiagramRevisionKey=@Revision)
 THROW 51103,'Diagram revision not found.',1;
SELECT o.ObjectType,LEFT(o.ImageName,65537),c.Text
FROM surf.DiagramObject o JOIN surf.TextContent c ON c.ContentKey=o.LabelTextContentKey
WHERE o.DiagramRevisionKey=@Revision ORDER BY o.SortOrdinal,o.DiagramObjectKey;
""");
        IndexSql.Add(command, "@Revision", SqlDbType.BigInt, revisionKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        var text = new StringBuilder();
        int count = 0;
        while (await reader.ReadAsync(token))
        {
            if (++count > MaximumSymbolsPerDocument) throw new IndexDocumentTooLargeException();
            var type = (DiagramObjectType)reader.GetInt32(0);
            string image = MetadataString(reader, 1);
            using var labelReader = reader.GetTextReader(2);
            string label = await IndexTextMatcher.ReadBoundedAsync(labelReader, 8 * 1024 * 1024, token);
            if ((long)text.Length + label.Length + image.Length + 64 > 8 * 1024 * 1024) throw new IndexDocumentTooLargeException();
            if (count > 1) text.Append(Environment.NewLine);
            text.Append(type).Append(' ').Append(label).Append(' ').Append(image);
        }
        return text.ToString();
    }

    // A transient, bounded ONE-table adapter for the existing formatting rules, never a library read.
    private static async Task<(DatabaseMetadataSnapshot Slice, SqlTable Table)> ReadSelectedTableAsync(
        SqlConnection connection, SqlTransaction? transaction, long revisionKey, CancellationToken token)
    {
        await using var command = IndexSql.Command(connection, transaction, """
SELECT LEFT(t.SchemaName,65537),LEFT(t.TableName,65537),t.HasFullData,t.FullDataRowCount,t.FullDataImportedAtUtc
FROM surf.TableMetadataRevision t JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=t.RevisionKey
WHERE t.RevisionKey=@Revision AND rr.IsSealed=1;
SELECT TOP (100001) LEFT(SchemaName,65537),LEFT(TableName,65537),LEFT(ColumnName,65537),LEFT(DataType,65537),MaxLength,
 NumericPrecision,NumericScale,IsNullable,IsIdentity,SourceOrdinal FROM surf.TableColumnRevision
WHERE TableMetadataRevisionKey=@Revision ORDER BY SortOrdinal,ColumnRevisionKey;
SELECT TOP (100001) LEFT(ColumnName,65537) FROM surf.PrimaryKeyColumn
WHERE TableMetadataRevisionKey=@Revision ORDER BY SortOrdinal,KeyColumnKey;
""");
        IndexSql.Add(command, "@Revision", SqlDbType.BigInt, revisionKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Table metadata revision not found.");
        var table = new SqlTable { SchemaName = MetadataString(reader, 0), TableName = MetadataString(reader, 1),
            HasFullData = reader.GetBoolean(2), FullDataRowCount = reader.GetInt64(3),
            FullDataImportedAtUtc = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4) };
        var slice = new DatabaseMetadataSnapshot { Tables = [table] };
        long size = 0;
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (slice.Columns.Count >= MaximumSymbolsPerDocument - 1) throw new IndexDocumentTooLargeException();
            var column = new SqlColumn { SchemaName = MetadataString(reader, 0), TableName = MetadataString(reader, 1),
                ColumnName = MetadataString(reader, 2), DataType = MetadataString(reader, 3), MaxLength = reader.GetInt32(4),
                NumericPrecision = reader.GetByte(5), NumericScale = reader.GetInt32(6), IsNullable = reader.GetBoolean(7),
                IsIdentity = reader.GetBoolean(8), Ordinal = reader.GetInt32(9) };
            size += (long)column.SchemaName.Length + column.TableName.Length + column.ColumnName.Length + column.DataType.Length;
            if (size > 8 * 1024 * 1024) throw new IndexDocumentTooLargeException();
            slice.Columns.Add(column);
        }
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (slice.PrimaryKeys.Count >= MaximumSymbolsPerDocument) throw new IndexDocumentTooLargeException();
            string name = MetadataString(reader, 0);
            size += name.Length;
            if (size > 8 * 1024 * 1024) throw new IndexDocumentTooLargeException();
            slice.PrimaryKeys.Add(new SqlPrimaryKeyColumn { SchemaName = table.SchemaName, TableName = table.TableName, ColumnName = name });
        }
        return (slice, table);
    }
    private static string MetadataString(SqlDataReader reader, int ordinal)
    {
        string value = reader.GetString(ordinal);
        if (value.Length > 65536) throw new IndexDocumentTooLargeException();
        return value;
    }
}

using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    private static async Task WriteSymbolsAsync(SqlConnection connection, SqlTransaction transaction, long documentKey,
        long revisionKey, ImmutableArray<SymbolInput> symbols, CancellationToken token)
    {
        long metadataCharacters = 0;
        foreach (SymbolInput s in symbols)
            metadataCharacters += (long)s.Name.Length + s.QualifiedName.Length + s.Locator.Length + s.ContainerName.Length + s.Language.Length;
        if (metadataCharacters > 16 * 1024 * 1024) throw new IndexDocumentTooLargeException();
        for (int start = 0; start < symbols.Length; start += IndexSql.MaximumBatch)
        {
            var rows = new DataTable();
            string[] strings = ["Name", "QualifiedName", "Locator", "Language", "ContainerName", "NormalizedName", "NormalizedQualifiedName"];
            foreach (string column in new[] { "Ordinal", "Name", "QualifiedName", "Kind", "Locator", "LineNumber", "ColumnNumber",
                "EndLineNumber", "EndColumnNumber", "ParameterCount", "MinimumArgumentCount", "MaximumArgumentCount", "Language",
                "ContainerName", "NormalizedName", "NameHash", "IsAscii", "NormalizedQualifiedName", "QualifiedHash", "QualifiedIsAscii" })
            {
                Type type = strings.Contains(column) ? typeof(string) : column.EndsWith("Hash", StringComparison.Ordinal) ? typeof(byte[]) :
                    column.EndsWith("Ascii", StringComparison.Ordinal) ? typeof(bool) : typeof(int);
                rows.Columns.Add(column, type);
            }
            for (int ordinal = start; ordinal < Math.Min(start + IndexSql.MaximumBatch, symbols.Length); ordinal++)
            {
                SymbolInput s = symbols[ordinal];
                string name = ReferenceMetadata.Normalize(s.Name), qualified = ReferenceMetadata.Normalize(s.QualifiedName);
                rows.Rows.Add(ordinal, s.Name, s.QualifiedName, (int)s.Kind, s.Locator, s.LineNumber, s.ColumnNumber,
                    s.EndLineNumber, s.EndColumnNumber, (object?)s.ParameterCount ?? DBNull.Value,
                    (object?)s.MinimumArgumentCount ?? DBNull.Value, (object?)s.MaximumArgumentCount ?? DBNull.Value,
                    s.Language, s.ContainerName, name, ReferenceMetadata.LookupHash(name), ReferenceMetadata.IsAscii(name),
                    qualified, ReferenceMetadata.LookupHash(qualified), ReferenceMetadata.IsAscii(qualified));
            }
            await using var command = IndexSql.Command(connection, transaction, """
INSERT surf.SymbolDefinition(DocumentKey,DocumentRevisionKey,SortOrdinal,Name,QualifiedName,Kind,Locator,
 LineNumber,ColumnNumber,EndLineNumber,EndColumnNumber,ParameterCount,MinimumArgumentCount,MaximumArgumentCount,Language,ContainerName)
SELECT @Document,@Revision,Ordinal,Name,QualifiedName,Kind,Locator,LineNumber,ColumnNumber,EndLineNumber,EndColumnNumber,
 ParameterCount,MinimumArgumentCount,MaximumArgumentCount,Language,ContainerName FROM @Symbols;
INSERT surf.SymbolNameLookup(SymbolKey,NameOrdinal,NormalizedName,NameHash,IsAscii)
SELECT s.SymbolKey,0,b.NormalizedName,b.NameHash,b.IsAscii FROM @Symbols b
 JOIN surf.SymbolDefinition s ON s.DocumentRevisionKey=@Revision AND s.SortOrdinal=b.Ordinal WHERE DATALENGTH(b.NormalizedName)>0;
INSERT surf.SymbolNameLookup(SymbolKey,NameOrdinal,NormalizedName,NameHash,IsAscii)
SELECT s.SymbolKey,1,b.NormalizedQualifiedName,b.QualifiedHash,b.QualifiedIsAscii FROM @Symbols b
 JOIN surf.SymbolDefinition s ON s.DocumentRevisionKey=@Revision AND s.SortOrdinal=b.Ordinal WHERE DATALENGTH(b.NormalizedQualifiedName)>0;
""");
            IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
            IndexSql.Add(command, "@Revision", SqlDbType.BigInt, revisionKey);
            command.Parameters.Add(new SqlParameter("@Symbols", SqlDbType.Structured) { TypeName = "surf.IndexSymbolBatch", Value = rows });
            using var cancel = RelationalSession.CancelCommand(command, token);
            await command.ExecuteNonQueryAsync(token);
        }
    }
}

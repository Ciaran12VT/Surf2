using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    public async Task<bool> IsContextCurrentAsync(IndexRequestContext context, CancellationToken token = default)
    {
        await using var connection = await OpenReadAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        try
        {
            await IndexSql.CheckContextAsync(connection, transaction, _session, context, token);
            return true;
        }
        catch (IndexGenerationChangedException) { return false; }
    }

    public async Task<IndexDocumentPage> ReadDocumentsPageAsync(IndexRequestContext context, SearchCursor? cursor = null,
        int pageSize = 64, CancellationToken token = default)
    {
        ValidatePage(pageSize);
        cursor ??= new(0, 0);
        if (cursor.AfterDocumentKey < 0 || cursor.AfterScopeResourceKey < 0) throw new ArgumentException("Invalid search cursor.");
        await using var connection = await OpenReadAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, token);
        await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@Take) d.DocumentKey,d.CurrentRevisionKey,d.Kind,LEFT(m.DisplayName,65537),d.Language,
 LEFT(m.Locator,65537),LEFT(m.NodeKey,65537),LEFT(m.ParentNodeKey,65537),m.ScopeResourceKey,
 {IndexSql.FreshnessExpression},p.ContentKey,r.SourceRevisionKey,r.SourceFingerprint,LEFT(f.OriginalPath,65537),
 r.ParserVersion,r.RendererVersion,r.PolicyVersion
FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
JOIN surf.Document d ON d.DocumentKey=m.DocumentKey
LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
LEFT JOIN surf.SearchProjection p ON p.DocumentRevisionKey=r.DocumentRevisionKey
LEFT JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey
{IndexSql.OwnerJoins}
WHERE {IndexSql.ScopePredicate}
 AND (m.ScopeResourceKey>@Resource OR (m.ScopeResourceKey=@Resource AND d.DocumentKey>@Document))
ORDER BY m.ScopeResourceKey,d.DocumentKey;
""");
        IndexSql.ScopeParameters(command, context);
        IndexSql.Add(command, "@Take", SqlDbType.Int, pageSize);
        IndexSql.Add(command, "@Resource", SqlDbType.BigInt, cursor.AfterScopeResourceKey);
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, cursor.AfterDocumentKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = ImmutableArray.CreateBuilder<IndexedDocumentSummary>();
        long chars = 0;
        while (await reader.ReadAsync(token))
        {
            var item = new IndexedDocumentSummary(reader.GetInt64(0), NullableKey(reader, 1), (IndexedDocumentKind)reader.GetInt32(2),
                MetadataString(reader, 3), reader.GetString(4), MetadataString(reader, 5), MetadataString(reader, 6),
                MetadataString(reader, 7), reader.GetInt64(8), (IndexFreshness)reader.GetInt32(9), NullableKey(reader, 10),
                NullableKey(reader, 11), reader.IsDBNull(12) ? null : IndexSql.Hex((byte[])reader[12]),
                reader.IsDBNull(13) ? null : MetadataString(reader, 13), reader.IsDBNull(14) ? null :
                    new(reader.GetString(14), reader.GetString(15), reader.GetString(16)));
            chars += (long)item.DisplayName.Length + item.Locator.Length + item.NodeKey.Length + item.ParentNodeKey.Length + (item.PhysicalPath?.Length ?? 0);
            MetadataBudget(chars);
            items.Add(item);
        }
        SearchCursor next = items.Count == 0 ? cursor : new(items[^1].ScopeResourceKey, items[^1].DocumentKey);
        return new(items.ToImmutable(), next, items.Count < pageSize);
    }

    // Page cursor is the last SQL candidate, including verified false positives.
    // ASCII hashes accelerate ASCII requests; non-ASCII entries remain candidates.
    // Non-ASCII requests scan scoped names rather than risking a collation false negative.
    public async Task<IndexPage<SymbolSummary>> LookupNamesPageAsync(IndexRequestContext context, IEnumerable<string> tokens,
        long afterSymbolKey = 0, int pageSize = 64, CancellationToken token = default)
    {
        ValidatePage(pageSize);
        string[] input = tokens.Take(65).ToArray();
        if (input.Length > 64) throw new ArgumentException("Name batches contain at most 64 tokens.");
        string[] requested = input.Select(ReferenceMetadata.Normalize).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length > 64 || requested.Any(s => s.Length > 65536) || afterSymbolKey < 0) throw new ArgumentException("Invalid name batch.");
        string[] names = requested.SelectMany(s => s.Contains('.', StringComparison.Ordinal) ?
            new[] { s, s.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty } : new[] { s })
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await using var connection = await OpenReadAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, token);
        await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@Take) s.SymbolKey,s.DocumentKey,s.DocumentRevisionKey,LEFT(s.Name,65537),LEFT(s.QualifiedName,65537),s.Kind,
 LEFT(s.Locator,65537),s.LineNumber,s.ColumnNumber,s.EndLineNumber,s.EndColumnNumber,s.ParameterCount,s.MinimumArgumentCount,
 s.MaximumArgumentCount,s.Language,LEFT(s.ContainerName,65537),{IndexSql.FreshnessExpression}
FROM surf.SymbolDefinition s JOIN surf.Document d ON d.DocumentKey=s.DocumentKey AND d.CurrentRevisionKey=s.DocumentRevisionKey
JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=s.DocumentRevisionKey
{IndexSql.OwnerJoins}
WHERE s.SymbolKey>@After AND EXISTS(SELECT 1 FROM surf.ResourceDocument m
 JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey WHERE m.DocumentKey=d.DocumentKey AND {IndexSql.ScopePredicate})
AND EXISTS(SELECT 1 FROM surf.SymbolNameLookup l CROSS JOIN @Names n WHERE l.SymbolKey=s.SymbolKey
 AND (n.IsAscii=0 OR l.IsAscii=0 OR l.NameHash=n.NameHash))
ORDER BY s.SymbolKey;
""");
        IndexSql.ScopeParameters(command, context);
        IndexSql.Add(command, "@Take", SqlDbType.Int, pageSize);
        IndexSql.Add(command, "@After", SqlDbType.BigInt, afterSymbolKey);
        var table = new DataTable();
        table.Columns.Add("Ordinal", typeof(int)); table.Columns.Add("NameHash", typeof(byte[])); table.Columns.Add("IsAscii", typeof(bool));
        for (int i = 0; i < names.Length; i++) table.Rows.Add(i, ReferenceMetadata.LookupHash(names[i]), ReferenceMetadata.IsAscii(names[i]));
        command.Parameters.Add(new SqlParameter("@Names", SqlDbType.Structured) { TypeName = "surf.IndexNameBatch", Value = table });
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = ImmutableArray.CreateBuilder<SymbolSummary>();
        int count = 0;
        long after = afterSymbolKey, chars = 0;
        while (await reader.ReadAsync(token))
        {
            count++;
            after = reader.GetInt64(0);
            var definition = new SymbolInput(MetadataString(reader, 3), MetadataString(reader, 4), (ReferenceEntityKind)reader.GetInt32(5),
                MetadataString(reader, 6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
                NullableInt(reader, 11), NullableInt(reader, 12), NullableInt(reader, 13), reader.GetString(14), MetadataString(reader, 15));
            chars += (long)definition.Name.Length + definition.QualifiedName.Length + definition.Locator.Length + definition.ContainerName.Length;
            MetadataBudget(chars);
            if (names.Any(n => ReferenceMetadata.HasName(definition, n)))
                items.Add(new(after, reader.GetInt64(1), reader.GetInt64(2), definition, (IndexFreshness)reader.GetInt32(16)));
        }
        return new(items.ToImmutable(), after, count < pageSize);
    }

    public async Task<IndexPage<HighlightName>> ReadHighlightNamesPageAsync(IndexRequestContext context,
        long afterSymbolKey = 0, int pageSize = 64, CancellationToken token = default)
    {
        ValidatePage(pageSize);
        if (afterSymbolKey < 0) throw new ArgumentException("Invalid highlight cursor.");
        await using var connection = await OpenReadAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, token);
        await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@Take) s.SymbolKey,LEFT(s.Name,65537),s.Kind
FROM surf.SymbolDefinition s JOIN surf.Document d ON d.DocumentKey=s.DocumentKey AND d.CurrentRevisionKey=s.DocumentRevisionKey
WHERE s.SymbolKey>@After AND s.Kind<>0 AND DATALENGTH(s.Name)>2
AND EXISTS(SELECT 1 FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 WHERE m.DocumentKey=d.DocumentKey AND {IndexSql.ScopePredicate}) ORDER BY s.SymbolKey;
""");
        IndexSql.ScopeParameters(command, context);
        IndexSql.Add(command, "@Take", SqlDbType.Int, pageSize);
        IndexSql.Add(command, "@After", SqlDbType.BigInt, afterSymbolKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = ImmutableArray.CreateBuilder<HighlightName>();
        long after = afterSymbolKey, chars = 0;
        while (await reader.ReadAsync(token))
        {
            after = reader.GetInt64(0);
            string name = MetadataString(reader, 1);
            chars += name.Length; MetadataBudget(chars);
            items.Add(new(name, [(ReferenceEntityKind)reader.GetInt32(2)]));
        }
        return new(items.ToImmutable(), after, items.Count < pageSize);
    }

    public async Task<IndexPage<ResolvedIndexLocator>> ResolveLocatorPageAsync(IndexRequestContext context, string locator,
        long afterLocatorKey = 0, int pageSize = 64, CancellationToken token = default)
    {
        ValidatePage(pageSize);
        if (locator.Length > 65536 || afterLocatorKey < 0) throw new ArgumentException("Invalid locator request.");
        await using var connection = await OpenReadAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await IndexSql.CheckContextAsync(connection, transaction, _session, context, token);
        await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@Take) l.LocatorKey,l.DocumentKey,l.ScopeResourceKey,l.Kind,LEFT(l.OriginalLocator,65537)
FROM surf.DocumentLocator l JOIN surf.Document d ON d.DocumentKey=l.DocumentKey
JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=l.ScopeResourceKey
WHERE {IndexSql.ScopePredicate} AND l.LocatorKey>@After AND (@Ascii=0 OR l.IsAscii=0 OR l.LocatorHash=@Hash)
ORDER BY l.LocatorKey;
""");
        IndexSql.ScopeParameters(command, context);
        IndexSql.Add(command, "@Take", SqlDbType.Int, pageSize);
        IndexSql.Add(command, "@After", SqlDbType.BigInt, afterLocatorKey);
        IndexSql.Add(command, "@Ascii", SqlDbType.Bit, ReferenceMetadata.IsAscii(locator));
        IndexSql.Add(command, "@Hash", SqlDbType.Binary, ReferenceMetadata.LookupHash(locator), 32);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var items = ImmutableArray.CreateBuilder<ResolvedIndexLocator>();
        long after = afterLocatorKey, chars = 0;
        int count = 0;
        while (await reader.ReadAsync(token))
        {
            count++; after = reader.GetInt64(0);
            string found = MetadataString(reader, 4);
            chars += found.Length; MetadataBudget(chars);
            if (string.Equals(locator, found, StringComparison.OrdinalIgnoreCase))
                items.Add(new(after, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), found));
        }
        return new(items.ToImmutable(), after, count < pageSize);
    }

    public async Task<string> ReadRevisionTextAsync(long documentKey, long revisionKey,
        int maximumCharacters = 8 * 1024 * 1024, CancellationToken token = default)
    {
        await using var connection = await OpenReadAsync(token);
        await using var command = IndexSql.Command(connection, null, """
SELECT c.Text FROM surf.DocumentRevision r JOIN surf.TextContent c ON c.ContentKey=r.ContentKey
WHERE r.DocumentKey=@Document AND r.DocumentRevisionKey=@Revision;
""");
        IndexSql.Add(command, "@Document", SqlDbType.BigInt, documentKey);
        IndexSql.Add(command, "@Revision", SqlDbType.BigInt, revisionKey);
        using var cancel = RelationalSession.CancelCommand(command, token);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Document revision not found.");
        using var text = reader.GetTextReader(0);
        return await IndexTextMatcher.ReadBoundedAsync(text, maximumCharacters, token);
    }
    private static void ValidatePage(int take)
    {
        if (take is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(take));
    }
    private static int? NullableInt(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static void MetadataBudget(long chars)
    {
        if (chars > 4 * 1024 * 1024) throw new InvalidOperationException("Metadata page exceeds 8 MiB; use a smaller page or repair the oversized derived entry.");
    }
}

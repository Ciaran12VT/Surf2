using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalReferenceService
{
    // Only one active view is retained. No source bodies, parsed models or providers reach this cache.
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private MetadataCatalogue? _metadata;

    internal async Task<ReferenceCatalogue> RebaseVerifiedViewAsync(ExplorerScope verified,
        ExplorerIndexRefreshProgress proof, CancellationToken ct)
    {
        if (proof.Context == null || !ContextMatches(verified.Context, proof.Context)) throw new IndexGenerationChangedException();
        AcceptCompletedDiscovery(proof);
        await _metadataGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = Volatile.Read(ref _metadata);
            if (cached != null && cached.Context.Epoch == verified.Context.Epoch && cached.Context.ScopeKey == verified.Context.ScopeKey &&
                cached.Context.ScopeVersion == verified.Context.ScopeVersion &&
                cached.Context.SnapshotCatalogueVersion == verified.Context.SnapshotCatalogueVersion &&
                cached.Context.DiagramCatalogueVersion == verified.Context.DiagramCatalogueVersion &&
                cached.Context.RestrictDocumentKeys == verified.Context.RestrictDocumentKeys &&
                cached.Context.UnloadedScopeResourceKeys.SequenceEqual(verified.Context.UnloadedScopeResourceKeys) &&
                cached.Context.DocumentKeys.SequenceEqual(verified.Context.DocumentKeys) &&
                cached.Resources.SequenceEqual(verified.Resources.Select(ResourceSelection.From)) && cached.SortCultureName == verified.SortCultureName)
            {
                var rebased = cached with { Context = verified.Context, Paint = new(verified.Context, cached.Paint) };
                await RequireCurrentAsync(verified.Context, ct).ConfigureAwait(false);
                Volatile.Write(ref _metadata, rebased);
                return new(rebased.Paint, Coverage(rebased));
            }
        }
        finally { _metadataGate.Release(); }
        return await LoadPaintAsync(verified, ct).ConfigureAwait(false);
    }

    private readonly record struct ResourceSelection(long Key, ResourceKind Kind, string Path, bool IncludeChildren, bool IsLoaded)
    {
        internal static ResourceSelection From(ExplorerResource resource) =>
            new(resource.ScopeResourceKey, resource.Kind, resource.Path, resource.IncludeChildren, resource.IsLoaded);
    }
    private sealed record MetadataCatalogue(IndexRequestContext Context, ImmutableArray<ResourceSelection> Resources,
        string SortCultureName, SharedReferencePaintLookup Paint,
        FrozenDictionary<string, ImmutableArray<SymbolSummary>> Candidates, long Documents, long StaleOrUnindexed);

    internal static bool ContextMatches(IndexRequestContext left, IndexRequestContext right) =>
        left.Epoch == right.Epoch && left.ScopeKey == right.ScopeKey &&
        left.CatalogueGeneration == right.CatalogueGeneration && left.ScopeVersion == right.ScopeVersion &&
        left.SnapshotCatalogueVersion == right.SnapshotCatalogueVersion && left.DiagramCatalogueVersion == right.DiagramCatalogueVersion &&
        left.DiscoveryReconciled == right.DiscoveryReconciled && left.RestrictDocumentKeys == right.RestrictDocumentKeys &&
        left.UnloadedScopeResourceKeys.SequenceEqual(right.UnloadedScopeResourceKeys) && left.DocumentKeys.SequenceEqual(right.DocumentKeys);

    private static bool Matches(MetadataCatalogue? cached, ExplorerScope scope) => cached != null &&
        ContextMatches(cached.Context, scope.Context) && cached.SortCultureName == scope.SortCultureName &&
        cached.Resources.SequenceEqual(scope.Resources.Select(ResourceSelection.From));

    private async Task<MetadataCatalogue> GetMetadataAsync(ExplorerScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cached = Volatile.Read(ref _metadata);
        if (Matches(cached, scope)) return cached!;
        await _metadataGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _metadata);
            if (Matches(cached, scope)) return cached!;
            var loaded = await ReadMetadataAsync(scope, ct).ConfigureAwait(false);
            await RequireCurrentAsync(scope.Context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _metadata, loaded);
            return loaded;
        }
        finally { _metadataGate.Release(); }
    }

    private async Task<MetadataCatalogue> ReadMetadataAsync(ExplorerScope scope, CancellationToken ct)
    {
        var context = scope.Context;
        await session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await IndexSql.CheckContextAsync(connection, transaction, session, context, ct).ConfigureAwait(false);
        // Stream bounded candidate metadata once. Coverage is one SQL aggregate in the same fenced
        // transaction, rather than materializing every document in 64-record pages on each click.
        await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@MaximumRows) s.SymbolKey,s.DocumentKey,s.DocumentRevisionKey,LEFT(s.Name,65537),LEFT(s.QualifiedName,65537),s.Kind,
 LEFT(s.Locator,65537),s.LineNumber,s.ColumnNumber,s.EndLineNumber,s.EndColumnNumber,s.ParameterCount,s.MinimumArgumentCount,
 s.MaximumArgumentCount,s.Language,LEFT(s.ContainerName,65537),{IndexSql.FreshnessExpression},LEFT(f.OriginalPath,65537)
FROM surf.SymbolDefinition s JOIN surf.Document d ON d.DocumentKey=s.DocumentKey AND d.CurrentRevisionKey=s.DocumentRevisionKey
JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=s.DocumentRevisionKey
LEFT JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey
{IndexSql.OwnerJoins}
WHERE EXISTS(SELECT 1 FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 WHERE m.DocumentKey=d.DocumentKey AND {IndexSql.ScopePredicate}) ORDER BY s.SymbolKey;

SELECT COUNT_BIG(*),COALESCE(SUM(CONVERT(bigint,CASE WHEN ({IndexSql.FreshnessExpression})<>1 THEN 1 ELSE 0 END)),0)
FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
JOIN surf.Document d ON d.DocumentKey=m.DocumentKey
LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
{IndexSql.OwnerJoins}
WHERE {IndexSql.ScopePredicate};
""");
        IndexSql.ScopeParameters(command, context);
        IndexSql.Add(command, "@MaximumRows", SqlDbType.BigInt, (long)_limits.MaximumMetadataRows + 1);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        var names = new List<PaintName>();
        var candidates = new Dictionary<string, List<SymbolSummary>>(StringComparer.OrdinalIgnoreCase);
        var budget = new ExplorerMetadataBudget(_limits);
        long documents, stale;
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long symbolKey = reader.GetInt64(0), documentKey = reader.GetInt64(1), revisionKey = reader.GetInt64(2);
                string name = MetadataString(reader, 3), qualified = MetadataString(reader, 4);
                var kind = (ReferenceEntityKind)reader.GetInt32(5);
                string locator = MetadataString(reader, 6);
                int line = reader.GetInt32(7), column = reader.GetInt32(8), endLine = reader.GetInt32(9), endColumn = reader.GetInt32(10);
                int? parameterCount = NullableInt(reader, 11), minimum = NullableInt(reader, 12), maximum = NullableInt(reader, 13);
                string language = reader.GetString(14), container = MetadataString(reader, 15);
                var freshness = (IndexFreshness)reader.GetInt32(16);
                string? physical = reader.IsDBNull(17) ? null : MetadataString(reader, 17);
                budget.Add(name, qualified, locator, language, container, physical ?? string.Empty);
                if (name.Length > 1 && kind != ReferenceEntityKind.File && (physical == null || ReferenceAllowed(scope, physical)))
                    names.Add(new(name, language, kind));
                if (!ReferenceAllowed(scope, locator)) continue;
                var symbol = new SymbolSummary(symbolKey, documentKey, revisionKey,
                    new(name, qualified, kind, locator, line, column, endLine, endColumn, parameterCount, minimum, maximum, language, container), freshness);
                AddCandidate(candidates, ReferenceMetadata.Normalize(name), symbol);
                string normalizedQualified = ReferenceMetadata.Normalize(qualified);
                if (!normalizedQualified.Equals(ReferenceMetadata.Normalize(name), StringComparison.OrdinalIgnoreCase))
                    AddCandidate(candidates, normalizedQualified, symbol);
            }
            if (!await reader.NextResultAsync(ct).ConfigureAwait(false) || !await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Reference metadata coverage was not returned.");
            documents = reader.GetInt64(0); stale = reader.GetInt64(1);
            if (documents > _limits.MaximumMetadataRows)
                throw new ExplorerLimitException("Selected reference documents exceed their metadata row budget.");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new(context, scope.Resources.Select(ResourceSelection.From).ToImmutableArray(), scope.SortCultureName, new(context, names, scope.SortCultureName),
            candidates.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.OrdinalIgnoreCase), documents, stale);
    }

    private static void AddCandidate(Dictionary<string, List<SymbolSummary>> candidates, string name, SymbolSummary symbol)
    {
        if (name.Length == 0) return;
        if (!candidates.TryGetValue(name, out var entries)) candidates.Add(name, entries = []);
        entries.Add(symbol);
    }

    private static string MetadataString(SqlDataReader reader, int ordinal)
    {
        string value = reader.GetString(ordinal);
        if (value.Length > 65536) throw new ExplorerLimitException("A reference metadata value exceeds its character limit.");
        return value;
    }
    private static int? NullableInt(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed record PaintName(string Name, string Language, ReferenceEntityKind Kind);
public sealed record PaintStyleKey(string Language, ReferenceEntityKind Kind);
public sealed record ReferenceCoverage(long Documents, long StaleOrUnindexed, bool DiscoveryReconciled, bool IsSubset = false)
{
    public bool FullyPublished => DiscoveryReconciled && StaleOrUnindexed == 0 && !IsSubset;
}
public sealed record ReferenceCatalogue(SharedReferencePaintLookup Paint, ReferenceCoverage Coverage);
public sealed record ReferenceQuery(string Token, int? ArgumentCount = null);
public sealed record ReferenceTargets(ReferenceQuery Query, ImmutableArray<SymbolSummary> Candidates);
public sealed record ReferenceResolutionBatch(ImmutableArray<ReferenceTargets> Targets, ReferenceCoverage Coverage);
public sealed class ReferenceIndexNotReadyException(ReferenceCoverage coverage) : InvalidOperationException(
    "Derived references are unindexed, stale, failed or cover only a subset. No-target results are not authoritative.")
{
    public ReferenceCoverage Coverage { get; } = coverage;
}

// Construct once off the dispatcher, share by reference, then replace atomically at the owner.
// Painting cannot reach a provider, parser, task, file, or WPF object through this object.
public sealed class SharedReferencePaintLookup
{
    public SharedReferencePaintLookup(IndexRequestContext context, IEnumerable<PaintName> names, string sortCultureName)
    {
        Context = context;
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(sortCultureName), false);
        Names = names.Where(n => n.Name.Length > 1 && n.Kind != ReferenceEntityKind.File)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.Select(n => new PaintStyleKey(n.Language, n.Kind))
                .DistinctBy(k => (k.Language.ToUpperInvariant(), k.Kind))
                .OrderBy(k => KindRank(k.Kind)).ThenBy(k => k.Language, comparer).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
    }
    public const string StylePolicyVersion = "legacy-language-kind-v1";
    public IndexRequestContext Context { get; }
    public FrozenDictionary<string, ImmutableArray<PaintStyleKey>> Names { get; }
    public bool TryGetStyle(string identifier, string? preferredLanguage, out PaintStyleKey? style)
    {
        style = null;
        if (!Names.TryGetValue(identifier, out var kinds)) return false;
        if (!string.IsNullOrEmpty(preferredLanguage))
            foreach (var kind in kinds)
                if (kind.Language.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase)) { style = kind; return true; }
        style = kinds[0];
        return true;
    }
    internal static int KindRank(ReferenceEntityKind kind) => kind switch
    {
        ReferenceEntityKind.Class => 0, ReferenceEntityKind.Interface => 1, ReferenceEntityKind.Struct => 2,
        ReferenceEntityKind.Enum => 3, ReferenceEntityKind.Delegate => 4, ReferenceEntityKind.Method => 5,
        ReferenceEntityKind.Function => 6, ReferenceEntityKind.StoredProcedure => 7, ReferenceEntityKind.View => 8,
        ReferenceEntityKind.Trigger => 9, ReferenceEntityKind.Table => 10, ReferenceEntityKind.Field => 11, _ => 99
    };
}

public sealed class RelationalReferenceService(RelationalSession session, RelationalIndexStore index,
    ExplorerLimits? limits = null)
{
    private readonly ExplorerLimits _limits = Validated(limits);
    private static ExplorerLimits Validated(ExplorerLimits? limits) { var value = limits ?? new(); value.Validate(); return value; }

    public async Task<ReferenceCatalogue> LoadPaintAsync(ExplorerScope scope, CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var names = new List<PaintName>();
        var budget = new ExplorerMetadataBudget(_limits);
        long after = 0;
        bool exhausted;
        do
        {
            var page = await ReadPaintPageAsync(scope, after, ct).ConfigureAwait(false);
            foreach (var name in page.Items)
            {
                budget.Add(name.Name, name.Language);
                names.Add(name);
            }
            after = page.AfterKey; exhausted = page.Exhausted;
        } while (!exhausted);
        var coverage = await ReadCoverageAsync(scope.Context, ct).ConfigureAwait(false);
        await RequireCurrentAsync(scope.Context, ct).ConfigureAwait(false);
        return new(new(scope.Context, names, scope.SortCultureName), coverage);
    }

    // All candidate pages are completed before applying overload and fallback rules.
    // Oversized candidate sets fail explicitly; they never become a truncated navigation result.
    public async Task<ImmutableArray<ReferenceTargets>> ResolveAsync(IndexRequestContext context,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var scope = await new RelationalExplorerMetadataQueries(session, index).ReadScopeAsync(context.ScopeKey, null,
            CultureInfo.CurrentCulture.Name, ct).ConfigureAwait(false);
        // Keep the caller's generation/subset/unloaded fence, not the newer capture made by the summary reader.
        scope = scope with { Context = context, Resources = scope.Resources.Select(r => r with
            { IsLoaded = !context.UnloadedScopeResourceKeys.Contains(r.ScopeResourceKey) }).ToImmutableArray() };
        return await ResolveAsync(scope, queries, ct).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<ReferenceTargets>> ResolveAsync(ExplorerScope scope,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default)
    {
        var result = await ResolveWithCoverageAsync(scope, queries, ct).ConfigureAwait(false);
        if (!result.Coverage.FullyPublished) throw new ReferenceIndexNotReadyException(result.Coverage);
        return result.Targets;
    }

    // Consumers that deliberately display provisional/loaded-subset results must carry this coverage to their UI.
    public async Task<ReferenceResolutionBatch> ResolveWithCoverageAsync(ExplorerScope scope,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var context = scope.Context;
        var input = queries.Take(65).ToImmutableArray();
        if (input.Length > 64) throw new ArgumentException("Reference requests contain at most 64 tokens.", nameof(queries));
        var coverage = await ReadCoverageAsync(context, ct).ConfigureAwait(false);
        var candidates = new List<SymbolSummary>();
        var budget = new ExplorerMetadataBudget(_limits);
        long after = 0;
        IndexPage<SymbolSummary> page;
        do
        {
            page = await index.LookupNamesPageAsync(context, input.Select(q => q.Token), after, _limits.PageSize, ct).ConfigureAwait(false);
            foreach (var symbol in page.Items)
            {
                var d = symbol.Definition;
                budget.Add(d.Name, d.QualifiedName, d.Locator, d.Language, d.ContainerName);
                if (!ReferenceAllowed(scope, d.Locator)) continue;
                candidates.Add(symbol);
            }
            after = page.AfterKey;
        } while (!page.Exhausted);
        await RequireCurrentAsync(context, ct).ConfigureAwait(false);
        return new(input.Select(q => new ReferenceTargets(q, ReferenceMetadata.Resolve(candidates, q.Token, q.ArgumentCount))).ToImmutableArray(), coverage);
    }

    private async Task<ReferenceCoverage> ReadCoverageAsync(IndexRequestContext context, CancellationToken ct)
    {
        long documents = 0, stale = 0; SearchCursor? cursor = null; bool exhausted;
        do
        {
            var page = await index.ReadDocumentsPageAsync(context, cursor, _limits.PageSize, ct).ConfigureAwait(false);
            documents += page.Items.Length; stale += page.Items.Count(d => d.Freshness != IndexFreshness.Indexed);
            cursor = page.Next; exhausted = page.Exhausted;
        } while (!exhausted);
        return new(documents, stale, context.DiscoveryReconciled, context.RestrictDocumentKeys || context.UnloadedScopeResourceKeys.Length != 0);
    }

    private async Task<IndexPage<PaintName>> ReadPaintPageAsync(ExplorerScope scope, long after, CancellationToken ct)
    {
        var context = scope.Context;
        await session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await IndexSql.CheckContextAsync(connection, transaction, session, context, ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@Take) s.SymbolKey,LEFT(s.Name,65537),s.Language,s.Kind,LEFT(f.OriginalPath,65537)
FROM surf.SymbolDefinition s JOIN surf.Document d ON d.DocumentKey=s.DocumentKey AND d.CurrentRevisionKey=s.DocumentRevisionKey
LEFT JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey
WHERE s.SymbolKey>@After AND s.Kind<>0 AND DATALENGTH(s.Name)>2
AND EXISTS(SELECT 1 FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 WHERE m.DocumentKey=d.DocumentKey AND {IndexSql.ScopePredicate}) ORDER BY s.SymbolKey;
""");
        IndexSql.ScopeParameters(command, context);
        IndexSql.Add(command, "@Take", SqlDbType.Int, _limits.PageSize);
        IndexSql.Add(command, "@After", SqlDbType.BigInt, after);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<PaintName>();
        var budget = new ExplorerMetadataBudget(_limits);
        int read = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            after = reader.GetInt64(0);
            read++;
            string name = reader.GetString(1), language = reader.GetString(2);
            if (name.Length > 65536) throw new ExplorerLimitException("A reference name exceeds its metadata limit.");
            budget.Add(name, language);
            if (!reader.IsDBNull(4))
            {
                string path = reader.GetString(4); budget.Add(path);
                if (path.Length > 65536) throw new ExplorerLimitException("A reference locator exceeds its metadata limit.");
                if (!ReferenceAllowed(scope, path)) continue;
            }
            items.Add(new(name, language, (ReferenceEntityKind)reader.GetInt32(3)));
        }
        return new(items.ToImmutable(), after, read < _limits.PageSize);
    }
    internal static bool ReferenceAllowed(ExplorerScope scope, string locator) =>
        locator.StartsWith("db://", StringComparison.OrdinalIgnoreCase) || locator.StartsWith("surf2://", StringComparison.OrdinalIgnoreCase) ||
        scope.Resources.Any(r => r.IsLoaded && RelationalScopeIndexRefresher.OwnsFile(r, locator) &&
            RelationalScopeIndexRefresher.ReferenceFileEligible(r, locator));
    private async Task RequireCurrentAsync(IndexRequestContext context, CancellationToken ct)
    {
        if (!await index.IsContextCurrentAsync(context, ct).ConfigureAwait(false)) throw new IndexGenerationChangedException();
    }
}

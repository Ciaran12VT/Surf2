using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
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
    internal SharedReferencePaintLookup(IndexRequestContext context, SharedReferencePaintLookup previous)
    { Context = context; Names = previous.Names; }
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

public sealed partial class RelationalReferenceService(RelationalSession session, RelationalIndexStore index,
    ExplorerLimits? limits = null)
{
    private readonly ExplorerLimits _limits = Validated(limits);
    private readonly object _discoveryGate = new();
    private IndexRequestContext? _completedDiscovery;
    private static ExplorerLimits Validated(ExplorerLimits? limits)
    {
        // Candidate addresses/overloads are larger than explorer row labels; retain a bounded compact
        // catalogue without reinstating a cache of code bodies or syntax trees.
        var value = limits ?? new ExplorerLimits(MaximumMetadataCharacters: 16 * 1024 * 1024);
        value.Validate(); return value;
    }

    // An unloaded-resource view cannot publish a global SQL discovery marker. Retain only the
    // worker's completed, exact view; a different selection/generation or a new runtime must recheck it.
    internal void AcceptCompletedDiscovery(ExplorerIndexRefreshProgress result)
    {
        if (!result.FullyPublished || result.Context is not { } context || context.Epoch != session.Epoch ||
            context.RestrictDocumentKeys || result.UnloadedResources != context.UnloadedScopeResourceKeys.Length)
            throw new InvalidOperationException("Reference discovery requires a completed active-scope view.");
        lock (_discoveryGate) _completedDiscovery = context;
    }

    internal bool IsDiscoveryReady(IndexRequestContext context)
    {
        if (context.RestrictDocumentKeys) return false;
        if (context.UnloadedScopeResourceKeys.IsEmpty) return context.DiscoveryReconciled;
        lock (_discoveryGate)
        {
            var completed = _completedDiscovery;
            return completed != null && completed.Epoch == context.Epoch && completed.ScopeKey == context.ScopeKey &&
                completed.CatalogueGeneration == context.CatalogueGeneration && completed.ScopeVersion == context.ScopeVersion &&
                completed.SnapshotCatalogueVersion == context.SnapshotCatalogueVersion && completed.DiagramCatalogueVersion == context.DiagramCatalogueVersion &&
                completed.DiscoveryReconciled == context.DiscoveryReconciled &&
                completed.UnloadedScopeResourceKeys.SequenceEqual(context.UnloadedScopeResourceKeys);
        }
    }

    public async Task<ReferenceCatalogue> LoadPaintAsync(ExplorerScope scope, CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var catalogue = await GetMetadataAsync(scope, ct).ConfigureAwait(false);
        await RequireCurrentAsync(scope.Context, ct).ConfigureAwait(false);
        return new(catalogue.Paint, Coverage(catalogue));
    }

    // Complete, bounded candidate metadata is shared with painting before applying overload rules.
    // Oversized candidate sets fail explicitly; they never become a truncated navigation result.
    public async Task<ImmutableArray<ReferenceTargets>> ResolveAsync(IndexRequestContext context,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var input = ValidateQueries(queries);
        var cached = Volatile.Read(ref _metadata);
        if (cached != null && ContextMatches(cached.Context, context))
        {
            await RequireCurrentAsync(context, ct).ConfigureAwait(false);
            var coverage = Coverage(cached);
            if (!coverage.FullyPublished) throw new ReferenceIndexNotReadyException(coverage);
            return Resolve(cached, input, ct);
        }
        var scope = await new RelationalExplorerMetadataQueries(session, index).ReadScopeAsync(context.ScopeKey, null,
            CultureInfo.CurrentCulture.Name, ct).ConfigureAwait(false);
        // Keep the caller's generation/subset/unloaded fence, not the newer capture made by the summary reader.
        scope = scope with { Context = context, Resources = scope.Resources.Select(r => r with
            { IsLoaded = !context.UnloadedScopeResourceKeys.Contains(r.ScopeResourceKey) }).ToImmutableArray() };
        return await ResolveAsync(scope, input, ct).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<ReferenceTargets>> ResolveAsync(ExplorerScope scope,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default)
    {
        var result = await ResolveWithCoverageAsync(scope, queries, ct).ConfigureAwait(false);
        if (!result.Coverage.FullyPublished) throw new ReferenceIndexNotReadyException(result.Coverage);
        return result.Targets;
    }

    // Consumers that deliberately display provisional/loaded-subset results must carry this coverage to their UI.
    public Task<ReferenceResolutionBatch> ResolveWithCoverageAsync(ExplorerScope scope,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default) =>
        ResolveWithCoverageAsync(scope, queries, verifiedOnly: false, ct);

    // Navigation can use a current positive target before every other document has been indexed.
    // Coverage remains explicit: an empty result here is not necessarily an authoritative miss.
    public Task<ReferenceResolutionBatch> ResolveVerifiedWithCoverageAsync(ExplorerScope scope,
        IEnumerable<ReferenceQuery> queries, CancellationToken ct = default) =>
        ResolveWithCoverageAsync(scope, queries, verifiedOnly: true, ct);

    private async Task<ReferenceResolutionBatch> ResolveWithCoverageAsync(ExplorerScope scope,
        IEnumerable<ReferenceQuery> queries, bool verifiedOnly, CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var context = scope.Context;
        var input = ValidateQueries(queries);
        var catalogue = await GetMetadataAsync(scope, ct).ConfigureAwait(false);
        await RequireCurrentAsync(context, ct).ConfigureAwait(false);
        return new(Resolve(catalogue, input, ct, verifiedOnly), Coverage(catalogue));
    }

    private ReferenceCoverage Coverage(MetadataCatalogue catalogue) =>
        new(catalogue.Documents, catalogue.StaleOrUnindexed, IsDiscoveryReady(catalogue.Context), catalogue.Context.RestrictDocumentKeys);

    private static ImmutableArray<ReferenceQuery> ValidateQueries(IEnumerable<ReferenceQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var input = queries.Take(65).ToImmutableArray();
        if (input.Length > 64) throw new ArgumentException("Reference requests contain at most 64 tokens.", nameof(queries));
        if (input.Any(q => q == null || q.Token == null || q.Token.Length > 65536))
            throw new ArgumentException("Invalid reference token.", nameof(queries));
        return input;
    }

    private static ImmutableArray<ReferenceTargets> Resolve(MetadataCatalogue catalogue,
        ImmutableArray<ReferenceQuery> queries, CancellationToken ct, bool verifiedOnly = false)
    {
        var result = ImmutableArray.CreateBuilder<ReferenceTargets>(queries.Length);
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            string key = ReferenceMetadata.Normalize(query.Token);
            if (!catalogue.Candidates.TryGetValue(key, out var candidates) && key.Contains('.', StringComparison.Ordinal))
            {
                key = key.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                catalogue.Candidates.TryGetValue(key, out candidates);
            }
            IEnumerable<SymbolSummary> eligible = candidates.IsDefault ? [] : candidates;
            if (verifiedOnly) eligible = eligible.Where(s => s.Freshness == IndexFreshness.Indexed);
            result.Add(new(query, ReferenceMetadata.Resolve(eligible, query.Token, query.ArgumentCount)));
        }
        return result.ToImmutable();
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

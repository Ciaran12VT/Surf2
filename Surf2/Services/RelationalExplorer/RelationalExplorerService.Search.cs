using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Surf2.Models;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalExplorerService
{
    private sealed record Candidate(ExplorerNodeSummary Node, ImmutableArray<ExplorerNodeSummary> Ancestors,
        IndexScanStatus? TraversalFailure = null)
    {
        internal string EvaluationKey => Node.OccurrenceKey + (TraversalFailure.HasValue ? ":traversal" : ":source");
    }
    private sealed record Evaluation(bool Matched, bool ContentMatched, ImmutableArray<int> Columns,
        IndexScanStatus Status, IndexFreshness Freshness, bool CurrentSourceEvaluated);

    public async IAsyncEnumerable<ExplorerSearchBatch> SearchAsync(ExplorerSearchRequest request, IExplorerSearchSources sources,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<(int Part, ExplorerSearchBatch Batch)>(new BoundedChannelOptions(4)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        var progress = new ExplorerSearchCoverage?[2];
        var producers = new[] { RunPartAsync(0), RunPartAsync(1) };
        var completion = CompleteAsync();
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                progress[item.Part] = item.Batch.Coverage;
                var values = progress.OfType<ExplorerSearchCoverage>().ToArray();
                var coverage = new ExplorerSearchCoverage(values.Sum(value => value.Considered), values.Sum(value => value.Matched),
                    values.Sum(value => value.Incomplete), values.Sum(value => value.IndexStaleOrUnindexed),
                    values.All(value => value.DiscoveryReconciled), values.Length == 2 && values.All(value => value.Completed),
                    values.Any(value => value.GenerationChanged), values.Any(value => value.IsSubset),
                    values.Any(value => value.PhysicalDiscoveryNotFrozen));
                yield return item.Batch with { Coverage = coverage };
                if (coverage.GenerationChanged) yield break;
            }
        }
        finally
        {
            lifetime.Cancel();
            await completion.ConfigureAwait(false);
        }

        async Task RunPartAsync(int part)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            try
            {
                await foreach (var batch in SearchPartAsync(request, sources, part == 0, lifetime.Token).ConfigureAwait(false))
                    await channel.Writer.WriteAsync((part, batch), lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Publish the first failure before cancellation can produce secondary errors in the other worker.
                channel.Writer.TryComplete(error);
                lifetime.Cancel();
                throw;
            }
        }
        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(producers).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                lifetime.Cancel();
                channel.Writer.TryComplete(error);
            }
        }
    }

    private async IAsyncEnumerable<ExplorerSearchBatch> SearchPartAsync(ExplorerSearchRequest request, IExplorerSearchSources sources,
        bool physicalPart, [EnumeratorCancellation] CancellationToken ct)
    {
        if (request.RequestIdentity == Guid.Empty) throw new ArgumentException("Search requests require an owner identity.", nameof(request));
        if (request.Target is not (IndexSearchTarget.Name or IndexSearchTarget.Content)) throw new ArgumentException("Invalid search target.");
        if (request.Scope.Context.RestrictDocumentKeys) throw new ArgumentException("Explorer traversal requires a scope context; use Index.SearchAsync for document-key subsets.");
        var matcher = new IndexTextMatcher(request.Query, request.UseRegex, request.RegexTimeout);
        long considered = 0, matched = 0, incomplete = 0, unpublished = 0;
        bool changed = false, finished = false;
        ExplorerSearchCoverage Coverage(bool completed) => new(considered, matched, incomplete, unpublished,
            request.Scope.Context.DiscoveryReconciled, completed, changed, request.Scope.Context.RestrictDocumentKeys,
            request.Scope.Resources.Any(r => r.Kind == ResourceKind.Folder && r.IsLoaded));
        await using var candidates = WalkSearchAsync(request.Scope, physicalPart, ct).GetAsyncEnumerator(ct);
        while (!finished && !changed)
        {
            var page = ImmutableArray.CreateBuilder<Candidate>();
            while (page.Count < _limits.PageSize)
            {
                bool next = false;
                try { next = await candidates.MoveNextAsync().ConfigureAwait(false); }
                catch (IndexGenerationChangedException) { changed = true; }
                if (changed) break;
                if (!next) { finished = true; break; }
                page.Add(candidates.Current);
            }
            if (changed) break;
            var hits = ImmutableArray.CreateBuilder<ExplorerSearchHit>();
            var outcomes = ImmutableArray.CreateBuilder<ExplorerSearchOutcome>();
            ImmutableDictionary<string, Evaluation> evaluations = ImmutableDictionary<string, Evaluation>.Empty;
            try
            {
                evaluations = await EvaluatePageAsync(request, sources, matcher, page.ToImmutable(), ct).ConfigureAwait(false);
            }
            catch (IndexGenerationChangedException) { changed = true; }
            if (changed) break; // Drop unpublished page hits; the owner must invalidate earlier provisional batches too.
            foreach (var candidate in page)
            {
                var value = evaluations[candidate.EvaluationKey];
                if (candidate.TraversalFailure == null) considered++;
                if (value.Status is not (IndexScanStatus.Matched or IndexScanStatus.NotMatched)) incomplete++;
                if (candidate.TraversalFailure == null && IsDocument(candidate.Node) && value.Freshness != IndexFreshness.Indexed) unpublished++;
                outcomes.Add(new(candidate.Node.OccurrenceKey, value.Status, value.Freshness, value.CurrentSourceEvaluated));
                if (!value.Matched) continue;
                matched++;
                hits.Add(new(candidate.Node, candidate.Ancestors, value.ContentMatched, value.Columns,
                    request.Query, request.UseRegex, value.Freshness, value.CurrentSourceEvaluated));
            }
            ct.ThrowIfCancellationRequested();
            yield return new(request.RequestIdentity, hits.ToImmutable(), outcomes.ToImmutable(), Coverage(false));
        }
        if (!changed)
        {
            try { await RequireSearchRequestCurrentAsync(request, ct).ConfigureAwait(false); }
            catch (IndexGenerationChangedException) { changed = true; }
        }
        ct.ThrowIfCancellationRequested();
        yield return new(request.RequestIdentity, [], [], Coverage(finished && !changed));
    }

    private async Task<ImmutableDictionary<string, Evaluation>> EvaluatePageAsync(ExplorerSearchRequest request,
        IExplorerSearchSources sources, IndexTextMatcher matcher, ImmutableArray<Candidate> page, CancellationToken ct)
    {
        var result = ImmutableDictionary.CreateBuilder<string, Evaluation>(StringComparer.Ordinal);
        var documents = page.Where(c => c.TraversalFailure == null && IsDocument(c.Node))
            .Select(c => c.Node).ToImmutableArray();
        ImmutableArray<ExplorerIndexBinding> bindings = [];
        var indexed = new Dictionary<(long Resource, long Document), SearchSourceOutcome>();
        var currentScans = new Dictionary<string, ExplorerSearchOutcome>(StringComparer.Ordinal);
        if (request.Target == IndexSearchTarget.Content && sources is IExplorerCurrentContentSearch currentSources)
        {
            foreach (var outcome in await currentSources.SearchDefinitionsAsync(request.Scope, documents, matcher,
                         _limits.MaximumDocumentCharacters, ct).ConfigureAwait(false))
                currentScans[outcome.OccurrenceKey] = outcome;
        }
        else if (request.Target == IndexSearchTarget.Content && !documents.IsEmpty)
        {
            try { bindings = await sources.BindIndexDocumentsAsync(request.Scope, documents, ct).ConfigureAwait(false); }
            catch (ExplorerLimitException) { bindings = []; } // Exact current-source scans remain available without a derived binding.
            var searchableKeys = bindings.Where(b => documents.Any(n => n.OccurrenceKey == b.OccurrenceKey && n.Category != ExplorerCategory.Tables))
                .Select(b => b.DocumentKey).Distinct().ToImmutableArray();
            if (request.Target == IndexSearchTarget.Content && !searchableKeys.IsEmpty)
            {
                var context = request.Scope.Context with { DocumentKeys = searchableKeys, RestrictDocumentKeys = true };
                bool completed = false;
                await foreach (var batch in sources.SearchIndexAsync(new(context, request.Query, request.UseRegex,
                    IndexSearchTarget.Content, _limits.PageSize, _limits.MaximumDocumentCharacters, request.RegexTimeout), ct).ConfigureAwait(false))
                {
                    if (batch.Coverage.GenerationChanged) throw new IndexGenerationChangedException();
                    foreach (var outcome in batch.Outcomes)
                        if (bindings.Any(b => b.DocumentKey == outcome.DocumentKey && b.ScopeResourceKey == outcome.ScopeResourceKey))
                            indexed[(outcome.ScopeResourceKey, outcome.DocumentKey)] = outcome;
                    completed |= batch.Coverage.Completed;
                }
                if (!completed) throw new IndexGenerationChangedException();
            }
        }
        foreach (var candidate in page)
        {
            ct.ThrowIfCancellationRequested();
            var node = candidate.Node;
            var binding = bindings.FirstOrDefault(b => b.OccurrenceKey == node.OccurrenceKey);
            var freshness = binding?.Freshness ?? IndexFreshness.Unindexed;
            Evaluation value;
            if (candidate.TraversalFailure.HasValue)
                value = new(false, false, [], candidate.TraversalFailure.Value, IndexFreshness.Stale, false);
            else
            {
                try
                {
                    if (request.Target == IndexSearchTarget.Name)
                    {
                        bool found = matcher.IsMatch(node.MatchName);
                        value = new(found, false, [], node.Availability == ExplorerAvailability.Missing ? IndexScanStatus.Missing :
                            found ? IndexScanStatus.Matched : IndexScanStatus.NotMatched, freshness, node.Availability != ExplorerAvailability.Missing);
                    }
                    else if (!IsDocument(node)) value = new(false, false, [], IndexScanStatus.NotMatched, IndexFreshness.Indexed, true);
                    else if (currentScans.TryGetValue(node.OccurrenceKey, out var currentScan))
                        value = new(currentScan.Status == IndexScanStatus.Matched, currentScan.Status == IndexScanStatus.Matched,
                            [], currentScan.Status, currentScan.IndexFreshness, currentScan.CurrentSourceEvaluated);
                    else if (node.Category == ExplorerCategory.Tables)
                    {
                        var table = sources is IExplorerCurrentContentSearch currentTables
                            ? await currentTables.SearchTableAsync(request.Scope, node, matcher, _limits.MaximumDocumentCharacters, ct).ConfigureAwait(false)
                            : await sources.SearchTableAsync(node, matcher, _limits.MaximumDocumentCharacters, ct).ConfigureAwait(false);
                        value = new(table.CodeMatched || !table.MatchingColumns.IsEmpty, table.CodeMatched, table.MatchingColumns,
                            table.Status, freshness, table.CurrentSourceEvaluated);
                    }
                    else if (binding != null && indexed.TryGetValue((binding.ScopeResourceKey, binding.DocumentKey), out var scan) &&
                        scan.Status is IndexScanStatus.Matched or IndexScanStatus.NotMatched &&
                        (RelationalExplorerSearchSources.IsPhysicalFile(node) || scan.Freshness == IndexFreshness.Indexed))
                        value = new(scan.Status == IndexScanStatus.Matched, scan.Status == IndexScanStatus.Matched, [], scan.Status, scan.Freshness, true);
                    else if (binding != null && indexed.TryGetValue((binding.ScopeResourceKey, binding.DocumentKey), out var failed) &&
                        (RelationalExplorerSearchSources.IsPhysicalFile(node) || failed.Freshness == IndexFreshness.Indexed))
                        value = new(false, false, [], failed.Status, failed.Freshness, false);
                    else
                    {
                        string text = await sources.ReadCurrentContentAsync(node, _limits.MaximumDocumentCharacters, ct).ConfigureAwait(false);
                        if (text.Length > _limits.MaximumDocumentCharacters) throw new IndexDocumentTooLargeException();
                        bool found = matcher.IsMatch(text);
                        value = new(found, found, [], node.Availability == ExplorerAvailability.Missing ? IndexScanStatus.Missing :
                            found ? IndexScanStatus.Matched : IndexScanStatus.NotMatched, freshness, node.Availability != ExplorerAvailability.Missing);
                    }
                }
                catch (Exception ex) when (RelationalExplorerSearchSources.SearchFailure(ex) != null)
                { value = new(false, false, [], RelationalExplorerSearchSources.SearchFailure(ex)!.Value, freshness, false); }
            }
            result[candidate.EvaluationKey] = value;
        }
        return result.ToImmutable();
    }

    private Task RequireSearchRequestCurrentAsync(ExplorerSearchRequest request, CancellationToken ct) =>
        RequireCurrentAsync(request.Scope, ct);

    private async IAsyncEnumerable<Candidate> WalkSearchAsync(ExplorerScope scope, bool physicalPart,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        var roots = await ReadRootLevelAsync(scope, ct).ConfigureAwait(false);
        foreach (var root in roots.Where(root => (root.ResourceKind is ResourceKind.File or ResourceKind.Folder) == physicalPart))
            await foreach (var candidate in WalkNodeAsync(scope, root, [], 0, _limits, ct).ConfigureAwait(false)) yield return candidate;
    }

    private async IAsyncEnumerable<Candidate> WalkNodeAsync(ExplorerScope scope, ExplorerNodeSummary node,
        ImmutableArray<ExplorerNodeSummary> ancestors, long retainedCharacters, ExplorerLimits limits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!node.IsScopeResourceLoaded || node.ScopeResourceKey.HasValue && scope.Context.UnloadedScopeResourceKeys.Contains(node.ScopeResourceKey.Value)) yield break;
        if (node.Role == ExplorerNodeRole.DiagramRoot && !scope.Resources.Any(r => r.Kind == ResourceKind.Diagram && r.IsLoaded)) yield break;
        if (node.Availability != ExplorerAvailability.Present && node.Role != ExplorerNodeRole.Diagram)
        {
            yield return new(node, ancestors, node.Availability switch
            {
                ExplorerAvailability.Missing => IndexScanStatus.Missing,
                ExplorerAvailability.Inaccessible => IndexScanStatus.Inaccessible, _ => IndexScanStatus.Changed
            });
            yield break;
        }
        if (node.Role is not (ExplorerNodeRole.Category or ExplorerNodeRole.VirtualFolder)) yield return new(node, ancestors);
        if (!node.IsDirectory) yield break;
        ImmutableArray<ExplorerNodeSummary> children = [];
        IndexScanStatus? failure = null;
        if (ancestors.Length >= limits.MaximumDepth || node.IsReparsePoint) failure = IndexScanStatus.TooLarge;
        else
        {
            try
            {
                children = await ReadNaturalChildrenAsync(scope, node, ct, limits).ConfigureAwait(false);
                retainedCharacters = checked(retainedCharacters + children.Sum(n => (long)n.Name.Length + n.FullPath.Length + n.NodeKey.Length));
                if (retainedCharacters > limits.MaximumMetadataCharacters) throw new ExplorerLimitException("Active search ancestry exceeds its metadata budget.");
            }
            catch (Exception ex) when (RelationalExplorerSearchSources.SearchFailure(ex) != null)
            { failure = RelationalExplorerSearchSources.SearchFailure(ex); }
        }
        if (failure.HasValue) { yield return new(node, ancestors, failure); yield break; }
        var path = ancestors.Add(node);
        foreach (var child in children)
            await foreach (var candidate in WalkNodeAsync(scope, child, path, retainedCharacters, limits, ct).ConfigureAwait(false)) yield return candidate;
    }
    private static bool IsDocument(ExplorerNodeSummary node) => node.Role is ExplorerNodeRole.DatabaseDocument or ExplorerNodeRole.Diagram ||
        RelationalExplorerSearchSources.IsPhysicalFile(node);
}

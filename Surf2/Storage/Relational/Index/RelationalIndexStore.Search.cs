using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Surf2.Storage.Relational.Index;

public sealed partial class RelationalIndexStore
{
    // Back-pressure is enumeration itself: one metadata page and one source buffer.
    // No LIKE/full-text/RE2 prefilter and no per-chunk regex evaluation.
    public async IAsyncEnumerable<IndexSearchBatch> SearchAsync(IndexSearchRequest request,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        ValidatePage(request.PageSize);
        if (request.MaximumDocumentCharacters is < 1 or > RelationalContentStore.MaximumSingleContentBytes / 2)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumDocumentCharacters));
        if (request.Target is not (IndexSearchTarget.Name or IndexSearchTarget.Content)) throw new ArgumentException("Invalid search target.");
        var matcher = new IndexTextMatcher(request.Query, request.UseRegex, request.RegexTimeout);
        long considered = 0, scanned = 0, matched = 0, incomplete = 0, stale = 0, delegated = 0;
        SearchCursor cursor = new(0, 0);
        bool exhausted = false, changed = false;
        SearchCoverage Coverage(bool completed) => new(considered, scanned, matched, incomplete, stale, delegated,
            request.Context.DiscoveryReconciled, request.Context.RestrictDocumentKeys, completed, changed);
        while (!exhausted)
        {
            token.ThrowIfCancellationRequested();
            IndexDocumentPage? page = null;
            try { page = await ReadDocumentsPageAsync(request.Context, cursor, request.PageSize, token).ConfigureAwait(false); }
            catch (IndexGenerationChangedException) { changed = true; }
            if (changed) break;
            var hits = ImmutableArray.CreateBuilder<SearchHit>();
            var outcomes = ImmutableArray.CreateBuilder<SearchSourceOutcome>();
            foreach (IndexedDocumentSummary source in page!.Items)
            {
                token.ThrowIfCancellationRequested();
                considered++;
                var result = await ScanOneAsync(source, request, matcher, token).ConfigureAwait(false);
                if (result.Status is IndexScanStatus.Matched or IndexScanStatus.NotMatched) scanned++;
                else if (result.Status == IndexScanStatus.Delegated) delegated++;
                else incomplete++;
                if (result.Freshness != IndexFreshness.Indexed) stale++;
                outcomes.Add(new(source.DocumentKey, source.ScopeResourceKey, result.Status, result.Freshness));
                if (result.Status == IndexScanStatus.Matched)
                {
                    matched++;
                    hits.Add(new(source.DocumentKey, source.RevisionKey, source.ScopeResourceKey, source.DisplayName,
                        source.Locator, source.NodeKey, source.ParentNodeKey, result.Freshness,
                        result.EvaluatedFingerprint, result.AuthoritativeFileRead));
                }
            }
            if (!await IsContextCurrentAsync(request.Context, token).ConfigureAwait(false))
            {
                changed = true;
                break; // Never publish a stale page's hits.
            }
            cursor = page.Next;
            exhausted = page.Exhausted;
            yield return new(hits.ToImmutable(), outcomes.ToImmutable(), Coverage(false));
        }
        token.ThrowIfCancellationRequested();
        if (!changed && !await IsContextCurrentAsync(request.Context, token).ConfigureAwait(false)) changed = true;
        yield return new([], [], Coverage(exhausted && !changed));
    }

    private sealed record SourceScanResult(IndexScanStatus Status, IndexFreshness Freshness,
        string? EvaluatedFingerprint = null, bool AuthoritativeFileRead = false);
    private async Task<SourceScanResult> ScanOneAsync(IndexedDocumentSummary source,
        IndexSearchRequest request, IndexTextMatcher matcher, CancellationToken token)
    {
        IndexFreshness freshness = source.Freshness;
        try
        {
            string text;
            if (source.Kind == IndexedDocumentKind.File && request.ReadAuthoritativeFiles)
            {
                if (source.PhysicalPath == null) return new(IndexScanStatus.Unindexed, IndexFreshness.Unindexed);
                if (request.Target == IndexSearchTarget.Name)
                {
                    await using var existence = new FileStream(source.PhysicalPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 1, FileOptions.Asynchronous);
                    // Name search does not materialize/hash a huge body; content freshness remains unverified.
                    bool nameMatch = matcher.IsMatch(source.DisplayName);
                    return new(nameMatch ? IndexScanStatus.Matched : IndexScanStatus.NotMatched, IndexFreshness.Stale);
                }
                await using var file = await IndexedFileRead.OpenAsync(source.PhysicalPath, request.MaximumDocumentCharacters, token).ConfigureAwait(false);
                if (source.Fingerprint != file.Fingerprint.Sha256) freshness = IndexFreshness.Stale;
                text = request.Target == IndexSearchTarget.Name ? source.DisplayName : file.Text;
                bool match = matcher.IsMatch(text);
                token.ThrowIfCancellationRequested();
                return new(match ? IndexScanStatus.Matched : IndexScanStatus.NotMatched, freshness, file.Fingerprint.Sha256, true);
            }
            if (source.Kind == IndexedDocumentKind.File) freshness = IndexFreshness.Stale; // Index-only is never claimed current.
            if (request.Target == IndexSearchTarget.Name) text = source.DisplayName;
            else if (source.Kind == IndexedDocumentKind.TableCode) return new(IndexScanStatus.Delegated, freshness);
            else if (source.RevisionKey == null || source.ContentKey == null) return new(IndexScanStatus.Unindexed, freshness);
            else text = await ReadRevisionTextAsync(source.DocumentKey, source.RevisionKey.Value, request.MaximumDocumentCharacters, token).ConfigureAwait(false);
            bool found = matcher.IsMatch(text);
            token.ThrowIfCancellationRequested();
            return new(found ? IndexScanStatus.Matched : IndexScanStatus.NotMatched, freshness, source.Fingerprint);
        }
        catch (RegexMatchTimeoutException) { return new(IndexScanStatus.TimedOut, freshness); }
        catch (IndexDocumentTooLargeException) { return new(IndexScanStatus.TooLarge, freshness); }
        catch (FileNotFoundException) { return new(IndexScanStatus.Missing, IndexFreshness.Missing); }
        catch (DirectoryNotFoundException) { return new(IndexScanStatus.Missing, IndexFreshness.Missing); }
        catch (UnauthorizedAccessException) { return new(IndexScanStatus.Inaccessible, IndexFreshness.Inaccessible); }
        catch (IOException) { return new(IndexScanStatus.Changed, IndexFreshness.Stale); }
    }
}

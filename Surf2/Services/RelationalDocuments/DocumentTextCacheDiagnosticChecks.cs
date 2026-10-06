using System.IO;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalDocuments;

/// <summary>Pure producer-level diagnostics checks; no SQL, files or UI.</summary>
public static class DocumentTextCacheDiagnosticChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var passed = new List<string>();
        Guid epoch = Guid.Parse("cb7ea0a0-aabf-45f3-86a5-4ddf78c0889e");
        var limits = new DocumentOpenLimits { WarningBytes = 64, MaximumTextBytes = 128, CacheBytes = 4096, CacheEntries = 4, ConcurrentReads = 2 };
        var metrics = new RelationalQueryMetrics();
        try
        {
            _ = new DocumentTextCache(limits, metrics.Diagnostics);
            throw new InvalidOperationException("Diagnostic construction accepted an empty epoch.");
        }
        catch (ArgumentException) { passed.Add("Optional document diagnostics require an explicit nonempty session epoch"); }
        var key = new DocumentTextKey(new SnapshotResourceLocator(epoch, 1, 2, 3), "definition-legacy-empty-v1");
        const string output = "private-canary \ud800 \udc00 ";
        await using var cache = new DocumentTextCache(limits, metrics.Diagnostics, epoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        async Task<string> Read(CancellationToken token)
        {
            Interlocked.Increment(ref reads); entered.TrySetResult();
            return await finish.Task.WaitAsync(token).ConfigureAwait(false);
        }
        try
        {
            var first = cache.ReadAsync(key, Read, ct);
            await entered.Task.WaitAsync(ct).ConfigureAwait(false);
            var joined = cache.ReadAsync(key, Read, ct);
            Check(metrics.Snapshot().Active == 1 && metrics.Snapshot().Recent.Count == 0,
                "Joined callers retain exactly one unfinished producer measurement", passed);
            finish.TrySetResult(output);
            var results = await Task.WhenAll(first, joined).ConfigureAwait(false);
            var produced = metrics.Snapshot();
            Check(reads == 1 && ReferenceEquals(results[0], results[1]) && produced.Active == 0 && produced.Completed == 1 &&
                produced.RowsRead == 1 && produced.BytesRead == output.Length * 2L && produced.Queries == 0 && produced.CacheHits == 0,
                "Single-flight provider measures one validated UTF-16 output, never estimated SQL calls", passed);
        }
        finally { finish.TrySetResult(output); }

        var warm = await cache.ReadAsync(key, _ => throw new InvalidOperationException("A cache hit read the provider."), ct).ConfigureAwait(false);
        var hit = metrics.Snapshot();
        Check(warm.Text == output && hit.Completed == 2 && hit.RowsRead == 1 && hit.BytesRead == output.Length * 2L &&
            hit.Queries == 0 && hit.CacheHits == 1 && hit.Recent[^1].RowsRead == 0 && hit.Recent[^1].BytesRead == 0,
            "Actual cache-hit producers record hits only, without counting output bytes again", passed);
        _ = cache.TryDescribe(key, out long described);
        Check(described == output.Length * 2L && metrics.Snapshot().Recent.Count == hit.Recent.Count,
            "Metadata-only cache descriptions do not inflate read or hit diagnostics", passed);
        Check(hit.Recent.All(d => d.Operation == QueryOperation.DefinitionRead && d.Epoch == epoch &&
            d.Fingerprint.Value?.Length == 64 && !d.ToString().Contains("private-canary", StringComparison.Ordinal)),
            "Producer diagnostics retain only fixed operation fingerprints and epoch, never content or labels", passed);

        var tableKey = new DocumentTextKey(key.Locator, RelationalIndexStore.TableRendererVersion);
        const string table = "CREATE TABLE [dbo].[T] ();";
        _ = await cache.ReadAsync(tableKey, _ => Task.FromResult(table), ct).ConfigureAwait(false);
        var tableRead = metrics.Snapshot().Recent[^1];
        Check(tableRead.Operation == QueryOperation.TableMetadataRead && tableRead.RowsRead == 1 && tableRead.BytesRead == table.Length * 2L &&
            tableRead.QueryCount == 0 && tableRead.CacheHits == 0 && tableRead.Fingerprint != hit.Recent[0].Fingerprint,
            "Selected-table rendering uses its fixed metadata-output operation and fingerprint", passed);

        await ExpectAsync<InvalidDataException>(() => cache.ReadAsync(new(key.Locator, "failure"),
            _ => throw new InvalidDataException("private-canary failure"), ct)).ConfigureAwait(false);
        var failed = metrics.Snapshot().Recent[^1];
        Check(failed.Outcome == QueryOutcome.Failed && failed.RowsRead == 0 && failed.BytesRead == 0 &&
            failed.QueryCount == 0 && failed.CacheHits == 0 && metrics.Diagnostics.ActiveOperations == 0,
            "Provider failures complete only after exit and retain no exception text or output", passed);
        await ExpectAsync<DocumentTextLimitException>(() => cache.ReadAsync(new(key.Locator, "oversize"),
            _ => Task.FromResult(new string('x', 65)), ct)).ConfigureAwait(false);
        Check(metrics.Snapshot().Recent[^1] is { Outcome: QueryOutcome.Failed, RowsRead: 0, BytesRead: 0, QueryCount: 0 },
            "Rejected oversized provider output is not reported as a validated document read", passed);

        var cancellationMetrics = new RelationalQueryMetrics();
        var cancellationCache = new DocumentTextCache(limits, cancellationMetrics.Diagnostics, epoch);
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cancelled = cancellationCache.ReadAsync(key, async token =>
            {
                blocked.TrySetResult();
                await release.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return "unpublished";
            }, cancellation.Token);
            await blocked.Task.WaitAsync(ct).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await ExpectAsync<OperationCanceledException>(() => cancelled.WaitAsync(ct)).ConfigureAwait(false);
            Check(cancellationMetrics.Snapshot().Active == 1 && cancellationMetrics.Snapshot().Cancelled == 0 &&
                cancellationCache.ActiveReads == 1,
                "Cancelling a waiter does not complete a diagnostic or release real producer ownership early", passed);
        }
        finally
        {
            release.TrySetResult();
            await cancellationCache.DisposeAsync().ConfigureAwait(false);
        }
        var cancelledRead = cancellationMetrics.Snapshot();
        Check(cancelledRead.Active == 0 && cancelledRead.Cancelled == 1 && cancelledRead.RowsRead == 0 &&
            cancelledRead.BytesRead == 0 && cancelledRead.Queries == 0 && cancelledRead.CacheHits == 0 && cancellationCache.ActiveReads == 0,
            "Actual cancelled producer exit emits once and drains measurement, task and capacity ownership", passed);

        var admissionMetrics = new RelationalQueryMetrics();
        await using var uncached = new DocumentTextCache(limits with { CacheBytes = 1 }, admissionMetrics.Diagnostics, epoch);
        for (int i = 0; i < 2; i++) _ = await uncached.ReadAsync(key, _ => Task.FromResult("ok"), ct).ConfigureAwait(false);
        Check(admissionMetrics.Snapshot() is { Completed: 2, RowsRead: 2, BytesRead: 8, Queries: 0, CacheHits: 0 } && uncached.Usage.RetainedBytes == 0,
            "Cache admission failure remains two genuine provider outputs, never a false cache hit", passed);
        var brokenSink = new QueryDiagnostics(_ => throw new InvalidOperationException("private-canary sink"));
        await using var isolated = new DocumentTextCache(limits, brokenSink, epoch);
        Check((await isolated.ReadAsync(key, _ => Task.FromResult("ok"), ct).ConfigureAwait(false)).Text == "ok" &&
            brokenSink.SinkFailures == 1 && brokenSink.ActiveOperations == 0,
            "Telemetry sink failure never changes the result or leaks an active measurement", passed);
        return passed.AsReadOnly();
    }

    private static void Check(bool valid, string label, List<string> passed)
    { if (!valid) throw new InvalidOperationException(label); passed.Add(label); }

    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected producer rejection: " + typeof(T).Name);
    }
}

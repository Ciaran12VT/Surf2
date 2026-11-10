using System.Collections.Immutable;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access;

/// <summary>Pure checks: no database, physical file, dispatcher or shared build output is used.</summary>
public static class AccessContractChecks
{
    public static Task<IReadOnlyList<string>> RunCancellationAsync(CancellationToken ct = default) => AccessCancellationChecks.RunAsync(ct);

    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        deadline.Token.ThrowIfCancellationRequested();
        var passed = new List<string>();
        VerifyLocators(passed);
        VerifyCache(passed);
        VerifyLifetime(passed);
        VerifyDiagnostics(passed);
        await VerifySingleFlight(passed, deadline.Token).ConfigureAwait(false);
        passed.AddRange(await RunCancellationAsync(deadline.Token).ConfigureAwait(false));
        return passed.AsReadOnly();
    }

    private static void VerifyLocators(List<string> passed)
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var summary = new SnapshotResourceSummary(2, Guid.NewGuid(), 1, null, "dbo", "Alias", "original", 3, 0, new byte[8]);
        var first = SnapshotResourceLocator.From(a, summary);
        Check(first == new SnapshotResourceLocator(a, 1, 2, 3) && first != SnapshotResourceLocator.From(b, summary),
            "Locators share domain identity but never overlapping keys across epochs", passed);
        Expect<InvalidOperationException>(() => first.RequireEpoch(b));
        Expect<ArgumentException>(() => new SnapshotResourceLocator(Guid.Empty, 1, 2, 3));
        Expect<ArgumentOutOfRangeException>(() => new SnapshotResourceLocator(a, 1, 2, 0));
        Check(first != new SnapshotResourceLocator(a, 1, 2, 3, 4) &&
            first != new SnapshotResourceLocator(a, 1, 2, 3, tableSource: TableRevisionSource.DataCompanion),
            "Historical version and resurrected-table source remain part of the locator", passed);
        var data = CapturedDataSetLocator.From(new(a, new(5, 3, 6, -1, 0, DateTimeOffset.UtcNow, "Ready", 1), []));
        Check(data.DataSetKey == 5 && data.RevisionKey == 3 && data.DisplayFormatVersion == 1,
            "Dataset locator reuses the Capture descriptor and formatting revision", passed);
        var indexed = new IndexedDocumentSummary(7, 8, IndexedDocumentKind.Definition, "Name", "SQL", "alias", "node", "parent",
            9, IndexFreshness.Indexed, 10, 3, null, null, null);
        Check(IndexedDocumentLocator.From(a, indexed).RevisionKey == 8,
            "Index locator pins the existing published index revision", passed);
        Expect<InvalidOperationException>(() => IndexedDocumentLocator.From(a, indexed with { RevisionKey = null }));
        var diagram = DiagramRevisionLocator.From(new(new(11, a, new byte[8], Guid.NewGuid()), 0, "id", "name", 12,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Check(diagram.DiagramKey == 11 && diagram.RevisionKey == 12 && diagram.Epoch == a,
            "Diagram locator reuses StateToken epoch and the selected immutable revision", passed);
        Check(typeof(SessionLocator).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SessionLocator)))
            .SelectMany(t => t.GetProperties()).All(p => p.PropertyType.IsValueType),
            "Locator contracts carry no text, rows, assets, model graphs or mutable byte tokens", passed);
    }

    private static void VerifyCache(List<string> passed)
    {
        using var cache = new ByteBoundedCache<string, string>(10, 3);
        cache.TryStore("a", "A", 4);
        cache.TryStore("b", "B", 4);
        using (cache.TryAcquire("a")) { }
        cache.TryStore("c", "C", 4);
        Check(cache.TryAcquire("b") == null && cache.Usage.RetainedBytes == 8,
            "Cache evicts least-recently-used unpinned entries by bytes", passed);
        var pin = cache.TryAcquire("a")!;
        Check(!cache.TryStore("a", "replacement", 4) && !cache.TryStore("d", "D", 7) &&
            cache.Usage.VisibleEntries == 2 && cache.Usage.RetainedBytes == 8,
            "Pinned pressure rejects admission without replacement or unrelated eviction", passed);
        cache.Invalidate("a");
        Check(cache.TryAcquire("a") == null && pin.Value == "A" && cache.Usage.RetainedBytes == 8 &&
            cache.Usage.PinnedEntries == 1 && !cache.TryStore("d", "D", 7),
            "Invalidation hides pinned entries but retains their full byte charge", passed);
        pin.Dispose();
        pin.Dispose();
        Expect<ObjectDisposedException>(() => _ = pin.Value);
        Check(cache.TryStore("d", "D", 7) && cache.Usage.RetainedBytes == 7 && cache.Usage.RetainedEntries == 1,
            "Last lease release frees retired charge exactly once", passed);
        Check(!cache.TryStore("large", "large", 11) && cache.Usage.VisibleEntries == 1,
            "Oversized entries decline caching instead of truncating or flushing the cache", passed);
        Expect<ArgumentOutOfRangeException>(() => cache.TryStore("zero", "zero", 0));
        var finalPin = cache.TryAcquire("d")!;
        cache.Dispose();
        Check(cache.Usage.VisibleEntries == 0 && cache.Usage.RetainedBytes == 7 && finalPin.Value == "D",
            "Cache disposal preserves live leases and their accounting", passed);
        finalPin.Dispose();
        Check(cache.Usage.RetainedBytes == 0 && cache.Usage.RetainedEntries == 0,
            "All cache-owned memory charge leaves after logical lease close", passed);
        Expect<ObjectDisposedException>(() => cache.TryStore("new", "new", 1));

        using var replacements = new ByteBoundedCache<string, string>(20, 2);
        replacements.TryStore("same", "old", 6);
        using var old = replacements.TryAcquire("same")!;
        replacements.Invalidate("same");
        replacements.TryStore("same", "new", 8);
        using var current = replacements.TryAcquire("same")!;
        Check(old.Value == "old" && current.Value == "new" && replacements.Usage.RetainedBytes == 14 && replacements.Usage.RetainedEntries == 2,
            "Hidden old leases and replacement entries have independent retained charges", passed);
        using var counts = new ByteBoundedCache<int, int>(100, 1);
        counts.TryStore(1, 1, 1);
        counts.TryStore(2, 2, 1);
        Check(counts.TryAcquire(1) == null && counts.Usage.RetainedEntries == 1,
            "An independent entry-count budget bounds tiny-entry bookkeeping", passed);
    }

    private static void VerifyLifetime(List<string> passed)
    {
        Guid epoch = Guid.NewGuid();
        using var lifetime = new QueryLifetime(epoch, 1);
        using var first = lifetime.BeginRequest();
        using var second = lifetime.BeginRequest();
        int publications = 0;
        Check(first.CancellationToken.IsCancellationRequested && !lifetime.TryPublish(first.Stamp, () => publications++) &&
            lifetime.TryPublish(second.Stamp, () => publications++) && publications == 1 && lifetime.ActiveRequests == 2,
            "Superseded requests cancel and cannot publish; actual owners remain until disposal", passed);
        lifetime.InvalidateContext();
        Check(second.CancellationToken.IsCancellationRequested && !lifetime.IsCurrent(second.Stamp),
            "Membership, alias, unloaded-state or mutable-head invalidation rejects late results", passed);
        using var third = lifetime.BeginRequest();
        Check(third.Stamp.ContextGeneration == second.Stamp.ContextGeneration + 1,
            "Each new context generation is distinct from its request generation", passed);
        lifetime.ChangeContext(Guid.NewGuid(), 2);
        lifetime.ChangeContext(epoch, 1);
        using var fourth = lifetime.BeginRequest();
        Check(!lifetime.IsCurrent(third.Stamp) && lifetime.IsCurrent(fourth.Stamp) && fourth.Stamp.Epoch == epoch &&
            fourth.Stamp.ScopeKey == 1 && fourth.Stamp.ContextGeneration > third.Stamp.ContextGeneration,
            "Reconnect and scope A-B-A cannot revive an old result", passed);
        using var other = new QueryLifetime(epoch, 1);
        using var otherRequest = other.BeginRequest();
        Check(!other.IsCurrent(fourth.Stamp) && !lifetime.IsCurrent(otherRequest.Stamp),
            "Independent workflow owners cannot publish each other's results", passed);
        lifetime.Dispose();
        Check(fourth.CancellationToken.IsCancellationRequested && !lifetime.TryPublish(fourth.Stamp, () => publications++),
            "Logical close cancels all work and closes the publication gate", passed);
        Expect<ObjectDisposedException>(() => lifetime.BeginRequest());
        first.Dispose(); second.Dispose(); third.Dispose(); fourth.Dispose();
        Check(lifetime.ActiveRequests == 0, "Request completion releases tracked logical ownership", passed);
        Expect<ArgumentException>(() => new QueryLifetime(Guid.Empty));
        Expect<ArgumentException>(() => new QueryLifetime(epoch, 0));
    }

    private static void VerifyDiagnostics(List<string> passed)
    {
        var records = new List<QueryDiagnostic>();
        var time = new ManualTime();
        var diagnostics = new QueryDiagnostics(records.Add, time);
        var fingerprint = QueryFingerprint.FromTemplate("SELECT Text FROM surf.TextContent WHERE ContentKey=@Key;");
        using var measurement = diagnostics.Begin(QueryOperation.DefinitionRead, fingerprint, Guid.NewGuid());
        measurement.RecordRead(1, 200);
        measurement.RecordCacheHit();
        time.Advance(TimeSpan.FromMilliseconds(25));
        measurement.Complete();
        measurement.Complete(QueryOutcome.Failed);
        Check(records.Count == 1 && records[0].Duration == TimeSpan.FromMilliseconds(25) && records[0].RowsRead == 1 &&
            records[0].BytesRead == 200 && records[0].QueryCount == 1 && records[0].CacheHits == 1 && diagnostics.ActiveOperations == 0,
            "Diagnostics emit one redacted completion with deterministic duration and counters", passed);
        Expect<ObjectDisposedException>(() => measurement.RecordRead(1, 1));
        using (diagnostics.Begin(QueryOperation.CataloguePage, fingerprint, Guid.NewGuid())) { }
        Check(records[^1].Outcome == QueryOutcome.Abandoned, "Unfinished diagnostic scopes are not successful empty reads", passed);
        foreach (var outcome in new[] { QueryOutcome.Cancelled, QueryOutcome.Failed, QueryOutcome.Stale })
            diagnostics.Begin(QueryOperation.SearchBatch, fingerprint, Guid.NewGuid()).Complete(outcome);
        Check(records.TakeLast(3).Select(r => r.Outcome).SequenceEqual(new[] { QueryOutcome.Cancelled, QueryOutcome.Failed, QueryOutcome.Stale }),
            "Cancellation, failure and stale results remain distinct diagnostic outcomes", passed);
        Check(typeof(QueryDiagnostic).GetProperties().All(p => p.PropertyType.IsValueType) && fingerprint.Value!.Length == 64,
            "Diagnostic records cannot carry SQL, parameters, paths, content or exception messages", passed);
        Expect<ArgumentException>(() => QueryFingerprint.Parse("sensitive parameter value"));
        Expect<ArgumentException>(() => diagnostics.Begin(QueryOperation.StateRead, default, Guid.NewGuid()));
        var failing = new QueryDiagnostics(_ => throw new InvalidOperationException("not logged"));
        failing.Begin(QueryOperation.StateRead, fingerprint, Guid.NewGuid()).Complete();
        Check(failing.SinkFailures == 1 && failing.ActiveOperations == 0,
            "A failing diagnostic sink cannot fail the read or retain its operation", passed);
        var overflow = diagnostics.Begin(QueryOperation.DataSetPage, fingerprint, Guid.NewGuid());
        overflow.RecordRead(long.MaxValue, 1);
        Expect<OverflowException>(() => overflow.RecordRead(1, 1));
        overflow.Complete();
        Check(records[^1].RowsRead == long.MaxValue && records[^1].BytesRead == 1,
            "Counter overflow rejects the whole update without mixed diagnostic totals", passed);
    }

    private static async Task VerifySingleFlight(List<string> passed, CancellationToken ct)
    {
        await using var reads = new SingleFlight<string, int>(2);
        var started = Signal(); var finish = Signal<int>(); CancellationToken providerToken = default;
        using var hover = new CancellationTokenSource();
        async Task<int> Read(CancellationToken token)
        {
            providerToken = token;
            started.TrySetResult();
            return await finish.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        try
        {
            Task<int> first = reads.RunAsync("revision", Read, hover.Token);
            await started.Task.WaitAsync(ct).ConfigureAwait(false);
            Task<int> second = reads.RunAsync("revision", _ => throw new InvalidOperationException("A shared read was duplicated."), ct);
            hover.Cancel();
            await ExpectAsync<OperationCanceledException>(() => first).ConfigureAwait(false);
            Check(!providerToken.IsCancellationRequested && reads.ActiveCount == 1,
                "Cancelling one shared consumer preserves the provider needed by another", passed);
            finish.TrySetResult(42);
            Check(await second.ConfigureAwait(false) == 42 && reads.ActiveCount == 0,
                "Equivalent concurrent reads execute one provider and release on real completion", passed);
        }
        finally { finish.TrySetResult(42); }
        await ExpectAsync<InvalidOperationException>(() => reads.RunAsync("fault", _ => throw new InvalidOperationException("read failed"), ct)).ConfigureAwait(false);
        Check(await reads.RunAsync("fault", _ => Task.FromResult(7), ct).ConfigureAwait(false) == 7,
            "Faulted flights are removed and can be retried", passed);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => reads.RunAsync("pre-cancelled", _ => throw new InvalidOperationException("Should not start."), cancelled.Token)).ConfigureAwait(false);
        Check(reads.ActiveCount == 0, "Precancelled consumers allocate no provider slot", passed);

        await using var bounded = new SingleFlight<string, int>(1, 1);
        var blockedFinish = Signal<int>(); var cancelledProvider = Signal();
        using var only = new CancellationTokenSource();
        Task<int> blocked = bounded.RunAsync("blocked", async token =>
        {
            using var registration = token.Register(() => cancelledProvider.TrySetResult());
            return await blockedFinish.Task.WaitAsync(ct).ConfigureAwait(false);
        }, only.Token);
        try
        {
            await ExpectAsync<QueryCapacityException>(() => bounded.RunAsync("blocked", _ => Task.FromResult(0), ct)).ConfigureAwait(false);
            only.Cancel();
            await ExpectAsync<OperationCanceledException>(() => blocked).ConfigureAwait(false);
            await cancelledProvider.Task.WaitAsync(ct).ConfigureAwait(false);
            await ExpectAsync<QueryCapacityException>(() => bounded.RunAsync("other", _ => Task.FromResult(0), ct)).ConfigureAwait(false);
            Check(bounded.ActiveCount == 1, "Last consumer cancels without prematurely freeing the provider slot", passed);
            Task disposal = bounded.DisposeAsync().AsTask();
            Check(!disposal.IsCompleted, "Async disposal waits for an uncooperative provider to actually finish", passed);
            await ExpectAsync<ObjectDisposedException>(() => bounded.RunAsync("closed", _ => Task.FromResult(0), ct)).ConfigureAwait(false);
            blockedFinish.TrySetResult(1);
            await disposal.WaitAsync(ct).ConfigureAwait(false);
            Check(bounded.ActiveCount == 0, "Teardown drains provider ownership and rejects new work", passed);
        }
        finally { blockedFinish.TrySetResult(1); }

        await using var replacements = new SingleFlight<string, int>(2);
        var oldFinish = Signal<int>(); var newFinish = Signal<int>();
        using var oldConsumer = new CancellationTokenSource();
        Task<int> old = replacements.RunAsync("same", _ => oldFinish.Task.WaitAsync(ct), oldConsumer.Token);
        try
        {
            oldConsumer.Cancel();
            await ExpectAsync<OperationCanceledException>(() => old).ConfigureAwait(false);
            Task<int> newer = replacements.RunAsync("same", _ => newFinish.Task.WaitAsync(ct), ct);
            oldFinish.TrySetResult(1);
            while (replacements.ActiveCount != 1) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
            Task<int> joined = replacements.RunAsync("same", _ => throw new InvalidOperationException("Old cleanup removed the replacement."), ct);
            newFinish.TrySetResult(2);
            Check(await newer.ConfigureAwait(false) == 2 && await joined.ConfigureAwait(false) == 2,
                "Abandoned same-key flight cleanup cannot remove its running replacement", passed);
        }
        finally { oldFinish.TrySetResult(1); newFinish.TrySetResult(2); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string label, List<string> passed)
    {
        if (!condition) throw new InvalidOperationException("Access contract check failed: " + label);
        passed.Add(label);
    }
    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected access contract rejection: " + typeof(TException).Name);
    }
    private static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected async access contract rejection: " + typeof(TException).Name);
    }
}

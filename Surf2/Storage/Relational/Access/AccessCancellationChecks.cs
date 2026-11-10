namespace Surf2.Storage.Relational.Access;

// Blocking gates simulate provider cancellation I/O, not a timing-only sleep.
internal static class AccessCancellationChecks
{
    internal static async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        ct = deadline.Token;
        ct.ThrowIfCancellationRequested();
        var passed = new List<string>();
        await VerifyCancellationOwner(passed, ct).ConfigureAwait(false);
        for (int transition = 0; transition < 5; transition++)
            await VerifyRequestTransition(transition, passed, ct).ConfigureAwait(false);
        await VerifyFlightRelease(passed, ct).ConfigureAwait(false);
        await VerifyFlightDisposal(passed, ct).ConfigureAwait(false);
        return passed.AsReadOnly();
    }

    private static async Task VerifyCancellationOwner(List<string> passed, CancellationToken ct)
    {
        int failures = 0;
        var cancellation = new AccessCancellation(() => Interlocked.Increment(ref failures));
        using var unblock = new ManualResetEventSlim();
        var entered = Signal();
        var registration = cancellation.Token.Register(() =>
        {
            entered.TrySetResult();
            unblock.Wait(ct);
            throw new InvalidOperationException("Callback error must be observed, not logged.");
        });
        try
        {
            Task? callbacks = null;
            await PromptAsync(() => callbacks = cancellation.Cancel(), ct).ConfigureAwait(false);
            Task callbackCompletion = callbacks ?? throw new InvalidOperationException("Cancellation did not return its callback task.");
            await entered.Task.WaitAsync(ct).ConfigureAwait(false);
            Check(cancellation.Token.IsCancellationRequested && !callbackCompletion.IsCompleted,
                "CancelAsync marks requested before returning without waiting for blocked callbacks", passed);
            Check(ReferenceEquals(callbacks, cancellation.Cancel()),
                "Repeated cancellation keeps the original callback-completion task", passed);
            Task retirement = cancellation.RetireAsync();
            Check(ReferenceEquals(retirement, cancellation.RetireAsync()) && !retirement.IsCompleted && !cancellation.IsDisposed,
                "CTS retirement waits for callback completion and is idempotent", passed);
            unblock.Set();
            await retirement.WaitAsync(ct).ConfigureAwait(false);
            await callbackCompletion.ConfigureAwait(false);
            Check(cancellation.IsDisposed && failures == 1 && cancellation.Cancel().IsCompletedSuccessfully,
                "Faulted callback batches are observed once and the CTS is disposed exactly once", passed);
        }
        finally
        {
            unblock.Set();
            await cancellation.RetireAsync().ConfigureAwait(false);
            registration.Dispose();
        }
    }

    private static async Task VerifyRequestTransition(int transition, List<string> passed, CancellationToken ct)
    {
        string name = new[] { "Supersession", "Context switch", "Context invalidation", "Owner close", "Request release" }[transition];
        await using var owner = new QueryLifetime(Guid.NewGuid(), 1);
        var request = owner.BeginRequest();
        QueryRequest? replacement = null;
        using var unblock = new ManualResetEventSlim();
        var entered = Signal();
        var registration = request.CancellationToken.Register(() =>
        {
            entered.TrySetResult();
            unblock.Wait(ct);
            if (transition == 2) throw new InvalidOperationException("Observed request callback fault.");
        });
        try
        {
            await PromptAsync(() =>
            {
                switch (transition)
                {
                    case 0: replacement = owner.BeginRequest(); break;
                    case 1: owner.ChangeContext(Guid.NewGuid(), 2); break;
                    case 2: owner.InvalidateContext(); break;
                    case 3: owner.Dispose(); break;
                    case 4: request.Dispose(); break;
                }
            }, ct).ConfigureAwait(false);
            await entered.Task.WaitAsync(ct).ConfigureAwait(false);
            Check(request.CancellationToken.IsCancellationRequested && !owner.TryPublish(request.Stamp, () => throw new InvalidOperationException("Stale publication.")),
                name + " returns promptly and blocks stale publication while callbacks wait", passed);
            Task released = request.DisposeAsync().AsTask();
            Task closed = owner.DisposeAsync().AsTask();
            Check(!released.IsCompleted && !closed.IsCompleted && !request.IsCancellationDisposed && owner.ActiveRequests >= 1,
                name + " retains request/CTS ownership through racing release and async close", passed);
            replacement?.Dispose();
            unblock.Set();
            await released.WaitAsync(ct).ConfigureAwait(false);
            await closed.WaitAsync(ct).ConfigureAwait(false);
            Check(owner.ActiveRequests == 0 && request.IsCancellationDisposed &&
                owner.CancellationCallbackFailures == (transition == 2 ? 1 : 0),
                name + " drains requests and observes callback errors without retained CTS ownership", passed);
        }
        finally
        {
            unblock.Set();
            replacement?.Dispose();
            await request.DisposeAsync().ConfigureAwait(false);
            registration.Dispose();
        }
    }

    private static async Task VerifyFlightRelease(List<string> passed, CancellationToken ct)
    {
        await using var owner = new SingleFlight<string, int>(1);
        using var consumer = new CancellationTokenSource();
        using var unblock = new ManualResetEventSlim();
        var entered = Signal();
        var provider = Signal<int>();
        CancellationTokenRegistration registration = default;
        Task<int> read = owner.RunAsync("revision", token =>
        {
            registration = token.Register(() => { entered.TrySetResult(); unblock.Wait(ct); });
            return provider.Task;
        }, consumer.Token);
        try
        {
            consumer.Cancel();
            await ExpectAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
            await entered.Task.WaitAsync(ct).ConfigureAwait(false);
            provider.TrySetResult(1);
            Check(owner.ActiveCount == 1, "Last consumer returns promptly while provider/callback capacity remains charged", passed);
            await ExpectAsync<QueryCapacityException>(() => owner.RunAsync("other", _ => Task.FromResult(0), ct)).ConfigureAwait(false);
            Check(provider.Task.IsCompleted && owner.ActiveCount == 1,
                "A finished provider cannot release capacity ahead of its blocked cancellation callbacks", passed);
            unblock.Set();
            while (owner.ActiveCount != 0) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
            Check(await owner.RunAsync("revision", _ => Task.FromResult(2), ct).ConfigureAwait(false) == 2,
                "Drained callback retirement frees capacity for a same-key retry", passed);
        }
        finally { unblock.Set(); provider.TrySetResult(1); registration.Dispose(); }
    }

    private static async Task VerifyFlightDisposal(List<string> passed, CancellationToken ct)
    {
        await using var owner = new SingleFlight<string, int>(1);
        using var unblock = new ManualResetEventSlim();
        var entered = Signal();
        var provider = Signal<int>();
        CancellationTokenRegistration registration = default;
        Task<int> read = owner.RunAsync("revision", token =>
        {
            registration = token.Register(() =>
            {
                entered.TrySetResult();
                unblock.Wait(ct);
                throw new InvalidOperationException("Observed flight callback fault.");
            });
            return provider.Task;
        }, ct);
        try
        {
            // Separate obtaining the disposal task from awaiting full teardown.
            // A synchronous Cancel() regression cannot trap the test caller here.
            Task<Task> invocation = Task.Factory.StartNew(() => owner.DisposeAsync().AsTask(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task disposal = await invocation.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await entered.Task.WaitAsync(ct).ConfigureAwait(false);
            provider.TrySetResult(1);
            Check(!disposal.IsCompleted && !read.IsCompleted && owner.ActiveCount == 1,
                "Single-flight async close returns its task promptly but awaits provider and callback teardown", passed);
            await ExpectAsync<ObjectDisposedException>(() => owner.RunAsync("closed", _ => Task.FromResult(0), ct)).ConfigureAwait(false);
            unblock.Set();
            await disposal.WaitAsync(ct).ConfigureAwait(false);
            await ExpectAsync<OperationCanceledException>(() => read).ConfigureAwait(false);
            Check(owner.ActiveCount == 0 && owner.CancellationCallbackFailures == 1,
                "Single-flight close drains capacity and observes callback failure before completing", passed);
        }
        finally { unblock.Set(); provider.TrySetResult(1); registration.Dispose(); }
    }

    private static async Task PromptAsync(Action transition, CancellationToken ct)
    {
        Task invocation = Task.Factory.StartNew(transition, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await invocation.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string label, List<string> passed)
    {
        if (!condition) throw new InvalidOperationException("Access cancellation check failed: " + label);
        passed.Add(label);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); } catch (T) { return; }
        throw new InvalidOperationException("Expected cancellation-contract rejection: " + typeof(T).Name);
    }
}

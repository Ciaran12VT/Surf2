using System.Collections.Immutable;
using System.IO;
using System.Threading.Channels;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Index;

public static partial class StorageRegressionSuite
{
    // Pure file-system fixtures; this suite never opens SQL, application settings, or a WPF window.
    public static async Task RunPhysicalReferenceWatchChecksAsync(Action<bool, string> check)
    {
        string owned = Path.Combine(Path.GetTempPath(), "Surf2_ReferenceWatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        try
        {
            await PhysicalWatchFilteringAsync(owned, check);
            await PhysicalWatchRealEventsAsync(owned, check);
            await PhysicalWatchMissingRootsAsync(owned, check);
            await PhysicalWatchDisposalAsync(owned, check);
            await PhysicalWatchCallbackFailureAsync(owned, check);
        }
        finally { Directory.Delete(owned, recursive: true); }
    }

    private static async Task PhysicalWatchFilteringAsync(string owned, Action<bool, string> check)
    {
        string root = Path.Combine(owned, "filtering"), src = Path.Combine(root, "src"), bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(src); Directory.CreateDirectory(bin);
        string explicitFile = Path.Combine(bin, "explicit.cs");
        File.WriteAllText(explicitFile, "class ExplicitSource { }");
        var scope = PhysicalWatchScope([
            PhysicalWatchRoot(1, root), PhysicalWatchRoot(2, root), PhysicalWatchRoot(3, src),
            PhysicalWatchRoot(4, explicitFile, ResourceKind.File), PhysicalWatchRoot(5, root) with { IsLoaded = false },
            PhysicalWatchRoot(6, "unused", ResourceKind.DatabaseSnapshot), PhysicalWatchRoot(7, bin)]);
        var notifications = Channel.CreateUnbounded<PhysicalReferenceInvalidation>();
        using var watch = new PhysicalReferenceWatch(scope, notification => notifications.Writer.TryWrite(notification));
        var initial = watch.InitialInvalidation;
        check(initial.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2, 3, 4, 7 }) &&
            initial.Reason.HasFlag(PhysicalReferenceInvalidationReason.InitialReconciliation) && initial.RequiresWholeRootReconciliation,
            "Every process start requires reconciliation of loaded physical roots, not unloaded or captured resources");
        check(initial.Failures.IsEmpty && watch.UnwatchedRoots.IsEmpty && watch.WatcherCount > 0,
            "Watchers are installed before startup reconciliation is requested by the owner");
        await Task.Delay(350);
        check(!notifications.Reader.TryRead(out _), "Creating a watcher reports startup state without scheduling a callback refresh loop");

        string visible = Path.Combine(src, "visible.cs");
        for (int i = 0; i < 20; i++) watch.RecordChange(WatcherChangeTypes.Changed, visible);
        var burst = await PhysicalWatchNextAsync(notifications);
        check(burst.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2, 3 }) && burst.Paths.SequenceEqual([visible]) &&
            !burst.RequiresWholeRootReconciliation && burst.Reason == PhysicalReferenceInvalidationReason.Changed,
            "Write bursts coalesce and retain every alias and overlapping folder owner without duplicate paths");
        await Task.Delay(350);
        check(!notifications.Reader.TryRead(out _), "A coalesced burst does not produce trailing duplicate callbacks");

        watch.RecordChange(WatcherChangeTypes.Changed, explicitFile);
        var generatedExplicit = await PhysicalWatchNextAsync(notifications);
        check(generatedExplicit.ScopeResourceKeys.SequenceEqual(new long[] { 4, 7 }),
            "An explicit generated-directory file and generated-directory root remain watched while outer roots exclude them");

        string excluded = Path.Combine(root, "obj", "ignored.cs");
        watch.RecordChange(WatcherChangeTypes.Changed, excluded);
        watch.RecordChange(WatcherChangeTypes.Deleted, Path.Combine(root, "obj"));
        await Task.Delay(350);
        check(!notifications.Reader.TryRead(out _), "Generated descendants and removed generated-directory boundaries are excluded");

        watch.RecordChange(WatcherChangeTypes.Renamed, Path.Combine(src, "moved.cs"), Path.Combine(bin, "old.cs"));
        var renamed = await PhysicalWatchNextAsync(notifications);
        check(renamed.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2, 3, 7 }) && renamed.Paths.Length == 2 &&
            renamed.RequiresWholeRootReconciliation,
            "Rename invalidation covers both old and new owners and requests topology reconciliation");

        string visibleDirectory = Path.Combine(root, "became-visible"), excludedDirectory = Path.Combine(root, "obj");
        watch.RecordChange(WatcherChangeTypes.Renamed, visibleDirectory, excludedDirectory);
        var becameVisible = await PhysicalWatchNextAsync(notifications);
        check(becameVisible.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2 }) && becameVisible.RequiresWholeRootReconciliation,
            "Renaming an excluded directory into a visible name invalidates parent-root child discovery");
        watch.RecordChange(WatcherChangeTypes.Renamed, excludedDirectory, visibleDirectory);
        var becameExcluded = await PhysicalWatchNextAsync(notifications);
        check(becameExcluded.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2 }) && becameExcluded.RequiresWholeRootReconciliation,
            "Renaming a visible directory into an excluded name still invalidates its old parent-root memberships");

        for (int i = 0; i < 300; i++) watch.RecordChange(WatcherChangeTypes.Changed, Path.Combine(src, "burst-" + i + ".cs"));
        var bounded = await PhysicalWatchNextAsync(notifications);
        check(bounded.Paths.IsEmpty && bounded.RequiresWholeRootReconciliation &&
            bounded.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2, 3 }),
            "Large bursts have bounded path memory and safely fall back to whole-root reconciliation");

        watch.RecordWatcherError(root, new InternalBufferOverflowException());
        var overflow = await PhysicalWatchNextAsync(notifications);
        check(overflow.Reason.HasFlag(PhysicalReferenceInvalidationReason.WatcherError) && overflow.RequiresWholeRootReconciliation &&
            overflow.ScopeResourceKeys.SequenceEqual(new long[] { 1, 2, 3, 4, 7 }) &&
            overflow.Failures.All(f => f.Code == "BufferOverflow"),
            "Buffer overflow explicitly invalidates all affected aliases and requests full reconciliation");

        int emptyCallbacks = 0;
        using var emptyWatch = new PhysicalReferenceWatch(scope with { Resources = [] }, () => Interlocked.Increment(ref emptyCallbacks));
        await Task.Delay(350);
        check(emptyWatch.WatcherCount == 0 && emptyCallbacks == 0, "An empty physical selection creates no watchers or callbacks");
    }

    private static async Task PhysicalWatchRealEventsAsync(string owned, Action<bool, string> check)
    {
        string root = Path.Combine(owned, "live"); Directory.CreateDirectory(root);
        var notifications = Channel.CreateUnbounded<PhysicalReferenceInvalidation>();
        using var watch = new PhysicalReferenceWatch(PhysicalWatchScope([PhysicalWatchRoot(10, root)]),
            notification => notifications.Writer.TryWrite(notification));
        string child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
        string file = Path.Combine(child, "code.cs"); await File.WriteAllTextAsync(file, "class Initial { }");
        var created = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(file, StringComparer.OrdinalIgnoreCase));
        check(created.ScopeResourceKeys.SequenceEqual([10L]), "Actual recursive file creation invalidates the owning root");
        await File.AppendAllTextAsync(file, "class Changed { }");
        var changed = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(file, StringComparer.OrdinalIgnoreCase));
        check(changed.Reason.HasFlag(PhysicalReferenceInvalidationReason.Changed), "Actual writes are observed without loading source bodies");
        string moved = Path.Combine(child, "renamed.cs"); File.Move(file, moved);
        var rename = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(moved, StringComparer.OrdinalIgnoreCase));
        check(rename.Paths.Contains(file, StringComparer.OrdinalIgnoreCase) && rename.RequiresWholeRootReconciliation,
            "Actual renames preserve old and new paths for pruning and discovery");
        File.Delete(moved);
        var deleted = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(moved, StringComparer.OrdinalIgnoreCase));
        check(deleted.RequiresWholeRootReconciliation, "Actual deletion requests reconciliation rather than claiming freshness");

        string renamedRoot = root + "-renamed"; Directory.Move(root, renamedRoot);
        var missing = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(root, StringComparer.OrdinalIgnoreCase));
        check(missing.RequiresWholeRootReconciliation, "A parent subscription observes renaming the watched root itself");
        Directory.CreateDirectory(root);
        string returned = Path.Combine(root, "returned.cs"); await File.WriteAllTextAsync(returned, "class Returned { }");
        await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(root, StringComparer.OrdinalIgnoreCase));
        await Task.Delay(350);
        while (notifications.Reader.TryRead(out _)) { }
        await File.AppendAllTextAsync(returned, "class AfterRearm { }");
        var rearmed = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(returned, StringComparer.OrdinalIgnoreCase));
        check(rearmed.ScopeResourceKeys.SequenceEqual([10L]), "Recreated roots are watched again before subsequent reconciliation callbacks");
    }

    private static async Task PhysicalWatchMissingRootsAsync(string owned, Action<bool, string> check)
    {
        string root = Path.Combine(owned, "missing", "nested");
        string file = Path.Combine(owned, "missing-file", "nested", "code.cs");
        var notifications = Channel.CreateUnbounded<PhysicalReferenceInvalidation>();
        using var watch = new PhysicalReferenceWatch(PhysicalWatchScope([
            PhysicalWatchRoot(20, root), PhysicalWatchRoot(21, file, ResourceKind.File), PhysicalWatchRoot(22, "\0")]),
            notification => notifications.Writer.TryWrite(notification));
        var initial = watch.InitialInvalidation;
        check(initial.ScopeResourceKeys.SequenceEqual(new long[] { 20, 21, 22 }) && initial.RequiresWholeRootReconciliation &&
            initial.Failures.Count(f => f.Code == "MissingRoot") == 2 && initial.Failures.Any(f => f.Code == "InvalidPath"),
            "Missing roots and malformed paths are explicit failures rather than silently unobserved resources");
        check(watch.UnwatchedRoots.SequenceEqual(initial.Failures), "Initial setup failures remain available without callback retries");
        await Task.Delay(350);
        check(!notifications.Reader.TryRead(out _), "Missing-root setup does not repeatedly schedule immediate reconciliation");
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "class CreatedLater { }");
        string nestedFile = Path.Combine(root, "new.cs"); await File.WriteAllTextAsync(nestedFile, "class New { }");
        await PhysicalWatchUntilAsync(notifications, p => p.ScopeResourceKeys.Contains(20) && p.ScopeResourceKeys.Contains(21));
        await Task.Delay(350);
        while (notifications.Reader.TryRead(out _)) { }
        await File.AppendAllTextAsync(file, "class LaterWrite { }");
        var fileChange = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(file, StringComparer.OrdinalIgnoreCase));
        check(fileChange.ScopeResourceKeys.SequenceEqual([21L]), "Previously missing explicit file roots observe later writes after hierarchy creation");
        await File.AppendAllTextAsync(nestedFile, "class LaterNestedWrite { }");
        var folderChange = await PhysicalWatchUntilAsync(notifications, p => p.Paths.Contains(nestedFile, StringComparer.OrdinalIgnoreCase));
        check(folderChange.ScopeResourceKeys.SequenceEqual([20L]), "Previously missing folder roots observe later descendant writes");
    }

    private static async Task PhysicalWatchDisposalAsync(string owned, Action<bool, string> check)
    {
        string root = Path.Combine(owned, "dispose"); Directory.CreateDirectory(root);
        int callbacks = 0;
        var pending = new PhysicalReferenceWatch(PhysicalWatchScope([PhysicalWatchRoot(30, root)]), () => Interlocked.Increment(ref callbacks));
        pending.RecordChange(WatcherChangeTypes.Changed, Path.Combine(root, "pending.cs"));
        pending.Dispose(); pending.Dispose();
        await Task.Delay(400);
        check(callbacks == 0 && pending.WatcherCount == 0, "Disposal is idempotent and cancels queued callbacks and subscriptions");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var running = new PhysicalReferenceWatch(PhysicalWatchScope([PhysicalWatchRoot(31, root)]), () =>
        { Interlocked.Increment(ref callbacks); entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); });
        running.RecordChange(WatcherChangeTypes.Changed, Path.Combine(root, "running.cs"));
        check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "Running callback fixture starts within its deadline");
        var disposing = Task.Run(running.Dispose);
        try
        {
            await Task.Delay(100);
            check(!disposing.IsCompleted, "Disposal waits for an already-running callback to finish");
        }
        finally { release.Set(); await disposing.WaitAsync(TimeSpan.FromSeconds(5)); }
        running.RecordChange(WatcherChangeTypes.Changed, Path.Combine(root, "after-disposal.cs"));
        await Task.Delay(350);
        check(callbacks == 1, "No additional callback can begin after disposal returns");
    }

    private static async Task PhysicalWatchCallbackFailureAsync(string owned, Action<bool, string> check)
    {
        string root = Path.Combine(owned, "callback-failure"); Directory.CreateDirectory(root);
        using var watch = new PhysicalReferenceWatch(PhysicalWatchScope([PhysicalWatchRoot(40, root)]),
            () => throw new InvalidOperationException("Test callback failure"));
        watch.RecordChange(WatcherChangeTypes.Changed, Path.Combine(root, "failure.cs"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (watch.LastCallbackException == null && DateTime.UtcNow < deadline) await Task.Delay(25);
        check(watch.LastCallbackException is InvalidOperationException,
            "Callback exceptions remain observable and do not escape a thread-pool timer callback");
    }

    private static ExplorerResource PhysicalWatchRoot(long key, string path, ResourceKind kind = ResourceKind.Folder) =>
        new(key, key, "watch-" + key, kind, path, "", false, true, null, null, null, null, false);

    private static ExplorerScope PhysicalWatchScope(ImmutableArray<ExplorerResource> resources) =>
        new(new IndexRequestContext(Guid.NewGuid(), 1, 1, "scope", "snapshot", "diagram", false, [], [], false),
            "watch-scope", "Watch fixture", resources, [], "en-IE");

    private static async Task<PhysicalReferenceInvalidation> PhysicalWatchNextAsync(Channel<PhysicalReferenceInvalidation> channel) =>
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));

    private static async Task<PhysicalReferenceInvalidation> PhysicalWatchUntilAsync(Channel<PhysicalReferenceInvalidation> channel,
        Func<PhysicalReferenceInvalidation, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var notification = await channel.Reader.ReadAsync(deadline.Token);
            if (predicate(notification)) return notification;
        }
    }
}

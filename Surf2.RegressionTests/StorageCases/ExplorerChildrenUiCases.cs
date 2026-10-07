using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    // SQL-free; invoke on the existing runtime WPF checks' initialized application dispatcher.
    public static async Task RunExplorerChildrenUiChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var application = Application.Current ?? throw new InvalidOperationException("Explorer UI checks require initialized WPF resources.");
        application.Dispatcher.VerifyAccess();
        const string privateSource = "EXPLORER_PRIVATE_SOURCE_CONTENT";
        var options = SqlServerConnectionOptions.FromConnectionString(
            "Server=127.0.0.1,1;Database=Surf2_ExplorerChildren_NoSql;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=1");
        var runtime = new RelationalRuntime(options); // Construction opens no SQL connections.
        var context = new IndexRequestContext(runtime.Session.Epoch, 1, 2, "scope", "snapshot", "diagram", false, [], [], false);
        var snapshot = new SnapshotSummary(10, Guid.NewGuid(), privateSource, privateSource, privateSource,
            DateTimeOffset.UnixEpoch, 0, null, []);
        var resource = new ExplorerResource(20, 0, privateSource, ResourceKind.DatabaseSnapshot, privateSource,
            privateSource, true, true, snapshot, null, null, null, false);
        var scope = new ExplorerScope(context, privateSource, privateSource, [resource], [], "en-IE");
        var metadata = new ExplorerChildrenUiMetadata(scope);
        var explorer = new RelationalExplorerService(metadata);
        await using var loader = new ExplorerChildrenLoader(explorer);
        var root = (await explorer.GetRootsAsync(scope)).Single(n => n.Role == ExplorerNodeRole.Resource);
        ExplorerNodeSummary? category = null;
        await foreach (var batch in explorer.GetChildrenAsync(scope, root))
            category ??= batch.Nodes.FirstOrDefault(n => n.Category == ExplorerCategory.Procedures);
        if (category == null) throw new InvalidOperationException("The fake database root has no procedure category.");

        var previousMainWindow = application.MainWindow;
        var main = new Surf2.MainWindow(options);
        Type cancellationType = typeof(Surf2.MainWindow).Assembly.GetType(
            "Surf2.Services.RelationalExplorer.ExplorerCancellationLifetime", true)!;
        object cancellation = Activator.CreateInstance(cancellationType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [Array.Empty<CancellationToken>()], CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Cannot construct the explorer cancellation owner.");
        var summaries = RuntimeWpfField<Dictionary<FileSystemNode, ExplorerNodeSummary>>(main, "_relationalExplorerNodes");
        RuntimeWpfSetField(main, "_relational", runtime);
        RuntimeWpfSetField(main, "_relationalExplorerScope", scope);
        RuntimeWpfSetField(main, "_relationalExplorerOwner", Guid.NewGuid());
        RuntimeWpfSetField(main, "_relationalExplorerCancellation", cancellation);
        RuntimeWpfSetField(main, "_relationalChildrenLoader", loader);

        // Redirect logging to one owned temporary file without initializing or pruning user logs.
        Type logging = typeof(Surf2.MainWindow).Assembly.GetType("Surf2.Services.InternalLogService", true)!;
        string logPath = Path.Combine(Path.GetTempPath(), "surf2-explorer-children-" + Guid.NewGuid().ToString("N") + ".log");
        var logFields = new[] { "_isEnabled", "_initialized", "_logFilePath" }
            .Select(name => logging.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        object?[] previousLogValues = logFields.Select(field => field.GetValue(null)).ToArray();
        logFields[2].SetValue(null, logPath);
        logFields[1].SetValue(null, true);
        logFields[0].SetValue(null, true);
        try
        {
            foreach (Exception failure in new Exception[]
                { new ExplorerLimitException(privateSource), new InvalidOperationException(privateSource) })
            {
                var node = NewCategory();
                metadata.Failure = failure;
                await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", node);
                AssertFailure(node, failure is ExplorerLimitException ? "Failed batch" : "Thrown exception");
                await RuntimeWpfIdleAsync();

                metadata.Failure = null;
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                metadata.BeforeRead = async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); };
                Task retry = (Task)RuntimeWpfInvoke(main, "LoadRelationalChildrenAsync", node)!;
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    RuntimeWpfAssert(!node.IsLoaded && node.Children.Single().Name == "Loading...", check,
                        "Retry restores the loading placeholder only while the provider is pending");
                    release.TrySetResult();
                    await retry.WaitAsync(TimeSpan.FromSeconds(10));
                    RuntimeWpfAssert(node.IsLoaded && node.Children.Count == 1 && node.Children[0].Name == "dbo.RetryProc" &&
                        summaries.ContainsKey(node.Children[0]), check,
                        "Successful retry replaces the error/loading row with mapped children");
                    int reads = metadata.Reads;
                    await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", node);
                    RuntimeWpfAssert(metadata.Reads == reads, check, "Successfully loaded children are not fetched again");
                }
                finally { release.TrySetResult(); await retry.WaitAsync(TimeSpan.FromSeconds(10)); }
                metadata.BeforeRead = _ => Task.CompletedTask;
            }

            metadata.Empty = true;
            var empty = NewCategory();
            metadata.Failure = new ExplorerLimitException(privateSource);
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", empty);
            AssertFailure(empty, "Empty-folder retry setup");
            await RuntimeWpfIdleAsync();
            metadata.Failure = null;
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", empty);
            RuntimeWpfAssert(empty.IsLoaded && empty.Children.Count == 0, check,
                "Successful empty retry is loaded, not a cached failure or loading placeholder");
            metadata.Empty = false;

            string log = File.ReadAllText(logPath);
            RuntimeWpfAssert(log.Contains("FailureCode: MetadataLimit", StringComparison.Ordinal) &&
                log.Contains("FailureCode: InvalidOperationException", StringComparison.Ordinal) &&
                log.Contains("ScopeKey: 1", StringComparison.Ordinal) && log.Contains("ScopeResourceKey: 20", StringComparison.Ordinal) &&
                log.Contains("SnapshotKey: 10", StringComparison.Ordinal) && log.Contains("Category: Procedures", StringComparison.Ordinal) &&
                log.Contains("StackTrace:", StringComparison.Ordinal) && log.Contains("ReadDatabaseCategoryAsync", StringComparison.Ordinal) &&
                !log.Contains(privateSource, StringComparison.Ordinal) && !log.Contains(".cs:line", StringComparison.Ordinal), check,
                "Failures log typed owner context and exception method stacks without provider messages, source identifiers or file locations");

            foreach (string outcome in new[] { "FailedBatch", "Exception", "Success", "RemovedNode" })
            {
                await RuntimeWpfIdleAsync();
                RuntimeWpfSetField(main, "_relationalExplorerOwner", Guid.NewGuid());
                var node = NewCategory();
                metadata.Failure = outcome == "FailedBatch" ? new ExplorerLimitException(privateSource)
                    : outcome is "Exception" or "RemovedNode" ? new InvalidOperationException(privateSource) : null;
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                metadata.BeforeRead = async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); };
                Task pending = (Task)RuntimeWpfInvoke(main, "LoadRelationalChildrenAsync", node)!;
                try
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (outcome == "RemovedNode") summaries.Remove(node);
                    else RuntimeWpfSetField(main, "_relationalExplorerOwner", Guid.NewGuid());
                    var replacement = new FileSystemNode("replacement", false, displayName: "Replacement owner row");
                    node.Children.Clear(); node.Children.Add(replacement);
                    RuntimeWpfSetField(main, "_statusText", "Replacement owner status");
                    release.TrySetResult();
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    RuntimeWpfAssert(!node.IsLoaded && node.Children.Count == 1 && ReferenceEquals(node.Children[0], replacement) &&
                        main.StatusText == "Replacement owner status", check,
                        outcome + ": retired owner or removed node cannot publish children, errors, or status");
                }
                finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            RuntimeWpfAssert(runtime.Session.Metrics.Snapshot().Started == 0, check,
                "Explorer child terminal-state checks execute no SQL commands");
        }
        finally
        {
            for (int i = 0; i < logFields.Length; i++) logFields[i].SetValue(null, previousLogValues[i]);
            if (File.Exists(logPath)) File.Delete(logPath);
            await loader.DisposeAsync();
            await RuntimeWpfInvokeTaskAsync(cancellation, "RetireAsync");
            RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
            main.Close();
            application.MainWindow = previousMainWindow;
        }

        FileSystemNode NewCategory()
        {
            main.RootNodes.Clear(); summaries.Clear();
            var node = (FileSystemNode)RuntimeWpfInvoke(main, "CreateRelationalExplorerNode", category)!;
            main.RootNodes.Add(node);
            return node;
        }

        void AssertFailure(FileSystemNode node, string scenario)
        {
            RuntimeWpfAssert(!node.IsLoaded && node.Children.Count == 1 && !node.Children[0].Exists &&
                !node.Children[0].IsDirectory && node.Children[0].Name == "Unavailable - retry expansion" &&
                node.Children[0].ToolTip?.Contains("retry", StringComparison.Ordinal) == true &&
                summaries.Count == 1, check, scenario + ": terminal failure replaces Loading with a retryable error row");
        }
    }

    private sealed class ExplorerChildrenUiMetadata(ExplorerScope scope) : IExplorerMetadataQueries
    {
        internal Exception? Failure;
        internal bool Empty;
        internal int Reads;
        internal Func<CancellationToken, Task> BeforeRead = _ => Task.CompletedTask;
        public Task<ExplorerScope> ReadScopeAsync(long scopeKey, IReadOnlySet<string>? unloadedResourceIds,
            string? sortCultureName, CancellationToken ct) => Task.FromResult(scope);
        public Task<bool> IsCurrentAsync(IndexRequestContext context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        public async IAsyncEnumerable<ExplorerDatabaseItem> ReadDatabaseCategoryAsync(ExplorerScope selected,
            ExplorerResource resource, ExplorerCategory category, [EnumeratorCancellation] CancellationToken ct)
        {
            Reads++;
            await BeforeRead(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            if (!Empty) yield return new(new(1, Guid.Empty, 10, DatabaseVersionedResourceKind.StoredProcedure,
                "dbo", "RetryProc", "SP|dbo.RetryProc", 101, 0, []), SqlDatabaseObjectKind.StoredProcedure);
        }
    }
}

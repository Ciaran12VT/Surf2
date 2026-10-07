using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

public static partial class StorageRegressionSuite
{
    private sealed record SampleRoot(long Key, long Scope, ResourceKind Kind, string Original, string Mapped);

    // The caller must restore the backup to a dedicated generated LocalDB instance/database first.
    // Never use the application's bootstrap connection, normal App.OnStartup, or original source paths.
    public static async Task RunSampleDatabaseChecksAsync(string connection, string ownedDirectory, string mode, Action<bool, string> check)
    {
        var builder = new SqlConnectionStringBuilder(connection);
        const string prefix = "Surf2_Regression_Sample_";
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedDirectory));
        if (!builder.DataSource.StartsWith("(localdb)\\Surf2Sample_", StringComparison.OrdinalIgnoreCase) ||
            !builder.InitialCatalog.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(builder.InitialCatalog[prefix.Length..], "N", out _) || !builder.IntegratedSecurity ||
            Path.GetDirectoryName(directory) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            Path.GetFileName(directory) != builder.InitialCatalog || mode is not ("missing" or "mapped" or "changed" or "restart" or "pooled" or "views"))
            throw new InvalidOperationException("Sample checks refuse anything except an explicitly restored, generated LocalDB fixture and its matching temporary directory.");
        if (!File.Exists(Path.Combine(directory, "sample.bak"))) throw new InvalidOperationException("The owned restored backup is missing.");
        builder.ApplicationName = "Surf2_Regression_StateAccess_Sample_" + mode;
        builder.Pooling = mode is "pooled" or "views";
        builder.TrustServerCertificate = true;
        connection = builder.ConnectionString;
        var session = new RelationalSession(connection);
        await session.RequireReadyAsync();
        var roots = await SampleReadRootsAsync(session, directory);
        if (mode == "missing") roots = roots.Select(r => r with
            { Mapped = Path.Combine(directory, "unavailable", r.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(r.Mapped)) }).ToList();
        check(roots.All(r => Path.GetFullPath(r.Mapped).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
            "All sample physical resources are confined to the owned fixture directory");
        if (mode is "missing" or "mapped" or "changed") await SampleMapRootsAsync(session, roots);
        if (mode is "mapped" or "changed") await SamplePopulateFilesAsync(session, roots, directory, check, mode == "changed");
        string stateBefore = await SampleSourceVersionsAsync(session);
        if (mode == "views") await SampleCapturedViewsAsync(connection, directory, check);
        else await SampleRunWpfAsync(connection, directory, mode, check);
        check(stateBefore == await SampleSourceVersionsAsync(session),
            "Startup and reference checks do not save preferences, workspace, scopes, workbenches, diagrams or captured-source catalogues");
    }

    private static async Task SampleCapturedViewsAsync(string connection, string directory, Action<bool, string> check)
    {
        var owner = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var refresher = new RelationalScopeIndexRefresher(owner.Session, owner.Index, owner.Snapshots);
        var keys = new List<long>();
        await using (var sql = await owner.Session.OpenAsync())
        await using (var command = sql.CreateCommand())
        {
            command.CommandText = "SELECT ScopeKey FROM surf.Scope ORDER BY ScopeKey;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) keys.Add(reader.GetInt64(0));
        }
        var report = new StringBuilder();
        foreach (long key in keys)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var original = await owner.Explorer.OpenScopeAsync(key, ct: deadline.Token);
            var excluded = original.Resources.Where(r => r.Kind is ResourceKind.File or ResourceKind.Folder).Select(r => r.ResourceId).ToHashSet();
            var view = await owner.Explorer.OpenScopeAsync(key, excluded, ct: deadline.Token);
            var policy = ExplorerIndexLanguagePolicy.Capture(new CodeWindowSettings());
            var clock = Stopwatch.StartNew();
            TimeSpan lastProgress = TimeSpan.Zero;
            Console.WriteLine("SAMPLE views: preparing captured references for " + view.Name);
            var completed = await refresher.RefreshAsync(view, policy, progress =>
            {
                if (clock.Elapsed - lastProgress >= TimeSpan.FromSeconds(5) || progress.Completed)
                {
                    Console.WriteLine($"SAMPLE views: {view.Name}; {progress.Phase}; {progress.Considered} checked; {progress.Published} indexed; {progress.Failed} failures; {clock.Elapsed.TotalSeconds:F1}s");
                    lastProgress = clock.Elapsed;
                }
                return Task.CompletedTask;
            }, deadline.Token);
            check(completed.FullyPublished, "Captured reference discovery succeeds for actual scope " + view.Name);
            view = view with { Context = completed.Context! }; owner.References.AcceptCompletedDiscovery(completed);
            var catalogue = await owner.References.LoadPaintAsync(view, deadline.Token);
            check(catalogue.Coverage.FullyPublished && catalogue.Coverage.Documents > 0,
                "The actual scope's full captured-symbol catalogue fits its metadata budgets: " + view.Name);
            await SampleMetadataPlanAsync(owner, view, directory, check, deadline.Token);
            long document; string token;
            await using (var sql = await owner.Session.OpenAsync(deadline.Token))
            await using (var command = sql.CreateCommand())
            {
                command.CommandText = """
SELECT TOP(1) d.DocumentKey,s.QualifiedName FROM surf.Document d
JOIN surf.SymbolDefinition s ON s.DocumentRevisionKey=d.CurrentRevisionKey
WHERE d.Kind=2 AND s.Kind=@Kind AND EXISTS(SELECT 1 FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 WHERE m.DocumentKey=d.DocumentKey AND sr.ScopeKey=@Scope AND sr.Kind=2) ORDER BY s.SymbolKey;
""";
                command.Parameters.Add(RelationalSession.Parameter("@Scope", SqlDbType.BigInt, key));
                command.Parameters.Add(RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)ReferenceEntityKind.Table));
                await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                if (!await reader.ReadAsync(deadline.Token)) throw new InvalidOperationException("The sample scope has no indexed captured table.");
                document = reader.GetInt64(0); token = reader.GetString(1);
            }
            var result = await owner.References.ResolveVerifiedWithCoverageAsync(view, [new(token)], deadline.Token);
            check(result.Targets[0].Candidates.Any(c => c.DocumentKey == document && c.Freshness == IndexFreshness.Indexed),
                "The actual scope resolves a selected current captured table: " + view.Name);
            TimeSpan cold = clock.Elapsed;
            var restarted = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
            var restartedRefresher = new RelationalScopeIndexRefresher(restarted.Session, restarted.Index, restarted.Snapshots);
            var reopened = await restarted.Explorer.OpenScopeAsync(key, excluded, ct: deadline.Token);
            var warm = await restartedRefresher.RefreshAsync(reopened, policy, ct: deadline.Token);
            check(warm.FullyPublished && warm.Considered == 0 && warm.Published == 0,
                "A fresh coordinator reuses every captured-root checkpoint for actual scope " + view.Name);
            report.AppendLine($"scope={view.Name};ready_seconds={cold.TotalSeconds:F3};documents={catalogue.Coverage.Documents};warm_checked={warm.Considered};warm_published={warm.Published}");
        }
        check(keys.Count == 4 && owner.Session.Metrics.Snapshot().Failed == 0, "All four supplied scopes have captured reference coverage without SQL command failures");
        await File.WriteAllTextAsync(Path.Combine(directory, "views-results.txt"), report.ToString());
        Console.WriteLine(report);
    }

    private static async Task SampleMetadataPlanAsync(RelationalRuntime owner, ExplorerScope view, string directory,
        Action<bool, string> check, CancellationToken ct)
    {
        await using var connection = await owner.Session.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await IndexSql.CheckContextAsync(connection, transaction, owner.Session, view.Context, ct);
        await using var command = IndexSql.Command(connection, transaction, "SET STATISTICS XML ON;\n" + RelationalReferenceService.MetadataSql);
        IndexSql.ScopeParameters(command, view.Context);
        IndexSql.Add(command, "@MaximumRows", SqlDbType.BigInt, 100001L);
        var plans = new List<XDocument>();
        long memberships = -1, symbols = 0;
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            do
            {
                while (await reader.ReadAsync(ct))
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string))
                        plans.Add(XDocument.Parse(reader.GetString(0)));
                    else if (reader.FieldCount == 2) memberships = reader.GetInt64(0);
                    else if (reader.FieldCount == 18) symbols++;
                    else throw new InvalidOperationException("Unexpected sample metadata-plan result.");
                }
            } while (await reader.NextResultAsync(ct));
        }
        await transaction.CommitAsync(ct);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        long maximumGrant = plans.SelectMany(p => p.Descendants(ns + "MemoryGrantInfo"))
            .Select(p => (long?)p.Attribute("RequestedMemory") ?? (long?)p.Attribute("SerialDesiredMemory") ?? 0).DefaultIfEmpty().Max();
        bool wideSort = plans.SelectMany(p => p.Descendants(ns + "RelOp")).Any(p =>
            (string?)p.Attribute("PhysicalOp") == "Sort" && ((long?)p.Attribute("AvgRowSize") ?? 0) > 512);
        for (int i = 0; i < plans.Count; i++)
            await File.WriteAllTextAsync(Path.Combine(directory, $"metadata-scope-{view.Context.ScopeKey}-{i}.sqlplan"), plans[i].ToString(), ct);
        check(plans.Count > 0 && !wideSort && maximumGrant <= 16 * 1024,
            $"Actual captured metadata plan has no wide sort and requests at most 16 MiB: {view.Name} ({maximumGrant} KiB)");
        check(memberships > 0 && symbols > 0, "Plan verification consumes the actual scope's full metadata result: " + view.Name);
        Console.WriteLine($"SAMPLE metadata plan: {view.Name}; {memberships} memberships; {symbols} symbols; maximum requested grant={maximumGrant} KiB");
    }

    private static async Task<string> SampleSourceVersionsAsync(RelationalSession session)
    {
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT Kind,Id,CONVERT(varchar(16),Version,2) FROM (
 SELECT 'scope' Kind,ScopeKey Id,Version FROM surf.Scope
 UNION ALL SELECT 'diagram',DiagramKey,Version FROM surf.Diagram
 UNION ALL SELECT 'workbench',WorkbenchKey,Version FROM surf.Workbench
 UNION ALL SELECT 'preference',ProfileKey,Version FROM surf.ApplicationPreference
 UNION ALL SELECT 'workspace',ProfileKey,Version FROM surf.WorkspaceSession
 UNION ALL SELECT 'snapshot',SnapshotKey,RowVersion FROM surf.DatabaseSnapshot
 UNION ALL SELECT 'captured-head',UserKey,RowVersion FROM surf.SnapshotCatalogueHead
) v ORDER BY Kind,Id;
""";
        var result = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Append(reader.GetString(0)).Append(':').Append(reader.GetInt64(1)).Append(':').Append(reader.GetString(2)).Append(';');
        return result.ToString();
    }

    private static async Task<List<SampleRoot>> SampleReadRootsAsync(RelationalSession session, string directory)
    {
        var result = new List<SampleRoot>();
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ScopeResourceKey,ScopeKey,Kind,Path FROM surf.ScopeResource WHERE Kind IN (0,1) ORDER BY ScopeResourceKey;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string original = reader.GetString(3);
            string mapped;
            if (original.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) mapped = original;
            else
            {
                string key = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(original)))[..16];
                mapped = Path.Combine(directory, "physical", key);
                if ((ResourceKind)reader.GetInt32(2) == ResourceKind.File) mapped = Path.Combine(mapped, Path.GetFileName(original));
            }
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), (ResourceKind)reader.GetInt32(2), original, mapped));
        }
        // Retain original roots for reconstructing captured file text on subsequent test invocations.
        string manifest = Path.Combine(directory, "resource-mapping.json");
        if (File.Exists(manifest)) return System.Text.Json.JsonSerializer.Deserialize<List<SampleRoot>>(await File.ReadAllTextAsync(manifest))!;
        await File.WriteAllTextAsync(manifest, System.Text.Json.JsonSerializer.Serialize(result));
        return result;
    }

    private static async Task SampleMapRootsAsync(RelationalSession session, List<SampleRoot> roots)
    {
        await using var connection = await session.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        foreach (var root in roots)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE surf.ScopeResource SET Path=@Path WHERE ScopeResourceKey=@Key AND Kind=@Kind;";
            command.Parameters.Add(RelationalSession.Parameter("@Path", SqlDbType.NVarChar, root.Mapped, -1));
            command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, root.Key));
            command.Parameters.Add(RelationalSession.Parameter("@Kind", SqlDbType.Int, (int)root.Kind));
            if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Sample root mapping changed.");
        }
        foreach (long scope in roots.Select(r => r.Scope).Distinct())
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE surf.Scope SET PublicationId=NEWID() WHERE ScopeKey=@Scope;";
            command.Parameters.Add(RelationalSession.Parameter("@Scope", SqlDbType.BigInt, scope));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task SamplePopulateFilesAsync(RelationalSession session, List<SampleRoot> roots, string directory, Action<bool, string> check, bool changed)
    {
        foreach (var root in roots)
        {
            string parent = root.Kind == ResourceKind.Folder ? root.Mapped : Path.GetDirectoryName(root.Mapped)!;
            Directory.CreateDirectory(parent);
            if (root.Kind == ResourceKind.File && !File.Exists(root.Mapped)) await File.WriteAllTextAsync(root.Mapped, "// Owned sample fixture\n");
        }
        int files = 0;
        string edit = "\n// Owned fixture edit for concurrent reference-publication checks: " + Guid.NewGuid().ToString("N") + "\n";
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT f.OriginalPath,c.ByteCount,c.Text FROM surf.Document d JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey
JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey JOIN surf.TextContent c ON c.ContentKey=r.ContentKey
WHERE d.Kind=0 AND d.Freshness=1 ORDER BY d.DocumentKey;
""";
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        while (await reader.ReadAsync())
        {
            string original = reader.GetString(0);
            long bytes = reader.GetInt64(1);
            if (bytes > 16 * 1024 * 1024) throw new InvalidOperationException("Sample physical text exceeds its single-file budget.");
            string text = reader.GetString(2);
            foreach (var root in roots)
            {
                string? relative = root.Kind == ResourceKind.File
                    ? original.Equals(root.Original, StringComparison.OrdinalIgnoreCase) ? "" : null
                    : ExplorerCompatibility.IsWithinPhysicalRoot(original, root.Original) ? Path.GetRelativePath(root.Original, original) : null;
                if (relative == null) continue;
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".vs" or ".git" or "bin" or "obj" or "node_modules")) continue;
                string target = Path.GetFullPath(relative.Length == 0 ? root.Mapped : Path.Combine(root.Mapped, relative));
                if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Sample reconstruction refuses a path outside its owned root.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, changed && Path.GetExtension(target).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? text + edit : text, new UTF8Encoding(false));
                files++;
            }
        }
        check(files > 400, $"Reconstructed {files} fixture file occurrences from the supplied persisted index; originals were not read or changed");
    }

    private static async Task SampleRunWpfAsync(string connection, string directory, string mode, Action<bool, string> check)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                Surf2.App? app = null;
                try
                {
                    if (Application.Current != null) throw new InvalidOperationException("Sample UI checks require a fresh process.");
                    app = new Surf2.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    await SampleMainWindowAsync(connection, directory, mode, check);
                    completion.TrySetResult(true);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { app?.Shutdown(); dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Surf2 restored sample database WPF checks" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task; }
        finally { if (!await Task.Run(() => thread.Join(TimeSpan.FromSeconds(30)))) throw new TimeoutException("Sample UI dispatcher did not exit."); }
    }

    private static async Task SampleMainWindowAsync(string connection, string directory, string mode, Action<bool, string> check)
    {
        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        var root = new Grid { DataContext = main };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources); root.Resources.MergedDictionaries.Add(main.Resources);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 restored sample offscreen host")
        { PositionX = -32000, PositionY = -32000, Width = 1400, Height = 1000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var clock = Stopwatch.StartNew();
        TimeSpan startup, captured;
        try
        {
            var content = (FrameworkElement)main.Content; main.Content = null; root.Children.Add(content);
            RuntimeWpfArrange(root, 1280, 820); host.RootVisual = root;
            var start = RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync") as Task<bool>
                ?? throw new InvalidOperationException("Missing startup hook.");
            check(await start.WaitAsync(TimeSpan.FromMinutes(3)), "Restored sample startup succeeds through the real relational startup path");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            RuntimeWpfInvoke(main, "HideMainCanvasLoading");
            startup = clock.Elapsed;
            var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
            check(RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope").Name == "Tempest and Friends", "Restored sample loads the actual saved scope and unloaded selection");
            Task expansion = ExpandProceduresAsync();
            string last = "";
            while (RuntimeWpfOptionalField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue")?.Coverage.FullyPublished != true)
            {
                string status = main.StatusText;
                if (status != last) { Console.WriteLine("SAMPLE " + mode + ": " + status); last = status; }
                var refresh = RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask");
                if (refresh.IsCompleted) throw new InvalidOperationException("Sample captured references never became ready: " + status);
                if (clock.Elapsed > TimeSpan.FromMinutes(8)) throw new TimeoutException("Sample captured-reference readiness exceeded eight minutes.");
                await Task.Delay(500);
            }
            captured = clock.Elapsed;
            Console.WriteLine($"SAMPLE {mode}: startup={startup.TotalSeconds:F3}s; captured-ready={captured.TotalSeconds:F3}s");
            await expansion.WaitAsync(TimeSpan.FromSeconds(90));
            var scope = await (Task<ExplorerScope>)RuntimeWpfInvoke(main, "CurrentRelationalReferenceViewAsync", CancellationToken.None)!;
            var documents = RuntimeWpfField<RelationalDocumentService>(main, "_relationalDocuments");
            var source = await documents.ResolveResourceAsync(5, 1193, scope);
            await RuntimeWpfInvokeTaskAsync(main, "OpenFileAsync", source.DocumentPath);
            var sourceWindow = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Single(w => w.State.BoundResourceKey == 1193);
            check(sourceWindow.Text.Contains("r_Zapp_Shifts", StringComparison.Ordinal), "The supplied dbo.sproc_batchQs contains the clicked table reference");
            var request = new ReferenceNavigationRequestedEventArgs("r_Zapp_Shifts", 158, 6, null);
            (ExplorerScope View, ImmutableArray<SymbolSummary> Candidates) resolution;
            using (await (Task<IDisposable>)RuntimeWpfInvoke(main, "EnterRelationalReferenceReadAsync", CancellationToken.None)!)
                resolution = await RuntimeWpfResolveReferenceAsync(main, request, source.DocumentPath);
            check(resolution.Candidates.Length == 1 && resolution.Candidates[0].Definition.Kind == ReferenceEntityKind.Table &&
                resolution.Candidates[0].Definition.QualifiedName == "dbo.r_Zapp_Shifts", "The real corpus resolves the clicked table without an ambiguous picker");
            await RuntimeWpfInvokeTaskAsync(main, "PreviewRelationalReferenceAsync", request, source.DocumentPath);
            Console.WriteLine($"SAMPLE {mode}: preview-status={main.StatusText}; preview-length={RuntimeWpfNamed<ICSharpCode.AvalonEdit.TextEditor>(main, "PreviewEditor").Text.Length}");
            check(RuntimeWpfNamed<ICSharpCode.AvalonEdit.TextEditor>(main, "PreviewEditor").Text.Contains("FROM [dbo].[r_Zapp_Shifts];", StringComparison.Ordinal),
                "The real clicked table is previewed from its selected captured metadata");
            var navigation = Stopwatch.StartNew();
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", request, source.DocumentPath, sourceWindow);
            check(RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(w => w.State.BoundSnapshotKey == 5 && w.State.BoundResourceKey == 1523 &&
                w.Text.Contains("FROM [dbo].[r_Zapp_Shifts];", StringComparison.Ordinal)), "Double-click opens the exact NexusLive table from the screenshot");
            Console.WriteLine($"SAMPLE {mode}: table-navigation={navigation.Elapsed.TotalMilliseconds:F1}ms; status={main.StatusText}");
            foreach (var (token, resource) in new[] { ("r_Zapp_Shifts_calc_details", 1524L), ("r_Zapp_Shifts_messages", 1525L),
                ("r_Zapp_Shifts_recordIds", 1526L), ("r_Zapp_Shifts_shift_parts", 1527L) })
            {
                await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync",
                    new ReferenceNavigationRequestedEventArgs(token, 1, 1, null), source.DocumentPath, sourceWindow);
                check(RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(w => w.State.BoundSnapshotKey == 5 &&
                    w.State.BoundResourceKey == resource && w.Text.Contains("FROM [dbo].[" + token + "];", StringComparison.Ordinal)),
                    "Repeated background-index navigation opens the selected captured table " + token);
            }
            await SampleWaitForIndexAsync(main, mode);
            TimeSpan finished = clock.Elapsed;
            var progress = RuntimeWpfField<ExplorerIndexRefreshProgress>(main, "_relationalIndexProgress");
            var catalogue = RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue");
            check(catalogue.Coverage.FullyPublished, "Captured references remain usable after the physical stage terminates");
            if (mode == "missing") check(progress.Failed > 0 && !progress.FullyPublished,
                "Missing machine-local resources are reported incomplete without disabling captured table navigation");
            else check(progress.FullyPublished, "Mapped real-corpus physical reconciliation finishes without errors or SQL timeouts");
            if (mode == "changed") check(progress.Published > 100, "Concurrent navigation remains correct while more than one hundred changed physical files publish new generations");
            if (mode is "restart" or "pooled") check(progress.Published == 0 && progress.Considered < 1000 && progress.Unchanged >= 11676 && progress.ReusedResources >= 22,
                "A fresh process reuses captured completion checkpoints and verifies only physical files, without rebuilding any unchanged document");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", request, source.DocumentPath, sourceWindow);
            check(RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(w => w.State.BoundResourceKey == 1523),
                "The table remains navigable after all index work has finished");
            var metrics = runtime.Session.Metrics.Snapshot();
            check(metrics.Failed == 0, "The sample run has no SQL command failures");
            var latencies = new List<double>();
            using (var trace = new StateAccessSqlTrace(connection))
            {
                for (int i = 0; i < 20; i++)
                {
                    var lookupClock = Stopwatch.StartNew();
                    var cached = await RuntimeWpfResolveReferenceAsync(main, request, source.DocumentPath);
                    latencies.Add(lookupClock.Elapsed.TotalMilliseconds);
                    if (cached.Candidates.Length != 1 || cached.Candidates[0].Definition.QualifiedName != "dbo.r_Zapp_Shifts")
                        throw new InvalidOperationException("Repeated real-corpus reference resolution changed the selected table.");
                }
                StateAccessNoReads(trace.Commands, check, "Real-corpus repeated lookup", "SymbolDefinition", "ResourceDocument", "TextContent", "TableColumnRevision", "Asset");
                check(trace.Commands.Count <= 250, "Twenty real-corpus reference lookups use bounded generation/source guards rather than scope-sized SQL work");
            }
            latencies.Sort();
            RuntimeWpfCheckEmptyLibraries(main, check);
            await RuntimeWpfIdleAsync(); root.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 820, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var screenshot = File.Create(Path.Combine(directory, mode + "-view.png"))) encoder.Save(screenshot);
            string report = $"mode={mode}\nstartup_seconds={startup.TotalSeconds:F3}\ncaptured_seconds={captured.TotalSeconds:F3}\n" +
                $"finished_seconds={finished.TotalSeconds:F3}\nlookup_median_ms={latencies[10]:F3}\nlookup_p95_ms={latencies[18]:F3}\n" +
                $"managed_memory_mib={GC.GetTotalMemory(false) / 1048576.0:F1}\nworking_set_mib={Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1}\n" +
                $"checked={progress.Considered}\npublished={progress.Published}\n" +
                $"unchanged={progress.Unchanged}\nreused_roots={progress.ReusedResources}\nfailures={progress.Failed}\n" +
                $"sql_commands={metrics.Started}\nsql_failures={metrics.Failed}\nstatus={main.StatusText}\n";
            await File.WriteAllTextAsync(Path.Combine(directory, mode + "-results.txt"), report);
            Console.WriteLine("SAMPLE RESULT: " + report.Replace('\n', ';'));

            async Task ExpandProceduresAsync()
            {
                var nodes = RuntimeWpfField<Dictionary<FileSystemNode, ExplorerNodeSummary>>(main, "_relationalExplorerNodes");
                var database = nodes.Single(p => p.Value.Role == ExplorerNodeRole.Resource && p.Key.Name == "TempestTest").Key;
                await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", database);
                var procedures = database.Children.Single(n => nodes.TryGetValue(n, out var summary) && summary.Category == ExplorerCategory.Procedures);
                await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", procedures);
                check(procedures.IsLoaded && procedures.Children.Count > 100 && procedures.Children.All(n => n.Name != "Loading..."),
                    "Actual TempestTest Stored Procedures expand while reference preparation runs, without a stuck loading row");
            }
        }
        finally
        {
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            try { await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync"); }
            finally
            {
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true); host.RootVisual = null;
                root.Children.Clear(); root.Resources.MergedDictionaries.Clear(); main.Close();
            }
        }
    }

    private static async Task SampleWaitForIndexAsync(Surf2.MainWindow main, string mode)
    {
        var clock = Stopwatch.StartNew(); string last = "";
        while (true)
        {
            var refresh = RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask");
            if (refresh.IsCompleted)
            {
                await refresh; await RuntimeWpfField<Task>(main, "_relationalHighlightTask"); await RuntimeWpfIdleAsync();
                if (ReferenceEquals(refresh, RuntimeWpfField<Task>(main, "_relationalIndexRefreshTask"))) return;
            }
            if (clock.Elapsed > TimeSpan.FromMinutes(12)) throw new TimeoutException("Sample physical reconciliation exceeded twelve minutes.");
            string status = main.StatusText;
            if (status != last) { Console.WriteLine("SAMPLE " + mode + ": " + status); last = status; }
            await Task.Delay(500);
        }
    }
}

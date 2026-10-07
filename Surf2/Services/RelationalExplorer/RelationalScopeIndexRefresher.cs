using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

// Capture on the dispatcher; workers never read mutable application settings or GUI objects.
public sealed record ExplorerIndexLanguagePolicy(ImmutableArray<(string Extension, string Language)> Extensions)
{
    public static ExplorerIndexLanguagePolicy Capture(CodeWindowSettings settings) => new(settings.BackcolorsByExtension
        .Select(s => (s.Extension, s.Language)).ToImmutableArray());
    public string LanguageFor(string path)
    {
        string extension = Path.GetExtension(path);
        var setting = Extensions.FirstOrDefault(s => string.Equals(s.Extension, extension, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(setting.Language) ? CodeWindowSettings.GetDefaultLanguageForExtension(extension)
            : CodeWindowSettings.NormalizeLanguage(setting.Language);
    }
}

public sealed record ExplorerIndexRefreshProgress(long Considered, long Published, long Unchanged, long Failed,
    long UnloadedResources, bool Completed, bool DiscoveryReconciled, IndexRequestContext? Context = null,
    string? FailureCode = null, string? Resource = null, string? Document = null,
    string Phase = "Discovering", TimeSpan Elapsed = default, string? FailedResource = null, string? FailedDocument = null,
    long ReusedResources = 0)
{
    public bool FullyPublished => Completed && DiscoveryReconciled && Failed == 0;
}

// A single-flight, single-document worker. There is no scope-sized content/syntax cache or task fan-out.
public sealed partial class RelationalScopeIndexRefresher
{
    private readonly RelationalSession _session;
    private readonly RelationalIndexStore _index;
    private readonly RelationalSnapshotStore _snapshots;
    private readonly RelationalExplorerMetadataQueries _metadata;
    private readonly IPhysicalExplorerQueries _physical;
    private readonly ExplorerLimits _limits;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly SemaphoreSlim _publicationGate = new(1, 1);

    // Pause local catalogue mutations for a foreground request, not for the whole refresh.
    // SQL/domain fences still reject changes made by other writers or processes.
    internal async Task<IDisposable> EnterReferenceReadAsync(CancellationToken ct)
    {
        await _publicationGate.WaitAsync(ct).ConfigureAwait(false);
        return new PublicationLease(_publicationGate);
    }

    private sealed class PublicationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
    public RelationalScopeIndexRefresher(RelationalSession session, RelationalIndexStore index,
        RelationalSnapshotStore snapshots, IPhysicalExplorerQueries? physical = null, ExplorerLimits? limits = null)
    {
        _session = session; _index = index; _snapshots = snapshots; _metadata = new(session, index);
        _physical = physical ?? new PhysicalExplorerQueries(); _limits = limits ?? new(); _limits.Validate();
    }

    public async Task<ExplorerIndexRefreshProgress> RefreshAsync(ExplorerScope scope, ExplorerIndexLanguagePolicy languagePolicy,
        Func<ExplorerIndexRefreshProgress, Task>? progress = null, CancellationToken ct = default,
        IReadOnlySet<long>? physicalRootsToReconcile = null)
    {
        _session.RejectValidationWrite();
        await _singleFlight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Reuse file completion only inside a continuously watched session. Default calls and
            // every app/scope reopen reconcile files, including changes made while Surf was closed.
            if (physicalRootsToReconcile == null) _verifiedPhysicalRoots.Clear();
            else foreach (long root in physicalRootsToReconcile) _verifiedPhysicalRoots.TryRemove(root, out _);
            return await RefreshCoreAsync(scope, languagePolicy, progress, ct).ConfigureAwait(false);
        }
        finally { _singleFlight.Release(); }
    }

    private async Task<ExplorerIndexRefreshProgress> RefreshCoreAsync(ExplorerScope scope, ExplorerIndexLanguagePolicy languagePolicy,
        Func<ExplorerIndexRefreshProgress, Task>? progress, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        if (progress != null) await progress(new(0, 0, 0, 0, scope.Resources.Count(r => !r.IsLoaded), false, false,
            Phase: "Checking resource checkpoints", Elapsed: elapsed.Elapsed)).ConfigureAwait(false);
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await EnsureCompletionStoreAsync(ct).ConfigureAwait(false);
        var reusable = await ReadCompletedRootsAsync(scope, languagePolicy, ct).ConfigureAwait(false);
        // A changed alias rebuilds its entire typed owner group, so no cached alias can be pruned accidentally.
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && CapturedRoot(r)))
        {
            var aliases = scope.Resources.Where(r => r.IsLoaded && (resource.Snapshot != null
                ? r.Snapshot?.SnapshotKey == resource.Snapshot.SnapshotKey : r.DiagramRevisionKey == resource.DiagramRevisionKey));
            if (aliases.Any(r => !reusable.ContainsKey(r.ScopeResourceKey)))
                foreach (var alias in aliases) reusable.Remove(alias.ScopeResourceKey);
        }
        bool removed;
        do
        {
            removed = false;
            var changedRoots = scope.Resources.Where(r => r.IsLoaded && PhysicalRoot(r) && !reusable.ContainsKey(r.ScopeResourceKey)).ToArray();
            foreach (var cached in scope.Resources.Where(r => PhysicalRoot(r) && reusable.ContainsKey(r.ScopeResourceKey)))
                if (changedRoots.Any(r => OwnsFile(r, cached.Path) || OwnsFile(cached, r.Path)))
                    removed |= reusable.Remove(cached.ScopeResourceKey);
        } while (removed);
        bool hasWork = scope.Resources.Any(r => r.IsLoaded && !reusable.ContainsKey(r.ScopeResourceKey));
        if (hasWork)
        {
            using var publication = await EnterReferenceReadAsync(ct).ConfigureAwait(false);
            await InvalidateDiscoveryAsync(scope, ct).ConfigureAwait(false);
        }
        long considered = 0, published = 0, unchanged = 0, failed = 0;
        bool reusedPhysical = scope.Resources.Any(r => PhysicalRoot(r) && reusable.ContainsKey(r.ScopeResourceKey));
        if (reusedPhysical) unchanged = await CountReusedDocumentsAsync(reusable.Keys, ct).ConfigureAwait(false);
        long reusedResources = 0;
        long unloaded = scope.Resources.Count(r => !r.IsLoaded);
        var visited = new HashSet<long>();
        var seen = new HashSet<(long Resource, long Document)>();
        var completedRoots = new HashSet<long>();
        var discoveredSnapshots = new Dictionary<long, bool>();
        var discoveredDiagrams = new Dictionary<long, bool>();
        var reusedOwners = new HashSet<(ResourceKind Kind, long Key)>();
        var budget = new ExplorerMetadataBudget(_limits);
        string? currentResource = null, currentDocument = null, lastFailure = null;
        string? failedResource = null, failedDocument = null;
        string phase = "Discovering";
        ExplorerIndexRefreshProgress Status(bool done = false, bool reconciled = false, IndexRequestContext? context = null,
            string? error = null) => new(considered, published, unchanged, failed, unloaded, done, reconciled, context,
                error ?? lastFailure, currentResource, currentDocument, phase, elapsed.Elapsed, failedResource, failedDocument, reusedResources);
        async Task ReportAsync(string? error = null)
        {
            if (error != null) { lastFailure = error; failedResource = currentResource; failedDocument = currentDocument; }
            if (progress != null) await progress(Status(error: error)).ConfigureAwait(false);
        }
        await ReportAsync().ConfigureAwait(false);
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded))
        {
            ct.ThrowIfCancellationRequested();
            if (reusable.TryGetValue(resource.ScopeResourceKey, out long count))
            {
                reusedResources++;
                if (!reusedPhysical && reusedOwners.Add((resource.Kind, resource.Snapshot?.SnapshotKey ?? resource.DiagramRevisionKey!.Value))) unchanged += count;
                continue;
            }
            await RequireDomainAsync(scope, ct).ConfigureAwait(false);
            currentResource = ExplorerCompatibility.ResourceDisplayName(resource);
            currentDocument = null; phase = "Discovering";
            await ReportAsync().ConfigureAwait(false);
            bool rootComplete = true;
            long failuresBeforeRoot = failed;
            try
            {
                if (resource.Kind == ResourceKind.File)
                    await FileAsync(resource.Path).ConfigureAwait(false);
                else if (resource.Kind == ResourceKind.Folder)
                    await WalkAsync(resource.Path, 0).ConfigureAwait(false);
                else if (resource.Kind == ResourceKind.DatabaseSnapshot)
                {
                    if (resource.Snapshot == null) { failed++; rootComplete = false; await ReportAsync("MissingSnapshot").ConfigureAwait(false); }
                    else
                    {
                        // All aliases are attached by DatabaseAsync; enumerate one immutable snapshot once.
                        if (discoveredSnapshots.TryGetValue(resource.Snapshot.SnapshotKey, out bool discovered))
                        { if (discovered) completedRoots.Add(resource.ScopeResourceKey); continue; }
                        discoveredSnapshots.Add(resource.Snapshot.SnapshotKey, false);
                        SnapshotCursor? cursor = null;
                        do
                        {
                            var page = await _snapshots.ListObjectsAsync(resource.Snapshot.SnapshotKey, pageSize: _limits.PageSize,
                                cursor: cursor, ct: ct).ConfigureAwait(false);
                            var entries = page.Items.Where(item => item.ObjectKind != SqlDatabaseObjectKind.Unknown).Select(item =>
                            {
                                var category = item.ObjectKind switch
                                {
                                    SqlDatabaseObjectKind.StoredProcedure => ExplorerCategory.Procedures,
                                    SqlDatabaseObjectKind.View => ExplorerCategory.Views,
                                    SqlDatabaseObjectKind.Function => ExplorerCategory.Functions,
                                    SqlDatabaseObjectKind.Trigger => ExplorerCategory.Triggers,
                                    _ => throw new InvalidOperationException("Unsupported object kind.")
                                };
                                return CreateDatabaseEntry(scope, resource, new(item.Resource, item.ObjectKind), category, ct);
                            }).ToArray();
                            await DatabasePageAsync(entries).ConfigureAwait(false);
                            cursor = page.Next;
                        } while (cursor != null);
                        cursor = null;
                        do
                        {
                            var page = await _snapshots.ListTablesAsync(resource.Snapshot.SnapshotKey, pageSize: _limits.PageSize,
                                cursor: cursor, ct: ct).ConfigureAwait(false);
                            var entries = page.Items.Select(item => CreateDatabaseEntry(scope, resource,
                                new(item.Resource, null, item.HasFullData, item.FullDataRowCount), ExplorerCategory.Tables, ct)).ToArray();
                            await DatabasePageAsync(entries).ConfigureAwait(false);
                            cursor = page.Next;
                        } while (cursor != null);
                        discoveredSnapshots[resource.Snapshot.SnapshotKey] = failed == failuresBeforeRoot;
                    }
                }
                else if (resource.Kind == ResourceKind.Diagram)
                {
                    if (resource.DiagramRevisionKey is not long revision) { failed++; rootComplete = false; await ReportAsync("MissingDiagram").ConfigureAwait(false); }
                    else
                    {
                        if (discoveredDiagrams.TryGetValue(revision, out bool discovered))
                        { if (discovered) completedRoots.Add(resource.ScopeResourceKey); continue; }
                        discoveredDiagrams.Add(revision, false);
                        string path = DiagramDocumentService.CreateDiagramDocumentPath(resource.Path);
                        var aliases = scope.Resources.Where(r => r.DiagramRevisionKey == revision).ToArray();
                        var memberships = aliases.Select(r => new DocumentMembership(r.ScopeResourceKey,
                            ExplorerCompatibility.ResourceDisplayName(r), path, path, ExplorerCompatibility.DiagramRootPath, r.SortOrdinal)).ToImmutableArray();
                        await DocumentAsync(new DiagramDocumentOwner(revision), resource.DiagramName ?? resource.Path, "Diagram",
                            new("none-v1", RelationalIndexStore.DiagramRendererVersion, "explorer-v1"), memberships,
                            aliases.Select(r => new IndexLocator(r.ScopeResourceKey, 2, path)).ToImmutableArray(), null,
                            () => _index.PrepareDiagramAsync(revision, ct), null, true).ConfigureAwait(false);
                        discoveredDiagrams[revision] = failed == failuresBeforeRoot;
                    }
                }
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                if (ex is Microsoft.Data.SqlClient.SqlException)
                    InternalLogService.Error(ex, "SQL failure while discovering a relational reference resource.");
                failed++; rootComplete = false; await ReportAsync(FailureCode(ex)).ConfigureAwait(false);
            }
            if (rootComplete && failed == failuresBeforeRoot) completedRoots.Add(resource.ScopeResourceKey);

            async Task WalkAsync(string directory, int depth)
            {
                if (depth >= _limits.MaximumDepth) throw new ExplorerLimitException("Physical discovery exceeds its depth budget.");
                // Reparse-point trees cannot be declared fully discovered, including a reparse root.
                bool reparse = await Task.Run(() => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0, ct).ConfigureAwait(false);
                if (reparse) { failed++; rootComplete = false; await ReportAsync("ReparsePoint").ConfigureAwait(false); return; }
                var children = await _physical.ReadDirectoryAsync(directory, scope.SortCultureName, _limits, ct).ConfigureAwait(false);
                foreach (var entry in children)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory && ExcludedReferenceDirectory(Path.GetFileName(entry.Path))) continue;
                    budget.Add(entry.Path);
                    if (entry.IsReparsePoint) { failed++; rootComplete = false; await ReportAsync("ReparsePoint").ConfigureAwait(false); continue; }
                    try
                    {
                        if (entry.IsDirectory) await WalkAsync(entry.Path, depth + 1).ConfigureAwait(false);
                        else await FileAsync(entry.Path).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsSourceFailure(ex))
                    {
                        if (ex is Microsoft.Data.SqlClient.SqlException)
                            InternalLogService.Error(ex, "SQL failure while reconciling physical-file references.");
                        failed++; rootComplete = false; await ReportAsync(FailureCode(ex)).ConfigureAwait(false);
                    }
                }
            }
        }
        phase = "Reconciling"; currentDocument = null;
        await ReportAsync().ConfigureAwait(false);
        foreach (long resource in completedRoots)
        {
            currentResource = ExplorerCompatibility.ResourceDisplayName(scope.Resources.Single(r => r.ScopeResourceKey == resource));
            await ReportAsync().ConfigureAwait(false);
            using var publication = await EnterReferenceReadAsync(ct).ConfigureAwait(false);
            await PruneAsync(scope, resource, seen, ct).ConfigureAwait(false);
        }
        using (await EnterReferenceReadAsync(ct).ConfigureAwait(false))
            await CompleteRootsAsync(scope, completedRoots, seen, languagePolicy, failed != 0, ct).ConfigureAwait(false);
        await RequireDomainAsync(scope, ct).ConfigureAwait(false);
        var context = await _index.CaptureContextAsync(scope.Context.ScopeKey, scope.Context.UnloadedScopeResourceKeys, token: ct).ConfigureAwait(false);
        RequireSameDomain(scope.Context, context);
        if (reusable.Count != 0)
        {
            var stillReady = await ReadCompletedRootsAsync(scope, languagePolicy, ct).ConfigureAwait(false);
            if (reusable.Keys.Any(key => !stillReady.ContainsKey(key)) || !await _index.IsContextCurrentAsync(context, ct).ConfigureAwait(false))
                throw new IndexGenerationChangedException();
        }
        bool reconciled = failed == 0;
        // The persisted marker describes the entire scope, never an intentionally unloaded view.
        if (reconciled && unloaded == 0 && !context.DiscoveryReconciled)
        {
            using var publication = await EnterReferenceReadAsync(ct).ConfigureAwait(false);
            await _index.MarkDiscoveryReconciledAsync(context, ct).ConfigureAwait(false);
            context = await _index.CaptureContextAsync(context.ScopeKey, token: ct).ConfigureAwait(false);
            RequireSameDomain(scope.Context, context);
        }
        phase = "Finished"; currentResource = currentDocument = null;
        var result = Status(true, reconciled, context);
        if (progress != null) await progress(result).ConfigureAwait(false);
        return result;

        async Task FileAsync(string rawPath)
        {
            string path = Path.GetFullPath(rawPath), language = languagePolicy.LanguageFor(path);
            var aliases = scope.Resources.Where(r => OwnsFile(r, path)).ToArray();
            var memberships = aliases.Select(r => new DocumentMembership(r.ScopeResourceKey,
                r.Kind == ResourceKind.File ? ExplorerCompatibility.ResourceDisplayName(r) : Path.GetFileName(path), path, path,
                r.Kind == ResourceKind.File ? ExplorerCompatibility.RootKey : Path.GetDirectoryName(path)!, r.SortOrdinal)).ToImmutableArray();
            // Symbols are shared by document, not scope. Excluded-directory membership is filtered at reference query time.
            var policy = new IndexPolicy("definitions-v1", "physical-text-v1", "explorer-v1:" + language);
            await DocumentAsync(new FileDocumentOwner(path), Path.GetFileName(path), language, policy, memberships,
                aliases.Select(r => new IndexLocator(r.ScopeResourceKey, 0, path)).ToImmutableArray(), null, null, path, true).ConfigureAwait(false);
        }

        async Task DatabasePageAsync(IReadOnlyList<DatabaseIndexEntry> entries)
        {
            Dictionary<long, PreparedDocument> prepared;
            using (await EnterReferenceReadAsync(ct).ConfigureAwait(false))
                prepared = await PrimeDatabaseBatchAsync(scope, entries, ct).ConfigureAwait(false);
            foreach (var entry in entries)
                await DocumentAsync(entry.Registration.Owner, entry.Registration.DisplayName, entry.Registration.Language,
                    entry.Policy, entry.Memberships, entry.Locators, entry.SourceRevision, entry.Prepare, null, true,
                    prepared[((SnapshotDocumentOwner)entry.Registration.Owner).ResourceKey]).ConfigureAwait(false);
        }

        async Task DocumentAsync(DocumentOwner owner, string name, string language, IndexPolicy policy,
            ImmutableArray<DocumentMembership> memberships, ImmutableArray<IndexLocator> locators, long? sourceRevision,
            Func<Task<PreparedIndexSource>>? prepare, string? physicalPath, bool symbols, PreparedDocument? primed = null)
        {
            ct.ThrowIfCancellationRequested();
            currentDocument = name; phase = "Indexing";
            await ReportAsync().ConfigureAwait(false);
            using var publication = await EnterReferenceReadAsync(ct).ConfigureAwait(false);
            await PublishDocumentAsync().ConfigureAwait(false);
            publication.Dispose();
            await ReportAsync().ConfigureAwait(false);

            async Task PublishDocumentAsync()
            {
                var prepared = primed ?? await GetOrRegisterAsync(new(owner, name, language), ct).ConfigureAwait(false);
                var handle = prepared.Handle;
                foreach (var m in memberships)
                {
                    seen.Add((m.ScopeResourceKey, handle.DocumentKey));
                    if (seen.Count > _limits.MaximumMetadataRows) throw new ExplorerLimitException("Discovery membership budget exceeded.");
                }
                if (!visited.Add(handle.DocumentKey)) return;
                budget.Add(name);
                considered++;
                if (primed == null) await SetScopeMembershipAsync(scope, handle.DocumentKey, memberships, locators, ct).ConfigureAwait(false);
                var previous = prepared.Published;
                IndexWorkLease? lease = null;
                IndexedFileRead? file = null;
                try
                {
                    PreparedIndexSource source;
                    if (physicalPath != null)
                    {
                        file = await IndexedFileRead.OpenAsync(physicalPath, _limits.MaximumDocumentCharacters, ct).ConfigureAwait(false);
                        if (CanReuse(previous.Freshness, previous.Fingerprint, previous.Policy, previous.SourceRevisionKey, previous.Language,
                            file.Fingerprint.Sha256, policy, null, language))
                        { unchanged++; return; }
                        lease = await _index.BeginWorkAsync(handle, Guid.NewGuid(), file.Fingerprint.Sha256, policy, ct).ConfigureAwait(false);
                        var definitions = symbols ? await Task.Run(() => RelationalIndexStore.ExtractFileSymbols(physicalPath, file.Text,
                            ParserFor(language), ct), ct).ConfigureAwait(false) : ImmutableArray<SymbolInput>.Empty;
                        source = new(file.Text, file.Fingerprint.Sha256, language, definitions);
                    }
                    else
                    {
                        // Typed owner identity pins immutable diagram revisions; captured code additionally pins its source revision.
                        if ((sourceRevision.HasValue || owner is DiagramDocumentOwner) && CanReuse(previous.Freshness, previous.Fingerprint, previous.Policy, previous.SourceRevisionKey,
                            previous.Language, previous.Fingerprint, policy, sourceRevision, language))
                        { unchanged++; return; }
                        source = await prepare!().ConfigureAwait(false);
                        if (CanReuse(previous.Freshness, previous.Fingerprint, previous.Policy, previous.SourceRevisionKey, previous.Language,
                            source.Fingerprint, policy, sourceRevision, source.Language))
                        { unchanged++; return; }
                        lease = await _index.BeginWorkAsync(handle, Guid.NewGuid(), source.Fingerprint, policy, ct).ConfigureAwait(false);
                    }
                    ct.ThrowIfCancellationRequested();
                    await RequireDomainAsync(scope, ct).ConfigureAwait(false);
                    await _index.PublishAsync(new(lease, source.Text, source.Language, source.Symbols, source.SourceRevisionKey, file?.Fingerprint), ct).ConfigureAwait(false);
                    published++;
                }
                catch (Exception ex) when (ex is OperationCanceledException || IsSourceFailure(ex))
                {
                    if (ex is Microsoft.Data.SqlClient.SqlException)
                        InternalLogService.Error(ex, "SQL failure while preparing or publishing a reference document.");
                    // Even a read/parse failure before BeginWork must not leave an old successful generation looking current.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if (lease == null)
                    {
                        var current = await _index.GetHandleAsync(handle.DocumentKey, cleanup.Token).ConfigureAwait(false);
                        lease = await _index.BeginWorkAsync(current, Guid.NewGuid(), previous.Fingerprint ?? new string('0', 64), policy, cleanup.Token).ConfigureAwait(false);
                    }
                    await _index.FailWorkAsync(lease, ex is OperationCanceledException ? "Cancelled" : FailureCode(ex),
                        ex is FileNotFoundException or DirectoryNotFoundException ? IndexFreshness.Missing : ex is UnauthorizedAccessException
                            ? IndexFreshness.Inaccessible : ex is OperationCanceledException ? IndexFreshness.Stale : IndexFreshness.Failed, cleanup.Token).ConfigureAwait(false);
                    if (ex is OperationCanceledException) throw;
                    failed++;
                    lastFailure = FailureCode(ex); failedResource = currentResource; failedDocument = currentDocument;
                }
                finally { if (file != null) await file.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }

    internal static bool CanReuse(IndexFreshness freshness, string? oldFingerprint, IndexPolicy? oldPolicy,
        long? oldRevision, string oldLanguage, string? fingerprint, IndexPolicy policy, long? revision, string language) =>
        freshness == IndexFreshness.Indexed && fingerprint != null && oldFingerprint == fingerprint &&
        oldPolicy == policy && oldRevision == revision && oldLanguage == language;
    internal static bool OwnsFile(ExplorerResource resource, string path) => resource.Kind == ResourceKind.File
        ? string.Equals(Path.GetFullPath(resource.Path), path, StringComparison.OrdinalIgnoreCase)
        : resource.Kind == ResourceKind.Folder && ExplorerCompatibility.IsWithinPhysicalRoot(path, resource.Path);
    internal static bool ReferenceFileEligible(ExplorerResource resource, string path)
    {
        if (resource.Kind == ResourceKind.File) return true;
        string relative = Path.GetRelativePath(resource.Path, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.SkipLast(1).Any(ExcludedReferenceDirectory);
    }
    private static bool ExcludedReferenceDirectory(string name) => name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vs", StringComparison.OrdinalIgnoreCase) || name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
    private static IReferenceDefinitionParser? ParserFor(string language) => language switch
    {
        CodeWindowSettings.CSharpLanguage => new CSharpReferenceDefinitionParser(),
        CodeWindowSettings.VisualBasicLanguage => new VisualBasicReferenceDefinitionParser(),
        CodeWindowSettings.SqlServerLanguage => new SqlServerReferenceDefinitionParser(),
        CodeWindowSettings.JavaScriptLanguage => new JavaScriptReferenceDefinitionParser(),
        _ => null
    };
    private async Task RequireDomainAsync(ExplorerScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await _metadata.IsDomainCurrentAsync(scope.Context, ct).ConfigureAwait(false)) throw new IndexGenerationChangedException();
    }
    private static void RequireSameDomain(IndexRequestContext before, IndexRequestContext after)
    {
        if (before.Epoch != after.Epoch || before.ScopeKey != after.ScopeKey || before.ScopeVersion != after.ScopeVersion ||
            before.SnapshotCatalogueVersion != after.SnapshotCatalogueVersion || before.DiagramCatalogueVersion != after.DiagramCatalogueVersion)
            throw new IndexGenerationChangedException();
    }
    private static bool IsSourceFailure(Exception ex) => ex is not IndexGenerationChangedException &&
        ex is IOException or UnauthorizedAccessException or ExplorerLimitException or SnapshotReadLimitException or ArgumentException or InvalidOperationException;
    private static string FailureCode(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException ? "Missing" :
        ex is UnauthorizedAccessException ? "Inaccessible" : ex is IndexDocumentTooLargeException or ExplorerLimitException or SnapshotReadLimitException
            ? "TooLarge" : "SourceOrParserFailure";
}

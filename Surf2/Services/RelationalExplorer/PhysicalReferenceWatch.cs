using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using Surf2.Models;

namespace Surf2.Services.RelationalExplorer;

[Flags]
public enum PhysicalReferenceInvalidationReason
{
    None = 0,
    InitialReconciliation = 1,
    Changed = 2,
    WatcherError = 4,
    MissingRoot = 8,
    WatcherUnavailable = 16
}

public sealed record PhysicalReferenceWatchFailure(long ScopeResourceKey, string RootPath, string Code,
    string? ExceptionType = null);

public sealed record PhysicalReferenceInvalidation(ImmutableArray<long> ScopeResourceKeys,
    ImmutableArray<string> Paths, PhysicalReferenceInvalidationReason Reason,
    bool RequiresWholeRootReconciliation, ImmutableArray<PhysicalReferenceWatchFailure> Failures);

// Notifications invalidate discovery; they never prove freshness, including after a process restart.
public sealed class PhysicalReferenceWatch : IDisposable
{
    private const int DebounceMilliseconds = 250;
    private const int MaximumDelayMilliseconds = 1000;
    private const int MaximumPendingPaths = 256;
    private readonly object _gate = new();
    private readonly object _callbackGate = new();
    private readonly Action<PhysicalReferenceInvalidation> _callback;
    private readonly ImmutableArray<ExplorerResource> _roots;
    private readonly ImmutableArray<PhysicalReferenceWatchFailure> _invalidRoots;
    private readonly Timer _timer;
    private readonly HashSet<long> _pendingResources = [];
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(long Resource, string Code), PhysicalReferenceWatchFailure> _pendingFailures = [];
    private List<FileSystemWatcher> _watchers = [];
    private PhysicalReferenceInvalidationReason _pendingReason;
    private bool _wholeRoot, _pathsOverflowed, _started, _disposed, _reconfigure;
    private long _firstPendingTimestamp;
    private Exception? _lastCallbackException;
    private ImmutableArray<PhysicalReferenceWatchFailure> _unwatchedRoots = [];

    // Startup state is exposed through properties, not callbacks. Worker callbacks must enqueue UI work.
    public PhysicalReferenceWatch(ExplorerScope scope, Action<PhysicalReferenceInvalidation> callback)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _timer = new Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
        var roots = ImmutableArray.CreateBuilder<ExplorerResource>();
        var invalidRoots = ImmutableArray.CreateBuilder<PhysicalReferenceWatchFailure>();
        foreach (var resource in scope.Resources.Where(r => r.IsLoaded && r.Kind is ResourceKind.File or ResourceKind.Folder))
        {
            try { roots.Add(resource with { Path = Normalize(resource.Path) }); }
            catch (Exception ex) when (IsWatchFailure(ex))
            {
                invalidRoots.Add(new(resource.ScopeResourceKey, resource.Path, "InvalidPath", ex.GetType().Name));
                Queue([resource], [], PhysicalReferenceInvalidationReason.WatcherUnavailable, true,
                    "InvalidPath", ex.GetType().Name);
            }
        }
        _roots = roots.ToImmutable();
        _invalidRoots = invalidRoots.ToImmutable();
        Queue(_roots, [], PhysicalReferenceInvalidationReason.InitialReconciliation, true);
        InstallWatchers();
        lock (_gate)
        {
            // The caller already schedules startup reconciliation. Recreating a watcher must not schedule it again.
            InitialInvalidation = DrainLocked();
            _unwatchedRoots = InitialInvalidation.Failures;
            _reconfigure = false;
            _started = true;
        }
    }

    public PhysicalReferenceWatch(ExplorerScope scope, Action callback)
        : this(scope, Adapt(callback)) { }

    public Exception? LastCallbackException { get { lock (_gate) return _lastCallbackException; } }
    public PhysicalReferenceInvalidation InitialInvalidation { get; }
    public ImmutableArray<PhysicalReferenceWatchFailure> UnwatchedRoots { get { lock (_gate) return _unwatchedRoots; } }

    internal int WatcherCount { get { lock (_gate) return _watchers.Count; } }

    private static Action<PhysicalReferenceInvalidation> Adapt(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _ => callback();
    }

    private void InstallWatchers()
    {
        var plan = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var failures = _invalidRoots.ToDictionary(f => (f.ScopeResourceKey, f.Code));
        foreach (var root in _roots)
        {
            bool present = false, reparse = false;
            try
            {
                var attributes = File.GetAttributes(root.Path);
                present = (attributes.HasFlag(FileAttributes.Directory)) == (root.Kind == ResourceKind.Folder);
                reparse = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (!present || reparse)
                    SetupFailure([root], PhysicalReferenceInvalidationReason.WatcherUnavailable,
                        reparse ? "ReparsePoint" : "WrongRootKind");
            }
            catch (Exception ex) when (IsWatchFailure(ex))
            {
                bool missing = ex is FileNotFoundException or DirectoryNotFoundException;
                SetupFailure([root], missing ? PhysicalReferenceInvalidationReason.MissingRoot
                    : PhysicalReferenceInvalidationReason.WatcherUnavailable,
                    missing ? "MissingRoot" : "RootUnavailable", ex.GetType().Name);
            }

            string? desired = root.Kind == ResourceKind.Folder && present && !reparse
                ? root.Path : Path.GetDirectoryName(root.Path);
            string? directory = NearestExistingDirectory(desired);
            if (directory == null)
            {
                SetupFailure([root], PhysicalReferenceInvalidationReason.WatcherUnavailable, "NoWatchableAncestor");
                continue;
            }
            // Missing roots need recursive ancestor coverage so creation followed immediately by writes is observed.
            bool recursive = !reparse && (root.Kind == ResourceKind.Folder || !SamePath(directory, desired));
            Add(directory, recursive);
            for (string? parent = Path.GetDirectoryName(directory); parent != null; parent = Path.GetDirectoryName(parent))
                Add(parent, false);
        }

        var replacement = new List<FileSystemWatcher>();
        foreach (var item in plan)
        {
            if (plan.Any(other => other.Value && !SamePath(other.Key, item.Key) && IsWithin(item.Key, other.Key))) continue;
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(item.Key)
                {
                    IncludeSubdirectories = item.Value,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                        NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes | NotifyFilters.Security,
                    InternalBufferSize = 16 * 1024
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                replacement.Add(watcher);
            }
            catch (Exception ex) when (IsWatchFailure(ex))
            {
                watcher?.Dispose();
                SetupFailure(RelatedRoots(item.Key), PhysicalReferenceInvalidationReason.WatcherUnavailable,
                    "WatchSetupFailed", ex.GetType().Name);
            }
        }
        List<FileSystemWatcher> previous;
        lock (_gate)
        {
            previous = _watchers; _watchers = replacement;
            _unwatchedRoots = failures.Values.OrderBy(f => f.ScopeResourceKey).ThenBy(f => f.Code, StringComparer.Ordinal).ToImmutableArray();
        }
        // Replacement subscriptions are active before old ones are retired and before reconciliation is requested.
        foreach (var watcher in previous) watcher.Dispose();

        void Add(string path, bool recursive)
        {
            path = Normalize(path);
            plan[path] = recursive || plan.GetValueOrDefault(path);
        }

        void SetupFailure(IEnumerable<ExplorerResource> roots, PhysicalReferenceInvalidationReason reason,
            string code, string? exceptionType = null)
        {
            var affected = roots.ToArray();
            foreach (var root in affected)
                failures[(root.ScopeResourceKey, code)] = new(root.ScopeResourceKey, root.Path, code, exceptionType);
            Queue(affected, [], reason, true, code, exceptionType);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => RecordChange(e.ChangeType, e.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs e) => RecordChange(e.ChangeType, e.FullPath, e.OldFullPath);
    private void OnError(object sender, ErrorEventArgs e) => RecordWatcherError(((FileSystemWatcher)sender).Path, e.GetException());

    internal void RecordChange(WatcherChangeTypes change, string path, string? oldPath = null)
    {
        string[] paths;
        try { paths = oldPath == null ? [Normalize(path)] : [Normalize(path), Normalize(oldPath)]; }
        catch (Exception ex) when (IsWatchFailure(ex))
        {
            Queue(_roots, [], PhysicalReferenceInvalidationReason.WatcherError, true, "InvalidEventPath", ex.GetType().Name);
            return;
        }
        bool topology = change != WatcherChangeTypes.Changed;
        var affected = _roots.Where(root => paths.Any(candidate => Affects(root, candidate, topology))).ToArray();
        if (affected.Length == 0) return;
        bool changedBoundary = topology && affected.Any(root => paths.Any(candidate =>
            SamePath(root.Path, candidate) || IsWithin(root.Path, candidate)));
        lock (_gate)
        {
            if (_disposed) return;
            _reconfigure |= changedBoundary;
            QueueLocked(affected, paths, PhysicalReferenceInvalidationReason.Changed, topology);
        }
    }

    internal void RecordWatcherError(string watchPath, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            if (_disposed) return;
            _reconfigure = true;
            string code = error is InternalBufferOverflowException ? "BufferOverflow" : "WatcherError";
            var affected = RelatedRoots(watchPath);
            QueueLocked(affected, [], PhysicalReferenceInvalidationReason.WatcherError, true, code, error.GetType().Name);
            _unwatchedRoots = _unwatchedRoots.Concat(affected.Select(root =>
                new PhysicalReferenceWatchFailure(root.ScopeResourceKey, root.Path, code, error.GetType().Name)))
                .DistinctBy(f => (f.ScopeResourceKey, f.Code)).OrderBy(f => f.ScopeResourceKey).ToImmutableArray();
        }
    }

    private static bool Affects(ExplorerResource root, string path, bool topology)
    {
        if (SamePath(root.Path, path)) return topology || root.Kind == ResourceKind.File;
        if (topology && IsWithin(root.Path, path)) return true;
        if (root.Kind == ResourceKind.File || !IsWithin(path, root.Path)) return false;
        if (!RelationalScopeIndexRefresher.ReferenceFileEligible(root, path)) return false;
        bool directory = Directory.Exists(path);
        if (directory && !topology) return false;
        // Removed paths cannot be probed; exclude generated-directory boundary events as well as their descendants.
        bool generatedBoundary = topology && !RelationalScopeIndexRefresher.ReferenceFileEligible(root,
            Path.Combine(path, "__reference_watch_entry__"));
        string eligibilityPath = directory || generatedBoundary ? Path.Combine(path, "__reference_watch_entry__") : path;
        return RelationalScopeIndexRefresher.ReferenceFileEligible(root, eligibilityPath);
    }

    private ExplorerResource[] RelatedRoots(string path) => _roots.Where(root => SamePath(root.Path, path) ||
        IsWithin(root.Path, path) || root.Kind == ResourceKind.Folder && IsWithin(path, root.Path)).ToArray();

    private void Queue(IEnumerable<ExplorerResource> roots, IEnumerable<string> paths,
        PhysicalReferenceInvalidationReason reason, bool wholeRoot, string? code = null, string? exceptionType = null)
    {
        lock (_gate) QueueLocked(roots, paths, reason, wholeRoot, code, exceptionType);
    }

    private void QueueLocked(IEnumerable<ExplorerResource> roots, IEnumerable<string> paths,
        PhysicalReferenceInvalidationReason reason, bool wholeRoot, string? code = null, string? exceptionType = null)
    {
        if (_disposed) return;
        bool any = false;
        foreach (var root in roots)
        {
            any = true;
            _pendingResources.Add(root.ScopeResourceKey);
            if (code != null) _pendingFailures[(root.ScopeResourceKey, code)] = new(root.ScopeResourceKey, root.Path, code, exceptionType);
        }
        if (!any) return;
        if (_pendingReason == 0) _firstPendingTimestamp = Stopwatch.GetTimestamp();
        _pendingReason |= reason;
        _wholeRoot |= wholeRoot;
        if (!_pathsOverflowed)
        {
            foreach (string path in paths)
            {
                _pendingPaths.Add(path);
                if (_pendingPaths.Count <= MaximumPendingPaths) continue;
                _pendingPaths.Clear();
                _pathsOverflowed = _wholeRoot = true;
                break;
            }
        }
        ArmLocked();
    }

    private void ArmLocked()
    {
        if (!_started || _disposed || _pendingReason == 0) return;
        double remaining = MaximumDelayMilliseconds - Stopwatch.GetElapsedTime(_firstPendingTimestamp).TotalMilliseconds;
        _timer.Change((int)Math.Clamp(remaining, 1, DebounceMilliseconds), Timeout.Infinite);
    }

    private void Flush(object? state)
    {
        lock (_callbackGate)
        {
            bool reconfigure;
            lock (_gate)
            {
                if (_disposed) return;
                reconfigure = _reconfigure;
                _reconfigure = false;
            }
            if (reconfigure) InstallWatchers();
            PhysicalReferenceInvalidation notification;
            lock (_gate)
            {
                if (_disposed || _pendingReason == 0) return;
                notification = DrainLocked();
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            try { _callback(notification); }
            catch (Exception ex) { lock (_gate) _lastCallbackException = ex; }
        }
    }

    private PhysicalReferenceInvalidation DrainLocked()
    {
        var notification = new PhysicalReferenceInvalidation(_pendingResources.Order().ToImmutableArray(),
            _pendingPaths.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), _pendingReason, _wholeRoot,
            _pendingFailures.Values.OrderBy(f => f.ScopeResourceKey).ThenBy(f => f.Code, StringComparer.Ordinal).ToImmutableArray());
        _pendingResources.Clear(); _pendingPaths.Clear(); _pendingFailures.Clear();
        _pendingReason = 0; _wholeRoot = _pathsOverflowed = false;
        return notification;
    }

    public void Dispose()
    {
        List<FileSystemWatcher> watchers;
        lock (_callbackGate)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Dispose();
                watchers = _watchers; _watchers = [];
                _pendingResources.Clear(); _pendingPaths.Clear(); _pendingFailures.Clear();
            }
        }
        foreach (var watcher in watchers) watcher.Dispose();
    }

    private static string? NearestExistingDirectory(string? path)
    {
        for (; path != null; path = Path.GetDirectoryName(path))
        {
            try { if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) return Normalize(path); }
            catch (Exception ex) when (IsWatchFailure(ex)) { }
        }
        return null;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string root) => !SamePath(path, root) && path.StartsWith(
        Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsWatchFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception;
}

using System.Windows;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2;

public partial class MainWindow
{
    private Task _relationalSnapshotMemoryTask = Task.CompletedTask;
    private bool _relationalSnapshotMemoryStarted;
    private SnapshotMemoryProgress? _relationalSnapshotMemoryProgress;
    private Exception? _relationalSnapshotMemoryFailure;

    private static long[] LoadedRelationalSnapshotKeys(ExplorerScope scope) => scope.Resources
        .Where(resource => resource.IsLoaded && resource.Snapshot != null)
        .Select(resource => resource.Snapshot!.SnapshotKey).Distinct().ToArray();

    private async Task ClearRetiredSnapshotMemoryAsync(Task retirement, RelationalRuntime runtime)
    {
        await retirement;
        if (ReferenceEquals(runtime, _relational) && _relationalExplorerScope == null) runtime.InvalidateSnapshotMemory();
    }

    private void StartRelationalSnapshotMemoryLoad()
    {
        Dispatcher.VerifyAccess();
        if (_relationalSnapshotMemoryStarted || !_relationalIndexWorkerFinished || _relationalPhysicalRefreshPending ||
            _relational is not { } runtime || _relationalExplorerScope is not { } scope ||
            _relationalExplorerCancellation is not { IsCancellationRequested: false } lifetime) return;

        _relationalSnapshotMemoryStarted = true;
        _relationalSnapshotMemoryFailure = null;
        long[] snapshots = LoadedRelationalSnapshotKeys(scope);
        runtime.RetainSnapshotMemory(snapshots);
        foreach (var snapshot in scope.Resources.Where(resource => resource.IsLoaded && resource.Snapshot != null)
            .Select(resource => resource.Snapshot!).DistinctBy(snapshot => snapshot.SnapshotKey))
            if (runtime.Session.SnapshotMemory.TryGetSnapshot(snapshot.SnapshotKey, out var cached) &&
                !cached.RowVersion.AsSpan().SequenceEqual(snapshot.RowVersion))
                runtime.InvalidateSnapshotMemory(snapshot.SnapshotKey);
        if (snapshots.Length == 0 || runtime.IsSnapshotMemoryReady(scope)) return;

        var owner = _relationalExplorerOwner;
        var ct = lifetime.Token;
        _relationalSnapshotMemoryProgress = new("Loading snapshot history", 0, snapshots.Length);
        var progress = new Progress<SnapshotMemoryProgress>(value =>
        {
            if (!AcceptsRelationalExplorer(owner, runtime) || runtime.IsSnapshotMemoryReady(scope)) return;
            _relationalSnapshotMemoryProgress = value;
            UpdateRelationalIndexUi();
        });
        _relationalSnapshotMemoryTask = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await runtime.PreloadSnapshotMemoryAsync(snapshots, progress, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                failure = error;
                InternalLogService.Error(error, "The scope's snapshot history and comparison data could not be loaded into memory.");
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (!AcceptsRelationalExplorer(owner, runtime)) return;
                _relationalSnapshotMemoryFailure = failure ?? (runtime.IsSnapshotMemoryReady(scope) ? null : new SnapshotConcurrencyException());
                _relationalSnapshotMemoryProgress = null;
                UpdateRelationalIndexUi();
            });
        });
    }

    private void UpdateRelationalSnapshotMemoryUi(bool indexReady)
    {
        StartRelationalSnapshotMemoryLoad();
        if (!indexReady) return;
        if (_relationalSnapshotMemoryFailure != null)
        {
            RelationalIndexFooter.Visibility = Visibility.Visible;
            RelationalIndexSpinner.Visibility = Visibility.Collapsed;
            RelationalIndexRetryButton.Visibility = Visibility.Visible;
            RelationalIndexRetryButton.ToolTip = "Retry loading snapshot history and comparison data";
            RelationalIndexProgressText.Text = "Snapshot history loading incomplete";
            RelationalIndexProgressText.ToolTip = _relationalSnapshotMemoryFailure.Message;
        }
        else if (_relationalSnapshotMemoryProgress is { } progress)
        {
            RelationalIndexFooter.Visibility = Visibility.Visible;
            RelationalIndexSpinner.Visibility = Visibility.Visible;
            RelationalIndexRetryButton.Visibility = Visibility.Collapsed;
            RelationalIndexProgressText.Text = progress.Total > 0
                ? $"{progress.Message}… {progress.Completed:N0} of {progress.Total:N0}"
                : progress.Message + "…";
            RelationalIndexProgressText.ToolTip = "Loading database snapshots for history and comparisons. Search and resource viewing remain available.";
        }
    }

    private async Task EnsureRelationalSnapshotMemoryReadyAsync(CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        if (_relational is not { } runtime || _relationalExplorerScope is not { } scope) return;
        long[] snapshots = LoadedRelationalSnapshotKeys(scope);
        if (snapshots.Length == 0 || runtime.IsSnapshotMemoryReady(scope)) return;
        var owner = _relationalExplorerOwner;
        StatusText = "Waiting for snapshot history and comparison data to finish loading…";
        await _relationalIndexRefreshTask.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!AcceptsRelationalExplorer(owner, runtime)) throw new OperationCanceledException("The selected scope changed.");
        StartRelationalSnapshotMemoryLoad();
        await _relationalSnapshotMemoryTask.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!AcceptsRelationalExplorer(owner, runtime)) throw new OperationCanceledException("The selected scope changed.");
        if (!runtime.IsSnapshotMemoryReady(scope))
            throw new InvalidOperationException("Snapshot history has not finished loading. Use Retry in Object Explorer to load it again.",
                _relationalSnapshotMemoryFailure);
    }

    private void RetryRelationalSnapshotMemoryLoad()
    {
        if (!_relationalSnapshotMemoryTask.IsCompleted) return;
        if (_relationalSnapshotMemoryFailure is SnapshotConcurrencyException)
        {
            RequestRelationalReferenceRefresh();
            return;
        }
        _relationalSnapshotMemoryStarted = false;
        _relationalSnapshotMemoryFailure = null;
        StartRelationalSnapshotMemoryLoad();
        UpdateRelationalIndexUi();
    }
}

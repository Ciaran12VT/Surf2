using System.Windows;
using System.Windows.Controls;
using Surf2.Controls;
using Surf2.Controls.RelationalGrid;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalGrid;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.State;

namespace Surf2;

public partial class MainWindow
{
    private sealed class RelationalGridLifecycleOwner(FloatingSpreadsheetWindow window, IDataGridSource source, RelationalRuntime runtime,
        long? scopeKey)
    {
        internal FloatingSpreadsheetWindow Window { get; } = window;
        internal IDataGridSource Source { get; } = source;
        internal RelationalRuntime Runtime { get; } = runtime;
        internal QueryLifetime Queries { get; } = new(runtime.Session.Epoch, scopeKey);
        internal bool Reopening, Retired;
        internal Task Pending { get; set; } = Task.CompletedTask;
    }

    private readonly Dictionary<FloatingSpreadsheetWindow, RelationalGridLifecycleOwner> _relationalGridLifecycleOwners = [];
    private readonly HashSet<Task> _relationalGridLifecycleTasks = [];
    private readonly List<Exception> _relationalGridLifecycleFailures = [];
    private bool _relationalGridLifecycleClosing;
    private Guid _relationalGridLifecycleEpoch;

    // A UI host may provide its own confirmation. A null hook uses the normal explicit Yes/No dialog.
    public Func<FloatingSpreadsheetWindow, CancellationToken, Task<bool>>? RelationalGridDiscardEditsConfirmation { get; set; }

    // Parent calls after the provider constructor, before publishing the new window. No read occurs here.
    private void AttachRelationalGridLifecycle(FloatingSpreadsheetWindow window, IDataGridSource source)
    {
        Dispatcher.VerifyAccess();
        var runtime = _relational ?? throw new InvalidOperationException("A ready relational runtime is required for grid ownership.");
        if (_relationalGridLifecycleEpoch != runtime.Session.Epoch)
        {
            if (_relationalGridLifecycleOwners.Count != 0)
                throw new InvalidOperationException("Drain the previous runtime's grid owners before attaching another epoch.");
            _relationalGridLifecycleEpoch = runtime.Session.Epoch;
            _relationalGridLifecycleClosing = false;
        }
        if (_relationalGridLifecycleClosing || !window.UsesGridProvider || window.IsGridDisposed)
            throw new InvalidOperationException("The relational grid lifecycle is closing or the provider is unavailable.");
        if (_relationalGridLifecycleOwners.TryGetValue(window, out var existing))
        {
            if (!ReferenceEquals(existing.Source, source)) throw new InvalidOperationException("The window already owns another grid source.");
            return;
        }
        _relationalGridLifecycleOwners.Add(window, new(window, source, runtime, _relationalExplorerScope?.Context.ScopeKey));
        window.GridReadFailed += RelationalGrid_ReadFailed;
        window.GridReopenRequested += RelationalGrid_ReopenRequested;
    }

    // Parent calls only on logical close. Source ownership stays with DisposeGridAsync, not this event subscription.
    private void RetireRelationalGridLifecycle(FloatingSpreadsheetWindow window)
    {
        Dispatcher.VerifyAccess();
        if (!_relationalGridLifecycleOwners.Remove(window, out var owner)) return;
        owner.Retired = true;
        window.GridReadFailed -= RelationalGrid_ReadFailed;
        window.GridReopenRequested -= RelationalGrid_ReopenRequested;
        owner.Queries.Dispose();
        TrackRelationalGridLifecycle(owner.Queries.DisposeAsync().AsTask());
    }

    private async Task DrainRelationalGridLifecycleAsync()
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(DrainRelationalGridLifecycleAsync).Task.Unwrap(); return; }
        _relationalGridLifecycleClosing = true;
        foreach (var window in _relationalGridLifecycleOwners.Keys.ToArray()) RetireRelationalGridLifecycle(window);
        while (true)
        {
            Task[] pending;
            lock (_relationalGridLifecycleTasks) pending = _relationalGridLifecycleTasks.ToArray();
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending); } catch { /* Failures are retained by the tracking boundary below. */ }
            lock (_relationalGridLifecycleTasks)
            {
                foreach (var task in pending.Where(t => t.IsCompleted)) _relationalGridLifecycleTasks.Remove(task);
            }
        }
        Exception[] failures;
        lock (_relationalGridLifecycleTasks) { failures = _relationalGridLifecycleFailures.ToArray(); _relationalGridLifecycleFailures.Clear(); }
        if (failures.Length != 0) throw new AggregateException("Relational grid lifecycle cleanup failed.", failures);
    }

    private void TrackRelationalGridLifecycle(Task task)
    {
        lock (_relationalGridLifecycleTasks)
        {
            _relationalGridLifecycleTasks.RemoveWhere(t => t.IsCompleted);
            _relationalGridLifecycleTasks.Add(ObserveAsync());
        }
        async Task ObserveAsync()
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (_relationalGridLifecycleTasks)
                    if (_relationalGridLifecycleFailures.Count < 16) _relationalGridLifecycleFailures.Add(error);
            }
        }
    }

    private void RelationalGrid_ReadFailed(object? sender, GridReadErrorEventArgs error)
    {
        if (sender is not FloatingSpreadsheetWindow window || !_relationalGridLifecycleOwners.TryGetValue(window, out var owner) ||
            !IsRelationalGridMember(owner) || error.SourceId != owner.Source.Descriptor.SourceId) return;
        StatusText = error.RequiresReopen
            ? "Table source is unavailable. Retry can prepare a replacement; session edits are retained until you confirm reopening."
            : "Table read failed. Retry repeats the pinned query without discarding session edits.";
    }

    private void RelationalGrid_ReopenRequested(object? sender, EventArgs e)
    {
        if (sender is FloatingSpreadsheetWindow window) _ = ReopenRelationalGridAsync(window); // The entry point tracks its task.
    }

    // The same entry point is awaitable by an alternative host or regression fixture. Repeat clicks join one operation.
    public Task ReopenRelationalGridAsync(FloatingSpreadsheetWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(() => ReopenRelationalGridAsync(window)).Task.Unwrap();
        if (!_relationalGridLifecycleOwners.TryGetValue(window, out var owner) || !IsRelationalGridMember(owner))
            return Task.CompletedTask;
        if (owner.Reopening) return owner.Pending;
        owner.Reopening = true;
        Task task = owner.Pending = ReopenRelationalGridCoreAsync(owner);
        TrackRelationalGridLifecycle(task);
        return task;
    }

    private bool IsRelationalGridMember(RelationalGridLifecycleOwner owner) => !_relationalGridLifecycleClosing && !owner.Retired &&
        !owner.Window.IsGridDisposed && ReferenceEquals(_relational, owner.Runtime) &&
        _openSpreadsheetWindows.TryGetValue(owner.Window.State.FilePath, out var current) && ReferenceEquals(current, owner.Window);

    private async Task ReopenRelationalGridCoreAsync(RelationalGridLifecycleOwner owner)
    {
        var old = owner.Window;
        var runtime = owner.Runtime;
        var documents = _relationalDocuments;
        var scope = _relationalExplorerScope;
        var workspace = _relationalWorkspaceEdit;
        long context = _relationalDocumentContextGeneration;
        IDataGridSource? created = null;
        FloatingSpreadsheetWindow? replacement = null;
        QueryRequest? request = null;
        bool published = false;
        try
        {
            if (documents == null || !old.TryCaptureGridReopenView(out var view) || view == null) return;
            owner.Queries.ChangeContext(runtime.Session.Epoch, scope?.Context.ScopeKey);
            request = owner.Queries.BeginRequest();
            var token = request.CancellationToken;
            bool ContextCurrent() => !_relationalGridLifecycleClosing && ReferenceEquals(_relational, runtime) &&
                ReferenceEquals(_relationalExplorerScope, scope) && context == _relationalDocumentContextGeneration &&
                ReferenceEquals(_relationalDocuments, documents) && ReferenceEquals(_relationalWorkspaceEdit, workspace) &&
                owner.Queries.IsCurrent(request.Stamp);
            bool Current() => IsRelationalGridMember(owner) && ContextCurrent();
            if (view.OverlayBytes > 0)
            {
                bool confirmed = RelationalGridDiscardEditsConfirmation != null
                    ? await RelationalGridDiscardEditsConfirmation(old, token)
                    : MessageBox.Show(this, "Reopening will discard this table's unsaved session cell edits. Continue?",
                        "Reopen Table", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
                if (!confirmed) { if (Current()) StatusText = "Reopen canceled; the existing table and session edits were retained."; return; }
            }
            if (!Current()) return;
            var address = await documents.ResolveAsync(old.State.FilePath, scope, old.State, token);
            created = await documents.OpenGridAsync(address, token);
            if (old.State.BoundResourceKey.HasValue && !string.Equals(created.Descriptor.Revision, owner.Source.Descriptor.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("The current captured revision changed. The pinned table and edits were retained; open the new revision explicitly.");
            if (!created.Descriptor.Columns.SequenceEqual(owner.Source.Descriptor.Columns))
                throw new InvalidOperationException("Source headers changed. The original table and edits were retained; open the changed layout explicitly.");
            var preparedState = StateCopies.Window(old.State, new(RelationalRuntimeStateLimits));
            replacement = new(preparedState, created, view.Limits);
            created = null; // The prepared control now owns this source, including failure cleanup.
            replacement.RestoreGridReopenView(view);
            replacement.ApplyGridBackcolor(GetCodeWindowBackcolor(old.State.FilePath));
            await replacement.PrepareGridPublicationAsync(token);
            if (replacement.PreparedGridSource is GridSourceBase staged) await staged.ValidateSource(true, token);
            await documents.ValidateAddressAsync(address, scope, token);
            token.ThrowIfCancellationRequested();
            if (!Current() || !old.TryCaptureGridReopenView(out var final) || final == null ||
                final.OverlayGeneration != view.OverlayGeneration || final.Fingerprint != view.Fingerprint)
            {
                if (Current()) StatusText = "The table view or edits changed during reopening. The original table was retained; retry explicitly.";
                return;
            }
            replacement.AdoptPreparedGridState(old.State);
            var publishedWindow = replacement;
            SwapPreparedRelationalGrid(owner, publishedWindow);
            published = true;
            replacement = null;
            // No root library or captured data save. Persist only the selected workspace/layout owner.
            await old.DisposeGridAsync();
            if (!ContextCurrent() || publishedWindow.IsGridDisposed ||
                !_openSpreadsheetWindows.TryGetValue(old.State.FilePath, out var current) || !ReferenceEquals(current, publishedWindow)) return;
            try { await SaveRelationalWorkspaceAsync(token); }
            catch (Exception error)
            {
                if (ContextCurrent())
                    StatusText = "Table reopened, but its selected layout could not be saved: " + error.Message;
                return;
            }
            if (ContextCurrent())
                StatusText = "Reopened table source; the confirmed session edits were discarded.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsRelationalGridMember(owner)) StatusText = "Could not reopen table; the existing source and session edits were retained: " + error.Message;
            else if (published && ReferenceEquals(_relational, runtime)) StatusText = "Table reopened, but cleanup failed: " + error.Message;
        }
        finally
        {
            try
            {
                if (replacement != null) await replacement.DisposeGridAsync();
                if (created != null) await created.DisposeAsync();
            }
            finally
            {
                if (published) RetireRelationalGridLifecycle(old);
                if (request != null) await request.DisposeAsync();
                owner.Reopening = false;
            }
        }
    }

    private void SwapPreparedRelationalGrid(RelationalGridLifecycleOwner owner, FloatingSpreadsheetWindow replacement)
    {
        Dispatcher.VerifyAccess();
        if (!IsRelationalGridMember(owner)) throw new OperationCanceledException("The original table is no longer open.");
        var old = owner.Window;
        var tab = FindCodeDocumentTab(old.State.FilePath);
        bool docked = tab != null && ReferenceEquals(tab.Content, old);
        int index = WorkspaceCanvas.Children.IndexOf(old);
        if (!docked && index < 0) throw new InvalidOperationException("The original table has no supported canvas/tab host.");
        replacement.SetDockedMode(docked);
        Panel.SetZIndex(replacement, Panel.GetZIndex(old));
        Canvas.SetLeft(replacement, Canvas.GetLeft(old)); Canvas.SetTop(replacement, Canvas.GetTop(old));
        replacement.CloseRequested += SpreadsheetWindow_CloseRequested;
        replacement.BoundsChanged += FloatingWindow_BoundsChanged;
        replacement.FilterReferenceCopyRequested += SpreadsheetWindow_FilterReferenceCopyRequested;
        replacement.BringToFrontRequested += (_, _) =>
        { SetActiveCodeWindow(null, syncOpenTabsSelection: false); BringToFront(replacement); };
        bool swapped = false;
        try
        {
            AttachRelationalGridLifecycle(replacement, GetPreparedRelationalGridSource(replacement));
            if (docked) tab!.Content = replacement;
            else { WorkspaceCanvas.Children.RemoveAt(index); WorkspaceCanvas.Children.Insert(index, replacement); }
            _openSpreadsheetWindows[old.State.FilePath] = replacement;
            swapped = true;
            // The original State object, OpenTabs item, workspace descriptor and typed document identity remain valid.
            old.CloseRequested -= SpreadsheetWindow_CloseRequested;
            old.BoundsChanged -= FloatingWindow_BoundsChanged;
            old.FilterReferenceCopyRequested -= SpreadsheetWindow_FilterReferenceCopyRequested;
        }
        catch
        {
            swapped = false;
            _openSpreadsheetWindows[old.State.FilePath] = old;
            if (docked) tab!.Content = old;
            else
            {
                WorkspaceCanvas.Children.Remove(replacement);
                if (!WorkspaceCanvas.Children.Contains(old)) WorkspaceCanvas.Children.Insert(Math.Min(index, WorkspaceCanvas.Children.Count), old);
            }
            RetireRelationalGridLifecycle(replacement);
            throw;
        }
        finally
        {
            if (!swapped)
            {
                replacement.CloseRequested -= SpreadsheetWindow_CloseRequested;
                replacement.BoundsChanged -= FloatingWindow_BoundsChanged;
                replacement.FilterReferenceCopyRequested -= SpreadsheetWindow_FilterReferenceCopyRequested;
            }
        }
    }

    private static IDataGridSource GetPreparedRelationalGridSource(FloatingSpreadsheetWindow window) => window.PreparedGridSource;
}

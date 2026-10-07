using System.Windows;
using Surf2.Services;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services.RelationalGrid;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.State;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Access;

namespace Surf2;

public partial class MainWindow
{
    private RelationalRuntime? _relational;
    private readonly AccessCancellation _relationalStartupCancellation = new();
    private readonly List<Task> _relationalGridRetirements = [];

    private async Task<ExternalOpenResponse> OpenRelationalExternalResourceAsync(ExternalOpenRequest request)
    {
        using var operation = BeginRelationalStateOperation();
        using var requestLifetime = BeginRelationalSelection(operation.Token);
        var (generation, ct) = requestLifetime;
        ScopeSummary? chosen = null;
        StateCatalogueCursor<ScopeSummary>? cursor = null;
        int considered = 0;
        do
        {
            var page = await RelationalStateRuntime.State.ListScopesAsync(100, cursor, ct);
            foreach (var summary in page.Items)
            {
                if (++considered > RelationalRuntimeStateLimits.MaximumRows)
                    throw new InvalidOperationException("Scope lookup exceeds the metadata budget.");
                bool matches = !string.IsNullOrWhiteSpace(request.ScopeId)
                    ? string.Equals(summary.ScopeId, request.ScopeId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(summary.Name, request.ScopeName, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
                if (chosen != null) return ExternalOpenResponse.Fail("The requested scope identity is ambiguous.");
                chosen = summary;
            }
            cursor = page.Next;
        } while (cursor != null);
        RequireRelationalSelection(generation, ct);
        if (chosen == null) return ExternalOpenResponse.Fail("The requested scope was not found.");
        if (_relationalScopeEdit?.SubjectKey != chosen.Token.Key)
        {
            await WaitRelationalSelectedOwnerIdleAsync(ct);
            RequireRelationalSelection(generation, ct);
            if (!request.SuppressSavePrompt && !await ConfirmRelationalStateSwitchAsync(ct))
                return ExternalOpenResponse.Cancelled("External resource open was cancelled.");
            RequireRelationalSelection(generation, ct);
            await SaveRelationalScopeAsync(ct);
            await _relationalOwnerCommands.WaitAsync(ct);
            try
            {
                RequireRelationalSelection(generation, ct);
                var prepared = await PrepareRelationalScopeAsync(chosen, ct);
                try
                {
                    RequireRelationalSelection(generation, ct);
                    var selection = _relationalSelectionEdit ?? throw new InvalidOperationException("Scope selection is unavailable.");
                    selection.Replace(selection.Snapshot() with { LastActiveScopeId = prepared.Model.ScopeId });
                    await selection.SaveAsync(ct);
                    RequireRelationalSelection(generation, ct);
                    var previous = _relationalScopeEdit;
                    CloseAllOpenWindows(); ClearSelectedWorkbench();
                    _relationalRetainedWindows.Clear(); _relationalRetainedConnections.Clear();
                    ApplyRelationalScope(prepared);
                    RememberRelationalScopeSwitchWorkbenchBaseline();
                    if (previous != null) await previous.DisposeAsync();
                }
                catch { if (!ReferenceEquals(_relationalScopeEdit, prepared.Edit)) await prepared.Edit.DisposeAsync(); throw; }
            }
            finally { _relationalOwnerCommands.Release(); }
        }
        RequireRelationalSelection(generation, ct);
        return await OpenExternalResourceInActiveScopeAsync(request);
    }

    private void PresentRelationalGrid(OpenDocumentState state, IDataGridSource source, FloatingCodeWindow? sourceWindow)
    {
        Dispatcher.VerifyAccess();
        EnsureCodeViewVisible();
        var window = new FloatingSpreadsheetWindow(state, source);
        bool registered = false;
        try
        {
            AttachRelationalGridLifecycle(window, source);
            window.ApplyGridBackcolor(GetCodeWindowBackcolor(state.FilePath));
            window.CloseRequested += SpreadsheetWindow_CloseRequested;
            window.BoundsChanged += FloatingWindow_BoundsChanged;
            window.FilterReferenceCopyRequested += SpreadsheetWindow_FilterReferenceCopyRequested;
            window.BringToFrontRequested += (_, _) =>
            {
                SetActiveCodeWindow(null, syncOpenTabsSelection: false);
                BringToFront(window);
            };
            if (sourceWindow != null)
            {
                Point placement = FindReferenceWindowPlacement(sourceWindow, window.Width, window.Height);
                state.Left = placement.X; state.Top = placement.Y;
            }
            _openSpreadsheetWindows.Add(state.FilePath, window);
            registered = true;
            AddOpenTab(state); AddWindowToCodeView(window, state, select: true);
        }
        catch
        {
            if (registered)
            {
                _openSpreadsheetWindows.Remove(state.FilePath);
                RemoveWindowFromCodeView(window);
                RemoveOpenTab(state.FilePath);
            }
            RetireRelationalGridLifecycle(window);
            _relationalGridRetirements.Add(window.DisposeGridAsync());
            throw;
        }
        _windowSequence++; UpdateEmptyWorkspaceHint();
        StatusText = "Opened " + state.DisplayName;
    }

    private void RetireRelationalWindow(OpenDocumentState state, FloatingSpreadsheetWindow? grid = null)
    {
        if (_relational != null)
        {
            RetireRelationalAuxiliaryOwner(state);
            ForgetRelationalDocumentState(state);
            ReleaseRelationalDocumentAccess(state);
        }
        if (grid != null)
        {
            RetireRelationalGridLifecycle(grid);
            _relationalGridRetirements.RemoveAll(task => task.IsCompleted);
            _relationalGridRetirements.Add(DisposeGridAsync(grid));
        }
        static async Task DisposeGridAsync(FloatingSpreadsheetWindow window)
        {
            try { await window.DisposeGridAsync(); }
            catch (Exception error) { InternalLogService.Error(error, "Grid disposal failed."); }
        }
    }

    private async Task DisposeRelationalPersistenceAsync()
    {
        List<Exception> failures = [];
        await FinishAsync(_relationalStartupCancellation.Cancel);
        await FinishAsync(StopRelationalSnapshotWorkflowsAsync);
        await FinishAsync(DrainRelationalStateQueriesAsync);
        await FinishAsync(DrainRelationalAuxiliaryQueriesAsync);
        await FinishAsync(StopRelationalComparisonsAsync);
        await FinishAsync(DrainRelationalGridLifecycleAsync);
        await FinishAsync(StopRelationalExplorerAsync);
        await FinishAsync(DisposeRelationalDocumentAccessAsync);
        foreach (var grid in _openSpreadsheetWindows.Values.ToArray()) RetireRelationalWindow(grid.State, grid);
        await FinishAsync(() => Task.WhenAll(_relationalGridRetirements));
        _relationalGridRetirements.Clear();
        foreach (var edit in new IAsyncDisposable?[] { _relationalPreferenceEdit, _relationalWorkspaceEdit,
            _relationalSelectionEdit, _relationalScopeEdit, _relationalDiagramEdit, _relationalWorkbenchEdit })
            if (edit != null) await FinishAsync(() => edit.DisposeAsync().AsTask());
        await FinishAsync(_relationalStartupCancellation.RetireAsync);
        if (_relational != null)
        {
            var metrics = _relational.Session.Metrics.Snapshot();
            InternalLogService.Info("Relational SQL command execution metrics (reader consumption not included).",
                ("Started", metrics.Started), ("Completed", metrics.Completed), ("Failed", metrics.Failed),
                ("Cancelled", metrics.Cancelled), ("ActiveCommands", metrics.ActiveCommands),
                ("DroppedMeasurements", metrics.DroppedMeasurements), ("ExecutionMilliseconds", metrics.TotalExecutionDuration.TotalMilliseconds));
            var queries = _relational.QueryMetrics.Snapshot();
            InternalLogService.Info("Relational fetch metrics (operation-defined rows/bytes, not SQL Server network traffic).",
                ("Active", queries.Active), ("Completed", queries.Completed), ("Failed", queries.Failed),
                ("Cancelled", queries.Cancelled), ("Stale", queries.Stale), ("Abandoned", queries.Abandoned),
                ("Rows", queries.RowsRead), ("Bytes", queries.BytesRead), ("CacheHits", queries.CacheHits),
                ("DurationMilliseconds", queries.Duration.TotalMilliseconds));
        }
        if (failures.Count != 0) throw new AggregateException("Relational persistence cleanup failed.", failures);

        async Task FinishAsync(Func<Task> finish)
        {
            try { await finish(); }
            catch (Exception error) { failures.Add(error); }
        }
    }

    // True means the format was handled. Only an explicit legacy choice permits the old load path.
    private async Task<bool> TryLoadRelationalPersistenceAsync()
    {
        SqlServerConnectionOptions options = _persistenceOptions;
        var ct = _relationalStartupCancellation.Token;
        ct.ThrowIfCancellationRequested();
        SetStartupStage(StartupStage.DatabaseFormat);
        PersistenceFormatResult format = await new PersistenceFormatProbe().ProbeAsync(options.ConnectionString, ct);
        ct.ThrowIfCancellationRequested();
        if (format.Format == PersistenceFormat.Unavailable &&
            await RelationalDatabaseBootstrap.CanProveDatabaseAbsentAsync(options, ct))
        {
            if (MessageBox.Show(this, "The selected database does not exist. Create '" + options.DatabaseName + "'?",
                "New Database", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                throw new OperationCanceledException("Database creation cancelled.");
            await new RelationalSchemaInstaller().CreateEmptyDatabaseAsync(options.ConnectionString, ct);
            format = await new PersistenceFormatProbe().ProbeAsync(options.ConnectionString, ct);
        }
        if (format.Format == PersistenceFormat.Legacy)
        {
            var choice = MessageBox.Show(this,
                "This database uses the legacy document format. Migrate into a separate relational database? " +
                "The original will be preserved. Choose No to continue using the legacy database.",
                "Database Format", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.No) return false;
            if (choice != MessageBoxResult.Yes) throw new OperationCanceledException("Database loading cancelled.");
            var migration = new RelationalMigrationWindow(options.ConnectionString) { Owner = this };
            if (migration.ShowDialog() != true || migration.Result == null || migration.DestinationConnectionString == null)
                throw new OperationCanceledException("Migration was not completed. The original remains selected.");
            options = SqlServerConnectionOptions.FromConnectionString(migration.DestinationConnectionString);
            format = await new PersistenceFormatProbe().ProbeAsync(options.ConnectionString, ct);
            if (format.Format != PersistenceFormat.Relational)
                throw new InvalidOperationException("The migrated database is not ready for application use.");

            var remember = MessageBox.Show(this,
                "Use the validated destination on future starts as well?",
                "Database Selection", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (remember == MessageBoxResult.Yes)
            {
                var activation = await new RelationalApplicationConnection().ActivateAsync(options.ConnectionString,
                    new() { ExpectedDatabaseIdentity = format.DatabaseIdentity }, ct);
                if (activation.Status == RelationalConnectionActivationStatus.EnvironmentOverrideRequiresChoice)
                    MessageBox.Show(this,
                        "SURF2_CONNECTION_STRING overrides the saved selection. This session will use the migrated database; " +
                        "change that environment variable before restarting to select it again.",
                        "Database Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                else if (!activation.SettingsSaved)
                    throw new InvalidOperationException("The validated database could not be selected for future starts.");
            }
        }
        else if (format.Format == PersistenceFormat.Empty)
        {
            if (MessageBox.Show(this, "Initialize this verified empty database with the relational Surf schema?",
                "New Database", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                throw new OperationCanceledException("Database initialization cancelled.");
            await new RelationalSchemaInstaller().InitializeNewDatabaseAsync(options.ConnectionString, ct);
            format = await new PersistenceFormatProbe().ProbeAsync(options.ConnectionString, ct);
        }

        if (format.Format != PersistenceFormat.Relational)
            throw new InvalidOperationException("Cannot load this database: " + (format.Reason ?? format.Format.ToString()) +
                ". No schema or saved data was changed.");

        var runtime = new RelationalRuntime(options);
        SetStartupStage(StartupStage.DatabaseReadiness);
        await runtime.Session.RequireReadyAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (_shutdownRequested) throw new OperationCanceledException("Startup was cancelled by window close.");
        _relational = runtime;
        InitializeRelationalDocumentAccess();
        InitializeRelationalSnapshotWorkflows();
        RelationalGridDocumentPresenter = PresentRelationalGrid;
        PersistenceConnectionChanged?.Invoke(this, EventArgs.Empty);
        await LoadRelationalStartupAsync();
        return true;
    }
}

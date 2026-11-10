using System.ComponentModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Packages;

namespace Surf2;

public partial class SettingsWindow
{
    private SqlServerConnectionOptions? _relationalPersistenceSource;
    private string? _relationalPersistenceStagingRoot;
    private AccessCancellation? _relationalPersistenceOperation;
    private bool _relationalPersistenceCloseHandlerInstalled;

    /// <summary>Import creates a separate target; this result is not permission to activate it.</summary>
    public PersistenceTransferResult? RelationalPersistenceImportResult { get; private set; }

    public void InitializeRelationalPersistence(SqlServerConnectionOptions active, string? appDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(active); Dispatcher.VerifyAccess();
        _relationalPersistenceSource = active;
        _relationalPersistenceStagingRoot = Path.GetFullPath(appDataDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Surf2"));
        if (!_relationalPersistenceCloseHandlerInstalled)
        { Closing += RelationalPersistence_Closing; _relationalPersistenceCloseHandlerInstalled = true; }
    }

    // Parent replaces the existing TestConnectionAsync call with this read-only format probe.
    private async Task TestRelationalPersistenceConnectionAsync(CancellationToken ct = default)
    {
        if (!TryNormalizeConnectionString(PersistenceConnectionStringTextBox.Text, out var connection, out _))
        { SetRelationalPersistenceStatus("Enter a valid SQL Server connection string.", true); return; }
        await RunRelationalPersistenceOperationAsync(async token =>
        {
            var format = await new PersistenceFormatProbe().ProbeAsync(connection, token);
            if (format.Format == PersistenceFormat.Relational)
            { await new RelationalSession(connection).RequireReadyAsync(token); SetRelationalPersistenceStatus("Connection verified: supported Ready relational database."); }
            else if (format.Format == PersistenceFormat.Legacy)
                SetRelationalPersistenceStatus("Connection verified: legacy format. Migration is required for relational access.");
            else if (format.Format == PersistenceFormat.Empty)
                SetRelationalPersistenceStatus("Connection verified: empty database. No schema was initialized.");
            else SetRelationalPersistenceStatus("The database is not a supported Ready source. No schema or data was changed.", true);
        }, "Checking database format...", ct);
    }

    private async Task ExportRelationalPersistenceAsync(CancellationToken ct = default)
    {
        var source = GetRelationalPersistenceSource(); if (source == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Database", Filter = "Surf2 database export (*.surf2db.zip)|*.surf2db.zip|Zip files (*.zip)|*.zip",
            DefaultExt = ".surf2db.zip", FileName = "surf2-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".surf2db.zip",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        bool replace = File.Exists(dialog.FileName);
        if (replace && MessageBox.Show(this, "Replace the selected export file?", "Export Database", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunRelationalPersistenceOperationAsync(async token =>
        {
            var format = await new PersistenceFormatProbe().ProbeAsync(source.ConnectionString, token);
            if (format.Format == PersistenceFormat.Legacy)
            {
                bool allowBlockingLegacy = await ChooseRelationalExportIsolationAsync(new RelationalSession(source.ConnectionString), token);
                string localRoot = _relationalPersistenceStagingRoot ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Surf2");
                var legacy = await LegacyPersistenceExport.ExportAsync(dialog.FileName, source.ConnectionString, localRoot,
                    replace, allowBlockingLegacy, token);
                SetRelationalPersistenceStatus("Exported legacy format 1: " + legacy.DocumentCount.ToString("N0") +
                    " document(s) and " + legacy.LocalFileCount.ToString("N0") + " local file(s). No source schema or data was changed.");
                return;
            }
            if (format.Format != PersistenceFormat.Relational)
                throw new InvalidOperationException("Export requires a known legacy or supported Ready relational database.");
            var session = new RelationalSession(source.ConnectionString); await session.RequireReadyAsync(token);
            bool allowBlocking = await ChooseRelationalExportIsolationAsync(session, token);
            var result = await new RelationalPackageExporter(session).ExportAsync(dialog.FileName,
                new(ReplaceExisting: replace, AllowBlockingConsistentFallback: allowBlocking), token);
            long rows = result.Manifest.Tables.Sum(t => t.RowCount);
            SetRelationalPersistenceStatus("Exported " + rows.ToString("N0") + " authoritative row(s). Assets are embedded; local files were not added.");
        }, "Exporting the active database...", ct);
    }

    private async Task<bool> ChooseRelationalExportIsolationAsync(RelationalSession session, CancellationToken ct)
    {
        await using var connection = await session.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(int,snapshot_isolation_state) FROM sys.databases WHERE database_id=DB_ID();";
        using var cancel = RelationalSession.CancelCommand(command, ct);
        object? result = await command.ExecuteScalarAsync(ct);
        if (result is not int state || state is < 0 or > 3) throw new InvalidOperationException("The source consistency option could not be verified.");
        if (state == 1) return false;
        if (MessageBox.Show(this,
            "Snapshot isolation is not enabled. Export with a consistent blocking read instead? Other writers may wait until export finishes. No database options will be changed.",
            "Export Consistency", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            throw new OperationCanceledException("Blocking export was not authorized.");
        return true;
    }

    private async Task ImportRelationalPersistenceAsync(CancellationToken ct = default)
    {
        var current = GetRelationalPersistenceSource(); if (current == null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import Into New Database", Filter = "Surf2 database export (*.surf2db.zip)|*.surf2db.zip|Zip files (*.zip)|*.zip",
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var name = new RenameResourceWindow("Surf2_Import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"))
        { Owner = this, Title = "New Import Database" };
        if (name.ShowDialog() != true) return;
        if (MessageBox.Show(this,
            "Create a new database for this package? The current database and saved connection selection will remain unchanged. Package local files will not be restored into global application data.",
            "Import Into New Database", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunRelationalPersistenceOperationAsync(async token =>
        {
            _ = await RelationalPersistenceTransfer.InspectPackageAsync(dialog.FileName, token);
            var root = _relationalPersistenceStagingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Surf2");
            var progress = new Progress<string>(message => SetRelationalPersistenceStatus(message));
            var imported = await new RelationalPersistenceTransfer().ImportIntoNewDatabaseAsync(dialog.FileName, current,
                name.ResourceName, root, progress, token);
            RelationalPersistenceImportResult = imported;
            SetRelationalPersistenceStatus("Validated new database '" + imported.DatabaseName + "'. It has not been activated; the current selection is unchanged.");
            // Do not set PersistenceDatabaseImported, change ConnectionSettings, close, or hot-switch the current runtime.
        }, "Inspecting package...", ct);
    }

    private SqlServerConnectionOptions? GetRelationalPersistenceSource()
    {
        Dispatcher.VerifyAccess();
        if (_relationalPersistenceSource != null) return _relationalPersistenceSource;
        if (!TryNormalizeConnectionString(PersistenceConnectionStringTextBox.Text, out var connection, out _))
        { SetRelationalPersistenceStatus("Enter a valid SQL Server connection string.", true); return null; }
        return SqlServerConnectionOptions.FromConnectionString(connection);
    }

    private async Task RunRelationalPersistenceOperationAsync(Func<CancellationToken, Task> operation, string status, CancellationToken ct)
    {
        Dispatcher.VerifyAccess(); if (_relationalPersistenceOperation != null) return;
        if (!_relationalPersistenceCloseHandlerInstalled)
        { Closing += RelationalPersistence_Closing; _relationalPersistenceCloseHandlerInstalled = true; }
        var lifetime = new AccessCancellation(); _relationalPersistenceOperation = lifetime;
        using var externalCancellation = ct.Register(static owner => _ = ((AccessCancellation)owner!).Cancel(), lifetime);
        SetPersistenceActionsEnabled(false); SetRelationalPersistenceStatus(status);
        try { await operation(lifetime.Token); }
        catch (OperationCanceledException) { SetRelationalPersistenceStatus("Cancelled. The current database and connection selection are unchanged."); }
        // This is the UI operation boundary, including invalid packages, regex/timeouts,
        // and provider errors. Exception messages may contain connection details.
        catch (Exception)
        { SetRelationalPersistenceStatus("The operation did not complete. The current database and connection selection are unchanged; an incomplete import target requires review or a fresh destination.", true); }
        finally
        {
            // Keep the owner and controls fenced until the actual operation AND the
            // first CancelAsync callback batch finish. Closing never runs SQL I/O.
            await lifetime.RetireAsync();
            _relationalPersistenceOperation = null; SetPersistenceActionsEnabled(true);
        }
    }
    private void RelationalPersistence_Closing(object? sender, CancelEventArgs e)
    {
        if (_relationalPersistenceOperation == null) return;
        e.Cancel = true; _ = _relationalPersistenceOperation.Cancel(); SetRelationalPersistenceStatus("Cancelling the active operation...");
    }
    private void SetRelationalPersistenceStatus(string text, bool error = false)
    {
        PersistenceValidationText.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
        PersistenceValidationText.Text = text;
    }
}

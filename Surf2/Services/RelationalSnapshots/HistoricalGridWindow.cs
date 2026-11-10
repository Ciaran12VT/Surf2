using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services.RelationalGrid;

namespace Surf2.Services.RelationalSnapshots;

// A historical grid is deliberately not a saved current-document workspace window.
internal sealed class HistoricalGridWindow : Window
{
    private readonly FloatingSpreadsheetWindow _grid;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _drain;
    private bool _allowClose;
    public Task CloseCompletion => _closed.Task;
    public Exception? CleanupFailure { get; private set; }

    public HistoricalGridWindow(string title, IDataGridSource source)
    {
        Title = title; Width = 1000; Height = 650; MinWidth = 640; MinHeight = 420;
        SnapshotWindowStyles.ThemeWindow(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _grid = new(new OpenDocumentState { FilePath = "history://" + Guid.NewGuid().ToString("N"),
            DisplayName = title }, source);
        _grid.SetDockedMode(true); Content = _grid;
        if (_grid.FindName("SpreadsheetGrid") is DataGrid table) SnapshotWindowStyles.ThemeGrid(table);
        if (_grid.FindName("OuterBorder") is Border border) border.SetResourceReference(Border.BackgroundProperty, AppThemeService.SurfaceBrushKey);
        if (_grid.FindName("LoadingOverlay") is Border overlay) overlay.SetResourceReference(Border.BackgroundProperty, AppThemeService.SurfaceAltBrushKey);
        foreach (string name in new[] { "LoadingText", "StatusText" })
            if (_grid.FindName(name) is TextBlock text) text.SetResourceReference(TextBlock.ForegroundProperty, AppThemeService.SubtleTextBrushKey);
        if (_grid.FindName("StatusText") is TextBlock { Parent: Grid { Parent: Border footer } })
        {
            footer.SetResourceReference(Border.BackgroundProperty, AppThemeService.SurfaceAltBrushKey);
            footer.SetResourceReference(Border.BorderBrushProperty, AppThemeService.BorderBrushKey);
        }
        foreach (var action in new[] { ("RetryGridButton", "SurfPrimaryButtonStyle"), ("CancelGridOperationButton", "SurfCloseButtonStyle"),
            ("ExportGridButton", "SurfCompactSaveButtonStyle") })
            if (_grid.FindName(action.Item1) is Button button) button.SetResourceReference(FrameworkElement.StyleProperty, action.Item2);
        _grid.CloseRequested += (_, _) => Close();
        Closing += OnClosing; Closed += (_, _) =>
        {
            if (CleanupFailure is { } failure) _closed.TrySetException(failure);
            else _closed.TrySetResult();
        };
    }

    public Task CloseAndDrainAsync() { Dispatcher.VerifyAccess(); Close(); return CloseCompletion; }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_drain != null) { e.Cancel = true; return; }
        Task cleanup;
        try { cleanup = _grid.DisposeGridAsync(); }
        catch (Exception error) { CleanupFailure = error; _allowClose = true; return; }
        if (cleanup.IsCompleted)
        {
            try { cleanup.GetAwaiter().GetResult(); }
            catch (Exception error) { CleanupFailure = error; }
            _drain = Task.CompletedTask; _allowClose = true; return;
        }
        e.Cancel = true;
        _drain = DrainAsync(cleanup);
    }
    private async Task DrainAsync(Task cleanup)
    {
        try { await cleanup; }
        catch (Exception error) { CleanupFailure = error; }
        finally { _allowClose = true; await Dispatcher.InvokeAsync(Close); }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Surf2.Services;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalSnapshots;

internal sealed class SnapshotCopyTargetWindow : Window
{
    private readonly RelationalRuntime _runtime;
    private readonly ComboBox _snapshots = new() { DisplayMemberPath = nameof(SnapshotSummary.DisplayName) };
    private readonly TextBox _schema = new() { MaxLength = 128 }, _name = new() { MaxLength = 128 };
    private readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = new("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxLength = 4 * 1024 * 1024 };
    private readonly CheckBox _edit = new() { Content = "Edit definition" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _next = SnapshotWindowStyles.ToolButton("\uE72A", "Next snapshot page");
    private readonly Button _copy = SnapshotWindowStyles.Command("Copy", "SurfSaveButtonStyle");
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private Task _cancel = Task.CompletedTask;
    private Task? _completion;
    private SnapshotCursor? _cursor;
    private bool _busy, _closing, _closedWindow;
    public SnapshotSummary? SelectedSnapshot { get; private set; }
    public Exception? CleanupFailure { get; private set; }
    public string SchemaName => _schema.Text;
    public string ResourceName => _name.Text;
    public string? EditedText => _edit.IsChecked == true ? _text.Text : null;
    public Task CloseCompletion => _closed.Task;
    public Task CloseAndDrainAsync() { Dispatcher.VerifyAccess(); Close(); return CloseCompletion; }

    public SnapshotCopyTargetWindow(RelationalRuntime runtime, string schema, string name, string? definition)
    {
        _runtime = runtime; Title = "Copy Historical Resource"; Width = 740; Height = definition == null ? 300 : 560;
        SnapshotWindowStyles.ThemeWindow(this);
        MinWidth = 560; MinHeight = definition == null ? 300 : 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _copy.IsDefault = true; _schema.Text = schema; _name.Text = name; _text.Text = definition ?? "";
        _text.IsReadOnly = true; _edit.Visibility = _text.Visibility = definition == null ? Visibility.Collapsed : Visibility.Visible;
        _edit.Checked += (_, _) => _text.IsReadOnly = false; _edit.Unchecked += (_, _) => _text.IsReadOnly = true;
        var root = new Grid { Margin = new(16) };
        root.ColumnDefinitions.Add(new() { Width = new(90) }); root.ColumnDefinitions.Add(new()); root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        for (int i = 0; i < 3; i++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Add(root, new TextBlock { Text = "Snapshot" }, 0, 0); Add(root, _snapshots, 0, 1); Add(root, _next, 0, 2);
        Add(root, new TextBlock { Text = "Schema" }, 1, 0); Add(root, _schema, 1, 1, 2);
        Add(root, new TextBlock { Text = "Name" }, 2, 0); Add(root, _name, 2, 1, 2);
        Add(root, _edit, 3, 0, 3); Add(root, _text, 4, 0, 3); Add(root, SnapshotWindowStyles.Status(_status), 5, 0, 3);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = SnapshotWindowStyles.Command("Cancel", "SurfCloseButtonStyle"); close.IsCancel = true;
        commands.Children.Add(_copy); commands.Children.Add(close); Add(root, commands, 6, 0, 3); Content = root;
        _copy.Click += (_, _) =>
        {
            if (_snapshots.SelectedItem is not SnapshotSummary snapshot || string.IsNullOrWhiteSpace(SchemaName) || string.IsNullOrWhiteSpace(ResourceName))
            { _status.Text = "Select a snapshot and enter a schema and resource name."; return; }
            SelectedSnapshot = snapshot; DialogResult = true;
        };
        close.Click += (_, _) => Close(); _next.Click += async (_, _) => await ReadAsync(_cursor);
        Loaded += async (_, _) => await ReadAsync(null); Closing += OnClosing; Closed += (_, _) =>
        { _closedWindow = true; if (!_busy) CompleteClose(); };
    }
    private static void Add(Grid root, FrameworkElement child, int row, int column, int span = 1)
    { child.Margin = new(0, 0, 8, 8); Grid.SetRow(child, row); Grid.SetColumn(child, column); Grid.SetColumnSpan(child, span); root.Children.Add(child); }
    private async Task ReadAsync(SnapshotCursor? cursor)
    {
        if (_busy || _closing) return;
        _busy = true; _next.IsEnabled = _copy.IsEnabled = false;
        try
        {
            var page = await _runtime.Snapshots.ListSnapshotsAsync(pageSize: 50, cursor: cursor, ct: _cancellation.Token);
            if (_closing) return;
            _cursor = page.Next; _snapshots.ItemsSource = page.Items; _snapshots.SelectedIndex = page.Items.Count == 0 ? -1 : 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) _status.Text = error.Message; }
        finally
        {
            try { await _cancel; }
            catch (Exception error) { CleanupFailure ??= error; }
            _busy = false;
            if (_closedWindow) CompleteClose();
            else if (_closing) Close();
            else { _next.IsEnabled = _cursor != null; _copy.IsEnabled = _snapshots.Items.Count != 0; }
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (!_cancellation.IsCancellationRequested) _cancel = _cancellation.CancelAsync();
        if (_busy) e.Cancel = true;
    }
    private void CompleteClose()
    { _completion ??= CompleteAsync(); }
    private async Task CompleteAsync()
    {
        try { await _cancel; }
        catch (Exception error) { CleanupFailure ??= error; }
        finally
        {
            _cancellation.Dispose();
            if (CleanupFailure is { } failure) _closed.TrySetException(failure);
            else _closed.TrySetResult();
        }
    }
}

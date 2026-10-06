using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Surf2;

internal sealed record RelationalPickerPage<T>(IReadOnlyList<T> Items, object? Next) where T : class;

/// <summary>Retains one summary page. Details are requested only by the selected-item handler.</summary>
internal class RelationalQueryPickerWindow<T> : Window where T : class
{
    private readonly Func<object?, CancellationToken, Task<RelationalPickerPage<T>>> _read;
    private readonly Func<T, string> _label;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _cancellationGate = new();
    private readonly HashSet<Task> _operations = [];
    private CancellationTokenRegistration _ownerCancellation;
    private Task? _firstCancellation, _cleanup;
    private bool _closed, _ownerClosing;
    private readonly Stack<object?> _previous = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8) };
    private readonly Button _back, _next, _open;
    private object? _current, _continuation;
    private bool _loading;
    private bool _selecting;
    private bool _commandBusy;
    private long _request;
    protected readonly StackPanel Commands = new() { Orientation = Orientation.Horizontal };
    protected readonly Grid Details = new() { Margin = new(12, 0, 0, 0) };
    protected CancellationToken Lifetime => _lifetime.Token;
    public T? SelectedSummary { get; private set; }

    public RelationalQueryPickerWindow(string title, Func<object?, CancellationToken, Task<RelationalPickerPage<T>>> read,
        Func<T, string> label)
    {
        Title = title; Width = 800; Height = 560; MinWidth = 560; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; _read = read; _label = label;
        SetResourceReference(BackgroundProperty, "Theme.WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "Theme.TextBrush");
        var danger = new Style(typeof(Button), (Style)FindResource("SurfPrimaryButtonStyle"));
        danger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))));
        Resources["RelationalDangerButtonStyle"] = danger;
        var root = new Grid { Margin = new(12) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(Commands);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); Grid.SetRow(body, 1); root.Children.Add(body);
        body.Children.Add(_list); Grid.SetColumn(Details, 1); body.Children.Add(Details);
        Grid.SetRow(_status, 2); root.Children.Add(_status);
        var footer = new DockPanel { Margin = new(0, 8, 0, 0) }; Grid.SetRow(footer, 3); root.Children.Add(footer);
        _back = IconButton("\uE72B", "Previous page"); _next = IconButton("\uE72A", "Next page");
        var paging = new StackPanel { Orientation = Orientation.Horizontal }; paging.Children.Add(_back); paging.Children.Add(_next); footer.Children.Add(paging);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _open = CommandButton("Open"); var close = CommandButton("Close", "RelationalDangerButtonStyle"); actions.Children.Add(_open); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); Content = root;
        _back.Click += async (_, _) => { if (_previous.Count > 0) await ReadPageAsync(_previous.Pop(), false); };
        _next.Click += async (_, _) => { if (_continuation != null) { _previous.Push(_current); await ReadPageAsync(_continuation, false); } };
        _open.Click += async (_, _) =>
        {
            if (SelectedSummary == null || _loading || _selecting || _commandBusy || _closed) return;
            using var operation = BeginPickerOperation();
            try { await BeforeAcceptAsync(Lifetime); Lifetime.ThrowIfCancellationRequested(); DialogResult = true; }
            catch (Exception e) { Report(e); }
        };
        close.Click += async (_, _) =>
        {
            if (_closed || _commandBusy) return;
            using var operation = BeginPickerOperation();
            try { await BeforeAcceptAsync(Lifetime); Lifetime.ThrowIfCancellationRequested(); Close(); } catch (Exception e) { Report(e); }
        };
        _list.SelectionChanged += async (_, _) =>
        {
            if (_loading || _selecting || _commandBusy || _closed) return;
            using var operation = BeginPickerOperation();
            _selecting = true; _list.IsEnabled = _back.IsEnabled = _next.IsEnabled = false;
            long request = ++_request;
            var selected = (_list.SelectedItem as ListBoxItem)?.Tag as T;
            _open.IsEnabled = false;
            try
            {
                await OnSelectedAsync(selected, Lifetime);
                if (request != _request || Lifetime.IsCancellationRequested) return;
                SelectedSummary = selected; _open.IsEnabled = selected != null;
            }
            catch (Exception e) { SelectedSummary = null; Report(e); }
            finally { _selecting = false; if (!_closed) { _list.IsEnabled = true; _back.IsEnabled = _previous.Count > 0; _next.IsEnabled = _continuation != null; } }
        };
        Loaded += async (_, _) => await ReadPageAsync(null, true);
        Closed += (_, _) => { _closed = true; _ = DrainQueriesAsync(); };
        Closing += (_, e) =>
        {
            if (!_ownerClosing && !e.Cancel && !CanClose()) e.Cancel = true;
            if (!e.Cancel) { _closed = true; _request++; _ = CancelQueriesAsync(); }
        };
    }

    protected virtual Task OnSelectedAsync(T? selected, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task BeforeAcceptAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual bool CanClose() => true;
    protected virtual Task OnLogicalCloseAsync() => Task.CompletedTask;
    protected void Report(Exception e) => SetStatus(e is OperationCanceledException ? "Cancelled." :
        e is IOException ? "The selected item could not be read. No replacement was saved." : e.Message);
    protected void SetStatus(string text) { if (!_closed) _status.Text = text; }
    protected Task ReloadAsync() => ReadPageAsync(null, true);
    protected void ReplaceSelectedSummary(T summary) { SelectedSummary = summary; }

    protected bool IsLogicallyClosed => _closed;
    protected bool CanBeginPickerCommand => !_loading && !_selecting && !_closed && !_commandBusy;
    protected void SetPickerCommandBusy(bool busy)
    {
        _commandBusy = busy;
        if (_closed) return;
        _list.IsEnabled = !busy; _back.IsEnabled = !busy && _previous.Count > 0;
        _next.IsEnabled = !busy && _continuation != null; _open.IsEnabled = !busy && SelectedSummary != null;
    }
    private sealed class PickerOperation(RelationalQueryPickerWindow<T> owner) : IDisposable
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _finished.Task;
        public void Dispose() { owner._operations.Remove(Completion); _finished.TrySetResult(); }
    }
    protected IDisposable BeginPickerOperation()
    {
        if (_closed) throw new OperationCanceledException();
        var operation = new PickerOperation(this); _operations.Add(operation.Completion); return operation;
    }
    private Task CancelQueriesAsync() { lock (_cancellationGate) return _firstCancellation ??= _lifetime.CancelAsync(); }
    internal void LinkOwnerLifetime(CancellationToken ct)
    {
        _ownerCancellation = ct.Register(() =>
        {
            _ = CancelQueriesAsync();
            _ = Dispatcher.InvokeAsync(() => { if (!_closed) { _ownerClosing = true; Close(); } });
        });
    }
    internal Task DrainQueriesAsync() => _cleanup ??= DrainQueriesCoreAsync();
    private async Task DrainQueriesCoreAsync()
    {
        _closed = true; _request++;
        try { await Task.WhenAll(_operations.ToArray().Append(CancelQueriesAsync())); }
        finally
        {
            try { await OnLogicalCloseAsync(); }
            finally { _ownerCancellation.Dispose(); _lifetime.Dispose(); }
        }
    }

    private async Task ReadPageAsync(object? cursor, bool reset)
    {
        if (_loading || _selecting || _closed || Lifetime.IsCancellationRequested) return;
        using var operation = BeginPickerOperation();
        _loading = true; _list.IsEnabled = _back.IsEnabled = _next.IsEnabled = _open.IsEnabled = false;
        long request = ++_request;
        try
        {
            await BeforeAcceptAsync(Lifetime);
            var page = await _read(cursor, Lifetime);
            if (request != _request || Lifetime.IsCancellationRequested) return;
            _list.Items.Clear();
            foreach (var item in page.Items) _list.Items.Add(new ListBoxItem { Content = _label(item), Tag = item, Padding = new(8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch });
            SelectedSummary = null; await OnSelectedAsync(null, Lifetime);
            _current = cursor; _continuation = page.Next; if (reset) _previous.Clear();
            SetStatus(page.Items.Count == 0 ? "No saved items." : string.Empty);
        }
        catch (Exception e) { Report(e); }
            finally { _loading = false; if (!_closed) { _list.IsEnabled = true; _back.IsEnabled = _previous.Count > 0; _next.IsEnabled = _continuation != null; } }
    }

    protected static Button CommandButton(string text, string styleKey = "SurfPrimaryButtonStyle")
    {
        var button = new Button { Content = text, MinWidth = 76, Padding = new(10, 5, 10, 5), Margin = new(3) };
        button.SetResourceReference(StyleProperty, styleKey);
        return button;
    }
    private static Button IconButton(string glyph, string tooltip)
    {
        var button = new Button { Content = glyph, FontFamily = new("Segoe MDL2 Assets"), FontSize = 16,
            Width = 34, Height = 32, Margin = new(3), ToolTip = tooltip, Padding = new(0) };
        button.SetResourceReference(StyleProperty, "SurfPrimaryButtonStyle");
        return button;
    }
}

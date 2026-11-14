using System.Windows;
using System.Windows.Controls;
using Surf2.Services.LiveSql;

namespace Surf2;

public sealed class LiveSqlBrowserWindow : Window
{
    private readonly LiveSqlService _service;
    private readonly ListBox _objects = new() { DisplayMemberPath = nameof(LiveSqlObject.QualifiedName) };
    private readonly TextBox _definition = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Consolas"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    public event EventHandler? CreateSnapshotRequested;
    public LiveSqlBrowserWindow(string connectionString, string? title = null)
    {
        _service = new(connectionString); Title = title ?? _service.Database + " — Live SQL"; Width = 1100; Height = 740;
        var root = new DockPanel { Margin = new(12) }; var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "Refresh", Margin = new(4) }; refresh.Click += async (_, _) => await RefreshAsync();
        var query = new Button { Content = "Query", Margin = new(4) };
        query.Click += (_, _) =>
        {
            var item = _objects.SelectedItem as LiveSqlObject;
            string sql = item?.Type is "U" or "V" ? SqlQueryWindow.InitialQuery(item.Schema, item.Name) : string.Empty;
            new SqlQueryWindow(_service, sql) { Owner = this }.Show();
        };
        var capture = new Button { Content = "Create snapshot…", Margin = new(4) }; capture.Click += (_, _) => CreateSnapshotRequested?.Invoke(this, EventArgs.Empty);
        tools.Children.Add(refresh); tools.Children.Add(query); tools.Children.Add(capture); DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status); DockPanel.SetDock(_objects, Dock.Left); _objects.Width = 310; root.Children.Add(_objects); root.Children.Add(_definition);
        _objects.SelectionChanged += (_, _) => { if (_objects.SelectedItem is LiveSqlObject item) _definition.Text = item.Definition ?? "Table " + item.QualifiedName + "\nUse Query to read table data."; };
        Content = root; Loaded += async (_, _) => await RefreshAsync(); Closed += (_, _) => _lifetime.Cancel();
    }
    private async Task RefreshAsync()
    {
        _status.Text = "Qualifying source and loading definitions…";
        try
        {
            var result = await _service.QualifyAsync(_lifetime.Token); result.RequireQualified();
            if (_lifetime.IsCancellationRequested) return;
            _objects.ItemsSource = result.Objects.OrderBy(o => o.Type).ThenBy(o => o.Schema).ThenBy(o => o.Name).ToArray(); _status.Text = result.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _objects.ItemsSource = null; _definition.Clear(); _status.Text = e.Message; }
    }
}

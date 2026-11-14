using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Surf2.Services.RemoteFolders;

namespace Surf2;

public sealed class LocalCacheWindow : Window
{
    private readonly FolderCacheStore _cache;
    private readonly DataGrid _grid;
    private readonly TextBox _perSource, _total, _reserve;
    private readonly TextBlock _status;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _closed, _busy;
    public LocalCacheWindow(FolderCacheStore? cache = null)
    {
        _cache = cache ?? RemoteFolderServices.Default.Cache;
        var body = RemoteFolderDialogUi.Body(this, "Local Cache", 900, 720);
        RemoteFolderDialogUi.Note(body, "Surf2 keeps managed Git and Blob copies between launches. Removing a local copy keeps the source connection; it can be downloaded again. Linked user folders and checkouts are never removed here.");
        _grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, Height = 300, HeadersVisibility = DataGridHeadersVisibility.Column };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding(nameof(FolderCacheEntry.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Bytes on disk", Binding = new Binding(nameof(FolderCacheEntry.Bytes)) { StringFormat = "N0" }, Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Last used", Binding = new Binding(nameof(FolderCacheEntry.LastUsedUtc)) { StringFormat = "g" }, Width = 145 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Last refresh", Binding = new Binding(nameof(FolderCacheEntry.LastRefreshUtc)) { StringFormat = "g" }, Width = 145 });
        body.Children.Add(_grid);
        var settings = _cache.ReadSettings();
        _perSource = RemoteFolderDialogUi.Field(body, "Per-source limit (MiB)", (settings.PerSourceBytes / 1048576).ToString());
        _total = RemoteFolderDialogUi.Field(body, "Total managed cache limit (MiB)", (settings.TotalBytes / 1048576).ToString());
        _reserve = RemoteFolderDialogUi.Field(body, "Keep this much disk space free (MiB)", (settings.FreeSpaceReserveBytes / 1048576).ToString());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; body.Children.Add(buttons);
        RemoteFolderDialogUi.Button(buttons, "Save limits", Save_Click);
        RemoteFolderDialogUi.Button(buttons, "Remove local copy", Remove_Click);
        RemoteFolderDialogUi.Button(buttons, "Refresh list", async (_, _) => await RefreshAsync());
        RemoteFolderDialogUi.Button(buttons, "Close", (_, _) => Close());
        _status = RemoteFolderDialogUi.Note(body, "");
        Loaded += async (_, _) => await RefreshAsync(); Closed += (_, _) => { _closed = true; _cancellation.Cancel(); };
    }
    private async Task RefreshAsync()
    {
        if (_busy) return; _busy = true;
        try
        {
            var entries = await _cache.ListAsync(_cancellation.Token);
            long total = await Task.Run(() => FolderCacheStore.DirectoryBytes(_cache.Root), _cancellation.Token);
            if (_closed) return;
            _grid.ItemsSource = entries;
            _status.Text = $"Git and Blob copies: {entries.Sum(e => e.Bytes):N0} bytes. All local source data, including SQL result/definition storage: {total:N0} bytes. Cache: {_cache.Root}";
        }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex); }
        finally { _busy = false; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; _busy = true;
        try
        {
            static long Bytes(string text) => checked(long.Parse(text, System.Globalization.CultureInfo.InvariantCulture) * 1048576);
            await _cache.SaveSettingsAsync(new(Bytes(_perSource.Text), Bytes(_total.Text), Bytes(_reserve.Text)), _cancellation.Token);
            if (!_closed) _status.Text = "Cache limits saved. Existing copies are retained; new preparations must fit the new limits.";
        }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex); }
        finally { _busy = false; }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _grid.SelectedItem is not FolderCacheEntry entry) return;
        if (MessageBox.Show(this, "Remove Surf2's local copy of '" + entry.Name + "'? The connection stays registered.", "Remove local copy", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _busy = true;
        try { await _cache.RemoveAsync(entry.Key, _cancellation.Token); }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex); _busy = false; return; }
        _busy = false; await RefreshAsync();
    }
}

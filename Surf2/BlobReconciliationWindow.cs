using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Surf2.Models;
using Surf2.Services.RemoteFolders;

namespace Surf2;

/// <summary>The caller owns/disposes the plan. A true result means all reviewed actions verified.</summary>
public sealed class BlobReconciliationWindow : Window
{
    private readonly BlobSourceService _service;
    private readonly BlobReconciliationPlan _plan;
    private readonly DataGrid _grid;
    private readonly TextBlock _status;
    private readonly Button _apply, _compare;
    private readonly Row[] _rows;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _closed, _applying;
    public BlobReconciliationWindow(BlobSourceService service, BlobReconciliationPlan plan)
    {
        _service = service; _plan = plan;
        var body = RemoteFolderDialogUi.Body(this, "Review folder and Blob changes", 1050, 700);
        RemoteFolderDialogUi.Note(body, "Local: " + plan.LocalFolder + "\nBlob: " + plan.Source.Endpoint + "/" + plan.Source.Container + "/" + plan.Source.Prefix +
            "\nThe resource stays unloaded until both locations agree. Choose a version for conflicts and explicitly choose whether a missing copy should be restored or deleted.");
        _rows = plan.Entries.Where(e => e.Different).Select(e => new Row(e)).ToArray();
        _grid = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, Height = 400, Margin = new Thickness(0, 10, 0, 5), HeadersVisibility = DataGridHeadersVisibility.Column };
        _grid.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new Binding(nameof(Row.Path)), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Local", Binding = new Binding(nameof(Row.Local)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Blob", Binding = new Binding(nameof(Row.Blob)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Difference", Binding = new Binding(nameof(Row.Status)), IsReadOnly = true, Width = 150 });
        var choice = new FrameworkElementFactory(typeof(ComboBox));
        choice.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(Row.Choices)));
        choice.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(Row.Choice)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _grid.Columns.Add(new DataGridTemplateColumn { Header = "Action", CellTemplate = new DataTemplate { VisualTree = choice }, Width = 140 });
        body.Children.Add(_grid);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; body.Children.Add(buttons);
        _compare = RemoteFolderDialogUi.Button(buttons, "Compare selected", Compare_Click);
        _apply = RemoteFolderDialogUi.Button(buttons, "Apply reviewed changes", Apply_Click);
        RemoteFolderDialogUi.Button(buttons, "Cancel / keep unloaded", (_, _) => Close());
        _status = RemoteFolderDialogUi.Note(body, _rows.Length == 0 ? "Both locations agree." : _rows.Length + " differences require review.");
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); };
    }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not Row row || _applying) return;
        _compare.IsEnabled = false;
        try
        {
            var content = await _service.ReadComparisonAsync(_plan, row.Entry, _cancellation.Token);
            if (_closed) return;
            var left = new ComparisonResource("Local: " + row.Path, "File", row.Path, ComparisonResourceKind.File, "file", false, true, false, "local:" + row.Path);
            var right = new ComparisonResource("Blob: " + row.Path, "File", row.Path, ComparisonResourceKind.File, "file", false, true, false, "blob:" + row.Path);
            new ResourceFileDiffWindow(left, right, content.Local, content.Blob, false, false) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex); }
        finally { if (!_closed) _compare.IsEnabled = true; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_rows.Any(r => !r.Choices.Contains(r.Choice))) { _status.Text = "Choose an action for every differing file."; return; }
        var decisions = _rows.ToDictionary(r => r.Path, r => r.Choice, StringComparer.Ordinal);
        int uploads = decisions.Count(p => p.Value == BlobReconciliationChoice.KeepLocal), downloads = decisions.Count(p => p.Value == BlobReconciliationChoice.KeepBlob),
            deletes = decisions.Count(p => p.Value is BlobReconciliationChoice.DeleteLocal or BlobReconciliationChoice.DeleteBlob);
        if (MessageBox.Show(this, $"Apply this reviewed plan?\n\nUploads: {uploads}\nDownloads: {downloads}\nDeletions: {deletes}\n\nChosen versions replace the other copies. If a file changed since review, reconciliation stops and the resource stays unloaded.",
            "Confirm reconciliation", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _applying = true; _apply.IsEnabled = _compare.IsEnabled = false; _grid.IsEnabled = false;
        try
        {
            var result = await _service.ApplyAsync(_plan, decisions, new Progress<FolderSourceProgress>(p => { if (!_closed) _status.Text = p.Phase; }), _cancellation.Token);
            if (_closed) return;
            _status.Text = result.Message;
            if (result.Succeeded) DialogResult = true;
            // A failed/partial operation must be rescanned; never replay this stale reviewed plan.
        }
        catch (Exception ex) { if (!_closed) _status.Text = RemoteFolderServices.DescribeError(ex) + " Load the resource again to rescan."; }
        finally { _applying = false; }
    }
    private sealed class Row(BlobReconciliationEntry entry)
    {
        public BlobReconciliationEntry Entry { get; } = entry;
        public string Path => Entry.Path;
        public string Local => Entry.Local == null ? "Missing" : $"{Entry.Local.ByteCount:N0} bytes\n{Entry.Local.LastModified.LocalDateTime:g}";
        public string Blob => Entry.Remote == null ? "Missing" : $"{Entry.Remote.ByteCount:N0} bytes\n{Entry.Remote.LastModified?.LocalDateTime:g}";
        public string Status => Entry.Local == null || Entry.Remote == null ? Entry.PreviouslyMatched ? "Previously matched; one copy missing" : "New file on one side" : "Different content";
        public IReadOnlyList<BlobReconciliationChoice> Choices => Entry.Choices;
        public BlobReconciliationChoice Choice { get; set; } = entry.SuggestedChoice;
    }
}

using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Surf2.Controls;
using Surf2.Controls.RelationalGrid;
using Surf2.Models;
using Surf2.Services.RelationalGrid;

namespace Surf2.RegressionTests;

internal static class V2GridSelectionTests
{
    public static void Run()
    {
        var selection = new GridCellSelection();
        selection.Select(900_004, 5); selection.Select(900_000, 2, extend: true);
        Require(selection.Range == new GridCellRange(900_000, 900_004, 2, 5), "Reverse selection keeps a normalized rectangle.");
        Require(selection.Range!.Value.Contains(900_002, 3) && !selection.Range.Value.Contains(900_002, 1), "Range membership respects both axes.");
        selection.SelectAll(0, 5);
        Require(selection.Range!.Value.Contains(long.MaxValue - 1, 5), "Select All represents later rows without enumerating them.");
        selection.Select(1, 2, extend: true);
        Require(selection.Range == new GridCellRange(0, 1, 0, 2), "Shift navigation after Select All uses its top-left anchor.");
        selection.Clear(); Require(selection.Range == null, "A replacement query clears positional selection.");

        var source = new SelectionSource();
        var query = new SelectionQuery(source, 1_000_000);
        var copied = GridOutput.CopySelectionAsync(query, new(900_000, 900_001, 0, 1), [1, 0]).GetAwaiter().GetResult();
        string expected = "value 900000\t900000" + Environment.NewLine + "\"quoted\t\"\"x\"\"\"\t900001" + Environment.NewLine;
        Require(copied.Text == expected && copied.RowCount == 2 && copied.ColumnCount == 2,
            "Selected copy preserves range, displayed column order, quotes, tabs and no header.");
        Require(query.ReadStarts.SequenceEqual([900_000L]) && query.MaximumRequestRows == 2,
            "A distant selected range reads only its bounded provider page, without full-stream enumeration.");
        var all = GridOutput.CopySelectionAsync(new SelectionQuery(source, 3), new(0, long.MaxValue, 0, 0, true), [0]).GetAwaiter().GetResult();
        Require(all.RowCount == 3 && all.Text == "0" + Environment.NewLine + "1" + Environment.NewLine + "2" + Environment.NewLine,
            "Select All copy covers the full query without headers.");
        try { GridOutput.CopySelectionAsync(query, new(900_000, 900_001, 0, 1), [0, 1], maxClipboardBytes: 10).GetAwaiter().GetResult();
            throw new InvalidOperationException("Oversized selection copy succeeded."); }
        catch (GridLimitException) { }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { GridOutput.CopySelectionAsync(query, new(0, 1, 0, 0), [0], cancellationToken: cancellation.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("Canceled selection copy succeeded."); }
            catch (OperationCanceledException) { }
        }
        CheckRealGrid(source);
    }

    private static void CheckRealGrid(SelectionSource source)
    {
        var window = new FloatingSpreadsheetWindow(new OpenDocumentState { FilePath = "selection-fixture.csv", Width = 650, Height = 400 }, source);
        var table = (DataGrid)window.FindName("SpreadsheetGrid");
        var root = new Grid { Width = 650, Height = 400 }; root.Children.Add(window);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 selection regression offscreen")
        { PositionX = -32000, PositionY = -32000, Width = 650, Height = 400,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        try
        {
            Await((Task)Invoke(window, "PrepareGridPublicationAsync", CancellationToken.None)!);
            host.RootVisual = root;
            root.Measure(new Size(650, 400)); root.Arrange(new Rect(0, 0, 650, 400)); root.UpdateLayout();
            Await(Task.Delay(100));
            DataGrid.SelectAllCommand.Execute(null, table);
            var selected = Field<GridCellSelection>(window, "_gridSelection");
            var items = Field<GridViewportItems>(window, "_gridItems");
            Require(selected.Range?.AllRows == true && table.SelectedCells.Count <= 1 && items.ResidentItems < 512,
                "The real Select All command selects a million-row range while native selection and row residency stay bounded.");
            source.Latest!.AvailableRows += 10;
            Invoke(window, "PollGridCount");
            Require(selected.Range?.Contains(1_000_009, 1) == true,
                "Progressive row-count publication retains Select All coverage.");
            table.ScrollIntoView(items[900_000], table.Columns[1]);
            root.UpdateLayout(); Await(Task.Delay(150));
            Invoke(window, "PaintGridSelection");
            var cells = Visuals<DataGridCell>(table).ToArray();
            Require(cells.Length > 0 && cells.All(cell => GridSelectionVisual.GetIsSelected(cell)) &&
                items.ResidentItems <= items.Limits.MaxItems && table.SelectedCells.Count <= 1 && window.GridReadError == null,
                "Far scrolling paints only realized selected cells and preserves bounded row residency.");
            selected.Select(899_999, 0); selected.Select(900_001, 1, extend: true);
            Invoke(window, "PaintGridSelection");
            Require(cells.Where(cell => cell.DataContext is GridViewportRow row && row.Position > 900_001)
                    .All(cell => !GridSelectionVisual.GetIsSelected(cell)), "Replacing Select All removes stale selection paint from recycled cells.");
        }
        finally
        {
            Await(window.DisposeGridAsync()); host.RootVisual = null; root.Children.Clear();
        }
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
            Dispatcher.PushFrame(frame); timeout.Stop();
            if (!task.IsCompleted) throw new TimeoutException("The selection fixture did not complete.");
        }
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class SelectionSource : IDataGridSource
    {
        public GridDescriptor Descriptor { get; } = new(Guid.NewGuid(), "selection-test", [new(0, "Id", "Id"), new(1, "Value", "Value")], 1_000_000, 1_000_000, 1);
        public Task<long> DisplayRowCount => Task.FromResult(1_000_000L);
        public bool IsInvalidated => false;
        public long OverlayGeneration => 0;
        public long OverlayBytes => 0;
        public SelectionQuery? Latest { get; private set; }
        public void SetCell(GridRow row, int columnOrdinal, string? value) { }
        public void ClearEdits() { }
        public Task<IGridQuerySession> CreateQueryAsync(GridQuery? query = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IGridQuerySession>(Latest = new(this, 1_000_000)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SelectionQuery(SelectionSource source, long count) : IGridQuerySession
    {
        public long AvailableRows { get; set; } = count;
        public List<long> ReadStarts { get; } = [];
        public int MaximumRequestRows { get; private set; }
        public GridDescriptor Descriptor => source.Descriptor;
        public long Generation => 1;
        public long OverlayGeneration => 0;
        public string Fingerprint => "selection-test";
        public GridCount Count => new(AvailableRows, true);
        public Task<long> Completion => Task.FromResult(AvailableRows);
        public GridCacheStatistics CacheStatistics => new(0, 0, 0);
        public bool TryGetCachedPage(GridPageRequest request, out GridPage? page) { page = null; return false; }
        public Task<GridPage> ReadPageAsync(GridPageRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadStarts.Add(request.Start);
            MaximumRequestRows = Math.Max(MaximumRequestRows, request.MaxRows);
            var rows = Enumerable.Range(0, (int)Math.Max(0, Math.Min(request.MaxRows, AvailableRows - request.Start))).Select(offset =>
            {
                long position = request.Start + offset;
                return new GridRow(Descriptor.SourceId, position, [position.ToString(), position == 900_001 ? "quoted\t\"x\"" : "value " + position], 128);
            }).ToArray();
            return Task.FromResult(new GridPage(Descriptor.SourceId, 1, 0, request, rows, rows.Length * 128,
                request.Start + rows.Length < AvailableRows) { IsRangeComplete = true });
        }
        public IAsyncEnumerable<GridRow> StreamAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Selected copy must use bounded pages.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

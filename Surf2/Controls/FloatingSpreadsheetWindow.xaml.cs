using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Surf2.Models;
using Surf2.Services;

namespace Surf2.Controls;

public partial class FloatingSpreadsheetWindow : UserControl
{
    private const double MinimumFontSize = 8;
    private const double MaximumFontSize = 36;
    private const double FontZoomStep = 1.1;
    private const int ColumnAutoSizeSampleRows = 100;

    private readonly ObservableCollection<CsvGridRow> _rows = [];
    private readonly Dictionary<int, string> _filters = [];
    private readonly Dictionary<int, string> _pendingColumnFilters = [];
    private readonly Dictionary<string, string> _pendingColumnNameFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _anyMatchFilterColumnIndexes = [];
    private readonly DispatcherTimer _filterDebounceTimer;
    private List<string> _headers = [];
    private bool _isDragging;
    private bool _isApplyingColumnFilters;
    private bool _matchAnyColumnFilter;
    private bool _pendingColumnFiltersUseAnyMatch;
    private Point _dragStartPoint;
    private double _dragStartLeft;
    private double _dragStartTop;
    private int _totalRowCount;
    private int _columnCount;

    public FloatingSpreadsheetWindow(OpenDocumentState state, string content)
    {
        InitializeComponent();

        State = state;
        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        TitleText.Text = string.IsNullOrWhiteSpace(state.DisplayName)
            ? Path.GetFileName(state.FilePath)
            : state.DisplayName;
        ToolTip = state.FilePath;

        double initialFontSize = state.FontSize > 0 ? state.FontSize : 13;
        SpreadsheetGrid.FontSize = Math.Clamp(initialFontSize, MinimumFontSize, MaximumFontSize);
        State.FontSize = SpreadsheetGrid.FontSize;
        SpreadsheetGrid.ItemsSource = _rows;

        _filterDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _filterDebounceTimer.Tick += FilterDebounceTimer_Tick;

        PreviewMouseWheel += FloatingSpreadsheetWindow_PreviewMouseWheel;
        IsVisibleChanged += FloatingSpreadsheetWindow_IsVisibleChanged;
        ApplyColumnFilters(State.SpreadsheetFilters, useAnyMatch: false);
        Loaded += async (_, _) => await LoadCsvAsync(content);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? BoundsChanged;

    public event EventHandler? BringToFrontRequested;

    public event EventHandler? FilterReferenceCopyRequested;

    public OpenDocumentState State { get; }

    public void SetDockedMode(bool isDocked)
    {
        HeaderRow.Height = isDocked ? new GridLength(0) : new GridLength(32);
        HeaderBar.Visibility = isDocked ? Visibility.Collapsed : Visibility.Visible;
        OuterBorder.BorderThickness = isDocked ? new Thickness(0) : new Thickness(1);

        foreach (Thumb thumb in FindVisualChildren<Thumb>(this))
        {
            thumb.Visibility = isDocked ? Visibility.Collapsed : Visibility.Visible;
        }

        if (isDocked)
        {
            Width = double.NaN;
            Height = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        Width = Math.Max(MinWidth, State.Width);
        Height = Math.Max(MinHeight, State.Height);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public void ApplyGridBackcolor(Brush backcolor)
    {
        SpreadsheetGrid.Background = backcolor;
        SpreadsheetGrid.RowBackground = backcolor;
    }

    public void ApplyColumnFilters(IReadOnlyDictionary<int, string> filters)
    {
        ApplyColumnFilters(filters, useAnyMatch: true);
    }

    private void ApplyColumnFilters(IReadOnlyDictionary<int, string> filters, bool useAnyMatch)
    {
        _pendingColumnFilters.Clear();
        _pendingColumnFiltersUseAnyMatch = useAnyMatch;
        foreach ((int columnIndex, string filter) in filters)
        {
            if (columnIndex < 0 || string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            _pendingColumnFilters[columnIndex] = filter;
        }

        ApplyPendingColumnFilters();
    }

    public void ApplyColumnFiltersByName(IReadOnlyDictionary<string, string> filters)
    {
        _pendingColumnNameFilters.Clear();
        _pendingColumnFiltersUseAnyMatch = false;
        foreach ((string columnName, string filter) in filters)
        {
            if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            _pendingColumnNameFilters[columnName.Trim()] = filter.Trim();
        }

        ApplyPendingColumnFilters();
    }

    public IReadOnlyDictionary<string, string> GetColumnFiltersByName()
    {
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((int columnIndex, string filter) in _filters.OrderBy(pair => pair.Key))
        {
            if (columnIndex < 0 ||
                columnIndex >= _headers.Count ||
                string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            string header = _headers[columnIndex];
            if (!string.IsNullOrWhiteSpace(header))
            {
                filters[header] = filter;
            }
        }

        return filters;
    }

    private async Task LoadCsvAsync(string content)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingText.Text = "Loading CSV...";

        try
        {
            CsvGridDocument document = await Task.Run(() => CsvGridParser.Parse(content));
            _rows.Clear();
            foreach (CsvGridRow row in document.Rows)
            {
                _rows.Add(row);
            }

            _totalRowCount = _rows.Count;
            _columnCount = document.Headers.Count;
            BuildColumns(document.Headers);
            if (!ApplyPendingColumnFilters())
            {
                ApplyFilters();
            }

            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Could not load CSV: {ex.Message}";
        }
    }

    private void BuildColumns(IReadOnlyList<string> headers)
    {
        SpreadsheetGrid.Columns.Clear();
        _headers = headers.ToList();
        _filters.Clear();

        for (int index = 0; index < headers.Count; index++)
        {
            int columnIndex = index;
            var column = new DataGridTextColumn
            {
                Binding = new Binding($"[{columnIndex}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                Header = CreateColumnHeader(headers[index], columnIndex),
                MinWidth = 80,
                Width = new DataGridLength(EstimateColumnWidth(headers[index], columnIndex), DataGridLengthUnitType.Pixel)
            };

            SpreadsheetGrid.Columns.Add(column);
        }
    }

    private bool ApplyPendingColumnFilters()
    {
        if ((_pendingColumnFilters.Count == 0 && _pendingColumnNameFilters.Count == 0) ||
            SpreadsheetGrid.Columns.Count == 0)
        {
            return false;
        }

        _filterDebounceTimer.Stop();
        MapPendingColumnNameFilters();
        _filters.Clear();
        _anyMatchFilterColumnIndexes.Clear();
        _matchAnyColumnFilter = false;

        int appliedFilterCount = 0;
        _isApplyingColumnFilters = true;
        try
        {
            ClearFilterTextBoxes();

            foreach ((int columnIndex, string filter) in _pendingColumnFilters)
            {
                if (columnIndex >= SpreadsheetGrid.Columns.Count)
                {
                    continue;
                }

                _filters[columnIndex] = filter;
                _anyMatchFilterColumnIndexes.Add(columnIndex);
                SetFilterTextBoxText(columnIndex, filter);
                appliedFilterCount++;
            }
        }
        finally
        {
            _isApplyingColumnFilters = false;
        }

        _pendingColumnFilters.Clear();
        _pendingColumnNameFilters.Clear();
        _filterDebounceTimer.Stop();

        if (appliedFilterCount == 0)
        {
            _pendingColumnFiltersUseAnyMatch = false;
            return false;
        }

        _matchAnyColumnFilter = _pendingColumnFiltersUseAnyMatch && appliedFilterCount > 1;
        _pendingColumnFiltersUseAnyMatch = false;
        SyncStateFilters();
        ApplyFilters();
        return true;
    }

    private void MapPendingColumnNameFilters()
    {
        if (_pendingColumnNameFilters.Count == 0 || _headers.Count == 0)
        {
            return;
        }

        foreach ((string columnName, string filter) in _pendingColumnNameFilters)
        {
            int columnIndex = _headers.FindIndex(header =>
                string.Equals(header, columnName, StringComparison.OrdinalIgnoreCase));
            if (columnIndex >= 0)
            {
                _pendingColumnFilters[columnIndex] = filter;
            }
        }
    }

    private FrameworkElement CreateColumnHeader(string headerText, int columnIndex)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = headerText,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 3)
        };

        var filter = new TextBox
        {
            Height = 24,
            MinWidth = 48,
            FontWeight = FontWeights.Normal,
            FontSize = 10,
            Tag = columnIndex,
            ToolTip = $"Filter {headerText}",
            VerticalContentAlignment = VerticalAlignment.Center
        };
        filter.TextChanged += FilterTextBox_TextChanged;

        Grid.SetRow(header, 0);
        Grid.SetRow(filter, 1);
        panel.Children.Add(header);
        panel.Children.Add(filter);
        return panel;
    }

    private double EstimateColumnWidth(string headerText, int columnIndex)
    {
        int maxLength = headerText.Length;
        foreach (CsvGridRow row in _rows.Take(ColumnAutoSizeSampleRows))
        {
            maxLength = Math.Max(maxLength, row[columnIndex].Length);
        }

        return Math.Clamp((maxLength * 7) + 36, 90, 320);
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not int columnIndex)
        {
            return;
        }

        string filter = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            _filters.Remove(columnIndex);
        }
        else
        {
            _filters[columnIndex] = filter;
        }

        SyncStateFilters();

        if (!_isApplyingColumnFilters)
        {
            _matchAnyColumnFilter = false;
            _anyMatchFilterColumnIndexes.Clear();
        }

        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void FilterDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _filterDebounceTimer.Stop();
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = RowMatchesFilters;
        view.Refresh();
        UpdateStatusText(view.Cast<object>().Count());
    }

    private bool RowMatchesFilters(object item)
    {
        if (item is not CsvGridRow row)
        {
            return false;
        }

        if (_matchAnyColumnFilter && _anyMatchFilterColumnIndexes.Count > 0)
        {
            bool hasAnyColumnFilter = false;
            foreach (int columnIndex in _anyMatchFilterColumnIndexes)
            {
                if (!_filters.TryGetValue(columnIndex, out string? filter) ||
                    string.IsNullOrWhiteSpace(filter))
                {
                    continue;
                }

                hasAnyColumnFilter = true;
                if (row[columnIndex].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (hasAnyColumnFilter)
            {
                return false;
            }
        }

        foreach ((int columnIndex, string filter) in _filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            if (row[columnIndex].IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateStatusText(int visibleRows)
    {
        string rowText = visibleRows == _totalRowCount
            ? $"Rows: {_totalRowCount}"
            : $"Rows: {visibleRows}/{_totalRowCount}";
        StatusText.Text = $"{rowText}    Columns: {_columnCount}";
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _filters.Clear();
        _pendingColumnFilters.Clear();
        _pendingColumnNameFilters.Clear();
        _anyMatchFilterColumnIndexes.Clear();
        _matchAnyColumnFilter = false;
        _pendingColumnFiltersUseAnyMatch = false;
        SyncStateFilters();
        ClearFilterTextBoxes();
        ApplyFilters();
    }

    private void CopyFilterReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        FilterReferenceCopyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FloatingSpreadsheetWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        RestoreFiltersFromStateIfNeeded();
        RefreshFilterTextBoxes();
        ApplyFilters();
    }

    private void RestoreFiltersFromStateIfNeeded()
    {
        if (_filters.Count > 0 || State.SpreadsheetFilters.Count == 0)
        {
            return;
        }

        foreach ((int columnIndex, string filter) in State.SpreadsheetFilters)
        {
            if (columnIndex >= 0 && !string.IsNullOrWhiteSpace(filter))
            {
                _filters[columnIndex] = filter;
            }
        }
    }

    private void SyncStateFilters()
    {
        State.SpreadsheetFilters.Clear();
        foreach ((int columnIndex, string filter) in _filters.OrderBy(pair => pair.Key))
        {
            if (columnIndex >= 0 && !string.IsNullOrWhiteSpace(filter))
            {
                State.SpreadsheetFilters[columnIndex] = filter;
            }
        }
    }

    private void RefreshFilterTextBoxes()
    {
        if (SpreadsheetGrid.Columns.Count == 0)
        {
            return;
        }

        _isApplyingColumnFilters = true;
        try
        {
            List<KeyValuePair<int, string>> filters = _filters.ToList();
            _filters.Clear();
            ClearFilterTextBoxes();
            foreach ((int columnIndex, string filter) in filters)
            {
                _filters[columnIndex] = filter;
                SetFilterTextBoxText(columnIndex, filter);
            }

            SyncStateFilters();
        }
        finally
        {
            _isApplyingColumnFilters = false;
        }
    }

    private void ClearFilterTextBoxes()
    {
        foreach (DataGridColumn column in SpreadsheetGrid.Columns)
        {
            if (column.Header is not DependencyObject header)
            {
                continue;
            }

            foreach (TextBox textBox in FindVisualChildren<TextBox>(header))
            {
                textBox.Text = string.Empty;
            }
        }
    }

    private void SetFilterTextBoxText(int columnIndex, string filter)
    {
        if (columnIndex < 0 || columnIndex >= SpreadsheetGrid.Columns.Count)
        {
            return;
        }

        if (SpreadsheetGrid.Columns[columnIndex].Header is not DependencyObject header)
        {
            return;
        }

        foreach (TextBox textBox in FindVisualChildren<TextBox>(header))
        {
            if (textBox.Tag is int tag && tag == columnIndex)
            {
                textBox.Text = filter;
                return;
            }
        }
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        BringToFrontRequested?.Invoke(this, EventArgs.Empty);
        _isDragging = true;
        _dragStartPoint = e.GetPosition(parentCanvas);
        _dragStartLeft = Canvas.GetLeft(this);
        _dragStartTop = Canvas.GetTop(this);

        if (double.IsNaN(_dragStartLeft))
        {
            _dragStartLeft = 0;
        }

        if (double.IsNaN(_dragStartTop))
        {
            _dragStartTop = 0;
        }

        HeaderBar.CaptureMouse();
        e.Handled = true;
    }

    private void HeaderBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed || Parent is not Canvas parentCanvas)
        {
            return;
        }

        Point currentPoint = e.GetPosition(parentCanvas);
        double left = Math.Max(0, _dragStartLeft + currentPoint.X - _dragStartPoint.X);
        double top = Math.Max(0, _dragStartTop + currentPoint.Y - _dragStartPoint.Y);

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);

        State.Left = left;
        State.Top = top;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void HeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        HeaderBar.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        BringToFrontRequested?.Invoke(this, EventArgs.Empty);

        string direction = (sender as FrameworkElement)?.Tag?.ToString() ?? "BottomRight";
        bool resizeLeft = direction.Contains("Left", StringComparison.OrdinalIgnoreCase);
        bool resizeRight = direction.Contains("Right", StringComparison.OrdinalIgnoreCase);
        bool resizeTop = direction.Contains("Top", StringComparison.OrdinalIgnoreCase);
        bool resizeBottom = direction.Contains("Bottom", StringComparison.OrdinalIgnoreCase);

        double left = Canvas.GetLeft(this);
        double top = Canvas.GetTop(this);

        if (double.IsNaN(left))
        {
            left = 0;
        }

        if (double.IsNaN(top))
        {
            top = 0;
        }

        double newLeft = left;
        double newTop = top;
        double newWidth = Width;
        double newHeight = Height;

        if (resizeLeft)
        {
            newWidth = Math.Max(MinWidth, Width - e.HorizontalChange);
            newLeft = left + Width - newWidth;

            if (newLeft < 0)
            {
                newWidth += newLeft;
                newLeft = 0;
                newWidth = Math.Max(MinWidth, newWidth);
            }
        }

        if (resizeRight)
        {
            newWidth = Math.Max(MinWidth, newWidth + e.HorizontalChange);
        }

        if (resizeTop)
        {
            newHeight = Math.Max(MinHeight, Height - e.VerticalChange);
            newTop = top + Height - newHeight;

            if (newTop < 0)
            {
                newHeight += newTop;
                newTop = 0;
                newHeight = Math.Max(MinHeight, newHeight);
            }
        }

        if (resizeBottom)
        {
            newHeight = Math.Max(MinHeight, newHeight + e.VerticalChange);
        }

        Width = newWidth;
        Height = newHeight;
        Canvas.SetLeft(this, newLeft);
        Canvas.SetTop(this, newTop);

        State.Left = newLeft;
        State.Top = newTop;
        State.Width = Width;
        State.Height = Height;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void FloatingSpreadsheetWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        double multiplier = e.Delta > 0 ? FontZoomStep : 1 / FontZoomStep;
        SpreadsheetGrid.FontSize = Math.Clamp(SpreadsheetGrid.FontSize * multiplier, MinimumFontSize, MaximumFontSize);
        State.FontSize = SpreadsheetGrid.FontSize;
        e.Handled = true;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FloatingSpreadsheetWindow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        BringToFrontRequested?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target)
            {
                yield return target;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

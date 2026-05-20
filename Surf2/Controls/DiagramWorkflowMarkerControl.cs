using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Surf2.Controls;

public sealed class DiagramWorkflowMarkerControl : UserControl
{
    private const double MarkerSize = 36;

    private readonly TextBlock _itemNumberTextBlock;
    private readonly Ellipse _selectionRing;
    private readonly Border _queryWarningBadge;

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _startLeft;
    private double _startTop;

    public DiagramWorkflowMarkerControl(
        string workflowId,
        string workflowItemId,
        int itemNumber,
        string itemDescription,
        string? diagramObjectId = null)
    {
        DiagramObjectId = string.IsNullOrWhiteSpace(diagramObjectId)
            ? Guid.NewGuid().ToString("N")
            : diagramObjectId;
        WorkflowId = workflowId;
        WorkflowItemId = workflowItemId;
        ItemNumber = itemNumber;
        ItemDescription = itemDescription ?? string.Empty;

        Width = MarkerSize;
        Height = MarkerSize;
        MinWidth = MarkerSize;
        MinHeight = MarkerSize;
        Focusable = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeAll;
        ToolTip = ItemDescription;

        var markerCircle = new Ellipse
        {
            Fill = CreateFrozenBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            Stroke = CreateFrozenBrush(Color.FromRgb(0x1E, 0x3A, 0x8A)),
            StrokeThickness = 2
        };

        _itemNumberTextBlock = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            IsHitTestVisible = false,
            Text = FormatItemNumber(itemNumber)
        };

        _selectionRing = new Ellipse
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 3,
            Margin = new Thickness(-4),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _queryWarningBadge = CreateQueryWarningBadge();

        var layout = new Grid { Background = Brushes.Transparent };
        layout.Children.Add(markerCircle);
        layout.Children.Add(_itemNumberTextBlock);
        layout.Children.Add(_selectionRing);
        layout.Children.Add(_queryWarningBadge);
        Content = layout;

        ContextMenu = CreateContextMenu();
    }

    public event EventHandler? DeleteRequested;

    public event EventHandler<DiagramLayerChangeRequestedEventArgs>? LayerChangeRequested;

    public event EventHandler? Selected;

    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionCompleted;

    public event EventHandler? OpenRequested;

    public string DiagramObjectId { get; }

    public string WorkflowId { get; private set; }

    public string WorkflowItemId { get; private set; }

    public int ItemNumber { get; private set; }

    public string ItemDescription { get; private set; }

    public bool IsSelected
    {
        get => _selectionRing.Visibility == Visibility.Visible;
        set => _selectionRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetCanvasBounds(double left, double top, double width, double height)
    {
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = MarkerSize;
        Height = MarkerSize;
    }

    public void ApplyDetails(string workflowId, string workflowItemId, int itemNumber, string itemDescription)
    {
        WorkflowId = workflowId;
        WorkflowItemId = workflowItemId;
        ItemNumber = itemNumber;
        ItemDescription = itemDescription ?? string.Empty;
        _itemNumberTextBlock.Text = FormatItemNumber(ItemNumber);
        ToolTip = ItemDescription;
    }

    public void SetHasUnresolvedQueries(bool hasUnresolvedQueries)
    {
        _queryWarningBadge.Visibility = hasUnresolvedQueries ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            Selected?.Invoke(this, EventArgs.Empty);
            OpenRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (Parent is not Canvas parentCanvas)
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        Focus();
        Selected?.Invoke(this, EventArgs.Empty);
        InteractionStarted?.Invoke(this, EventArgs.Empty);

        _isDragging = true;
        _dragStartPoint = e.GetPosition(parentCanvas);
        _startLeft = GetCanvasLeft();
        _startTop = GetCanvasTop();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed || Parent is not Canvas parentCanvas)
        {
            base.OnMouseMove(e);
            return;
        }

        Point currentPoint = e.GetPosition(parentCanvas);
        Vector delta = currentPoint - _dragStartPoint;
        Canvas.SetLeft(this, _startLeft + delta.X);
        Canvas.SetTop(this, _startTop + delta.Y);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _isDragging = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (!AreClose(_startLeft, GetCanvasLeft()) || !AreClose(_startTop, GetCanvasTop()))
        {
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    private ContextMenu CreateContextMenu()
    {
        var openItem = new MenuItem { Header = "Open Workflow Item" };
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        var bringForwardItem = new MenuItem { Header = "Bring Forward" };
        bringForwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.BringForward));

        var sendBackwardItem = new MenuItem { Header = "Send Backward" };
        sendBackwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendBackward));

        var sendToBackItem = new MenuItem { Header = "Send to Back" };
        sendToBackItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendToBack));

        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(bringForwardItem);
        contextMenu.Items.Add(sendBackwardItem);
        contextMenu.Items.Add(sendToBackItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(deleteItem);
        return contextMenu;
    }

    private static Border CreateQueryWarningBadge()
    {
        return new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, -6, -6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Firebrick,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Text = "!"
            }
        };
    }

    private double GetCanvasLeft()
    {
        double left = Canvas.GetLeft(this);
        return double.IsNaN(left) ? 0 : left;
    }

    private double GetCanvasTop()
    {
        double top = Canvas.GetTop(this);
        return double.IsNaN(top) ? 0 : top;
    }

    private static string FormatItemNumber(int itemNumber)
    {
        return itemNumber <= 0 ? "?" : itemNumber.ToString();
    }

    private static bool AreClose(double first, double second)
    {
        return Math.Abs(first - second) < 0.1;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

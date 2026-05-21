using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Surf2.Controls;

public sealed class DiagramPortalControl : UserControl
{
    private const double PortalSize = 30;

    private readonly Ellipse _outerCircle;
    private readonly Ellipse _innerCircle;
    private readonly Border _selectionRing;

    private bool _isDragging;
    private bool _isLocked;
    private Point _dragStartPoint;
    private double _startLeft;
    private double _startTop;

    public DiagramPortalControl(
        string portalName,
        string pairedPortalDiagramId = "",
        string pairedPortalObjectId = "",
        string pairedAddress = "",
        string? diagramObjectId = null)
    {
        DiagramObjectId = string.IsNullOrWhiteSpace(diagramObjectId)
            ? Guid.NewGuid().ToString("N")
            : diagramObjectId;
        PortalName = portalName ?? string.Empty;
        PairedPortalDiagramId = pairedPortalDiagramId ?? string.Empty;
        PairedPortalObjectId = pairedPortalObjectId ?? string.Empty;

        Width = PortalSize;
        Height = PortalSize;
        MinWidth = PortalSize;
        MinHeight = PortalSize;
        Focusable = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeAll;

        _outerCircle = new Ellipse
        {
            StrokeThickness = 2
        };
        _innerCircle = new Ellipse
        {
            Width = 10,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _selectionRing = new Border
        {
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(PortalSize / 2),
            Margin = new Thickness(-4),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        var layout = new Grid { Background = Brushes.Transparent };
        layout.Children.Add(_outerCircle);
        layout.Children.Add(_innerCircle);
        layout.Children.Add(_selectionRing);
        Content = layout;

        ContextMenu = CreateContextMenu();
        ApplyPairing(PairedPortalDiagramId, PairedPortalObjectId, pairedAddress);
    }

    public event EventHandler? DeleteRequested;

    public event EventHandler<DiagramLayerChangeRequestedEventArgs>? LayerChangeRequested;

    public event EventHandler? Selected;

    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionCompleted;

    public event EventHandler? OpenRequested;

    public string DiagramObjectId { get; }

    public string PortalName { get; private set; }

    public string PairedPortalDiagramId { get; private set; }

    public string PairedPortalObjectId { get; private set; }

    public bool IsPaired =>
        !string.IsNullOrWhiteSpace(PairedPortalDiagramId) &&
        !string.IsNullOrWhiteSpace(PairedPortalObjectId);

    public bool IsSelected
    {
        get => _selectionRing.Visibility == Visibility.Visible;
        set => _selectionRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            _isLocked = value;
            if (_isLocked)
            {
                _isDragging = false;
                Cursor = null;
                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }
            }
            else
            {
                Cursor = Cursors.SizeAll;
            }
        }
    }

    public void SetCanvasBounds(double left, double top, double width, double height)
    {
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = PortalSize;
        Height = PortalSize;
    }

    public void ApplyDetails(string portalName)
    {
        PortalName = portalName ?? string.Empty;
    }

    public void ApplyPairing(string pairedPortalDiagramId, string pairedPortalObjectId, string pairedAddress)
    {
        PairedPortalDiagramId = pairedPortalDiagramId ?? string.Empty;
        PairedPortalObjectId = pairedPortalObjectId ?? string.Empty;
        ApplyVisualState(pairedAddress);
    }

    public void ClearPairing()
    {
        ApplyPairing(string.Empty, string.Empty, string.Empty);
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
        if (IsLocked)
        {
            e.Handled = true;
            return;
        }

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
        var openItem = new MenuItem { Header = "Open Portal" };
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var bringForwardItem = new MenuItem { Header = "Bring Forward" };
        bringForwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.BringForward));

        var sendBackwardItem = new MenuItem { Header = "Send Backward" };
        sendBackwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendBackward));

        var sendToBackItem = new MenuItem { Header = "Send to Back" };
        sendToBackItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendToBack));

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

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

    private void ApplyVisualState(string pairedAddress)
    {
        if (IsPaired)
        {
            _outerCircle.Fill = CreateFrozenBrush(Color.FromRgb(0xDB, 0xEA, 0xFE));
            _outerCircle.Stroke = CreateFrozenBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            _innerCircle.Fill = CreateFrozenBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
            ToolTip = $"To: {pairedAddress}";
            return;
        }

        _outerCircle.Fill = CreateFrozenBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        _outerCircle.Stroke = CreateFrozenBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        _innerCircle.Fill = CreateFrozenBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        ToolTip = "Unpaired portal";
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

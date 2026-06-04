using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Surf2.Models;

namespace Surf2.Controls;

public sealed class DiagramInfoPointControl : UserControl
{
    public const double InfoPointSize = 28;

    private const double MinimumInfoPointSize = 14;
    private const double ResizeHitThickness = 5;

    private readonly Ellipse _outerCircle;
    private readonly TextBlock _iconTextBlock;
    private readonly Ellipse _selectionRing;
    private readonly Border _queryWarningBadge;

    private bool _isDragging;
    private bool _isResizing;
    private bool _isLocked;
    private bool _hasQueries;
    private ResizeHandle _activeResizeHandle;
    private Point _dragStartPoint;
    private double _startLeft;
    private double _startTop;
    private double _startSize;

    public DiagramInfoPointControl(string? diagramObjectId = null)
    {
        DiagramObjectId = string.IsNullOrWhiteSpace(diagramObjectId)
            ? Guid.NewGuid().ToString("N")
            : diagramObjectId;

        Width = InfoPointSize;
        Height = InfoPointSize;
        MinWidth = MinimumInfoPointSize;
        MinHeight = MinimumInfoPointSize;
        MaxWidth = InfoPointSize;
        MaxHeight = InfoPointSize;
        Focusable = true;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeAll;

        _outerCircle = new Ellipse
        {
            StrokeThickness = 2
        };

        _iconTextBlock = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            IsHitTestVisible = false,
            Text = "i"
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
        layout.Children.Add(_outerCircle);
        layout.Children.Add(_iconTextBlock);
        layout.Children.Add(_selectionRing);
        layout.Children.Add(_queryWarningBadge);
        Content = layout;

        ContextMenu = CreateContextMenu();
        ApplyVisualState();
    }

    private enum ResizeHandle
    {
        None,
        Left,
        Top,
        Right,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public event EventHandler? MetadataRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler<DiagramLayerChangeRequestedEventArgs>? LayerChangeRequested;

    public event EventHandler? Selected;

    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionCompleted;

    public string DiagramObjectId { get; }

    public DiagramObjectMetadata Metadata { get; private set; } = new();

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
                _isResizing = false;
                _activeResizeHandle = ResizeHandle.None;
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
        double size = NormalizeRequestedSize(width, height);
        Width = size;
        Height = size;
        _iconTextBlock.FontSize = Math.Max(9, size * 0.64);
    }

    public void ApplyMetadata(DiagramObjectMetadata metadata)
    {
        Metadata = metadata.Clone();
        _hasQueries = Metadata.Queries.Count > 0;
        SetHasUnresolvedQueries(DiagramQueryState.HasUnresolvedMetadataQueries(Metadata));
        ApplyVisualState();
    }

    public void SetHasUnresolvedQueries(bool hasUnresolvedQueries)
    {
        _queryWarningBadge.Visibility = hasUnresolvedQueries ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetHasQueries(bool hasQueries)
    {
        _hasQueries = hasQueries;
        ApplyVisualState();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
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

        _dragStartPoint = e.GetPosition(parentCanvas);
        _startLeft = GetCanvasLeft();
        _startTop = GetCanvasTop();
        _startSize = GetCurrentSize();
        _activeResizeHandle = GetResizeHandle(e.GetPosition(this));
        _isResizing = _activeResizeHandle != ResizeHandle.None;
        _isDragging = !_isResizing;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsLocked)
        {
            Cursor = null;
            base.OnMouseMove(e);
            return;
        }

        if (_isResizing && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateResize(e);
            e.Handled = true;
            return;
        }

        if (_isDragging && e.LeftButton == MouseButtonState.Pressed && Parent is Canvas parentCanvas)
        {
            Point currentPoint = e.GetPosition(parentCanvas);
            Vector delta = currentPoint - _dragStartPoint;
            Canvas.SetLeft(this, _startLeft + delta.X);
            Canvas.SetTop(this, _startTop + delta.Y);
            e.Handled = true;
            return;
        }

        if (_isDragging || _isResizing)
        {
            base.OnMouseMove(e);
            return;
        }

        Cursor = GetCursor(GetResizeHandle(e.GetPosition(this)));
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_isDragging && !_isResizing)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        bool changed =
            !AreClose(_startLeft, GetCanvasLeft()) ||
            !AreClose(_startTop, GetCanvasTop()) ||
            !AreClose(_startSize, GetCurrentSize());

        _isDragging = false;
        _isResizing = false;
        _activeResizeHandle = ResizeHandle.None;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (changed)
        {
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!_isDragging && !_isResizing)
        {
            Cursor = null;
        }

        base.OnMouseLeave(e);
    }

    private ContextMenu CreateContextMenu()
    {
        var metadataItem = new MenuItem { Header = "Open Metadata" };
        metadataItem.Click += (_, _) => MetadataRequested?.Invoke(this, EventArgs.Empty);

        var bringForwardItem = new MenuItem { Header = "Bring Forward" };
        bringForwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.BringForward));

        var sendBackwardItem = new MenuItem { Header = "Send Backward" };
        sendBackwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendBackward));

        var sendToBackItem = new MenuItem { Header = "Send to Back" };
        sendToBackItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendToBack));

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(metadataItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(bringForwardItem);
        contextMenu.Items.Add(sendBackwardItem);
        contextMenu.Items.Add(sendToBackItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(deleteItem);
        return contextMenu;
    }

    private void ApplyVisualState()
    {
        Brush iconBrush = HasQueries()
            ? CreateFrozenBrush(Color.FromRgb(0xDC, 0x26, 0x26))
            : CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27));

        if (HasLinkOrDocumentation())
        {
            _outerCircle.Fill = Brushes.White;
            _outerCircle.Stroke = CreateFrozenBrush(Color.FromRgb(0x11, 0x18, 0x27));
            _iconTextBlock.Foreground = iconBrush;
            ToolTip = "Info point";
            return;
        }

        _outerCircle.Fill = CreateFrozenBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
        _outerCircle.Stroke = CreateFrozenBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        _iconTextBlock.Foreground = HasQueries()
            ? iconBrush
            : CreateFrozenBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        ToolTip = "Info point with no link or documentation";
    }

    private bool HasLinkOrDocumentation()
    {
        return !string.IsNullOrWhiteSpace(Metadata.Link) ||
            !string.IsNullOrWhiteSpace(Metadata.DocumentationXaml);
    }

    private bool HasQueries()
    {
        return _hasQueries;
    }

    private void UpdateResize(MouseEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        Vector delta = e.GetPosition(parentCanvas) - _dragStartPoint;
        double size = ClampSize(GetRequestedResizeSize(delta));
        double left = _startLeft;
        double top = _startTop;

        if (ResizesLeft(_activeResizeHandle))
        {
            left = _startLeft + (_startSize - size);
        }

        if (ResizesTop(_activeResizeHandle))
        {
            top = _startTop + (_startSize - size);
        }

        SetCanvasBounds(left, top, size, size);
    }

    private double GetRequestedResizeSize(Vector delta)
    {
        return _activeResizeHandle switch
        {
            ResizeHandle.Left => _startSize - delta.X,
            ResizeHandle.Top => _startSize - delta.Y,
            ResizeHandle.Right => _startSize + delta.X,
            ResizeHandle.Bottom => _startSize + delta.Y,
            ResizeHandle.TopLeft => _startSize + Math.Max(-delta.X, -delta.Y),
            ResizeHandle.TopRight => _startSize + Math.Max(delta.X, -delta.Y),
            ResizeHandle.BottomLeft => _startSize + Math.Max(-delta.X, delta.Y),
            ResizeHandle.BottomRight => _startSize + Math.Max(delta.X, delta.Y),
            _ => _startSize
        };
    }

    private ResizeHandle GetResizeHandle(Point point)
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        bool left = point.X <= ResizeHitThickness;
        bool right = point.X >= width - ResizeHitThickness;
        bool top = point.Y <= ResizeHitThickness;
        bool bottom = point.Y >= height - ResizeHitThickness;

        if (top && left)
        {
            return ResizeHandle.TopLeft;
        }

        if (top && right)
        {
            return ResizeHandle.TopRight;
        }

        if (bottom && left)
        {
            return ResizeHandle.BottomLeft;
        }

        if (bottom && right)
        {
            return ResizeHandle.BottomRight;
        }

        if (left)
        {
            return ResizeHandle.Left;
        }

        if (right)
        {
            return ResizeHandle.Right;
        }

        if (top)
        {
            return ResizeHandle.Top;
        }

        if (bottom)
        {
            return ResizeHandle.Bottom;
        }

        return ResizeHandle.None;
    }

    private static Cursor GetCursor(ResizeHandle handle)
    {
        return handle switch
        {
            ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.SizeAll
        };
    }

    private double GetCurrentSize()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        return double.IsNaN(width) || width <= 0 ? InfoPointSize : width;
    }

    private static double NormalizeRequestedSize(double width, double height)
    {
        double requestedSize = width > 0 && height > 0
            ? Math.Min(width, height)
            : Math.Max(width, height);
        return ClampSize(requestedSize > 0 ? requestedSize : InfoPointSize);
    }

    private static double ClampSize(double size)
    {
        return Math.Clamp(size, MinimumInfoPointSize, InfoPointSize);
    }

    private static bool ResizesLeft(ResizeHandle handle)
    {
        return handle is ResizeHandle.Left or ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
    }

    private static bool ResizesTop(ResizeHandle handle)
    {
        return handle is ResizeHandle.Top or ResizeHandle.TopLeft or ResizeHandle.TopRight;
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

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Surf2.Models;

namespace Surf2.Controls;

public sealed class DiagramLineControl : UserControl
{
    private const double LineCanvasPadding = 14;
    private const double HitDistance = 7;
    private const double EndpointHitDistance = 9;
    private const double ArrowLength = 15;
    private const double ArrowWidth = 10;

    private readonly Canvas _layout;
    private readonly Line _line;
    private readonly Polygon _arrowHead;
    private readonly Border _selectionBorder;
    private readonly Border _queryWarningBadge;
    private readonly MenuItem _looseStateItem;

    private InteractionMode _interactionMode;
    private Point _interactionStartPoint;
    private Point _startLineStartPoint;
    private Point _startLineEndPoint;

    public DiagramLineControl(
        string colorText,
        bool hasEndArrow,
        bool isLoose = false,
        string? diagramObjectId = null)
    {
        DiagramObjectId = string.IsNullOrWhiteSpace(diagramObjectId)
            ? Guid.NewGuid().ToString("N")
            : diagramObjectId;
        ColorText = NormalizeColorText(colorText, "#000000");
        HasEndArrow = hasEndArrow;
        IsLoose = isLoose;

        Focusable = true;
        Background = Brushes.Transparent;

        _layout = new Canvas { Background = Brushes.Transparent };
        _line = new Line
        {
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _arrowHead = new Polygon
        {
            IsHitTestVisible = false
        };
        _selectionBorder = new Border
        {
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _queryWarningBadge = CreateQueryWarningBadge();

        _layout.Children.Add(_line);
        _layout.Children.Add(_arrowHead);
        _layout.Children.Add(_selectionBorder);
        _layout.Children.Add(_queryWarningBadge);
        Content = _layout;

        _looseStateItem = new MenuItem();
        ContextMenu = CreateContextMenu();
        ApplyLineStyle();
        SetAbsoluteEndpoints(new Point(0, 0), new Point(80, 0));
        UpdateHitTestState();
    }

    private enum InteractionMode
    {
        None,
        Drag,
        ResizeStart,
        ResizeEnd
    }

    public event EventHandler? Selected;

    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionCompleted;

    public event EventHandler? EditRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler<DiagramLayerChangeRequestedEventArgs>? LayerChangeRequested;

    public event EventHandler? LooseStateToggleRequested;

    public string DiagramObjectId { get; }

    public string ColorText { get; private set; }

    public bool HasEndArrow { get; private set; }

    public bool IsLoose { get; private set; }

    public Point StartPoint { get; private set; }

    public Point EndPoint { get; private set; }

    public DiagramObjectMetadata Metadata { get; private set; } = new();

    public bool IsSelected
    {
        get => _selectionBorder.Visibility == Visibility.Visible;
        set => _selectionBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetAbsoluteEndpoints(Point startPoint, Point endPoint)
    {
        StartPoint = startPoint;
        EndPoint = endPoint;
        UpdateBoundsAndGeometry();
    }

    public void ApplyDetails(string colorText, bool hasEndArrow)
    {
        ColorText = NormalizeColorText(colorText, "#000000");
        HasEndArrow = hasEndArrow;
        ApplyLineStyle();
        UpdateBoundsAndGeometry();
    }

    public void ApplyMetadata(DiagramObjectMetadata metadata)
    {
        Metadata = metadata.Clone();
        SetHasUnresolvedQueries(DiagramQueryState.HasUnresolvedMetadataQueries(Metadata));
    }

    public void SetHasUnresolvedQueries(bool hasUnresolvedQueries)
    {
        _queryWarningBadge.Visibility = hasUnresolvedQueries ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLooseState(bool isLoose)
    {
        IsLoose = isLoose;
        if (!IsLoose)
        {
            _interactionMode = InteractionMode.None;
            Cursor = null;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        UpdateHitTestState();
    }

    public bool ContainsCanvasPoint(Point canvasPoint)
    {
        if ((canvasPoint - StartPoint).Length <= EndpointHitDistance)
        {
            return true;
        }

        if ((canvasPoint - EndPoint).Length <= EndpointHitDistance)
        {
            return true;
        }

        return DistanceToSegment(canvasPoint, StartPoint, EndPoint) <= HitDistance;
    }

    private void UpdateHitTestState()
    {
        IsHitTestVisible = IsLoose;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsLoose)
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        if (Parent is not Canvas parentCanvas)
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        Point localPoint = e.GetPosition(this);
        Point canvasPoint = e.GetPosition(parentCanvas);
        InteractionMode requestedMode = GetInteractionMode(localPoint);
        if (requestedMode == InteractionMode.None)
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        Focus();
        Selected?.Invoke(this, EventArgs.Empty);

        _interactionMode = requestedMode;
        _interactionStartPoint = canvasPoint;
        _startLineStartPoint = StartPoint;
        _startLineEndPoint = EndPoint;

        InteractionStarted?.Invoke(this, EventArgs.Empty);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsLoose)
        {
            Cursor = null;
            base.OnMouseMove(e);
            return;
        }

        if (_interactionMode != InteractionMode.None && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateInteraction(e);
            e.Handled = true;
            return;
        }

        Cursor = GetCursor(GetInteractionMode(e.GetPosition(this)));
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsLoose)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        EndInteraction();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_interactionMode == InteractionMode.None)
        {
            Cursor = null;
        }

        base.OnMouseLeave(e);
    }

    private ContextMenu CreateContextMenu()
    {
        _looseStateItem.Click += (_, _) => LooseStateToggleRequested?.Invoke(this, EventArgs.Empty);

        var editItem = new MenuItem { Header = "Edit" };
        editItem.Click += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);

        var bringForwardItem = new MenuItem { Header = "Bring Forward" };
        bringForwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.BringForward));

        var sendBackwardItem = new MenuItem { Header = "Send Backward" };
        sendBackwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendBackward));

        var sendToBackItem = new MenuItem { Header = "Send to Back" };
        sendToBackItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendToBack));

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        var contextMenu = new ContextMenu();
        contextMenu.Opened += (_, _) => UpdateContextMenuItems();
        contextMenu.Items.Add(_looseStateItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(bringForwardItem);
        contextMenu.Items.Add(sendBackwardItem);
        contextMenu.Items.Add(sendToBackItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(editItem);
        contextMenu.Items.Add(deleteItem);
        return contextMenu;
    }

    private void UpdateContextMenuItems()
    {
        _looseStateItem.Header = IsLoose
            ? "Set Fixed"
            : "Set Loose";
    }

    private void UpdateInteraction(MouseEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        Point currentPoint = e.GetPosition(parentCanvas);
        Vector delta = currentPoint - _interactionStartPoint;

        switch (_interactionMode)
        {
            case InteractionMode.Drag:
                SetAbsoluteEndpoints(_startLineStartPoint + delta, _startLineEndPoint + delta);
                break;

            case InteractionMode.ResizeStart:
                SetAbsoluteEndpoints(currentPoint, EndPoint);
                break;

            case InteractionMode.ResizeEnd:
                SetAbsoluteEndpoints(StartPoint, currentPoint);
                break;
        }
    }

    private void EndInteraction()
    {
        if (_interactionMode == InteractionMode.None)
        {
            return;
        }

        bool changed =
            !AreClose(_startLineStartPoint.X, StartPoint.X) ||
            !AreClose(_startLineStartPoint.Y, StartPoint.Y) ||
            !AreClose(_startLineEndPoint.X, EndPoint.X) ||
            !AreClose(_startLineEndPoint.Y, EndPoint.Y);

        _interactionMode = InteractionMode.None;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (changed)
        {
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateBoundsAndGeometry()
    {
        double left = Math.Min(StartPoint.X, EndPoint.X) - LineCanvasPadding;
        double top = Math.Min(StartPoint.Y, EndPoint.Y) - LineCanvasPadding;
        double width = Math.Abs(EndPoint.X - StartPoint.X) + (LineCanvasPadding * 2);
        double height = Math.Abs(EndPoint.Y - StartPoint.Y) + (LineCanvasPadding * 2);

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _layout.Width = Width;
        _layout.Height = Height;
        _selectionBorder.Width = Width;
        _selectionBorder.Height = Height;

        Point localStart = new(StartPoint.X - left, StartPoint.Y - top);
        Point localEnd = new(EndPoint.X - left, EndPoint.Y - top);

        _line.X1 = localStart.X;
        _line.Y1 = localStart.Y;
        _line.X2 = localEnd.X;
        _line.Y2 = localEnd.Y;

        Canvas.SetLeft(_queryWarningBadge, Math.Max(0, Width - 16));
        Canvas.SetTop(_queryWarningBadge, 0);

        UpdateArrowHead(localStart, localEnd);
    }

    private static Border CreateQueryWarningBadge()
    {
        return new Border
        {
            Width = 16,
            Height = 16,
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

    private void UpdateArrowHead(Point localStart, Point localEnd)
    {
        Vector direction = localEnd - localStart;
        if (!HasEndArrow || direction.Length < 2)
        {
            _arrowHead.Visibility = Visibility.Collapsed;
            return;
        }

        direction.Normalize();
        Vector perpendicular = new(-direction.Y, direction.X);
        Point basePoint = localEnd - (direction * ArrowLength);

        _arrowHead.Points = new PointCollection
        {
            localEnd,
            basePoint + (perpendicular * (ArrowWidth / 2)),
            basePoint - (perpendicular * (ArrowWidth / 2))
        };
        _arrowHead.Visibility = Visibility.Visible;
    }

    private void ApplyLineStyle()
    {
        Brush brush = CreateBrush(ColorText);
        _line.Stroke = brush;
        _arrowHead.Fill = brush;
    }

    private InteractionMode GetInteractionMode(Point localPoint)
    {
        Point localStart = new(_line.X1, _line.Y1);
        Point localEnd = new(_line.X2, _line.Y2);

        if ((localPoint - localStart).Length <= EndpointHitDistance)
        {
            return InteractionMode.ResizeStart;
        }

        if ((localPoint - localEnd).Length <= EndpointHitDistance)
        {
            return InteractionMode.ResizeEnd;
        }

        return DistanceToSegment(localPoint, localStart, localEnd) <= HitDistance
            ? InteractionMode.Drag
            : InteractionMode.None;
    }

    private static Cursor? GetCursor(InteractionMode mode)
    {
        return mode switch
        {
            InteractionMode.ResizeStart or InteractionMode.ResizeEnd => Cursors.Cross,
            InteractionMode.Drag => Cursors.SizeAll,
            _ => null
        };
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        Vector segment = end - start;
        if (segment.LengthSquared == 0)
        {
            return (point - start).Length;
        }

        Vector pointOffset = point - start;
        double ratio = Math.Clamp(Vector.Multiply(pointOffset, segment) / segment.LengthSquared, 0, 1);
        Point projection = start + (segment * ratio);
        return (point - projection).Length;
    }

    private static Brush CreateBrush(string colorText)
    {
        if (string.Equals(colorText, DiagramShapeControl.TransparentColorText, StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.Transparent;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return Brushes.Black;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Black;
        }
    }

    private static string NormalizeColorText(string colorText, string fallbackColor)
    {
        if (string.Equals(colorText, DiagramShapeControl.TransparentColorText, StringComparison.OrdinalIgnoreCase))
        {
            return DiagramShapeControl.TransparentColorText;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return fallbackColor;
            }

            return color.A == 0
                ? DiagramShapeControl.TransparentColorText
                : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch (FormatException)
        {
            return fallbackColor;
        }
    }

    private static bool AreClose(double first, double second)
    {
        return Math.Abs(first - second) < 0.1;
    }
}

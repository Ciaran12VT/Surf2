using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Surf2.Models;

namespace Surf2.Controls;

public sealed class DiagramShapeControl : UserControl
{
    public const string TransparentColorText = "Transparent";

    private const double MinimumShapeWidth = 36;
    private const double MinimumShapeHeight = 28;
    private const double ResizeHitThickness = 8;

    private readonly Shape _shape;
    private readonly TextBlock _labelTextBlock;
    private readonly TextBox _labelTextBox;
    private readonly Border _selectionBorder;
    private readonly Border _queryWarningBadge;

    private InteractionMode _interactionMode;
    private ResizeHandle _activeResizeHandle;
    private Point _interactionStartPoint;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;
    private bool _isLabelEditing;
    private bool _isLocked;

    public DiagramShapeControl(
        DiagramShapeKind shapeKind,
        string outlineColorText,
        string backColorText,
        string? diagramObjectId = null)
    {
        DiagramObjectId = string.IsNullOrWhiteSpace(diagramObjectId)
            ? Guid.NewGuid().ToString("N")
            : diagramObjectId;
        ShapeKind = shapeKind;
        OutlineColorText = NormalizeColorText(outlineColorText, "#000000");
        BackColorText = NormalizeColorText(backColorText, "#FFFFFF");

        Width = 120;
        Height = 72;
        MinWidth = MinimumShapeWidth;
        MinHeight = MinimumShapeHeight;
        Focusable = true;
        Background = Brushes.Transparent;

        _shape = shapeKind == DiagramShapeKind.Rectangle
            ? new Rectangle { RadiusX = 2, RadiusY = 2 }
            : new Ellipse();
        _shape.Stretch = Stretch.Fill;
        _shape.StrokeThickness = 2;

        _labelTextBlock = new TextBlock
        {
            FontSize = DiagramTextStyle.DefaultFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8),
            IsHitTestVisible = false,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        _labelTextBox = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8),
            AcceptsReturn = false,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            FontSize = DiagramTextStyle.DefaultFontSize,
            Padding = new Thickness(2, 0, 2, 0),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _labelTextBox.LostKeyboardFocus += LabelTextBox_LostKeyboardFocus;
        _labelTextBox.KeyDown += LabelTextBox_KeyDown;

        _selectionBorder = new Border
        {
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _queryWarningBadge = CreateQueryWarningBadge();

        var layout = new Grid { Background = Brushes.Transparent };
        layout.Children.Add(_shape);
        layout.Children.Add(_labelTextBlock);
        layout.Children.Add(_labelTextBox);
        layout.Children.Add(_selectionBorder);
        layout.Children.Add(_queryWarningBadge);
        Content = layout;

        ContextMenu = CreateContextMenu();
        Loaded += (_, _) => RefreshTextContrast();
        ApplyColors();
    }

    private enum InteractionMode
    {
        None,
        Drag,
        Resize
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

    public event EventHandler? EditRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler<DiagramLayerChangeRequestedEventArgs>? LayerChangeRequested;

    public event EventHandler? Selected;

    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionCompleted;

    public event EventHandler<DiagramObjectLabelChangedEventArgs>? LabelChanged;

    public string DiagramObjectId { get; }

    public DiagramShapeKind ShapeKind { get; }

    public string LabelText { get; private set; } = string.Empty;

    public double LabelFontSize => _labelTextBlock.FontSize;

    public string OutlineColorText { get; private set; }

    public string BackColorText { get; private set; }

    public DiagramObjectMetadata Metadata { get; private set; } = new();

    public bool IsSelected
    {
        get => _selectionBorder.Visibility == Visibility.Visible;
        set => _selectionBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsLabelEditing => _isLabelEditing;

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            _isLocked = value;
            if (_isLocked)
            {
                EndInteraction();
                CommitLabelEdit();
                Cursor = null;
            }
        }
    }

    public void SetCanvasBounds(double left, double top, double width, double height)
    {
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Width = Math.Max(MinimumShapeWidth, width);
        Height = Math.Max(MinimumShapeHeight, height);
    }

    public void BeginEditLabel()
    {
        if (IsLocked)
        {
            return;
        }

        _isLabelEditing = true;
        _labelTextBox.Text = LabelText;
        _labelTextBlock.Visibility = Visibility.Collapsed;
        _labelTextBox.Visibility = Visibility.Visible;
        _labelTextBox.Focus();
        Keyboard.Focus(_labelTextBox);
        _labelTextBox.SelectAll();
    }

    public void ApplyDetails(string labelText, string outlineColorText, string backColorText, double? labelFontSize = null)
    {
        CommitLabelEdit(notifyChange: false);
        LabelText = labelText;
        _labelTextBlock.Text = LabelText;
        double fontSize = DiagramTextStyle.NormalizeFontSize(labelFontSize ?? LabelFontSize);
        _labelTextBlock.FontSize = fontSize;
        _labelTextBox.FontSize = fontSize;
        OutlineColorText = NormalizeColorText(outlineColorText, "#000000");
        BackColorText = NormalizeColorText(backColorText, "#FFFFFF");
        ApplyColors();
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

    public void RefreshTextContrast()
    {
        _labelTextBlock.Foreground = DiagramTextContrast.GetReadableTextBrush(BackColorText, this);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsLocked)
        {
            Focus();
            Selected?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (_isLabelEditing || Parent is not Canvas parentCanvas)
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        Focus();
        _activeResizeHandle = GetResizeHandle(e.GetPosition(this));
        _interactionMode = _activeResizeHandle == ResizeHandle.None
            ? InteractionMode.Drag
            : InteractionMode.Resize;

        _interactionStartPoint = e.GetPosition(parentCanvas);
        _startLeft = GetCanvasLeft();
        _startTop = GetCanvasTop();
        _startWidth = ActualWidth > 0 ? ActualWidth : Width;
        _startHeight = ActualHeight > 0 ? ActualHeight : Height;

        Selected?.Invoke(this, EventArgs.Empty);
        InteractionStarted?.Invoke(this, EventArgs.Empty);
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

        if (_interactionMode != InteractionMode.None && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateInteraction(e);
            e.Handled = true;
            return;
        }

        Cursor = GetCursor(GetResizeHandle(e.GetPosition(this)));
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
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
        contextMenu.Items.Add(editItem);
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

    private void UpdateInteraction(MouseEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        Point currentPoint = e.GetPosition(parentCanvas);
        Vector delta = currentPoint - _interactionStartPoint;

        if (_interactionMode == InteractionMode.Drag)
        {
            Canvas.SetLeft(this, _startLeft + delta.X);
            Canvas.SetTop(this, _startTop + delta.Y);
            return;
        }

        ResizeFromDelta(delta);
    }

    private void ResizeFromDelta(Vector delta)
    {
        double left = _startLeft;
        double top = _startTop;
        double width = _startWidth;
        double height = _startHeight;

        if (ResizesLeft(_activeResizeHandle))
        {
            double requestedLeft = _startLeft + delta.X;
            double maximumLeft = _startLeft + _startWidth - MinimumShapeWidth;
            left = Math.Min(requestedLeft, maximumLeft);
            width = _startWidth + (_startLeft - left);
        }
        else if (ResizesRight(_activeResizeHandle))
        {
            width = Math.Max(MinimumShapeWidth, _startWidth + delta.X);
        }

        if (ResizesTop(_activeResizeHandle))
        {
            double requestedTop = _startTop + delta.Y;
            double maximumTop = _startTop + _startHeight - MinimumShapeHeight;
            top = Math.Min(requestedTop, maximumTop);
            height = _startHeight + (_startTop - top);
        }
        else if (ResizesBottom(_activeResizeHandle))
        {
            height = Math.Max(MinimumShapeHeight, _startHeight + delta.Y);
        }

        SetCanvasBounds(left, top, width, height);
    }

    private void EndInteraction()
    {
        if (_interactionMode == InteractionMode.None)
        {
            return;
        }

        bool changed =
            !AreClose(_startLeft, GetCanvasLeft()) ||
            !AreClose(_startTop, GetCanvasTop()) ||
            !AreClose(_startWidth, ActualWidth > 0 ? ActualWidth : Width) ||
            !AreClose(_startHeight, ActualHeight > 0 ? ActualHeight : Height);

        _interactionMode = InteractionMode.None;
        _activeResizeHandle = ResizeHandle.None;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (changed)
        {
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private ResizeHandle GetResizeHandle(Point point)
    {
        bool left = point.X <= ResizeHitThickness;
        bool right = point.X >= ActualWidth - ResizeHitThickness;
        bool top = point.Y <= ResizeHitThickness;
        bool bottom = point.Y >= ActualHeight - ResizeHitThickness;

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

    private void LabelTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitLabelEdit();
    }

    private void LabelTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitLabelEdit();
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _labelTextBox.Text = LabelText;
            CommitLabelEdit();
            Focus();
            e.Handled = true;
        }
    }

    public void CommitLabelEdit(bool notifyChange = true)
    {
        if (!_isLabelEditing)
        {
            return;
        }

        string oldText = LabelText;
        LabelText = _labelTextBox.Text;
        _labelTextBlock.Text = LabelText;
        _labelTextBox.Visibility = Visibility.Collapsed;
        _labelTextBlock.Visibility = Visibility.Visible;
        _isLabelEditing = false;

        if (notifyChange && !string.Equals(oldText, LabelText, StringComparison.Ordinal))
        {
            LabelChanged?.Invoke(this, new DiagramObjectLabelChangedEventArgs(oldText, LabelText));
        }
    }

    private void ApplyColors()
    {
        _shape.Stroke = CreateBrush(OutlineColorText);
        _shape.Fill = CreateBrush(BackColorText);
        RefreshTextContrast();
    }

    private static Brush CreateBrush(string colorText)
    {
        if (IsTransparentColor(colorText))
        {
            return Brushes.Transparent;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return Brushes.Transparent;
            }

            if (color.A == 0)
            {
                return Brushes.Transparent;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    private static string NormalizeColorText(string colorText, string fallbackColor)
    {
        if (IsTransparentColor(colorText))
        {
            return TransparentColorText;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return fallbackColor;
            }

            return color.A == 0
                ? TransparentColorText
                : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch (FormatException)
        {
            return fallbackColor;
        }
    }

    private static bool IsTransparentColor(string colorText)
    {
        return string.Equals(colorText, TransparentColorText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreClose(double first, double second)
    {
        return Math.Abs(first - second) < 0.1;
    }

    private static bool ResizesLeft(ResizeHandle handle)
    {
        return handle is ResizeHandle.Left or ResizeHandle.TopLeft or ResizeHandle.BottomLeft;
    }

    private static bool ResizesRight(ResizeHandle handle)
    {
        return handle is ResizeHandle.Right or ResizeHandle.TopRight or ResizeHandle.BottomRight;
    }

    private static bool ResizesTop(ResizeHandle handle)
    {
        return handle is ResizeHandle.Top or ResizeHandle.TopLeft or ResizeHandle.TopRight;
    }

    private static bool ResizesBottom(ResizeHandle handle)
    {
        return handle is ResizeHandle.Bottom or ResizeHandle.BottomLeft or ResizeHandle.BottomRight;
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
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Surf2.Models;

namespace Surf2.Controls;

public sealed class DiagramLabelControl : UserControl
{
    private const double CanvasPadding = 12;
    private const double DefaultBoxWidth = 150;
    private const double DefaultBoxHeight = 36;
    private const double MinimumBoxWidth = 70;
    private const double MinimumBoxHeight = 28;
    private const double TextMeasureHorizontalPadding = 18;
    private const double TextMeasureVerticalPadding = 12;
    private const double ResizeHandleSize = 9;
    private const double ResizeHandleHitSize = 14;

    private readonly Canvas _layout;
    private readonly Line _connectorLine;
    private readonly Rectangle _boxRectangle;
    private readonly Rectangle _selectionRectangle;
    private readonly TextBlock _labelTextBlock;
    private readonly TextBox _labelTextBox;
    private readonly MenuItem _tetheredMenuItem;
    private readonly Border _queryWarningBadge;
    private readonly Rectangle _resizeHandle;

    private bool _isLabelEditing;
    private bool _isDragging;
    private bool _isResizing;
    private bool _isLocked;
    private Point _interactionStartPoint;
    private Point _startAnchorPoint;
    private Rect _startBoxRect;

    public DiagramLabelControl(
        string outlineColorText,
        string backColorText,
        string? diagramObjectId = null)
    {
        DiagramObjectId = string.IsNullOrWhiteSpace(diagramObjectId)
            ? Guid.NewGuid().ToString("N")
            : diagramObjectId;
        OutlineColorText = NormalizeColorText(outlineColorText, "#000000");
        BackColorText = NormalizeColorText(backColorText, "#FFFFFF");

        Focusable = true;
        Background = Brushes.Transparent;

        _layout = new Canvas { Background = Brushes.Transparent };
        _connectorLine = new Line
        {
            StrokeThickness = 1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _boxRectangle = new Rectangle
        {
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        _selectionRectangle = new Rectangle
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _labelTextBlock = new TextBlock
        {
            Foreground = Brushes.Black,
            IsHitTestVisible = false,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _labelTextBox = new TextBox
        {
            AcceptsReturn = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _labelTextBox.LostKeyboardFocus += LabelTextBox_LostKeyboardFocus;
        _labelTextBox.KeyDown += LabelTextBox_KeyDown;
        _labelTextBox.TextChanged += LabelTextBox_TextChanged;
        _queryWarningBadge = CreateQueryWarningBadge();
        _resizeHandle = new Rectangle
        {
            Width = ResizeHandleSize,
            Height = ResizeHandleSize,
            Fill = Brushes.White,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        _layout.Children.Add(_connectorLine);
        _layout.Children.Add(_boxRectangle);
        _layout.Children.Add(_selectionRectangle);
        _layout.Children.Add(_labelTextBlock);
        _layout.Children.Add(_labelTextBox);
        _layout.Children.Add(_queryWarningBadge);
        _layout.Children.Add(_resizeHandle);
        Content = _layout;

        var editItem = new MenuItem { Header = "Edit" };
        editItem.Click += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        var bringForwardItem = new MenuItem { Header = "Bring Forward" };
        bringForwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.BringForward));

        var sendBackwardItem = new MenuItem { Header = "Send Backward" };
        sendBackwardItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendBackward));

        var sendToBackItem = new MenuItem { Header = "Send to Back" };
        sendToBackItem.Click += (_, _) => LayerChangeRequested?.Invoke(this, new DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction.SendToBack));

        _tetheredMenuItem = new MenuItem
        {
            Header = "Tethered",
            IsCheckable = true,
            IsChecked = true
        };
        _tetheredMenuItem.Click += (_, _) =>
            TetherChangedRequested?.Invoke(this, new DiagramLabelTetherChangedEventArgs(_tetheredMenuItem.IsChecked));

        var contextMenu = new ContextMenu();
        contextMenu.Opened += (_, _) => _tetheredMenuItem.IsChecked = IsTethered;
        contextMenu.Items.Add(editItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(bringForwardItem);
        contextMenu.Items.Add(sendBackwardItem);
        contextMenu.Items.Add(sendToBackItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(deleteItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_tetheredMenuItem);
        ContextMenu = contextMenu;

        ApplyColors();
        SetGeometry(new Point(0, 0), new Rect(28, -DefaultBoxHeight / 2, DefaultBoxWidth, DefaultBoxHeight), isTethered: true);
    }

    public event EventHandler? Selected;

    public event EventHandler? InteractionStarted;

    public event EventHandler? InteractionCompleted;

    public event EventHandler<DiagramObjectLabelChangedEventArgs>? LabelChanged;

    public event EventHandler? EditRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler<DiagramLayerChangeRequestedEventArgs>? LayerChangeRequested;

    public event EventHandler<DiagramLabelTetherChangedEventArgs>? TetherChangedRequested;

    public string DiagramObjectId { get; }

    public string LabelText { get; private set; } = string.Empty;

    public string OutlineColorText { get; private set; }

    public string BackColorText { get; private set; }

    public bool IsTethered { get; private set; } = true;

    public Point AnchorPoint { get; private set; }

    public Rect BoxRect { get; private set; }

    public DiagramObjectMetadata Metadata { get; private set; } = new();

    public bool IsSelected
    {
        get => _selectionRectangle.Visibility == Visibility.Visible;
        set
        {
            _selectionRectangle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateResizeHandleVisibility();
        }
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
                CommitLabelEdit();
                EndDrag();
                EndResize();
                Cursor = null;
            }

            UpdateResizeHandleVisibility();
        }
    }

    public void PlaceAt(Point anchorPoint)
    {
        SetGeometry(
            anchorPoint,
            new Rect(anchorPoint.X + 28, anchorPoint.Y - DefaultBoxHeight / 2, DefaultBoxWidth, DefaultBoxHeight),
            isTethered: true);
    }

    public void SetGeometry(Point anchorPoint, Rect boxRect, bool isTethered)
    {
        AnchorPoint = anchorPoint;
        BoxRect = new Rect(
            boxRect.X,
            boxRect.Y,
            Math.Max(MinimumBoxWidth, boxRect.Width),
            Math.Max(MinimumBoxHeight, boxRect.Height));
        IsTethered = isTethered;
        UpdateBoundsAndGeometry();
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
        UpdateResizeHandleVisibility();
        ResizeBoxToText(_labelTextBox.Text);
        _labelTextBox.Focus();
        Keyboard.Focus(_labelTextBox);
        _labelTextBox.SelectAll();
    }

    public void ApplyDetails(
        string labelText,
        string outlineColorText,
        string backColorText,
        bool resizeToText = true)
    {
        CommitLabelEdit(notifyChange: false);
        LabelText = labelText;
        _labelTextBlock.Text = LabelText;
        OutlineColorText = NormalizeColorText(outlineColorText, "#000000");
        BackColorText = NormalizeColorText(backColorText, "#FFFFFF");
        ApplyColors();
        if (resizeToText)
        {
            ResizeBoxToText(LabelText);
        }
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

    public void SetTethered(bool isTethered)
    {
        IsTethered = isTethered;
        UpdateDashStyle();
        UpdateResizeHandleVisibility();
    }

    public void RetetherAtCurrentAnchor()
    {
        SetTethered(true);
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

        Point localPoint = e.GetPosition(this);
        bool isResizeHandle = IsPointInResizeHandle(localPoint);
        if (!isResizeHandle && !IsPointInLocalBox(localPoint))
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        Focus();
        Selected?.Invoke(this, EventArgs.Empty);

        _interactionStartPoint = e.GetPosition(parentCanvas);
        _startAnchorPoint = AnchorPoint;
        _startBoxRect = BoxRect;

        if (isResizeHandle)
        {
            _isResizing = true;
            Cursor = Cursors.SizeNWSE;
            InteractionStarted?.Invoke(this, EventArgs.Empty);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        _isDragging = true;

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

        if (_isResizing && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateResize(e);
            e.Handled = true;
            return;
        }

        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDrag(e);
            e.Handled = true;
            return;
        }

        Point localPoint = e.GetPosition(this);
        Cursor = IsPointInResizeHandle(localPoint)
            ? Cursors.SizeNWSE
            : IsPointInLocalBox(localPoint) ? Cursors.SizeAll : null;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_isResizing)
        {
            EndResize();
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            EndDrag();
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!_isDragging && !_isResizing)
        {
            Cursor = null;
        }

        base.OnMouseLeave(e);
    }

    private void UpdateDrag(MouseEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        Vector delta = e.GetPosition(parentCanvas) - _interactionStartPoint;
        Point anchorPoint = IsTethered ? _startAnchorPoint : _startAnchorPoint + delta;
        Rect boxRect = new(_startBoxRect.X + delta.X, _startBoxRect.Y + delta.Y, _startBoxRect.Width, _startBoxRect.Height);
        SetGeometry(anchorPoint, boxRect, IsTethered);
    }

    private void UpdateResize(MouseEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        Vector delta = e.GetPosition(parentCanvas) - _interactionStartPoint;
        double width = Math.Max(MinimumBoxWidth, _startBoxRect.Width + delta.X);
        double height = Math.Max(MinimumBoxHeight, _startBoxRect.Height + delta.Y);
        SetGeometry(
            _startAnchorPoint,
            new Rect(_startBoxRect.X, _startBoxRect.Y, width, height),
            IsTethered);
    }

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        bool changed =
            !AreClose(_startAnchorPoint.X, AnchorPoint.X) ||
            !AreClose(_startAnchorPoint.Y, AnchorPoint.Y) ||
            !AreClose(_startBoxRect.X, BoxRect.X) ||
            !AreClose(_startBoxRect.Y, BoxRect.Y);

        _isDragging = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (changed)
        {
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EndResize()
    {
        if (!_isResizing)
        {
            return;
        }

        bool changed =
            !AreClose(_startBoxRect.Width, BoxRect.Width) ||
            !AreClose(_startBoxRect.Height, BoxRect.Height);

        _isResizing = false;

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
        Point attachPoint = GetAttachPoint();

        double left = Math.Min(AnchorPoint.X, Math.Min(BoxRect.Left, attachPoint.X)) - CanvasPadding;
        double top = Math.Min(AnchorPoint.Y, Math.Min(BoxRect.Top, attachPoint.Y)) - CanvasPadding;
        double right = Math.Max(AnchorPoint.X, Math.Max(BoxRect.Right, attachPoint.X)) + CanvasPadding;
        double bottom = Math.Max(AnchorPoint.Y, Math.Max(BoxRect.Bottom, attachPoint.Y)) + CanvasPadding;

        Width = Math.Max(1, right - left);
        Height = Math.Max(1, bottom - top);
        _layout.Width = Width;
        _layout.Height = Height;
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);

        Point localAnchor = new(AnchorPoint.X - left, AnchorPoint.Y - top);
        Point localAttach = new(attachPoint.X - left, attachPoint.Y - top);
        Rect localBox = new(BoxRect.X - left, BoxRect.Y - top, BoxRect.Width, BoxRect.Height);

        _connectorLine.X1 = localAnchor.X;
        _connectorLine.Y1 = localAnchor.Y;
        _connectorLine.X2 = localAttach.X;
        _connectorLine.Y2 = localAttach.Y;

        Canvas.SetLeft(_boxRectangle, localBox.X);
        Canvas.SetTop(_boxRectangle, localBox.Y);
        _boxRectangle.Width = localBox.Width;
        _boxRectangle.Height = localBox.Height;

        Canvas.SetLeft(_selectionRectangle, localBox.X - 2);
        Canvas.SetTop(_selectionRectangle, localBox.Y - 2);
        _selectionRectangle.Width = localBox.Width + 4;
        _selectionRectangle.Height = localBox.Height + 4;

        Canvas.SetLeft(_labelTextBlock, localBox.X + 6);
        Canvas.SetTop(_labelTextBlock, localBox.Y + 4);
        _labelTextBlock.Width = Math.Max(1, localBox.Width - 12);
        _labelTextBlock.Height = Math.Max(1, localBox.Height - 8);

        Canvas.SetLeft(_labelTextBox, localBox.X + 4);
        Canvas.SetTop(_labelTextBox, localBox.Y + 3);
        _labelTextBox.Width = Math.Max(1, localBox.Width - 8);
        _labelTextBox.Height = Math.Max(1, localBox.Height - 6);

        Canvas.SetLeft(_queryWarningBadge, localBox.Right - 8);
        Canvas.SetTop(_queryWarningBadge, localBox.Top - 8);

        Canvas.SetLeft(_resizeHandle, localBox.Right - (ResizeHandleSize / 2));
        Canvas.SetTop(_resizeHandle, localBox.Bottom - (ResizeHandleSize / 2));

        UpdateDashStyle();
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

    private Point GetAttachPoint()
    {
        Point[] corners =
        [
            new(BoxRect.Left, BoxRect.Top),
            new(BoxRect.Right, BoxRect.Top),
            new(BoxRect.Left, BoxRect.Bottom),
            new(BoxRect.Right, BoxRect.Bottom)
        ];

        Point bestCorner = corners[0];
        double bestDistance = (bestCorner - AnchorPoint).LengthSquared;
        foreach (Point corner in corners.Skip(1))
        {
            double distance = (corner - AnchorPoint).LengthSquared;
            if (distance < bestDistance)
            {
                bestCorner = corner;
                bestDistance = distance;
            }
        }

        return bestCorner;
    }

    private bool IsPointInLocalBox(Point localPoint)
    {
        Rect localBox = GetLocalBoxRect();
        return localBox.Contains(localPoint);
    }

    private bool IsPointInResizeHandle(Point localPoint)
    {
        if (IsLocked || IsTethered || _isLabelEditing)
        {
            return false;
        }

        Rect localBox = GetLocalBoxRect();
        var handleHitBox = new Rect(
            localBox.Right - ResizeHandleHitSize,
            localBox.Bottom - ResizeHandleHitSize,
            ResizeHandleHitSize + (ResizeHandleSize / 2),
            ResizeHandleHitSize + (ResizeHandleSize / 2));

        return handleHitBox.Contains(localPoint);
    }

    private Rect GetLocalBoxRect()
    {
        double left = Canvas.GetLeft(_boxRectangle);
        double top = Canvas.GetTop(_boxRectangle);
        if (double.IsNaN(left))
        {
            left = 0;
        }

        if (double.IsNaN(top))
        {
            top = 0;
        }

        return new Rect(left, top, _boxRectangle.Width, _boxRectangle.Height);
    }

    private void ApplyColors()
    {
        Brush outlineBrush = CreateBrush(OutlineColorText, Brushes.Black);
        Brush backBrush = CreateBrush(BackColorText, Brushes.White);
        _connectorLine.Stroke = outlineBrush;
        _boxRectangle.Stroke = outlineBrush;
        _boxRectangle.Fill = backBrush;
    }

    private void UpdateDashStyle()
    {
        DoubleCollection? dashArray = IsTethered ? null : new DoubleCollection { 3, 3 };
        _connectorLine.StrokeDashArray = dashArray;
        _boxRectangle.StrokeDashArray = dashArray;
        UpdateResizeHandleVisibility();
    }

    private void UpdateResizeHandleVisibility()
    {
        _resizeHandle.Visibility = !IsLocked && !IsTethered && IsSelected && !_isLabelEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private void LabelTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLabelEditing)
        {
            ResizeBoxToText(_labelTextBox.Text);
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
        ResizeBoxToText(LabelText);
        _labelTextBox.Visibility = Visibility.Collapsed;
        _labelTextBlock.Visibility = Visibility.Visible;
        _isLabelEditing = false;
        UpdateResizeHandleVisibility();

        if (notifyChange && !string.Equals(oldText, LabelText, StringComparison.Ordinal))
        {
            LabelChanged?.Invoke(this, new DiagramObjectLabelChangedEventArgs(oldText, LabelText));
        }
    }

    private void ResizeBoxToText(string text)
    {
        Size textSize = MeasureLabelText(text);
        double width = Math.Max(MinimumBoxWidth, Math.Ceiling(textSize.Width + TextMeasureHorizontalPadding));
        double height = Math.Max(MinimumBoxHeight, Math.Ceiling(textSize.Height + TextMeasureVerticalPadding));

        if (AreClose(BoxRect.Width, width) && AreClose(BoxRect.Height, height))
        {
            return;
        }

        SetGeometry(AnchorPoint, new Rect(BoxRect.X, BoxRect.Y, width, height), IsTethered);
    }

    private Size MeasureLabelText(string text)
    {
        string measureText = string.IsNullOrEmpty(text) ? " " : text;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formattedText = new FormattedText(
            measureText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                _labelTextBlock.FontFamily,
                _labelTextBlock.FontStyle,
                _labelTextBlock.FontWeight,
                _labelTextBlock.FontStretch),
            _labelTextBlock.FontSize,
            Brushes.Black,
            pixelsPerDip);

        return new Size(
            formattedText.WidthIncludingTrailingWhitespace,
            formattedText.Height);
    }

    private static Brush CreateBrush(string colorText, Brush fallback)
    {
        if (string.Equals(colorText, DiagramShapeControl.TransparentColorText, StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.Transparent;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorText) is not Color color)
            {
                return fallback;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return fallback;
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

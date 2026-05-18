using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using Surf2.Models;

namespace Surf2.Controls;

public partial class FloatingCodeWindow : UserControl
{
    private const double MinimumFontSize = 8;
    private const double MaximumFontSize = 36;
    private const double FontZoomStep = 1.1;

    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartLeft;
    private double _dragStartTop;

    public FloatingCodeWindow(OpenDocumentState state, string content, IHighlightingDefinition? highlighting)
    {
        InitializeComponent();

        State = state;
        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        TitleText.Text = Path.GetFileName(state.FilePath);
        ToolTip = state.FilePath;

        double initialFontSize = state.FontSize > 0 ? state.FontSize : 13;
        Editor.Text = content;
        Editor.SyntaxHighlighting = highlighting;
        Editor.FontSize = Math.Clamp(initialFontSize, MinimumFontSize, MaximumFontSize);
        State.FontSize = Editor.FontSize;

        PreviewMouseWheel += FloatingCodeWindow_PreviewMouseWheel;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? BoundsChanged;

    public event EventHandler? BringToFrontRequested;

    public OpenDocumentState State { get; }

    public void ApplyCodeBackcolor(Brush backcolor)
    {
        Editor.Background = backcolor;
        Editor.TextArea.Background = backcolor;
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas parentCanvas)
        {
            return;
        }

        BringToFrontRequested?.Invoke(this, EventArgs.Empty);

        if (e.ClickCount == 2)
        {
            ExpandToContent();
            e.Handled = true;
            return;
        }

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

    private void FloatingCodeWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        double multiplier = e.Delta > 0 ? FontZoomStep : 1 / FontZoomStep;
        Editor.FontSize = Math.Clamp(Editor.FontSize * multiplier, MinimumFontSize, MaximumFontSize);
        State.FontSize = Editor.FontSize;
        e.Handled = true;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExpandToContent()
    {
        string[] lines = Editor.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        if (lines.Length == 0)
        {
            lines = [string.Empty];
        }

        double maxLineWidth = lines
            .Select(MeasureLineWidth)
            .DefaultIfEmpty(0)
            .Max();

        double lineHeight = Math.Max(Editor.FontSize * 1.5, Editor.TextArea.TextView.DefaultLineHeight);
        double lineCount = Math.Max(1, Editor.Document?.LineCount ?? lines.Length);

        Width = Math.Max(MinWidth, Math.Ceiling(maxLineWidth + 120));
        Height = Math.Max(MinHeight, Math.Ceiling((lineCount * lineHeight) + 70));

        State.Width = Width;
        State.Height = Height;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private double MeasureLineWidth(string line)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(Editor.FontFamily, Editor.FontStyle, Editor.FontWeight, Editor.FontStretch);
        var formattedText = new FormattedText(
            string.IsNullOrEmpty(line) ? " " : line,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Editor.FontSize,
            Brushes.Black,
            dpi.PixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FloatingCodeWindow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        BringToFrontRequested?.Invoke(this, EventArgs.Empty);
    }
}

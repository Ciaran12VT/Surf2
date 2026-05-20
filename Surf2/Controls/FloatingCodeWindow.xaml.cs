using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
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
    private ReferenceHighlightColorizer? _referenceHighlightColorizer;
    private bool _suppressCursorPositionChanged;

    public FloatingCodeWindow(OpenDocumentState state, string content, IHighlightingDefinition? highlighting)
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
        Editor.Text = content;
        Editor.SyntaxHighlighting = highlighting;
        Editor.FontSize = Math.Clamp(initialFontSize, MinimumFontSize, MaximumFontSize);
        State.FontSize = Editor.FontSize;

        Editor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
        Editor.PreviewMouseLeftButtonUp += Editor_PreviewMouseLeftButtonUp;
        Editor.PreviewMouseDoubleClick += Editor_PreviewMouseDoubleClick;
        PreviewMouseWheel += FloatingCodeWindow_PreviewMouseWheel;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? BoundsChanged;

    public event EventHandler? BringToFrontRequested;

    public event EventHandler<ReferenceNavigationRequestedEventArgs>? ReferenceNavigationRequested;

    public event EventHandler<ReferenceNavigationRequestedEventArgs>? ReferencePreviewRequested;

    public event EventHandler<CursorPositionChangedEventArgs>? CursorPositionChanged;

    public event EventHandler<CodeWindowContextMenuOpeningEventArgs>? ContextMenuOpeningRequested;

    public OpenDocumentState State { get; }

    public string Text => Editor.Text;

    public bool TryGetSelectedTextOrReferenceToken(out string token, out int startOffset)
    {
        token = string.Empty;
        startOffset = 0;

        if (Editor.Document == null || Editor.Document.TextLength == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Editor.SelectedText))
        {
            token = Editor.SelectedText.Trim();
            startOffset = Math.Clamp(Editor.SelectionStart, 0, Editor.Document.TextLength - 1);
            return true;
        }

        int caretOffset = Math.Clamp(Editor.CaretOffset, 0, Editor.Document.TextLength - 1);
        startOffset = caretOffset;
        return TryGetReferenceTokenAtOffset(caretOffset, out token, out _);
    }

    public void ApplyCodeBackcolor(Brush backcolor)
    {
        Editor.Background = backcolor;
        Editor.TextArea.Background = backcolor;
    }

    public void ApplySyntaxHighlighting(IHighlightingDefinition? highlighting)
    {
        Editor.SyntaxHighlighting = highlighting;
    }

    public void ApplyReferenceHighlights(IReadOnlyDictionary<string, ReferenceHighlightStyleSetting> highlightStyles)
    {
        if (_referenceHighlightColorizer != null)
        {
            Editor.TextArea.TextView.LineTransformers.Remove(_referenceHighlightColorizer);
            _referenceHighlightColorizer = null;
        }

        if (highlightStyles.Count == 0)
        {
            Editor.TextArea.TextView.Redraw();
            return;
        }

        _referenceHighlightColorizer = new ReferenceHighlightColorizer(highlightStyles);
        Editor.TextArea.TextView.LineTransformers.Add(_referenceHighlightColorizer);
        Editor.TextArea.TextView.Redraw();
    }

    public void ScrollToLine(int lineNumber)
    {
        ScrollToPosition(lineNumber, 1, selectLine: true, suppressCursorPositionChanged: false);
    }

    public void ScrollToPosition(
        int lineNumber,
        int columnNumber,
        bool selectLine = false,
        bool suppressCursorPositionChanged = false)
    {
        if (Editor.Document == null)
        {
            return;
        }

        if (!IsLoaded)
        {
            RoutedEventHandler? loadedHandler = null;
            loadedHandler = (_, _) =>
            {
                Loaded -= loadedHandler;
                ScrollToPosition(lineNumber, columnNumber, selectLine, suppressCursorPositionChanged);
            };
            Loaded += loadedHandler;
            return;
        }

        if (suppressCursorPositionChanged)
        {
            _suppressCursorPositionChanged = true;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyScrollToPosition(lineNumber, columnNumber, selectLine);
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyScrollToPosition(lineNumber, columnNumber, selectLine);
                _suppressCursorPositionChanged = false;
            }), DispatcherPriority.ApplicationIdle);
        }), DispatcherPriority.ContextIdle);
    }

    public void FitToLineRange(int startLineNumber, int endLineNumber)
    {
        if (Editor.Document == null)
        {
            return;
        }

        int startLine = Math.Clamp(startLineNumber, 1, Math.Max(1, Editor.Document.LineCount));
        int endLine = Math.Clamp(Math.Max(startLineNumber, endLineNumber), startLine, Math.Max(1, Editor.Document.LineCount));

        List<string> lines = [];
        for (int lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            ICSharpCode.AvalonEdit.Document.DocumentLine line = Editor.Document.GetLineByNumber(lineNumber);
            lines.Add(Editor.Document.GetText(line.Offset, line.Length));
        }

        double maxLineWidth = lines
            .Select(MeasureLineWidth)
            .DefaultIfEmpty(0)
            .Max();

        double lineHeight = Math.Max(Editor.FontSize * 1.5, Editor.TextArea.TextView.DefaultLineHeight);
        double lineCount = Math.Max(1, endLine - startLine + 1);

        Width = Math.Max(MinWidth, Math.Ceiling(maxLineWidth + 120));
        Height = Math.Max(MinHeight, Math.Ceiling((lineCount * lineHeight) + 76));

        State.Width = Width;
        State.Height = Height;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Point GetDocumentPositionInWindow(int lineNumber, int columnNumber)
    {
        if (Editor.Document == null)
        {
            return new Point(0, 0);
        }

        int targetLine = Math.Clamp(lineNumber, 1, Math.Max(1, Editor.Document.LineCount));
        ICSharpCode.AvalonEdit.Document.DocumentLine line = Editor.Document.GetLineByNumber(targetLine);
        int targetColumn = Math.Clamp(columnNumber, 1, line.Length + 1);

        UpdateLayout();
        Editor.UpdateLayout();
        Editor.TextArea.TextView.EnsureVisualLines();

        Point documentPosition;
        try
        {
            documentPosition = Editor.TextArea.TextView.GetVisualPosition(
                new TextViewPosition(targetLine, targetColumn),
                VisualYPosition.LineTop);
        }
        catch (InvalidOperationException)
        {
            double lineHeight = Math.Max(Editor.FontSize * 1.5, Editor.TextArea.TextView.DefaultLineHeight);
            documentPosition = new Point(0, (targetLine - 1) * lineHeight);
        }
        catch (ArgumentOutOfRangeException)
        {
            double lineHeight = Math.Max(Editor.FontSize * 1.5, Editor.TextArea.TextView.DefaultLineHeight);
            documentPosition = new Point(0, (targetLine - 1) * lineHeight);
        }

        var visibleTextPosition = new Point(
            Math.Max(0, documentPosition.X - Editor.HorizontalOffset),
            Math.Max(0, documentPosition.Y - Editor.VerticalOffset));

        try
        {
            return Editor.TextArea.TextView
                .TransformToAncestor(this)
                .Transform(visibleTextPosition);
        }
        catch (InvalidOperationException)
        {
            Point editorPosition = Editor.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Point(editorPosition.X + visibleTextPosition.X, editorPosition.Y + visibleTextPosition.Y);
        }
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

    private void Editor_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (!TryCreateReferenceRequestFromMouseEvent(e, out ReferenceNavigationRequestedEventArgs? request) ||
            request == null)
        {
            return;
        }

        ReferenceNavigationRequested?.Invoke(this, request);
        e.Handled = true;
    }

    private void Editor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1)
        {
            return;
        }

        if (!TryCreateReferenceRequestFromMouseEvent(e, out ReferenceNavigationRequestedEventArgs? request) ||
            request == null)
        {
            return;
        }

        ReferencePreviewRequested?.Invoke(this, request);
    }

    private bool TryCreateReferenceRequestFromMouseEvent(
        MouseButtonEventArgs e,
        out ReferenceNavigationRequestedEventArgs? request)
    {
        request = null;

        if (Editor.Document == null)
        {
            return false;
        }

        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position == null)
        {
            return false;
        }

        int offset;
        try
        {
            offset = Editor.Document.GetOffset(position.Value.Location);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (!TryGetReferenceTokenAtOffset(offset, out string token, out int? argumentCount))
        {
            return false;
        }

        ICSharpCode.AvalonEdit.Document.TextLocation location = Editor.Document.GetLocation(offset);
        request = new ReferenceNavigationRequestedEventArgs(token, location.Line, location.Column, argumentCount);
        return true;
    }

    private void Caret_PositionChanged(object? sender, EventArgs e)
    {
        if (_suppressCursorPositionChanged)
        {
            return;
        }

        var location = Editor.TextArea.Caret.Location;
        CursorPositionChanged?.Invoke(
            this,
            new CursorPositionChangedEventArgs(State.FilePath, location.Line, location.Column));
    }

    private void ApplyScrollToPosition(int lineNumber, int columnNumber, bool selectLine)
    {
        if (Editor.Document == null)
        {
            return;
        }

        int targetLine = Math.Clamp(lineNumber, 1, Math.Max(1, Editor.Document.LineCount));
        ICSharpCode.AvalonEdit.Document.DocumentLine line = Editor.Document.GetLineByNumber(targetLine);
        int targetColumn = Math.Clamp(columnNumber, 1, line.Length + 1);
        int targetOffset = line.Offset + targetColumn - 1;

        UpdateLayout();
        Editor.UpdateLayout();
        Editor.TextArea.Caret.Offset = targetOffset;

        if (selectLine)
        {
            Editor.Select(line.Offset, line.Length);
        }
        else
        {
            Editor.Select(targetOffset, 0);
        }

        Editor.TextArea.TextView.EnsureVisualLines();
        Editor.ScrollTo(targetLine, targetColumn, VisualYPosition.LineTop, 8, 0);
        Editor.Select(targetOffset, 0);
        Editor.Focus();
    }

    private bool TryGetReferenceTokenAtOffset(int offset, out string token, out int? argumentCount)
    {
        token = string.Empty;
        argumentCount = null;

        string text = Editor.Document.Text;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        offset = Math.Clamp(offset, 0, text.Length - 1);

        if (!IsReferenceTokenCharacter(text[offset]) && offset > 0 && IsReferenceTokenCharacter(text[offset - 1]))
        {
            offset--;
        }

        if (!IsReferenceTokenCharacter(text[offset]))
        {
            return false;
        }

        int start = offset;
        while (start > 0 && IsReferenceTokenCharacter(text[start - 1]))
        {
            start--;
        }

        int end = offset;
        while (end < text.Length - 1 && IsReferenceTokenCharacter(text[end + 1]))
        {
            end++;
        }

        token = text[start..(end + 1)].Trim('.', '[', ']', '`', '"', '\'');
        argumentCount = TryGetInvocationArgumentCount(text, end + 1);
        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool IsReferenceTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
               value is '_' or '$' or '#' or '@' or '.' or '[' or ']' or '`';
    }

    private static int? TryGetInvocationArgumentCount(string text, int index)
    {
        index = SkipWhitespace(text, index);
        int afterGenericArguments = SkipOptionalGenericArguments(text, index);
        index = SkipWhitespace(text, afterGenericArguments);

        if (index >= text.Length || text[index] != '(')
        {
            return null;
        }

        int commaCount = 0;
        bool sawArgumentContent = false;
        int parenthesisDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;

        for (int i = index + 1; i < text.Length; i++)
        {
            char value = text[i];

            if (value is '"' or '\'')
            {
                i = SkipQuotedLiteral(text, i);
                sawArgumentContent = true;
                continue;
            }

            if (value == '(')
            {
                parenthesisDepth++;
                sawArgumentContent = true;
                continue;
            }

            if (value == ')')
            {
                if (parenthesisDepth == 0)
                {
                    return sawArgumentContent ? commaCount + 1 : 0;
                }

                parenthesisDepth--;
                continue;
            }

            if (value == '[')
            {
                bracketDepth++;
                sawArgumentContent = true;
                continue;
            }

            if (value == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (value == '{')
            {
                braceDepth++;
                sawArgumentContent = true;
                continue;
            }

            if (value == '}' && braceDepth > 0)
            {
                braceDepth--;
                continue;
            }

            if (value == ',' && parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                commaCount++;
                continue;
            }

            if (!char.IsWhiteSpace(value))
            {
                sawArgumentContent = true;
            }
        }

        return null;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipOptionalGenericArguments(string text, int index)
    {
        if (index >= text.Length || text[index] != '<')
        {
            return index;
        }

        int originalIndex = index;
        int depth = 0;

        for (int i = index; i < text.Length; i++)
        {
            char value = text[i];

            if (value is '"' or '\'')
            {
                i = SkipQuotedLiteral(text, i);
                continue;
            }

            if (value == '<')
            {
                depth++;
                continue;
            }

            if (value == '>')
            {
                depth--;
                if (depth == 0)
                {
                    int afterGenericArguments = SkipWhitespace(text, i + 1);
                    return afterGenericArguments < text.Length && text[afterGenericArguments] == '('
                        ? afterGenericArguments
                        : originalIndex;
                }
            }
        }

        return originalIndex;
    }

    private static int SkipQuotedLiteral(string text, int quoteStart)
    {
        char quote = text[quoteStart];

        for (int i = quoteStart + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote)
            {
                return i;
            }
        }

        return text.Length - 1;
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

        ContextMenu? contextMenu = TryGetOpeningContextMenu(e.OriginalSource) ?? ContextMenu;
        if (contextMenu != null)
        {
            ContextMenuOpeningRequested?.Invoke(this, new CodeWindowContextMenuOpeningEventArgs(contextMenu));
        }
    }

    private ContextMenu? TryGetOpeningContextMenu(object originalSource)
    {
        DependencyObject? current = originalSource as DependencyObject;
        while (current != null)
        {
            if (current is FrameworkElement { ContextMenu: not null } frameworkElement)
            {
                return frameworkElement.ContextMenu;
            }

            DependencyObject? parent = null;
            try
            {
                parent = VisualTreeHelper.GetParent(current);
            }
            catch (InvalidOperationException)
            {
            }

            current = parent ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}

public sealed class CodeWindowContextMenuOpeningEventArgs(ContextMenu contextMenu) : EventArgs
{
    public ContextMenu ContextMenu { get; } = contextMenu;
}

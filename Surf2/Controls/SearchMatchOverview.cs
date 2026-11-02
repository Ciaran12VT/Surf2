using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace Surf2.Controls;

/// <summary>Draws search locations over the editor's scrollbar without changing its template or hit testing.</summary>
public sealed class SearchMatchOverview : FrameworkElement
{
    private static readonly Brush MatchBrush = CreateBrush(Color.FromRgb(0xD9, 0x91, 0x18));
    private static readonly Brush ActiveMatchBrush = CreateBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private readonly List<(int StartLine, int EndLine)> _lineRanges = new();
    private readonly List<Rect> _markerRectangles = new();
    private TextEditor? _editor;
    private IReadOnlyList<CodeSearchMatch> _matches = Array.Empty<CodeSearchMatch>();
    private int _activeMatchIndex = -1;
    private bool _searchVisible;
    private bool _geometryInvalid = true;
    private Rect _lastTrackBounds;
    private double _lastDocumentHeight;

    public int MatchCount => _searchVisible ? _matches.Count : 0;
    public int ActiveMatchNumber => _searchVisible ? _activeMatchIndex + 1 : 0;
    public int RenderedMarkerCount => _searchVisible ? _markerRectangles.Count : 0;

    internal void SetMatches(TextEditor editor, IReadOnlyList<CodeSearchMatch> matches, int activeMatchIndex, bool visible)
    {
        bool changed = !ReferenceEquals(_editor, editor) || !ReferenceEquals(_matches, matches) || _searchVisible != visible;
        _editor = editor;
        _matches = matches;
        _searchVisible = visible;
        if (changed)
        {
            _lineRanges.Clear();
            foreach (CodeSearchMatch match in matches)
            {
                if (_lineRanges.Count > 0 && match.StartLine <= _lineRanges[^1].EndLine + 1)
                {
                    var previous = _lineRanges[^1];
                    _lineRanges[^1] = (previous.StartLine, Math.Max(previous.EndLine, match.EndLine));
                }
                else
                {
                    _lineRanges.Add((match.StartLine, match.EndLine));
                }
            }

            _geometryInvalid = true;
        }

        if (_activeMatchIndex != activeMatchIndex || changed)
        {
            _activeMatchIndex = activeMatchIndex;
            InvalidateVisual();
        }
    }

    internal void SetActiveMatch(int index)
    {
        if (_activeMatchIndex != index)
        {
            _activeMatchIndex = index;
            InvalidateVisual();
        }
    }

    internal void InvalidateDocumentGeometry()
    {
        _geometryInvalid = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!_searchVisible || _matches.Count == 0 || _editor?.Document == null || !_editor.IsLoaded)
        {
            _markerRectangles.Clear();
            _geometryInvalid = true;
            return;
        }

        ScrollBar? scrollBar = FindDescendant<ScrollBar>(_editor, bar => bar.Orientation == Orientation.Vertical && bar.IsVisible);
        if (scrollBar == null || scrollBar.ActualHeight <= 0)
        {
            _markerRectangles.Clear();
            _geometryInvalid = true;
            return;
        }

        // Track bounds also respect scrollbar themes which include arrow buttons at either end.
        FrameworkElement track = FindDescendant<Track>(scrollBar, item => item.Orientation == Orientation.Vertical) ?? (FrameworkElement)scrollBar;
        Rect trackBounds = track.TransformToVisual(this).TransformBounds(new Rect(track.RenderSize));
        if (trackBounds.Height <= 0 || trackBounds.Width <= 0)
        {
            return;
        }

        double documentHeight = Math.Max(1, _editor.TextArea.TextView.DocumentHeight);
        if (_geometryInvalid || trackBounds != _lastTrackBounds || Math.Abs(documentHeight - _lastDocumentHeight) > 0.1)
        {
            _markerRectangles.Clear();
            foreach (var range in _lineRanges)
            {
                Rect marker = GetMarkerRect(range.StartLine, range.EndLine, trackBounds, documentHeight, false);
                if (_markerRectangles.Count > 0 && marker.Top <= _markerRectangles[^1].Bottom)
                {
                    Rect previous = _markerRectangles[^1];
                    previous.Union(marker);
                    _markerRectangles[^1] = previous;
                }
                else
                {
                    _markerRectangles.Add(marker);
                }
            }

            _lastTrackBounds = trackBounds;
            _lastDocumentHeight = documentHeight;
            _geometryInvalid = false;
        }

        foreach (Rect marker in _markerRectangles)
        {
            drawingContext.DrawRectangle(MatchBrush, null, marker);
        }

        if (_activeMatchIndex >= 0 && _activeMatchIndex < _matches.Count)
        {
            CodeSearchMatch active = _matches[_activeMatchIndex];
            drawingContext.DrawRectangle(ActiveMatchBrush, null,
                GetMarkerRect(active.StartLine, active.EndLine, trackBounds, documentHeight, true));
        }
    }

    private Rect GetMarkerRect(int startLine, int endLine, Rect track, double documentHeight, bool active)
    {
        var view = _editor!.TextArea.TextView;
        int lineCount = _editor.Document.LineCount;
        double documentTop = view.GetVisualTopByDocumentLine(Math.Clamp(startLine, 1, lineCount));
        double documentBottom = endLine < lineCount
            ? view.GetVisualTopByDocumentLine(Math.Clamp(endLine + 1, 1, lineCount))
            : documentHeight;
        double markerHeight = Math.Min(track.Height, Math.Max(active ? 4 : 3, (documentBottom - documentTop) / documentHeight * track.Height));
        double top = Math.Clamp(track.Top + documentTop / documentHeight * track.Height, track.Top, track.Bottom - markerHeight);
        double width = Math.Min(track.Width, active ? 7 : 5);
        return new Rect(track.Right - width, top, width, markerHeight);
    }

    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            T? descendant = FindDescendant(child, predicate);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

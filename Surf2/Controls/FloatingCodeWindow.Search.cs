using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Search;
using Surf2.Models;

namespace Surf2.Controls;

public partial class FloatingCodeWindow
{
    private readonly CodeSearchPanelState _searchState = new();
    private IReadOnlyList<CodeSearchMatch> _searchMatches = Array.Empty<CodeSearchMatch>();
    private int _currentSearchMatchIndex = -1;
    private bool _updatingSearchPanel;
    private bool _searchVisualUpdateQueued;
    private string? _searchError;

    public event EventHandler? SearchReferenceCopyRequested;

    public int SearchMatchCount => _searchMatches.Count;

    public int CurrentSearchMatchNumber => _currentSearchMatchIndex + 1;

    private void InitializeSearchEnhancements()
    {
        _searchPanel.Tag = _searchState;
        _searchPanel.Template = (ControlTemplate)Resources["CodeSearchPanelTemplate"];
        _searchPanel.SearchOptionsChanged += SearchPanel_SearchOptionsChanged;
        _searchPanel.Loaded += (_, _) => QueueSearchVisualUpdate();
        _searchPanel.Unloaded += (_, _) => QueueSearchVisualUpdate();
        _searchState.Changed += (_, _) => RefreshSearchMatches(applySettingsToPanel: true);
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCurrentSearchMatch();
        Editor.TextChanged += (_, _) => RefreshSearchMatches(applySettingsToPanel: false);
        Editor.SizeChanged += (_, _) => UpdateSearchVisuals();
        Loaded += (_, _) => QueueSearchVisualUpdate();
        Unloaded += (_, _) => SearchOverview.SetMatches(Editor, Array.Empty<CodeSearchMatch>(), -1, false);
    }

    public CodeSearchReference? GetSearchReference()
    {
        if (string.IsNullOrEmpty(_searchState.SearchText) || _searchError != null ||
            string.IsNullOrWhiteSpace(State.FilePath))
        {
            return null;
        }

        return new CodeSearchReference(
            State.FilePath,
            _searchState.SearchText,
            _searchState.MatchCase,
            _searchState.WholeWords,
            _searchState.UseRegex);
    }

    public void ApplySearchReference(CodeSearchReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        SetSearchSettings(reference);
        if (!IsLoaded)
        {
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                OpenSearchAndSelectFirstMatch();
            };
            Loaded += handler;
            return;
        }

        OpenSearchAndSelectFirstMatch();
    }

    private void SetSearchSettings(CodeSearchReference reference)
    {
        _searchState.SetSettings(reference.SearchText, reference.MatchCase, reference.WholeWords, reference.UseRegex);
    }

    private void OpenSearchAndSelectFirstMatch()
    {
        _searchPanel.Open();
        UpdateSearchVisuals();
        if (_searchMatches.Count > 0)
        {
            CodeSearchMatch match = _searchMatches[0];
            var location = Editor.Document.GetLocation(match.Offset);
            ScrollToSearchMatch(match.Offset, match.Length, location.Line, location.Column, false);
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(_searchPanel.Reactivate));
    }

    private void SearchPanel_SearchOptionsChanged(object? sender, SearchOptionsChangedEventArgs e)
    {
        if (_updatingSearchPanel)
        {
            return;
        }

        // Also support existing callers which set AvalonEdit's public search properties directly.
        _searchState.SetSettings(e.SearchPattern ?? string.Empty, e.MatchCase, e.WholeWords, e.UseRegex, notifyChange: false);
        RefreshSearchMatches(applySettingsToPanel: false);
    }

    private void RefreshSearchMatches(bool applySettingsToPanel)
    {
        var matches = new List<CodeSearchMatch>();
        _searchError = null;
        if (!string.IsNullOrEmpty(_searchState.SearchText) && Editor.Document != null)
        {
            try
            {
                ISearchStrategy strategy = SearchStrategyFactory.Create(
                    _searchState.SearchText,
                    !_searchState.MatchCase,
                    _searchState.WholeWords,
                    _searchState.UseRegex ? SearchMode.RegEx : SearchMode.Normal);
                foreach (ISearchResult result in strategy.FindAll(Editor.Document, 0, Editor.Document.TextLength))
                {
                    int endOffset = result.Length > 0 ? result.EndOffset - 1 : result.Offset;
                    matches.Add(new CodeSearchMatch(
                        result.Offset,
                        result.Length,
                        Editor.Document.GetLineByOffset(result.Offset).LineNumber,
                        Editor.Document.GetLineByOffset(endOffset).LineNumber));
                }
            }
            catch (SearchPatternException ex)
            {
                _searchError = ex.Message;
            }
        }

        _searchMatches = matches;
        if (applySettingsToPanel)
        {
            _updatingSearchPanel = true;
            try
            {
                // Clear before changing modes so a literal such as '[' cannot throw when regex is enabled.
                // The owned textbox retains invalid input while the built-in search safely has no results.
                _searchPanel.SearchPattern = string.Empty;
                _searchPanel.MatchCase = _searchState.MatchCase;
                _searchPanel.WholeWords = _searchState.WholeWords;
                _searchPanel.UseRegex = _searchState.UseRegex;
                _searchPanel.SearchPattern = _searchError == null ? _searchState.SearchText : string.Empty;
            }
            finally
            {
                _updatingSearchPanel = false;
            }
        }

        UpdateCurrentSearchMatch();
        UpdateSearchVisuals();
    }

    private void UpdateCurrentSearchMatch()
    {
        int offset = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        int low = 0;
        int high = _searchMatches.Count - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_searchMatches[middle].Offset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        _currentSearchMatchIndex = -1;
        if (candidate >= 0)
        {
            CodeSearchMatch match = _searchMatches[candidate];
            bool exactSelection = Editor.SelectionStart == match.Offset && Editor.SelectionLength == match.Length;
            bool caretInside = Editor.SelectionLength == 0 && offset >= match.Offset && offset < match.Offset + match.Length;
            if (exactSelection || caretInside)
            {
                _currentSearchMatchIndex = candidate;
            }
        }

        _searchState.UpdateResults(_searchMatches.Count, CurrentSearchMatchNumber, _searchError,
            !string.IsNullOrWhiteSpace(State.FilePath));
        SearchOverview.SetActiveMatch(_currentSearchMatchIndex);
    }

    private void QueueSearchVisualUpdate()
    {
        if (_searchVisualUpdateQueued)
        {
            return;
        }

        _searchVisualUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _searchVisualUpdateQueued = false;
            UpdateSearchVisuals();
        }));
    }

    private void UpdateSearchVisuals()
    {
        double availableWidth = Editor.TextArea.ActualWidth > 0 ? Editor.TextArea.ActualWidth : Editor.ActualWidth;
        _searchState.PanelWidth = Math.Clamp(availableWidth - 4, 120, 460);
        SearchOverview.SetMatches(Editor, _searchMatches, _currentSearchMatchIndex, IsLoaded && !_searchPanel.IsClosed);
    }

    private void CopySearchReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSearchReference() != null)
        {
            SearchReferenceCopyRequested?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }
}

internal readonly record struct CodeSearchMatch(int Offset, int Length, int StartLine, int EndLine);

internal sealed class CodeSearchPanelState : INotifyPropertyChanged
{
    private string _searchText = string.Empty;
    private bool _matchCase;
    private bool _wholeWords;
    private bool _useRegex;
    private string _resultText = "0 of 0";
    private string _resultToolTip = "No search matches";
    private bool _canCopyReference;
    private double _panelWidth = 360;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

    public string SearchText { get => _searchText; set => SetOption(ref _searchText, value ?? string.Empty); }
    public bool MatchCase { get => _matchCase; set => SetOption(ref _matchCase, value); }
    public bool WholeWords { get => _wholeWords; set => SetOption(ref _wholeWords, value); }
    public bool UseRegex { get => _useRegex; set => SetOption(ref _useRegex, value); }
    public string ResultText => _resultText;
    public string ResultToolTip => _resultToolTip;
    public bool CanCopyReference => _canCopyReference;

    public double PanelWidth
    {
        get => _panelWidth;
        set
        {
            if (Math.Abs(_panelWidth - value) > 0.5)
            {
                _panelWidth = value;
                OnPropertyChanged();
            }
        }
    }

    public void SetSettings(string text, bool matchCase, bool wholeWords, bool useRegex, bool notifyChange = true)
    {
        _searchText = text ?? string.Empty;
        _matchCase = matchCase;
        _wholeWords = wholeWords;
        _useRegex = useRegex;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(MatchCase));
        OnPropertyChanged(nameof(WholeWords));
        OnPropertyChanged(nameof(UseRegex));
        if (notifyChange)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UpdateResults(int count, int current, string? error, bool hasDocumentPath)
    {
        _resultText = error == null ? $"{current} of {count}" : "Invalid pattern";
        _resultToolTip = error ?? (count == 0 ? "No search matches" : $"Match {current} of {count}");
        _canCopyReference = hasDocumentPath && error == null && !string.IsNullOrEmpty(SearchText);
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(ResultToolTip));
        OnPropertyChanged(nameof(CanCopyReference));
    }

    private void SetOption<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

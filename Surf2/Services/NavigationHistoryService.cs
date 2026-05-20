using Surf2.Models;

namespace Surf2.Services;

public sealed class NavigationHistoryService
{
    private const int MaximumEntryCount = 500;

    private readonly List<NavigationHistoryEntry> _entries = [];
    private int _currentIndex = -1;

    public IReadOnlyList<NavigationHistoryEntry> Entries => _entries;

    public int CurrentIndex => _currentIndex;

    public bool CanMoveBack => _currentIndex > 0;

    public bool CanMoveForward => _currentIndex >= 0 && _currentIndex < _entries.Count - 1;

    public NavigationHistoryEntry? Current =>
        _currentIndex >= 0 && _currentIndex < _entries.Count ? _entries[_currentIndex] : null;

    public void Clear()
    {
        _entries.Clear();
        _currentIndex = -1;
    }

    public void Record(string filePath, int lineNumber, int columnNumber)
    {
        if (string.IsNullOrWhiteSpace(filePath) || lineNumber < 1)
        {
            return;
        }

        lineNumber = Math.Max(1, lineNumber);
        columnNumber = Math.Max(1, columnNumber);

        NavigationHistoryEntry? current = Current;
        if (current != null &&
            string.Equals(current.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            current.LineNumber == lineNumber)
        {
            current.ColumnNumber = columnNumber;
            current.CreatedAt = DateTimeOffset.Now;
            return;
        }

        if (_currentIndex < _entries.Count - 1)
        {
            _entries.RemoveRange(_currentIndex + 1, _entries.Count - _currentIndex - 1);
        }

        _entries.Add(new NavigationHistoryEntry
        {
            FilePath = filePath,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            CreatedAt = DateTimeOffset.Now
        });

        TrimOldEntries();
        _currentIndex = _entries.Count - 1;
    }

    public NavigationHistoryEntry? MoveBack()
    {
        if (!CanMoveBack)
        {
            return null;
        }

        _currentIndex--;
        return Current;
    }

    public NavigationHistoryEntry? MoveForward()
    {
        if (!CanMoveForward)
        {
            return null;
        }

        _currentIndex++;
        return Current;
    }

    private void TrimOldEntries()
    {
        if (_entries.Count <= MaximumEntryCount)
        {
            return;
        }

        int removeCount = _entries.Count - MaximumEntryCount;
        _entries.RemoveRange(0, removeCount);
        _currentIndex = Math.Max(-1, _currentIndex - removeCount);
    }
}

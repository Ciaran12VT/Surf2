using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Surf2.Models;

public sealed class CsvGridRow : INotifyPropertyChanged
{
    private readonly string[] _cells;

    public CsvGridRow(int columnCount)
        : this(new string[Math.Max(1, columnCount)])
    {
    }

    public CsvGridRow(IReadOnlyList<string> cells)
    {
        _cells = cells.ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int CellCount => _cells.Length;

    public string this[int index]
    {
        get => index >= 0 && index < _cells.Length ? _cells[index] : string.Empty;
        set
        {
            if (index < 0 || index >= _cells.Length || string.Equals(_cells[index], value, StringComparison.Ordinal))
            {
                return;
            }

            _cells[index] = value;
            OnPropertyChanged("Item[]");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record CsvGridDocument(IReadOnlyList<string> Headers, IReadOnlyList<CsvGridRow> Rows);

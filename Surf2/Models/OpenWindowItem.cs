using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Surf2.Models;

public sealed class OpenWindowItem : INotifyPropertyChanged
{
    private string _type;
    private Brush _typeBackBrush = Brushes.White;

    public OpenWindowItem(OpenDocumentState state, string type, Brush typeBackBrush)
    {
        State = state;
        _type = type;
        _typeBackBrush = typeBackBrush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public OpenDocumentState State { get; }

    public string FileName => string.IsNullOrWhiteSpace(State.DisplayName)
        ? Path.GetFileName(State.FilePath)
        : State.DisplayName;

    public string FilePath => State.FilePath;

    public string Type
    {
        get => _type;
        set
        {
            if (string.Equals(_type, value, StringComparison.Ordinal))
            {
                return;
            }

            _type = value;
            OnPropertyChanged();
        }
    }

    public Brush TypeBackBrush
    {
        get => _typeBackBrush;
        set
        {
            if (Equals(_typeBackBrush, value))
            {
                return;
            }

            _typeBackBrush = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

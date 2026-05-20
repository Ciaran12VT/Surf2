namespace Surf2.Controls;

public sealed class CursorPositionChangedEventArgs : EventArgs
{
    public CursorPositionChangedEventArgs(string filePath, int lineNumber, int columnNumber)
    {
        FilePath = filePath;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
    }

    public string FilePath { get; }

    public int LineNumber { get; }

    public int ColumnNumber { get; }
}

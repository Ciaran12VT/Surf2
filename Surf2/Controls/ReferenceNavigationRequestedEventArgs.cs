namespace Surf2.Controls;

public sealed class ReferenceNavigationRequestedEventArgs : EventArgs
{
    public ReferenceNavigationRequestedEventArgs(string token, int lineNumber, int columnNumber, int? argumentCount)
    {
        Token = token;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
        ArgumentCount = argumentCount;
    }

    public string Token { get; }

    public int LineNumber { get; }

    public int ColumnNumber { get; }

    public int? ArgumentCount { get; }
}

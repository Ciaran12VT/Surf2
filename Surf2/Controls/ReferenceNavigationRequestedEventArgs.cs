namespace Surf2.Controls;

public sealed class ReferenceNavigationRequestedEventArgs : EventArgs
{
    public ReferenceNavigationRequestedEventArgs(
        string token,
        int lineNumber,
        int columnNumber,
        int? argumentCount,
        int tokenStartColumnNumber = 0,
        int tokenEndColumnNumber = 0)
    {
        Token = token;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
        ArgumentCount = argumentCount;
        TokenStartColumnNumber = tokenStartColumnNumber <= 0 ? columnNumber : tokenStartColumnNumber;
        TokenEndColumnNumber = tokenEndColumnNumber <= TokenStartColumnNumber ? TokenStartColumnNumber + token.Length : tokenEndColumnNumber;
    }

    public string Token { get; }

    public int LineNumber { get; }

    public int ColumnNumber { get; }

    public int? ArgumentCount { get; }

    public int TokenStartColumnNumber { get; }

    public int TokenEndColumnNumber { get; }
}

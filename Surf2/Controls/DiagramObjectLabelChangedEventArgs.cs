namespace Surf2.Controls;

public sealed class DiagramObjectLabelChangedEventArgs : EventArgs
{
    public DiagramObjectLabelChangedEventArgs(string oldText, string newText)
    {
        OldText = oldText;
        NewText = newText;
    }

    public string OldText { get; }

    public string NewText { get; }
}

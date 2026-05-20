namespace Surf2.Controls;

public sealed class DiagramLabelTetherChangedEventArgs : EventArgs
{
    public DiagramLabelTetherChangedEventArgs(bool isTethered)
    {
        IsTethered = isTethered;
    }

    public bool IsTethered { get; }
}

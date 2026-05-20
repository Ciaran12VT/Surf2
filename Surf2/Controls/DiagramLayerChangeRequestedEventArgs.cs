namespace Surf2.Controls;

public enum DiagramLayerChangeAction
{
    BringForward,
    SendBackward,
    SendToBack
}

public sealed class DiagramLayerChangeRequestedEventArgs(DiagramLayerChangeAction action) : EventArgs
{
    public DiagramLayerChangeAction Action { get; } = action;
}

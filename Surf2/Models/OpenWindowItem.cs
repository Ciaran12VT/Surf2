using System.IO;

namespace Surf2.Models;

public sealed class OpenWindowItem
{
    public OpenWindowItem(OpenDocumentState state)
    {
        State = state;
    }

    public OpenDocumentState State { get; }

    public string FileName => Path.GetFileName(State.FilePath);

    public string FilePath => State.FilePath;
}

namespace Surf2.Models;

public sealed class OpenDocumentState
{
    public string FilePath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = 720;

    public double Height { get; set; } = 460;

    public double FontSize { get; set; } = 13;
}

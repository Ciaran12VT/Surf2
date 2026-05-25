using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class WorkspaceState
{
    public string? LastFolderPath { get; set; }

    public double CanvasZoom { get; set; } = 1;

    public double ViewportHorizontalOffset { get; set; }

    public double ViewportVerticalOffset { get; set; }

    public List<string> UnloadedResourceIds { get; set; } = [];

    public ObservableCollection<OpenDocumentState> OpenDocuments { get; set; } = [];
}

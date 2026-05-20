namespace Surf2.Models;

public sealed class WorkbenchState
{
    public string WorkbenchId { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string ScopeId { get; set; } = string.Empty;

    public string ScopeName { get; set; } = "No scope";

    public bool IsCodeViewVisible { get; set; } = true;

    public bool IsDiagramViewVisible { get; set; }

    public string ActiveWorkspaceView { get; set; } = "Code";

    public string WorkspaceSplitOrientation { get; set; } = "DiagramBottom";

    public string CodeViewMode { get; set; } = "Canvas";

    public double CodeCanvasZoom { get; set; } = 1;

    public double CodeViewportHorizontalOffset { get; set; }

    public double CodeViewportVerticalOffset { get; set; }

    public List<OpenDocumentState> OpenDocuments { get; set; } = [];

    public string ActiveDocumentPath { get; set; } = string.Empty;

    public string ActiveDiagramId { get; set; } = string.Empty;

    public string ActiveDiagramName { get; set; } = string.Empty;

    public DiagramDocument? ActiveDiagramSnapshot { get; set; }

    public double DiagramCanvasZoom { get; set; } = 1;

    public double DiagramViewportHorizontalOffset { get; set; }

    public double DiagramViewportVerticalOffset { get; set; }

    public string DisplayName => $"{ScopeName} - {SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}

public sealed class WorkbenchLibrary
{
    public List<WorkbenchState> Workbenches { get; set; } = [];
}

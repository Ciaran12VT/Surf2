namespace Surf2.Models;

public sealed class DiagramDocument
{
    public string DiagramId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Untitled Diagram";

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public double CanvasZoom { get; set; } = 1;

    public double ViewportHorizontalOffset { get; set; }

    public double ViewportVerticalOffset { get; set; }

    public List<DiagramObjectSnapshot> Objects { get; set; } = [];

    public List<WorkflowDocument> Workflows { get; set; } = [];
}

public sealed class DiagramLibrary
{
    public List<DiagramDocument> Diagrams { get; set; } = [];

    public DiagramDocument? Find(string? diagramId)
    {
        if (string.IsNullOrWhiteSpace(diagramId))
        {
            return null;
        }

        return Diagrams.FirstOrDefault(diagram =>
            string.Equals(diagram.DiagramId, diagramId, StringComparison.OrdinalIgnoreCase));
    }

    public void Upsert(DiagramDocument document)
    {
        DiagramDocument? existing = Find(document.DiagramId);
        if (existing == null)
        {
            Diagrams.Add(document);
            return;
        }

        int index = Diagrams.IndexOf(existing);
        Diagrams[index] = document;
    }
}

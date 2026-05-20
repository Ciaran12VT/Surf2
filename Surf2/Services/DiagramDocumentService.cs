using Surf2.Models;

namespace Surf2.Services;

public static class DiagramDocumentService
{
    private const string DiagramPathPrefix = "surf2://diagram/";
    public const string DiagramRootPath = "surf2://diagrams";

    public static string CreateDiagramDocumentPath(DiagramDocument diagram)
    {
        return CreateDiagramDocumentPath(diagram.DiagramId);
    }

    public static string CreateDiagramDocumentPath(string diagramId)
    {
        return $"{DiagramPathPrefix}{diagramId}";
    }

    public static bool IsDiagramDocumentPath(string path)
    {
        return path.StartsWith(DiagramPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetDiagramId(string path)
    {
        return IsDiagramDocumentPath(path)
            ? path[DiagramPathPrefix.Length..]
            : string.Empty;
    }
}

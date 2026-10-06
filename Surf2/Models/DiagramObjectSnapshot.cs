namespace Surf2.Models;

public enum DiagramObjectType
{
    Shape,
    Image,
    Line,
    Label,
    WorkflowMarker,
    Portal,
    InfoPoint
}

public sealed class DiagramObjectSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DiagramObjectType ObjectType { get; set; }

    public DiagramObjectMetadata Metadata { get; set; } = new();

    public int ZIndex { get; set; }

    public string WorkflowId { get; set; } = string.Empty;

    public string WorkflowItemId { get; set; } = string.Empty;

    public string PortalName { get; set; } = string.Empty;

    public string PairedPortalDiagramId { get; set; } = string.Empty;

    public string PairedPortalObjectId { get; set; } = string.Empty;

    public DiagramShapeKind ShapeKind { get; set; }

    public string ImageDefinitionId { get; set; } = string.Empty;

    public string ImageName { get; set; } = string.Empty;

    public string ImageDataBase64 { get; set; } = string.Empty;

    public string PastedImageFileName { get; set; } = string.Empty;

    public string LabelText { get; set; } = string.Empty;

    public double LabelFontSize { get; set; } = 12;

    public string OutlineColorText { get; set; } = "#000000";

    public string BackColorText { get; set; } = "#FFFFFF";

    public bool HasEndArrow { get; set; }

    public bool IsLineLoose { get; set; }

    public double LineStartX { get; set; }

    public double LineStartY { get; set; }

    public double LineEndX { get; set; }

    public double LineEndY { get; set; }

    public bool IsTethered { get; set; } = true;

    public double LabelAnchorX { get; set; }

    public double LabelAnchorY { get; set; }

    public double LabelBoxLeft { get; set; }

    public double LabelBoxTop { get; set; }

    public double LabelBoxWidth { get; set; }

    public double LabelBoxHeight { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public DiagramObjectSnapshot Clone()
    {
        return new DiagramObjectSnapshot
        {
            Id = Id,
            ObjectType = ObjectType,
            Metadata = Metadata.Clone(),
            ZIndex = ZIndex,
            WorkflowId = WorkflowId,
            WorkflowItemId = WorkflowItemId,
            PortalName = PortalName,
            PairedPortalDiagramId = PairedPortalDiagramId,
            PairedPortalObjectId = PairedPortalObjectId,
            ShapeKind = ShapeKind,
            ImageDefinitionId = ImageDefinitionId,
            ImageName = ImageName,
            ImageDataBase64 = ImageDataBase64,
            PastedImageFileName = PastedImageFileName,
            LabelText = LabelText,
            LabelFontSize = LabelFontSize,
            OutlineColorText = OutlineColorText,
            BackColorText = BackColorText,
            HasEndArrow = HasEndArrow,
            IsLineLoose = IsLineLoose,
            LineStartX = LineStartX,
            LineStartY = LineStartY,
            LineEndX = LineEndX,
            LineEndY = LineEndY,
            IsTethered = IsTethered,
            LabelAnchorX = LabelAnchorX,
            LabelAnchorY = LabelAnchorY,
            LabelBoxLeft = LabelBoxLeft,
            LabelBoxTop = LabelBoxTop,
            LabelBoxWidth = LabelBoxWidth,
            LabelBoxHeight = LabelBoxHeight,
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height
        };
    }
}

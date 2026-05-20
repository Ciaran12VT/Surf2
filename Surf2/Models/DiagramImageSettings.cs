namespace Surf2.Models;

public sealed class DiagramImageSettings
{
    public List<DiagramImageDefinition> Images { get; set; } = [];

    public DiagramImageSettings Clone()
    {
        Images ??= [];

        return new DiagramImageSettings
        {
            Images = Images
                .Select(image => image.Clone())
                .ToList()
        };
    }
}

public sealed class DiagramImageDefinition
{
    public const string NameMatchTarget = "Name";

    public const string ContentMatchTarget = "Content";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Regex { get; set; } = string.Empty;

    public string MatchTarget { get; set; } = NameMatchTarget;

    public string OriginalFileName { get; set; } = string.Empty;

    public string ImageDataBase64 { get; set; } = string.Empty;

    public DiagramImageDefinition Clone()
    {
        return new DiagramImageDefinition
        {
            Id = Id,
            Name = Name,
            Regex = Regex ?? string.Empty,
            MatchTarget = NormalizeMatchTarget(MatchTarget),
            OriginalFileName = OriginalFileName,
            ImageDataBase64 = ImageDataBase64
        };
    }

    public static string NormalizeMatchTarget(string? matchTarget)
    {
        return string.Equals(matchTarget, ContentMatchTarget, StringComparison.OrdinalIgnoreCase)
            ? ContentMatchTarget
            : NameMatchTarget;
    }
}

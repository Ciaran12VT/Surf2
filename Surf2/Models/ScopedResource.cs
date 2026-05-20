using System.IO;

namespace Surf2.Models;

public sealed class ScopedResource
{
    public string ResourceId { get; set; } = Guid.NewGuid().ToString("N");

    public ResourceKind Kind { get; set; }

    public string Path { get; set; } = string.Empty;

    public string DisplayNameOverride { get; set; } = string.Empty;

    public string DetailsOverride { get; set; } = string.Empty;

    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IncludeChildren { get; set; } = true;

    public bool Exists => Kind switch
    {
        ResourceKind.Folder => Directory.Exists(Path),
        ResourceKind.DatabaseSnapshot => true,
        ResourceKind.Diagram => true,
        _ => File.Exists(Path)
    };

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayNameOverride))
            {
                return DisplayNameOverride;
            }

            if (Kind == ResourceKind.DatabaseSnapshot)
            {
                return string.IsNullOrWhiteSpace(Path) ? "Database" : Path;
            }

            if (Kind == ResourceKind.Diagram)
            {
                return string.IsNullOrWhiteSpace(Path) ? "Diagram" : Path;
            }

            string name = System.IO.Path.GetFileName(Path);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    public string DisplayLabel => Kind switch
    {
        ResourceKind.DatabaseSnapshot => $"Database: {DisplayName}",
        ResourceKind.Diagram => $"Diagram: {DisplayName}",
        _ => $"{Kind}: {Path}{(Exists ? string.Empty : " (missing)")}"
    };

    public string ResourceTypeDisplay => Kind switch
    {
        ResourceKind.DatabaseSnapshot => "Database",
        ResourceKind.Diagram => "Diagram",
        _ => Kind.ToString()
    };

    public string Details => Kind switch
    {
        ResourceKind.DatabaseSnapshot when !string.IsNullOrWhiteSpace(DetailsOverride) => DetailsOverride,
        ResourceKind.Diagram when !string.IsNullOrWhiteSpace(DetailsOverride) => DetailsOverride,
        _ => Path
    };

    public string AddedDisplay => AddedAtUtc.LocalDateTime.ToString("g");
}

using System.IO;
using System.Text.Json.Serialization;

namespace Surf2.Models;

public sealed class ScopedResource
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public Guid ContainerId { get; set; }
    public ResourceSourceDefinition? Source { get; set; }
    public Guid? PairedResourceId { get; set; }
    [JsonIgnore] public string? RuntimePath { get; set; }
    [JsonIgnore] public string PhysicalPath => RuntimePath ?? Path;
    [JsonIgnore] public string? RuntimeStatus { get; set; }
    [JsonIgnore] public bool? RuntimeAvailable { get; set; }
    public string ResourceId { get; set; } = Guid.NewGuid().ToString("N");

    public ResourceKind Kind { get; set; }

    public string Path { get; set; } = string.Empty;

    public string DisplayNameOverride { get; set; } = string.Empty;

    public string DetailsOverride { get; set; } = string.Empty;

    internal string? SnapshotDisplayName { get; set; }
    internal string? SnapshotDetails { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IncludeChildren { get; set; } = true;

    public bool Exists => RuntimeAvailable ?? (Source?.Provider is SourceProvider.Git or SourceProvider.AzureBlob or SourceProvider.LiveSql || (Kind switch
    {
        ResourceKind.Folder => Directory.Exists(PhysicalPath),
        ResourceKind.DatabaseSnapshot => true,
        ResourceKind.LiveDatabase => true,
        ResourceKind.Diagram => true,
        _ => File.Exists(PhysicalPath)
    }));

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayNameOverride))
            {
                return DisplayNameOverride;
            }

            if (Kind is ResourceKind.DatabaseSnapshot or ResourceKind.LiveDatabase)
            {
                return SnapshotDisplayName ?? (string.IsNullOrWhiteSpace(Path) ? "Database" : Path);
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

    public string ResourceTypeDisplay => Source?.Provider switch
    {
        SourceProvider.Git => "Git Repo",
        SourceProvider.AzureBlob => "Blob Storage",
        SourceProvider.LiveSql => "Live SQL",
        _ => Kind switch
    {
        ResourceKind.DatabaseSnapshot => "Database",
        ResourceKind.Diagram => "Diagram",
        _ => Kind.ToString()
    }
    };

    public string Details => Kind switch
    {
        ResourceKind.DatabaseSnapshot when !string.IsNullOrWhiteSpace(DetailsOverride) => DetailsOverride,
        ResourceKind.DatabaseSnapshot => SnapshotDetails ?? Path,
        ResourceKind.Diagram when !string.IsNullOrWhiteSpace(DetailsOverride) => DetailsOverride,
        _ => Path
    };

    public string AddedDisplay => AddedAtUtc.LocalDateTime.ToString("g");
}

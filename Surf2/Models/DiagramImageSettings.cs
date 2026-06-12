namespace Surf2.Models;

public sealed class DiagramImageSettings
{
    public List<DiagramImageDefinition> Images { get; set; } = [];

    public bool EnsureDefaults()
    {
        Images ??= [];
        bool changed = false;

        bool needsSortOrder = Images.Any(image => image.SortOrder <= 0);
        for (int index = 0; index < Images.Count; index++)
        {
            DiagramImageDefinition image = Images[index];
            changed |= image.EnsureDefaults();
            if (needsSortOrder)
            {
                image.SortOrder = index + 1;
                changed = true;
            }
        }

        return changed;
    }

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

    public const string AnyResourceTypeFilter = "Any";

    public const string FileResourceTypeFilter = "File";

    public const string FolderResourceTypeFilter = "Folder";

    public const string DatabaseResourceTypeFilter = "Database";

    public const string DiagramResourceTypeFilter = "Diagram";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Regex { get; set; } = string.Empty;

    public string MatchTarget { get; set; } = NameMatchTarget;

    public string NameRegex { get; set; } = string.Empty;

    public string ContentRegex { get; set; } = string.Empty;

    public string ResourceTypeFilter { get; set; } = AnyResourceTypeFilter;

    public int SortOrder { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string ImageDataBase64 { get; set; } = string.Empty;

    public bool EnsureDefaults()
    {
        bool changed = false;

        if (string.IsNullOrWhiteSpace(NameRegex) &&
            string.IsNullOrWhiteSpace(ContentRegex) &&
            !string.IsNullOrWhiteSpace(Regex))
        {
            if (NormalizeMatchTarget(MatchTarget) == ContentMatchTarget)
            {
                ContentRegex = Regex;
            }
            else
            {
                NameRegex = Regex;
            }

            changed = true;
        }

        string normalizedResourceType = NormalizeResourceTypeFilter(ResourceTypeFilter);
        if (!string.Equals(ResourceTypeFilter, normalizedResourceType, StringComparison.Ordinal))
        {
            ResourceTypeFilter = normalizedResourceType;
            changed = true;
        }

        return changed;
    }

    public DiagramImageDefinition Clone()
    {
        EnsureDefaults();

        return new DiagramImageDefinition
        {
            Id = Id,
            Name = Name,
            Regex = Regex ?? string.Empty,
            MatchTarget = NormalizeMatchTarget(MatchTarget),
            NameRegex = NameRegex ?? string.Empty,
            ContentRegex = ContentRegex ?? string.Empty,
            ResourceTypeFilter = NormalizeResourceTypeFilter(ResourceTypeFilter),
            SortOrder = SortOrder,
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

    public static string NormalizeResourceTypeFilter(string? resourceTypeFilter)
    {
        return resourceTypeFilter?.Trim() switch
        {
            var value when string.Equals(value, FileResourceTypeFilter, StringComparison.OrdinalIgnoreCase) => FileResourceTypeFilter,
            var value when string.Equals(value, FolderResourceTypeFilter, StringComparison.OrdinalIgnoreCase) => FolderResourceTypeFilter,
            var value when string.Equals(value, DatabaseResourceTypeFilter, StringComparison.OrdinalIgnoreCase) => DatabaseResourceTypeFilter,
            var value when string.Equals(value, DatabaseResourceTypeFilter + "Snapshot", StringComparison.OrdinalIgnoreCase) => DatabaseResourceTypeFilter,
            var value when string.Equals(value, DiagramResourceTypeFilter, StringComparison.OrdinalIgnoreCase) => DiagramResourceTypeFilter,
            _ => AnyResourceTypeFilter
        };
    }

    public static string GetLegacyRegex(string nameRegex, string contentRegex)
    {
        return !string.IsNullOrWhiteSpace(contentRegex) && string.IsNullOrWhiteSpace(nameRegex)
            ? contentRegex
            : nameRegex;
    }

    public static string GetLegacyMatchTarget(string nameRegex, string contentRegex)
    {
        return !string.IsNullOrWhiteSpace(contentRegex) && string.IsNullOrWhiteSpace(nameRegex)
            ? ContentMatchTarget
            : NameMatchTarget;
    }
}

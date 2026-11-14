namespace Surf2.Models;

public sealed record ExistingScopeResourceCandidate(
    string Name,
    string Type,
    string Details,
    string Source,
    ResourceKind Kind,
    string Path,
    string DisplayNameOverride,
    string DetailsOverride,
    bool IncludeChildren,
    ResourceSourceDefinition? SourceConfiguration = null)
{
    public ScopedResource CreateScopedResource()
    {
        return new ScopedResource
        {
            Kind = Kind,
            Path = Path,
            DisplayNameOverride = DisplayNameOverride,
            DetailsOverride = DetailsOverride,
            IncludeChildren = IncludeChildren,
            Source = SourceConfiguration?.Copy(),
            AddedAtUtc = DateTimeOffset.UtcNow
        };
    }
}

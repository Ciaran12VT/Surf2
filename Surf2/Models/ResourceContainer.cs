namespace Surf2.Models;

/// <summary>A scope-owned group of alternative resources. All members remain in Scope.Resources.</summary>
public sealed class ResourceContainer
{
    public Guid ContainerId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid DefaultResourceId { get; set; }
}

public enum SourceProvider { LocalFileSystem, Git, AzureBlob, LiveSql }

/// <summary>Portable source configuration. Credentials and resolved cache paths never belong here.</summary>
public sealed class ResourceSourceDefinition
{
    public Guid SourceId { get; set; } = Guid.NewGuid();
    public SourceProvider Provider { get; set; }
    public long ConfigurationRevision { get; set; } = 1;
    public string Endpoint { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
    public string Subfolder { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string AuthenticationKind { get; set; } = string.Empty;
    public ResourceSourceDefinition Copy() => (ResourceSourceDefinition)MemberwiseClone();
}

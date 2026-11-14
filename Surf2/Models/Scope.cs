using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class Scope
{
    public string ScopeId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Scope";

    public string Description { get; set; } = string.Empty;

    public ObservableCollection<ScopedResource> Resources { get; set; } = [];

    public ObservableCollection<ResourceContainer> ResourceContainers { get; set; } = [];

    /// <summary>Upgrade old records without merging identities or losing inactive alternatives.</summary>
    public void EnsureResourceContainers()
    {
        var identities = new HashSet<Guid>();
        foreach (var resource in Resources)
        {
            if (resource.PublicId == Guid.Empty) resource.PublicId = Guid.NewGuid();
            if (!identities.Add(resource.PublicId)) throw new InvalidOperationException("Duplicate resource public identity.");
            var container = ResourceContainers.FirstOrDefault(c => c.ContainerId == resource.ContainerId);
            if (container == null)
            {
                container = new ResourceContainer { Name = resource.DisplayName, DefaultResourceId = resource.PublicId };
                ResourceContainers.Add(container);
                resource.ContainerId = container.ContainerId;
            }
        }
        foreach (var container in ResourceContainers.ToArray())
        {
            var members = Resources.Where(r => r.ContainerId == container.ContainerId).ToArray();
            if (members.Length == 0) { ResourceContainers.Remove(container); continue; }
            if (!members.Any(r => r.PublicId == container.DefaultResourceId)) container.DefaultResourceId = members[0].PublicId;
        }
        if (ResourceContainers.Select(c => c.ContainerId).Distinct().Count() != ResourceContainers.Count)
            throw new InvalidOperationException("Duplicate resource container identity.");
    }

    public ObservableCollection<VirtualFolder> VirtualFolders { get; set; } = [];

    public override string ToString() => Name;
}

using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class Scope
{
    public string ScopeId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Scope";

    public string Description { get; set; } = string.Empty;

    public ObservableCollection<ScopedResource> Resources { get; set; } = [];

    public ObservableCollection<VirtualFolder> VirtualFolders { get; set; } = [];

    public override string ToString() => Name;
}

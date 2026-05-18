using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class ScopeLibrary
{
    public int SchemaVersion { get; set; } = 1;

    public string? LastActiveScopeId { get; set; }

    public ObservableCollection<Scope> Scopes { get; set; } = [];
}

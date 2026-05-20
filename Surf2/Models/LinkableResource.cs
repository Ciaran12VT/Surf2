namespace Surf2.Models;

public enum LinkableResourceKind
{
    Folder,
    File,
    Database,
    StoredProcedure,
    View,
    Function,
    Trigger,
    Table,
    Diagram
}

public sealed record LinkableResource(
    string Name,
    string Type,
    string Path,
    LinkableResourceKind Kind);

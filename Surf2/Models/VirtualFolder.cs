using System.Collections.ObjectModel;

namespace Surf2.Models;

public sealed class VirtualFolder
{
    public string VirtualFolderId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Virtual Folder";

    public string ParentNodeKey { get; set; } = FileSystemNode.RootParentKey;

    public ObservableCollection<string> ChildNodeKeys { get; set; } = [];

    public VirtualFolder CloneWithNewId()
    {
        return new VirtualFolder
        {
            VirtualFolderId = Guid.NewGuid().ToString("N"),
            Name = Name,
            ParentNodeKey = ParentNodeKey,
            ChildNodeKeys = new ObservableCollection<string>(ChildNodeKeys)
        };
    }
}

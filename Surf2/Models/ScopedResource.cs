using System.IO;

namespace Surf2.Models;

public sealed class ScopedResource
{
    public string ResourceId { get; set; } = Guid.NewGuid().ToString("N");

    public ResourceKind Kind { get; set; }

    public string Path { get; set; } = string.Empty;

    public bool IncludeChildren { get; set; } = true;

    public bool Exists => Kind == ResourceKind.Folder ? Directory.Exists(Path) : File.Exists(Path);

    public string DisplayName
    {
        get
        {
            string name = System.IO.Path.GetFileName(Path);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    public string DisplayLabel => $"{Kind}: {Path}{(Exists ? string.Empty : " (missing)")}";
}

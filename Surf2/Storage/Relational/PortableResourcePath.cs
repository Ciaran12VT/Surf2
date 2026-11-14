using System.IO;
using Surf2.Services.RelationalDocuments;

namespace Surf2.Storage.Relational;

// Validates portable identity syntax without resolving a resource, touching files, or requiring
// the linked member still to exist. Removed members retain recoverable, unresolved links.
internal static class PortableResourcePath
{
    public static void Validate(string? value)
    {
        if(string.IsNullOrEmpty(value))return;
        // Diagram links may wrap the address in a search reference and/or add a line/filter suffix.
        // Inspect the decoded address without altering those saved navigation options.
        DurableDocumentLink.Rewrite(value,path=>{ValidateAddress(path);return path;});
    }

    private static void ValidateAddress(string value)
    {
        string path=value.Replace('\\','/');
        if(path.Contains("/Surf2/SourceCache/",StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Surf2/LiveDefinitions/",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A managed local cache path cannot be saved. Save the resource's portable document link instead.");
        bool source=value.StartsWith("source://",StringComparison.OrdinalIgnoreCase);
        bool sql=value.StartsWith("livesql://",StringComparison.OrdinalIgnoreCase);
        if(!source&&!sql)return;
        string[] parts=value[(source?9:10)..].Split('/');
        if(parts.Length<(source?2:4) || sql&&parts.Length!=4 ||
            !Guid.TryParseExact(parts[0],"N",out var member)||member==Guid.Empty)
            throw new InvalidDataException("The portable resource link has an invalid identity.");
        for(int i=1;i<parts.Length;i++)
        {
            if(source&&i==parts.Length-1&&parts[i].Length==0)break; // Typed directory marker, including the source root.
            string decoded=Uri.UnescapeDataString(parts[i]);
            if(decoded.Length==0 || decoded.Contains('\0') || source&&(decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\')) ||
                !string.Equals(Uri.EscapeDataString(decoded),parts[i],StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The portable resource link has an invalid item path.");
        }
    }
}

using System.IO;
using Surf2.Models;

namespace Surf2.Storage.Relational;

internal static class PortableSourceDefinition
{
    public static void Validate(ResourceSourceDefinition source)
    {
        if(source.SourceId==Guid.Empty||source.ConfigurationRevision<1||!Enum.IsDefined(source.Provider))
            throw new InvalidDataException("Invalid resource source identity or provider.");
        foreach(string field in new[]{source.Endpoint,source.Container,source.Prefix,source.Ref,source.Subfolder,source.DatabaseName,source.AuthenticationKind})
            if(field==null||field.Length>32768)throw new InvalidDataException("Source configuration exceeds its limit.");
        PortableResourcePath.Validate(source.Endpoint);
        if(Uri.TryCreate(source.Endpoint,UriKind.Absolute,out var uri))
        {
            bool sshUserOnly=source.Provider==SourceProvider.Git&&uri.Scheme=="ssh"&&!Uri.UnescapeDataString(uri.UserInfo).Contains(':');
            if(!string.IsNullOrEmpty(uri.UserInfo)&&!sshUserOnly ||
                source.Provider is SourceProvider.Git or SourceProvider.AzureBlob && !string.IsNullOrEmpty(uri.Query))
                throw new InvalidDataException("Remove credentials from the portable source endpoint.");
        }
    }
}

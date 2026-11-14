using System.IO;
using System.IO.Compression;

namespace Surf2.Storage.Relational.Packages;

public sealed partial class RelationalPackageImporter
{
    /// <summary>Read-only archive verification, also used before accepting an upgrade recovery export.</summary>
    public async Task<RelationalPackageManifest> VerifyArchiveAsync(string path,CancellationToken ct=default)
    {
        await using var file=new FileStream(Path.GetFullPath(path),FileMode.Open,FileAccess.Read,FileShare.Read,16384,FileOptions.Asynchronous);
        await PreflightZipAsync(file,ct);
        using var archive=new ZipArchive(file,ZipArchiveMode.Read,leaveOpen:true);
        var package=await ReadPackageAsync(archive,new(),ct);
        await VerifyLocalFilesAsync(package,ct);
        var buffer=new byte[_limits.ChunkBytes];
        foreach(var table in package.Tables.Values)
        {
            await using var input=package.Entries[table.Entry].Open();
            using var verified=new PackageEntryReadStream(input,table.ByteCount,PackageRowDecoding.Hash(table.Sha256));
            long rows=0;int read;
            while((read=await verified.ReadAsync(buffer,ct))!=0)
                for(int i=0;i<read;i++)if(buffer[i]==(byte)'\n')rows++;
            verified.Finish();
            if(rows!=table.RowCount)throw PackageRowDecoding.Bad("Recovery entry row count mismatch");
        }
        return package.Manifest;
    }
}

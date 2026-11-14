using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    private async Task WriteResourceContainersAsync(SqlConnection c, SqlTransaction t, long scope, Scope value, CancellationToken ct)
    {
        value.EnsureResourceContainers();
        foreach (var source in value.Resources.Select(r => r.Source).OfType<ResourceSourceDefinition>())
        {
            PortableSourceDefinition.Validate(source);
            await using var command = Command(c,t,"""
IF EXISTS(SELECT 1 FROM surf.SourceDefinition WITH(UPDLOCK,HOLDLOCK) WHERE SourceId=@Id AND ConfigurationRevision>@Revision)
 THROW 51121,'The source configuration changed; reload before saving.',1;
IF EXISTS(SELECT 1 FROM surf.SourceDefinition WHERE SourceId=@Id AND ConfigurationRevision=@Revision
 AND (Provider<>@Provider OR Endpoint COLLATE Latin1_General_100_BIN2<>@Endpoint OR Container COLLATE Latin1_General_100_BIN2<>@Container
 OR Prefix COLLATE Latin1_General_100_BIN2<>@Prefix OR Ref COLLATE Latin1_General_100_BIN2<>@Ref
 OR Subfolder COLLATE Latin1_General_100_BIN2<>@Subfolder OR DatabaseName COLLATE Latin1_General_100_BIN2<>@Database
 OR AuthenticationKind COLLATE Latin1_General_100_BIN2<>@Authentication))
 THROW 51122,'Source edits require a new configuration revision.',1;
UPDATE surf.SourceDefinition SET Provider=@Provider,ConfigurationRevision=@Revision,Endpoint=@Endpoint,
 Container=@Container,Prefix=@Prefix,Ref=@Ref,Subfolder=@Subfolder,DatabaseName=@Database,AuthenticationKind=@Authentication WHERE SourceId=@Id;
IF @@ROWCOUNT=0 INSERT surf.SourceDefinition(SourceId,Provider,ConfigurationRevision,Endpoint,Container,Prefix,Ref,Subfolder,DatabaseName,AuthenticationKind)
 VALUES(@Id,@Provider,@Revision,@Endpoint,@Container,@Prefix,@Ref,@Subfolder,@Database,@Authentication);
""", RelationalSession.Parameter("@Id",SqlDbType.UniqueIdentifier,source.SourceId),
                RelationalSession.Parameter("@Provider",SqlDbType.Int,(int)source.Provider), Key(source.ConfigurationRevision,"@Revision"),
                Text(source.Endpoint,"@Endpoint"),Text(source.Container,"@Container"),Text(source.Prefix,"@Prefix"),
                Text(source.Ref,"@Ref"),Text(source.Subfolder,"@Subfolder"),Text(source.DatabaseName,"@Database"),Text(source.AuthenticationKind,"@Authentication"));
            using var cancel=RelationalSession.CancelCommand(command,ct);
            await command.ExecuteNonQueryAsync(ct);
        }
        for (int i=0;i<value.ResourceContainers.Count;i++)
        {
            var container=value.ResourceContainers[i];
            if (!value.Resources.Any(r=>r.ContainerId==container.ContainerId && r.PublicId==container.DefaultResourceId))
                throw new InvalidDataException("Container default must be one of its members.");
            await ExecuteAsync(c,t,"""
IF EXISTS(SELECT 1 FROM surf.ResourceContainer WHERE ContainerId=@Id AND ScopeKey<>@Scope)
 THROW 51123,'A resource container belongs to another scope.',1;
UPDATE surf.ResourceContainer SET Name=@Name,SortOrdinal=@Ordinal,DefaultResourceId=@Default WHERE ContainerId=@Id AND ScopeKey=@Scope;
IF @@ROWCOUNT=0 INSERT surf.ResourceContainer(ContainerId,ScopeKey,Name,SortOrdinal,DefaultResourceId) VALUES(@Id,@Scope,@Name,@Ordinal,@Default);
""",ct,RelationalSession.Parameter("@Id",SqlDbType.UniqueIdentifier,container.ContainerId),Key(scope,"@Scope"),Text(container.Name,"@Name"),
                Key(i,"@Ordinal"),RelationalSession.Parameter("@Default",SqlDbType.UniqueIdentifier,container.DefaultResourceId));
        }
    }

    private async Task ReadResourceContainersAsync(SqlConnection c,SqlTransaction t,long scope,Scope value,StateBudget budget,CancellationToken ct)
    {
        await using(var command=Command(c,t,"SELECT ContainerId,CONVERT(bigint,DATALENGTH(Name)),Name,DefaultResourceId FROM surf.ResourceContainer WHERE ScopeKey=@Scope ORDER BY SortOrdinal,ContainerId;",Key(scope,"@Scope")))
        {
            using var cancel=RelationalSession.CancelCommand(command,ct);
            await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct);
            while(await reader.ReadAsync(ct))
            {
                budget.Row();var id=reader.GetGuid(0);var name=(await StringAsync(reader,1,budget,ct))!;
                value.ResourceContainers.Add(new ResourceContainer {ContainerId=id,Name=name,DefaultResourceId=reader.IsDBNull(3)?Guid.Empty:reader.GetGuid(3)});
            }
        }
        foreach(var resource in value.Resources.Where(r=>r.Source!=null))
        {
            await using var command=Command(c,t,"""
SELECT Provider,ConfigurationRevision,LEFT(Endpoint,32769),LEFT(Container,32769),LEFT(Prefix,32769),LEFT(Ref,32769),LEFT(Subfolder,32769),LEFT(DatabaseName,32769),LEFT(AuthenticationKind,32769)
FROM surf.SourceDefinition WHERE SourceId=@Id;
""",RelationalSession.Parameter("@Id",SqlDbType.UniqueIdentifier,resource.Source!.SourceId));
            using var cancel=RelationalSession.CancelCommand(command,ct);
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new InvalidDataException("Resource source is missing.");
            var source=resource.Source!;source.Provider=(SourceProvider)reader.GetInt32(0);source.ConfigurationRevision=reader.GetInt64(1);
            string Read(int n){var text=reader.GetString(n);if(text.Length>32768)throw new InvalidDataException("Source configuration exceeds its limit.");budget.Text((long)text.Length*2);return text;}
            source.Endpoint=Read(2);source.Container=Read(3);source.Prefix=Read(4);source.Ref=Read(5);source.Subfolder=Read(6);source.DatabaseName=Read(7);source.AuthenticationKind=Read(8);
        }
    }

    private Task PruneResourceContainersAsync(SqlConnection c,SqlTransaction t,long scope,CancellationToken ct)=>ExecuteAsync(c,t,"""
DELETE c FROM surf.ResourceContainer c WHERE ScopeKey=@Scope AND NOT EXISTS(SELECT 1 FROM surf.ScopeResource r WHERE r.ContainerId=c.ContainerId);
UPDATE c SET DefaultResourceId=(SELECT TOP(1) r.PublicId FROM surf.ScopeResource r WHERE r.ContainerId=c.ContainerId ORDER BY r.SortOrdinal,r.ScopeResourceKey)
FROM surf.ResourceContainer c WHERE ScopeKey=@Scope AND NOT EXISTS(SELECT 1 FROM surf.ScopeResource r WHERE r.ContainerId=c.ContainerId AND r.PublicId=c.DefaultResourceId);
""",ct,Key(scope,"@Scope"));
}

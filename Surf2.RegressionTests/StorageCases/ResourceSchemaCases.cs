using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.State;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.SourceResources;
using Surf2.Services;

public static partial class StorageRegressionSuite
{
    public static async Task RunResourceSchemaChecksAsync(Action<bool,string> check)
    {
        check(Convert.ToHexString(RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(1)))=="A2B44BF46E8D816B95CDF99CB15921C1A35390A32C433EE35B997BBBC2F6B642","Schema-1 installation checksum remains frozen");
        check(Convert.ToHexString(RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(2)))=="73AFD7D27D25A6E5BB60C64CE985126B44DAC8233A407BF0A9DBB2CC6CB329CF","Schema-2 installation checksum remains frozen");
        var model=new Scope();var member=new ScopedResource { Path=@"C:\not-read",ResourceId="duplicate" };model.Resources.Add(member);
        model.EnsureResourceContainers();var container=member.ContainerId;var identity=member.PublicId;
        model.EnsureResourceContainers();check(model.ResourceContainers.Count==1&&member.ContainerId==container&&member.PublicId==identity,"Singleton conversion is idempotent and performs no file access");
        model.Resources.Add(new ScopedResource {ResourceId="duplicate",Path=member.Path});model.EnsureResourceContainers();
        check(model.ResourceContainers.Count==2&&model.Resources.Select(r=>r.PublicId).Distinct().Count()==2,"Duplicate raw IDs and paths remain separate resource occurrences");
        var sharedLocal=new ResourceSourceDefinition {Endpoint=@"C:\original"};
        var rebound=new Scope {Resources=[new(){Path=@"C:\different",Source=sharedLocal.Copy()}]};
        var reboundCopy=StateCopies.Scope(rebound,new(new()));
        check(reboundCopy.Resources[0].Source!.SourceId!=sharedLocal.SourceId&&reboundCopy.Resources[0].Source!.ConfigurationRevision==1&&sharedLocal.Endpoint==@"C:\original",
            "Rebinding one local member forks its source descriptor without editing another scope's source");
        var projectionModel=new Scope();
        projectionModel.Resources.Add(new(){Path=@"C:\fixture-local"});projectionModel.EnsureResourceContainers();
        projectionModel.Resources.Add(new(){Path="https://example.test/repo",ContainerId=projectionModel.Resources[0].ContainerId,Source=new(){Provider=SourceProvider.Git,Endpoint="https://example.test/repo"}});
        var explorer=new ExplorerScope(new(Guid.NewGuid(),1,1,"scope","snapshots","diagrams",true,[],[],false),projectionModel.ScopeId,"Projection",
            [new(1,0,"local",ResourceKind.Folder,@"C:\fixture-local","",true,true,null,null,null,null,false),
             new(2,1,"remote",ResourceKind.Folder,"https://example.test/repo","",true,true,null,null,null,null,false)],[],"en-US");
        var projected=ResourceWorkspace.Project(explorer,projectionModel);
        check(projected.Resources.Length==1&&projected.Resources[0].ScopeResourceKey==1&&projected.Context.UnloadedScopeResourceKeys.SequenceEqual(new long[]{2})&&projectionModel.Resources.Count==2,
            "Active projection excludes inactive alternatives from index context while preserving all stored members");
        projectionModel.ResourceContainers[0].DefaultResourceId=projectionModel.Resources[1].PublicId;
        projected=ResourceWorkspace.Project(explorer,projectionModel);
        check(projected.Resources.Length==1&&!projected.Resources[0].IsLoaded&&projected.Context.UnloadedScopeResourceKeys.SequenceEqual(new long[]{1,2}),
            "Unprepared remote selection remains visible but excludes both inactive and unavailable members from search");
        projectionModel.VirtualFolders.Add(new(){ParentNodeKey=$"source://{projectionModel.Resources[1].PublicId:N}/src/",ChildNodeKeys=[$"source://{projectionModel.Resources[1].PublicId:N}/src/a.cs"]});
        var merged=StateScopeMerge.Prepare(new Scope(),projectionModel);var mergedRemote=merged.Resources.Single(r=>r.Source?.Provider==SourceProvider.Git);
        check(mergedRemote.PublicId!=projectionModel.Resources[1].PublicId&&merged.VirtualFolders.Single().ChildNodeKeys.Single()==$"source://{mergedRemote.PublicId:N}/src/a.cs"&&
            merged.VirtualFolders.Single().ParentNodeKey==$"source://{mergedRemote.PublicId:N}/src/","Scope merge remaps portable virtual-folder links to the copied resource identities");

        await using var old=await SqlFixture.CreateAsync();
        foreach(string script in RelationalSchemaInstaller.ReadScripts(2))await old.DestinationSqlAsync(script);
        Guid database=Guid.NewGuid();
        await old.DestinationSqlAsync("""
INSERT surf.StorageFormatInfo(Singleton,FormatIdentifier,SchemaVersion,MinimumReaderVersion,MinimumWriterVersion,State,DatabaseIdentity)
VALUES(1,N'Surf2.RelationalV2',2,2,2,'Ready',@Identity);
INSERT surf.SchemaMigration(SchemaVersion,ScriptChecksum) VALUES(2,@Checksum);
INSERT surf.TextContent(ContentHash,HashEncodingVersion,CharacterCount,ByteCount,LineCount,Text) VALUES(HASHBYTES('SHA2_256',CONVERT(varbinary(max),N'')),1,0,0,1,N'');
DECLARE @Text bigint=SCOPE_IDENTITY();
INSERT surf.Scope(ProfileKey,ScopeId,Name,DescriptionContentKey,SortOrdinal,PublicationId) VALUES(1,N'legacy',N'Legacy',@Text,0,NEWID());
DECLARE @Scope bigint=SCOPE_IDENTITY();
INSERT surf.ScopeResource(ScopeKey,SortOrdinal,ResourceId,Kind,Path,DisplayNameOverride,DetailsOverrideContentKey,AddedAtUtc,IncludeChildren)
VALUES(@Scope,0,N'duplicate',0,N'C:\not-read',N'First',@Text,SYSDATETIMEOFFSET(),1),(@Scope,1,N'duplicate',0,N'C:\not-read',N'Second',@Text,SYSDATETIMEOFFSET(),0);
""",RelationalSession.Parameter("@Identity",SqlDbType.UniqueIdentifier,database),RelationalSession.Parameter("@Checksum",SqlDbType.Binary,RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(2)),32));
        await using(var preferenceConnection=new SqlConnection(old.DestinationConnectionString))
        {
            await preferenceConnection.OpenAsync();await using var transaction=(SqlTransaction)await preferenceConnection.BeginTransactionAsync();
            await RelationalPreferenceSchema.InstallAsync(preferenceConnection,transaction,CancellationToken.None);
            var preferences=new AppSettings();preferences.CodeWindows.DefaultDarkBackcolor="#112233";preferences.Preferences.ShowExplorerPreview=false;
            await new RelationalStateStore(new(old.DestinationConnectionString)).ImportSettingsAsync(preferenceConnection,transaction,preferences,Guid.NewGuid());
            await transaction.CommitAsync();
        }
        string directory=Path.Combine(old.OwnedDirectory,"resource-schema");Directory.CreateDirectory(directory);
        string oldPackage=Path.Combine(directory,"schema2.zip");
        var exportedOld=await new RelationalPackageExporter(new(old.DestinationConnectionString),schemaVersion:2)
            .ExportAsync(oldPackage,new(){AllowBlockingConsistentFallback=true});
        check(exportedOld.Manifest.PackageVersion==2&&exportedOld.Manifest.DatabaseSchemaVersion==2,"Compatibility export retains the package-2/schema-2 tuple");
        await using(var competing=new SqlConnection(new SqlConnectionStringBuilder(old.DestinationConnectionString){Pooling=false}.ConnectionString))
        {
            await competing.OpenAsync();bool refused=false;
            try{await new RelationalResourceSchemaUpgrade().UpgradeAsync(old.DestinationConnectionString,recoveryDirectory:directory);}
            catch(SqlException error)when(error.Number==51126){refused=true;}
            check(refused,"Upgrade refuses another existing database session without disconnecting it");
        }
        using(var cancelled=new CancellationTokenSource())
        {
            bool interrupted=false;
            try{await new RelationalResourceSchemaUpgrade().UpgradeAsync(old.DestinationConnectionString,
                new InlineProgress(message=>{if(message.StartsWith("Exclusive database access",StringComparison.Ordinal))cancelled.Cancel();}),cancelled.Token,directory);}
            catch(OperationCanceledException){interrupted=true;}
            check(interrupted&&Equals(await old.DestinationSqlAsync("SELECT SchemaVersion FROM surf.StorageFormatInfo;"),2)&&
                Equals(await old.DestinationSqlAsync("SELECT CONVERT(int,user_access) FROM sys.databases WHERE database_id=DB_ID();"),0),
                "Cancellation after acquiring admission restores MULTI_USER and leaves schema2 unchanged");
        }
        bool lateClientRejected=false;
        string? recovery=await new RelationalResourceSchemaUpgrade().UpgradeAsync(old.DestinationConnectionString,
            new InlineProgress(message=>
            {
                if(!message.StartsWith("Exclusive database access",StringComparison.Ordinal))return;
                using var late=new SqlConnection(new SqlConnectionStringBuilder(old.DestinationConnectionString){Pooling=false,ConnectTimeout=2}.ConnectionString);
                try{late.Open();}catch(SqlException){lateClientRejected=true;}
            }),recoveryDirectory:directory);
        check(lateClientRejected&&Equals(await old.DestinationSqlAsync("SELECT CONVERT(int,user_access) FROM sys.databases WHERE database_id=DB_ID();"),0),
            "SQL admission fence rejects an old client connecting after preflight and restores MULTI_USER after publication");
        var format=await new PersistenceFormatProbe().ProbeAsync(old.DestinationConnectionString);
        check(format.SchemaVersion==3&&format.DatabaseIdentity==database&&recovery!=null&&File.Exists(recovery),"Schema upgrade publishes version3 while preserving database identity and verified recovery export");
        var session=new RelationalSession(old.DestinationConnectionString);var state=new RelationalStateStore(session);
        var upgradedPreferences=(await state.ReadSettingsAsync())!.Value;
        check(upgradedPreferences.CodeWindows.DefaultDarkBackcolor=="#112233"&&!upgradedPreferences.Preferences.ShowExplorerPreview,
            "Schema2's known additive preference fields retain their exact values during schema3 upgrade");
        var saved=await state.ReadScopeAsync(1)??throw new InvalidOperationException("Scope lost");
        check(saved.Value.Resources.Count==2&&saved.Value.ResourceContainers.Count==2&&saved.Value.Resources.All(r=>r.Source?.Provider==SourceProvider.LocalFileSystem),"Every old occurrence receives a singleton container and portable local source");
        check(saved.Value.Resources[0].DisplayNameOverride=="First"&&!saved.Value.Resources[1].IncludeChildren&&saved.Value.Resources.All(r=>r.ResourceId=="duplicate"),"Old aliases, options, duplicate IDs and paths survive conversion");
        var priorIds=saved.Value.Resources.Select(r=>r.PublicId).ToArray();
        Guid priorContainer=saved.Value.Resources[0].ContainerId;
        saved.Value.Resources[0].DisplayNameOverride="Renamed first";
        await state.SaveScopeAsync(saved.Value,saved.Token,Guid.NewGuid());saved=(await state.ReadScopeAsync(1))!;
        check(saved.Value.ResourceContainers.Single(c=>c.ContainerId==priorContainer).Name=="Renamed first"&&saved.Value.Resources[0].PublicId==priorIds[0],
            "A singleton alias edit updates its saved container label without changing resource or container identity");
        var alternate=new ScopedResource{Kind=ResourceKind.Folder,Path="https://example.test/team/repo.git",ContainerId=saved.Value.Resources[0].ContainerId,
            Source=new ResourceSourceDefinition{Provider=SourceProvider.Git,Endpoint="https://example.test/team/repo.git",Ref="main"}};
        saved.Value.Resources.Add(alternate);
        saved.Value.Resources[0].DisplayNameOverride="Local alternative label";
        await state.SaveScopeAsync(saved.Value,saved.Token,Guid.NewGuid());
        var changed=(await state.ReadScopeAsync(1))!;
        check(changed.Value.Resources.Count==3&&changed.Value.ResourceContainers.Count==2&&changed.Value.Resources.Take(2).Select(r=>r.PublicId).SequenceEqual(priorIds),"Saving alternatives preserves original occurrence keys and inactive members");
        check(changed.Value.Resources[2].Source?.Ref=="main","Provider selector survives SQL roundtrip");
        check(changed.Value.ResourceContainers.Single(c=>c.ContainerId==priorContainer).Name=="Renamed first",
            "Individual alternative labels do not rename a multi-member container");
        var selectedSource=changed.Value.Resources[2].Source!;
        var candidatePage=await new ExplorerResourceCatalogue(session).ListAsync(new ExplorerScope(
            new(session.Epoch,1,1,"","","",true,[],[],false),"different-target-scope","Target",[],[],"en-US"));
        check(candidatePage.Items.Single(c=>c.Header.SourceConfiguration?.Provider==SourceProvider.Git).Header.SourceConfiguration?.SourceId==selectedSource.SourceId,
            "Bounded SQL resource catalogue returns the remote descriptor with its selected header");
        var candidate=new ExistingScopeResourceCandidate("Repo","Git","","Another scope",ResourceKind.Folder,alternate.Path,"","",true,selectedSource);
        var candidateCopy=candidate.CreateScopedResource();
        check(candidateCopy.Source?.SourceId==selectedSource.SourceId&&!ReferenceEquals(candidateCopy.Source,selectedSource)&&candidateCopy.PublicId!=alternate.PublicId,"Catalogue copies retain source configuration and allocate a new resource occurrence");
        check(!ExplorerResourceCatalogue.SameIdentity(ResourceKind.Folder,"https://example.test/Repo",ResourceKind.Folder,"https://example.test/repo")&&
            ExplorerResourceCatalogue.SameIdentity(ResourceKind.Folder,alternate.Path,ResourceKind.Folder,"ignored-cache-path",selectedSource,candidateCopy.Source),"Remote catalogue matching preserves URI case and compares configured source identity");
        string sourceLink=$"source://{alternate.PublicId:N}/src/Example%20File.cs",liveLink=$"livesql://{Guid.NewGuid():N}/U/dbo/Orders";
        string runtimeCache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Surf2","SourceCache","fixture","Example.cs");
        PortableResourcePath.Validate($"source://{alternate.PublicId:N}/src/");
        PortableResourcePath.Validate(sourceLink+":42:[Column=Value]");
        PortableResourcePath.Validate(CodeSearchReferenceService.Encode(new(sourceLink+":42","needle")));
        check(true,"Portable link validation accepts typed directories, line/filter suffixes and encoded search references");
        bool wrappedCacheRejected=false;try{PortableResourcePath.Validate(CodeSearchReferenceService.Encode(new(runtimeCache,"needle")));}catch(InvalidDataException){wrappedCacheRejected=true;}
        bool wrappedTraversalRejected=false;try{PortableResourcePath.Validate(CodeSearchReferenceService.Encode(new($"source://{alternate.PublicId:N}/%2E%2E/file:3","needle")));}catch(InvalidDataException){wrappedTraversalRejected=true;}
        check(wrappedCacheRejected&&wrappedTraversalRejected,"Encoded search references cannot conceal cache paths or traversal segments");
        await state.CreateWorkspaceAsync(new WorkspaceState { OpenDocuments=[new(){FilePath=runtimeCache,PersistentSourcePath=sourceLink},new(){FilePath=liveLink}]},Guid.NewGuid());
        var workbench=new WorkbenchState {ScopeId="legacy",ActiveDocumentPath=sourceLink,OpenDocuments=[new(){FilePath=sourceLink},new(){FilePath=liveLink}],
            ReferenceConnectionLines=[new(){SourceFilePath=sourceLink,TargetFilePath=liveLink}]};
        var workbenchToken=await state.CreateWorkbenchAsync(new(workbench,new Dictionary<int,byte[]>()),0,Guid.NewGuid());
        check((await state.ReadWorkspaceAsync())!.Value.OpenDocuments.Select(d=>d.FilePath).SequenceEqual(new[]{sourceLink,liveLink}),"Workspace saves durable source and live SQL links without runtime cache paths");
        bool cacheRejected=false;try{StateCopies.Window(new(){FilePath=runtimeCache},new(new()));}catch(InvalidDataException){cacheRejected=true;}
        check(cacheRejected,"State publication rejects a raw managed-cache path when portable identity is absent");
        changed.Value.VirtualFolders.Add(new(){ParentNodeKey=$"source://{alternate.PublicId:N}/",ChildNodeKeys=[sourceLink]});
        await state.SaveScopeAsync(changed.Value,changed.Token,Guid.NewGuid());
        string currentPackage=Path.Combine(directory,"schema3.zip");
        var exported=await new RelationalPackageExporter(session).ExportAsync(currentPackage,new(){AllowBlockingConsistentFallback=true});
        check(exported.Manifest.PackageVersion==3&&exported.Manifest.DatabaseSchemaVersion==3,"Current exporter emits package3/schema3");
        await using(var restored=await SqlFixture.CreateAsync())
        {
            var import=await new RelationalPackageImporter(restored.DestinationConnectionString).ImportAsync(currentPackage);
            var restoredScope=(await new RelationalStateStore(new(restored.DestinationConnectionString)).ReadScopeAsync(1))!;
            check(import.DatabaseIdentity!=database&&restoredScope.Value.Resources.Select(r=>r.PublicId).SequenceEqual(changed.Value.Resources.Select(r=>r.PublicId)),"Package3 regenerates database identity while preserving member public IDs");
            var restoredState=new RelationalStateStore(new(restored.DestinationConnectionString));
            var restoredWorkbench=(await restoredState.ReadWorkbenchAsync(workbenchToken.Key))!.Value.Workbench;
            check(restoredWorkbench.ActiveDocumentPath==sourceLink&&restoredWorkbench.ReferenceConnectionLines.Single().TargetFilePath==liveLink&&
                restoredScope.Value.VirtualFolders.Single().ChildNodeKeys.Single()==sourceLink&&
                (await restoredState.ReadWorkspaceAsync())!.Value.OpenDocuments.All(d=>!d.FilePath.Contains("SourceCache",StringComparison.Ordinal)),
                "Package3 roundtrips portable document, reference, active-document and virtual-folder links without a cache location");
        }
        await using(var converted=await SqlFixture.CreateAsync())
        {
            await new RelationalPackageImporter(converted.DestinationConnectionString).ImportAsync(oldPackage);
            var convertedScope=(await new RelationalStateStore(new(converted.DestinationConnectionString)).ReadScopeAsync(1))!;
            check(convertedScope.Value.ResourceContainers.Count==2&&(await new PersistenceFormatProbe().ProbeAsync(converted.DestinationConnectionString)).SchemaVersion==3,"Old package2 imports through unpublished schema2 conversion and publishes schema3");
            check((await new RelationalStateStore(new(converted.DestinationConnectionString)).ReadSettingsAsync())!.Value.CodeWindows.DefaultDarkBackcolor=="#112233",
                "Package2 compatibility adapter preserves known additive preference values");
        }
        check(await new RelationalResourceSchemaUpgrade().UpgradeAsync(old.DestinationConnectionString,recoveryDirectory:directory)==null,"Repeated schema3 upgrade is a no-op");
        await old.VerifySourceUnchangedAsync(check);
    }
}

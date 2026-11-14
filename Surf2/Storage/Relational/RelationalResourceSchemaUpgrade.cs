using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational.Packages;

namespace Surf2.Storage.Relational;

/// <summary>Explicit local startup upgrade. The caller must stop its workspace and close other Surf2 instances.</summary>
public sealed class RelationalResourceSchemaUpgrade
{
    public async Task<string?> UpgradeAsync(string connectionString,IProgress<string>? progress=null,
        CancellationToken cancellationToken=default,string? recoveryDirectory=null)
    {
        var ct=cancellationToken;
        var options=SqlServerConnectionOptions.FromConnectionString(connectionString);
        // Remove this process's unused startup probe connections before checking for other users.
        using(var pooled=new SqlConnection(options.ConnectionString))SqlConnection.ClearPool(pooled);
        // An unpooled, continuously open connection owns SQL Server's SINGLE_USER slot for the
        // entire recovery/export/upgrade interval. Old binaries cannot bypass this admission fence.
        await using var connection=new SqlConnection(new SqlConnectionStringBuilder(options.ConnectionString){Pooling=false}.ConnectionString);
        await connection.OpenAsync(ct);
        var probe=new PersistenceFormatProbe();
        var format=await probe.ProbeConnectionAsync(connection,ct);
        if(format.Format==PersistenceFormat.Relational && format.SchemaVersion==3)return null;
        if(format.SchemaVersion is not (1 or 2) || format.Format!=PersistenceFormat.Relational)
            throw new InvalidOperationException("Only a supported Ready database can be upgraded here. Resume incomplete imports with their original recovery workflow.");
        await using var lease=connection.CreateCommand();
        lease.CommandTimeout=15;
        lease.CommandText="""
DECLARE @Result int;
EXEC @Result=sys.sp_getapplock @Resource=N'Surf2.ResourceSchema.Upgrade',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=10000;
IF @Result<0 THROW 51124,'Another resource upgrade is active.',1;
IF COALESCE(IS_SRVROLEMEMBER('sysadmin'),0)<>1
 AND COALESCE(HAS_PERMS_BY_NAME(NULL,'SERVER','VIEW SERVER STATE'),0)<>1
 AND COALESCE(HAS_PERMS_BY_NAME(NULL,'SERVER','VIEW SERVER PERFORMANCE STATE'),0)<>1
 THROW 51125,'Upgrade requires permission to verify other local database sessions. Ask the database administrator to perform the upgrade.',1;
IF EXISTS(SELECT 1 FROM sys.dm_exec_sessions WHERE database_id=DB_ID() AND is_user_process=1 AND session_id<>@@SPID)
 THROW 51126,'Close other connections to this Surf2 database before upgrading.',1;
IF EXISTS(SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND user_access<>0)
 THROW 51133,'The database is already in restricted access mode. Restore MULTI_USER access before starting this upgrade.',1;
""";
        using var cancel=RelationalSession.CancelCommand(lease,ct);
        await lease.ExecuteNonQueryAsync(ct);
        string databaseSql="["+connection.Database.Replace("]", "]]",StringComparison.Ordinal)+"]";
        bool fenced=false,verifiedRecovery=false;string? recovery=null;Exception? failure=null;
        try
        {
        lease.CommandText="ALTER DATABASE "+databaseSql+" SET SINGLE_USER WITH NO_WAIT;";
        fenced=true; // Include an uncertain acknowledgement/cancellation in the access-mode recovery path.
        await lease.ExecuteNonQueryAsync(ct);
        progress?.Report("Exclusive database access acquired; other Surf2 clients cannot connect until upgrade finishes.");
        // Recheck the identity after admission is closed, before exporting or changing anything.
        var pinned=await probe.ProbeConnectionAsync(connection,ct);
        if(pinned.DatabaseIdentity!=format.DatabaseIdentity || pinned.SchemaVersion!=format.SchemaVersion || pinned.Format!=format.Format)
            throw new InvalidOperationException("The selected database changed while exclusive access was being acquired.");
        await ValidateLegacyPreferencesAsync(connection,ct);
        var session=new RelationalSession(options.ConnectionString);
        // A partially converted schema 1 has already raised its compatibility floor and cannot be exported as a normal schema-1 package.
        await using(var marker=connection.CreateCommand())
        {
            marker.CommandText="SELECT MinimumReaderVersion FROM surf.StorageFormatInfo WHERE Singleton=1;";
            int floor=Convert.ToInt32(await marker.ExecuteScalarAsync(ct));
            if(format.SchemaVersion==1 && floor==2)
            {
                progress?.Report("Resuming the interrupted captured-data conversion...");
                await new RelationalCaptureSchemaUpgrade().UpgradeOwnedAsync(connection,session,progress,ct);
                format=await probe.ProbeConnectionAsync(connection,ct);
            }
        }
        recoveryDirectory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Surf2","SchemaRecovery");
        Directory.CreateDirectory(recoveryDirectory);
        recovery=Path.Combine(recoveryDirectory,$"schema-{format.SchemaVersion}-{format.DatabaseIdentity:N}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.zip");
        progress?.Report("Exporting and verifying the pre-upgrade recovery package...");
        await new RelationalPackageExporter(new(options.ConnectionString),schemaVersion:format.SchemaVersion!.Value)
            .ExportExclusiveAsync(connection,recovery,ct);
        await new RelationalPackageImporter(options.ConnectionString).VerifyArchiveAsync(recovery,ct);
        verifiedRecovery=true;
        progress?.Report("Verified recovery package retained at "+recovery);
            if(format.SchemaVersion==1)await new RelationalCaptureSchemaUpgrade().UpgradeOwnedAsync(connection,session,progress,ct);
            progress?.Report("Upgrading resource containers and portable source identities...");
            await ApplyAsync(connection,format.DatabaseIdentity!.Value,allowUnpublished:false,ct);
        progress?.Report("Resource upgrade complete. Recovery package: "+recovery);
        return recovery;
        }
        catch(Exception error)
        {
            failure=error;
            if(verifiedRecovery&&recovery!=null)error.Data["SchemaRecoveryPackage"]=recovery;
            throw;
        }
        finally
        {
            if(fenced)
            {
                try
                {
                    await using var restore=connection.CreateCommand();restore.CommandTimeout=15;
                    restore.CommandText="ALTER DATABASE "+databaseSql+" SET MULTI_USER WITH NO_WAIT;";
                    await restore.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch(Exception releaseError)
                {
                    string instruction="The database remains SINGLE_USER. Close Surf2 and ask the database owner to restore MULTI_USER access before retrying. "+releaseError.Message;
                    if(failure!=null)failure.Data["UpgradeAdmissionRecovery"]=instruction;
                    else throw new InvalidOperationException(instruction,releaseError);
                }
            }
        }
    }

    internal static async Task ApplyAsync(SqlConnection connection,Guid expectedIdentity,bool allowUnpublished,CancellationToken ct)
    {
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandTimeout=120;
        using var cancel=RelationalSession.CancelCommand(command,ct);
        command.CommandText="SELECT SchemaVersion,State,DatabaseIdentity FROM surf.StorageFormatInfo WITH(UPDLOCK,HOLDLOCK) WHERE Singleton=1;";
        int version;
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct)||reader.GetGuid(2)!=expectedIdentity ||
                (reader.GetString(1)!="Ready" && !(allowUnpublished&&reader.GetString(1)=="Migrating")))
                throw new InvalidOperationException("The pinned upgrade destination changed.");
            version=reader.GetInt32(0);
        }
        if(version==3){await ValidateAsync(connection,transaction,ct);await transaction.CommitAsync(ct);return;}
        if(version!=2)throw new InvalidOperationException("Complete schema 1 to 2 before upgrading resource storage.");
        command.CommandText="SELECT COUNT_BIG(*) FROM surf.SchemaMigration WHERE SchemaVersion=2 AND ScriptChecksum=@Checksum;";
        command.Parameters.Add(RelationalSession.Parameter("@Checksum",SqlDbType.Binary,RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(2)),32));
        if(!Equals(await command.ExecuteScalarAsync(ct),1L))throw new InvalidDataException("Unsupported schema-2 checksum.");
        command.Parameters.Clear();
        await RelationalPreferenceSchema.InstallAsync(connection,transaction,ct);
        command.CommandText=RelationalSchemaInstaller.ReadScripts(3)[^1];
        await command.ExecuteNonQueryAsync(ct);
        await ValidateAsync(connection,transaction,ct);
        command.CommandText="""
INSERT surf.SchemaMigration(SchemaVersion,ScriptChecksum) VALUES(3,@Checksum);
UPDATE surf.StorageFormatInfo SET SchemaVersion=3,MinimumReaderVersion=3,MinimumWriterVersion=3 WHERE Singleton=1 AND DatabaseIdentity=@Identity;
UPDATE surf.IndexCatalogueHead SET Generation=Generation+1 WHERE Singleton=1;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Checksum",SqlDbType.Binary,RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(3)),32));
        command.Parameters.Add(RelationalSession.Parameter("@Identity",SqlDbType.UniqueIdentifier,expectedIdentity));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal static async Task ValidateAsync(SqlConnection c,SqlTransaction t,CancellationToken ct)
    {
        await using var command=c.CreateCommand();command.Transaction=t;
        command.CommandText="""
IF EXISTS(SELECT 1 FROM surf.ResourceContainer c WHERE NOT EXISTS
 (SELECT 1 FROM surf.ScopeResource r WHERE r.ScopeKey=c.ScopeKey AND r.ContainerId=c.ContainerId AND r.PublicId=c.DefaultResourceId))
 THROW 51127,'A resource container has no valid default member.',1;
IF EXISTS(SELECT 1 FROM surf.ScopeResource r WHERE r.PairedResourceId IS NOT NULL AND NOT EXISTS
 (SELECT 1 FROM surf.ScopeResource p WHERE p.PublicId=r.PairedResourceId AND p.ScopeKey=r.ScopeKey AND p.ContainerId=r.ContainerId))
 THROW 51128,'A resource pair refers outside its container.',1;
IF EXISTS(SELECT 1 FROM surf.ScopeResource WHERE PublicId=CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000'))
 OR EXISTS(SELECT 1 FROM surf.ResourceContainer WHERE ContainerId=CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000'))
 OR EXISTS(SELECT 1 FROM surf.SourceDefinition WHERE SourceId=CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000'))
 THROW 51129,'Portable resource identities must be nonempty.',1;
IF EXISTS(SELECT 1 FROM surf.ScopeResource r LEFT JOIN surf.SourceDefinition s ON s.SourceId=r.SourceId
 WHERE r.Kind=4 AND (s.SourceId IS NULL OR s.Provider<>3))
 THROW 51130,'Live database resources require a LiveSql source.',1;
""";
        using var cancel=RelationalSession.CancelCommand(command,ct);await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ValidateLegacyPreferencesAsync(SqlConnection connection,CancellationToken ct)
    {
        await using var command=connection.CreateCommand();
        command.CommandText="""
DECLARE @Known TABLE(ObjectName nvarchar(128),ColumnName nvarchar(128),TypeName nvarchar(128),Bytes smallint);
INSERT @Known VALUES(N'ApplicationPreference',N'DefaultDarkBackcolor',N'nvarchar',-1),
 (N'ApplicationPreference',N'OpenDiagramDocumentationOnDoubleClick',N'bit',1),
 (N'ApplicationPreference',N'ShowExplorerPreview',N'bit',1),(N'ExtensionAppearance',N'DarkBackcolor',N'nvarchar',-1);
DECLARE @Count int=(SELECT COUNT(*) FROM @Known k JOIN sys.columns c ON c.object_id=OBJECT_ID(N'surf.'+k.ObjectName) AND c.name=k.ColumnName);
IF @Count NOT IN(0,4) THROW 51131,'Unsupported partial preference schema; no upgrade was performed.',1;
IF EXISTS(SELECT 1 FROM @Known k JOIN sys.columns c ON c.object_id=OBJECT_ID(N'surf.'+k.ObjectName) AND c.name=k.ColumnName
 WHERE TYPE_NAME(c.system_type_id)<>k.TypeName OR c.max_length<>k.Bytes OR c.is_nullable<>0)
 THROW 51132,'Unsupported preference column shape; no upgrade was performed.',1;
""";
        using var cancel=RelationalSession.CancelCommand(command,ct);await command.ExecuteNonQueryAsync(ct);
    }
}

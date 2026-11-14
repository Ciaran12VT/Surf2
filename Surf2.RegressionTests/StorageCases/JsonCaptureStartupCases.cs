using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.State;
using Surf2.Storage.Relational.Packages;
using Surf2.Models;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckJsonCaptureUpgradeAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        // Create the actual frozen v1 schema. Do not disguise a newer schema by downgrading its marker.
        foreach(string script in RelationalSchemaInstaller.ReadScripts(1))await fixture.DestinationSqlAsync(script);
        var state = new RelationalStateStore(new RelationalSession(fixture.DestinationConnectionString));
        await fixture.DestinationSqlAsync("""
INSERT surf.StorageFormatInfo(Singleton,FormatIdentifier,SchemaVersion,MinimumReaderVersion,MinimumWriterVersion,State,DatabaseIdentity)
VALUES(1,N'Surf2.RelationalV2',1,1,1,'Ready',NEWID());
INSERT surf.SchemaMigration(SchemaVersion,ScriptChecksum) VALUES(1,@Checksum);
""", RelationalSession.Parameter("@Checksum", SqlDbType.Binary,
            RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(1)), 32));
        await using(var seed=new SqlConnection(fixture.DestinationConnectionString))
        {
            await seed.OpenAsync();await using var transaction=(SqlTransaction)await seed.BeginTransactionAsync();
            // These are the recognized additive preference fields; resource and JSON capture tables remain absent.
            await RelationalPreferenceSchema.InstallAsync(seed,transaction,CancellationToken.None);
            var settings=new AppSettings();settings.EnsureDefaults();
            settings.Diagnostics.EnableInternalLogging=false;settings.LoadMostRecentWorkbenchOnStartup=false;
            await state.ImportSettingsAsync(seed,transaction,settings,Guid.NewGuid());
            await state.ImportScopeSelectionAsync(seed,transaction,1,null,Guid.NewGuid());
            await state.ImportWorkspaceAsync(seed,transaction,new WorkspaceState(),Guid.NewGuid());
            await transaction.CommitAsync();
        }
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_CaptureStartup_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        // Return idle seed connections before the upgrader's no-other-session admission check.
        using(var seededPool=new SqlConnection(fixture.DestinationConnectionString))SqlConnection.ClearPool(seededPool);
        string recoveryDirectory=Path.Combine(fixture.OwnedDirectory,"startup-recovery");
        var cancelledMain=new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection))
        {RelationalResourceUpgradeConfirmation=_=>false,RelationalResourceUpgradeRecoveryDirectory=recoveryDirectory};
        try
        {
            await ThrowsAsync<OperationCanceledException>(()=>((Task<bool>)RuntimeWpfInvoke(cancelledMain,"TryLoadRelationalPersistenceAsync")!).WaitAsync(TimeSpan.FromSeconds(60)),
                check,"Declining startup upgrade stops loading without silently converting the database");
            check((await new PersistenceFormatProbe().ProbeAsync(connection)).SchemaVersion==1&&!Directory.Exists(recoveryDirectory),
                "Declined upgrade preserves schema1 and creates no recovery/export files");
        }
        finally
        {
            await RuntimeWpfInvokeTaskAsync(cancelledMain,"DisposeRelationalPersistenceAsync");
            RuntimeWpfSetField(cancelledMain,"_shutdownSaveCompleted",true);cancelledMain.Close();
        }
        int confirmations=0;
        for (int startup = 0; startup < 2; startup++)
        {
            var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection))
            {RelationalResourceUpgradeConfirmation=_=>{confirmations++;return true;},RelationalResourceUpgradeRecoveryDirectory=recoveryDirectory};
            try
            {
                using var trace = new StateAccessSqlTrace(connection);
                var load = (Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!;
                check(await load.WaitAsync(TimeSpan.FromSeconds(60)) &&
                    RuntimeWpfField<object>(main, "_isPersistenceHydrated") is true,
                    startup == 0 ? "Explicitly confirmed WPF startup upgrades genuine schema1 to schema3 and loads the workspace" :
                    "The upgraded database opens normally on the following startup");
                if (startup == 0)
                {
                    var commands = trace.Commands.ToList();
                    int upgrade = commands.FindIndex(command => command.Text.Contains("CREATE TABLE SURF.FULLTABLEDATA", StringComparison.Ordinal));
                    int release=commands.FindIndex(command=>command.Text.Contains("SET MULTI_USER",StringComparison.Ordinal));
                    int preferences = commands.FindIndex(Math.Max(0,release+1),command => Regex.IsMatch(command.Text, @"\bFROM\s+\[?SURF\]?\.\[?APPLICATIONPREFERENCE\]?\b"));
                    check(upgrade >= 0 && release>upgrade && preferences > release,
                        "Runtime preference loading begins after the exclusive upgrade releases database admission");
                    string package=Directory.GetFiles(recoveryDirectory,"*.zip").Single();
                    var manifest=await new RelationalPackageImporter(connection).VerifyArchiveAsync(package);
                    check(manifest.DatabaseSchemaVersion==1&&manifest.PackageVersion==2,
                        "Startup retains a verified package of the original schema1 database in the selected recovery directory");
                }
                else
                    check(trace.Commands.All(command => !Regex.IsMatch(command.Text, @"\b(?:INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|MERGE)\b")),
                        "An already upgraded startup performs read-only persistence loading without schema mutations");
            }
            finally
            {
                await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync");
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                main.Close();
            }
        }
        var format = await new PersistenceFormatProbe().ProbeAsync(connection);
        check(format.SchemaVersion == PersistenceFormatProbe.SupportedSchemaVersion && format.Format == PersistenceFormat.Relational,
            "Confirmed startup publishes the supported Ready schema marker");
        check(confirmations==1,"An already upgraded database does not ask for another upgrade confirmation");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

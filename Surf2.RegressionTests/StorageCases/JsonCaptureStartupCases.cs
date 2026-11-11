using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckJsonCaptureUpgradeAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeNewDatabaseAsync(fixture.DestinationConnectionString);
        var state = new RelationalStateStore(new RelationalSession(fixture.DestinationConnectionString));
        var settings = Required(await state.ReadSettingsAsync(), "startup capture fixture settings");
        settings.Value.Diagnostics.EnableInternalLogging = false;
        settings.Value.LoadMostRecentWorkbenchOnStartup = false;
        await state.SaveSettingsAsync(settings.Value, settings.Token, Guid.NewGuid());
        // The original five schema scripts are identical. Removing the added
        // table and restoring its marker produces the previous empty database shape.
        await fixture.DestinationSqlAsync("""
DROP TABLE surf.FullTableData;
DELETE FROM surf.SchemaMigration;
INSERT surf.SchemaMigration(SchemaVersion,ScriptChecksum) VALUES(1,@Checksum);
UPDATE surf.StorageFormatInfo SET SchemaVersion=1,MinimumReaderVersion=1,MinimumWriterVersion=1;
""", RelationalSession.Parameter("@Checksum", SqlDbType.Binary,
            RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts(1)), 32));
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_CaptureStartup_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        for (int startup = 0; startup < 2; startup++)
        {
            var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
            try
            {
                using var trace = new StateAccessSqlTrace(connection);
                var load = (Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!;
                check(await load.WaitAsync(TimeSpan.FromSeconds(60)) &&
                    RuntimeWpfField<object>(main, "_isPersistenceHydrated") is true,
                    startup == 0 ? "Normal WPF startup automatically upgrades a v1 database and loads the workspace" :
                    "The upgraded database opens normally on the following startup");
                if (startup == 0)
                {
                    var commands = trace.Commands.ToList();
                    int upgrade = commands.FindIndex(command => command.Text.Contains("CREATE TABLE SURF.FULLTABLEDATA", StringComparison.Ordinal));
                    int preferences = commands.FindIndex(command => Regex.IsMatch(command.Text, @"\bFROM\s+SURF\.APPLICATIONPREFERENCE\b"));
                    check(upgrade >= 0 && preferences > upgrade,
                        "Startup completes the schema upgrade before reading saved application preferences");
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
            "Automatic startup publishes the supported Ready schema marker");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

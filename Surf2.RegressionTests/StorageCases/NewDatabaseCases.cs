using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.State;
using Surf2.Storage.Relational.Migration;

public static partial class StorageRegressionSuite
{
    public static async Task RunNewDatabaseChecksAsync(Action<bool, string> check)
    {
        check(RelationalSchemaInstaller.ScriptChecksum(["SELECT 1;\r\nSELECT 2;\r\n"])
            .SequenceEqual(RelationalSchemaInstaller.ScriptChecksum(["SELECT 1;\nSELECT 2;\n"])),
            "Schema checksums are independent of source checkout line endings");
        await VerifyMalformedFormatMarkersAsync(check);
        await using (var fixture = await SqlFixture.CreateAsync())
        {
            var installer = new RelationalSchemaInstaller();
            var leaseSession = new RelationalSession(fixture.DestinationConnectionString);
            await using (var firstLease = await MigrationTargetLease.AcquireAsync(leaseSession))
            {
                await ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await using var duplicate = await MigrationTargetLease.AcquireAsync(leaseSession);
                }, check, "Destination lease refuses concurrent conversion sessions");
            }
            await using (var released = await MigrationTargetLease.AcquireAsync(leaseSession))
                check(true, "Destination lease is released after the operation");
            await installer.InitializeNewDatabaseAsync(fixture.DestinationConnectionString);
            check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational,
                "Explicit new database publishes typed defaults atomically");
            var state = new RelationalStateStore(new(fixture.DestinationConnectionString));
            check((await state.ListScopesAsync()).Items.Count == 0 &&
                (await state.ListDiagramsAsync()).Items.Count == 0 &&
                (await state.ListWorkbenchesAsync()).Items.Count == 0,
                "New database catalogues are empty without legacy roots");
            var expected = new AppSettings();
            expected.EnsureDefaults();
            SameModel(expected, Required(await state.ReadSettingsAsync(), "default settings").Value,
                check, "New database settings preserve the application's current defaults");
            SameModel(new WorkspaceState(), Required(await state.ReadWorkspaceAsync(), "default workspace").Value,
                check, "New database has a typed default workspace");
            check(Required(await state.ReadScopeSelectionAsync(), "default selection").Value is { SchemaVersion: 1, LastActiveScopeId: null },
                "New database starts with no selected scope");
            await ThrowsAsync<InvalidOperationException>(() => installer.InitializeNewDatabaseAsync(fixture.DestinationConnectionString),
                check, "Explicit initializer cannot reset an existing Ready database");
            await ThrowsAsync<InvalidOperationException>(() => installer.InitializeNewDatabaseAsync(fixture.SourceConnectionString),
                check, "Explicit initializer cannot overwrite a legacy database");
            var pinned = new RelationalSession(fixture.DestinationConnectionString);
            await pinned.RequireReadyAsync();
            Guid originalIdentity = pinned.DatabaseIdentity!.Value;
            await fixture.DestinationSqlAsync("UPDATE surf.StorageFormatInfo SET DatabaseIdentity=NEWID();");
            await ThrowsAsync<RelationalSessionChangedException>(() => pinned.OpenAsync(), check,
                "A borrowed connection cannot reuse cached keys after database identity replacement");
            await fixture.DestinationSqlAsync("UPDATE surf.StorageFormatInfo SET DatabaseIdentity=@Identity;",
                RelationalSession.Parameter("@Identity", System.Data.SqlDbType.UniqueIdentifier, originalIdentity));
            await ThrowsAsync<RelationalSessionChangedException>(() => pinned.RequireReadyAsync(), check,
                "An invalidated session requires explicit reconnect even if the old identity is restored");
            await fixture.VerifySourceUnchangedAsync(check);
        }

        await using (var fixture = await SqlFixture.CreateAsync())
        {
            var installer = new RelationalSchemaInstaller();
            await ThrowsAsync<ArgumentException>(() => installer.InitializePackageDestinationAsync(
                fixture.DestinationConnectionString, Guid.Empty, fixture.Fingerprint), check,
                "Package initializer rejects an absent import identity");
            check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Empty,
                "Rejected package initialization leaves the destination empty");
            await installer.InitializePackageDestinationAsync(fixture.DestinationConnectionString,
                fixture.MigrationIdentity, fixture.Fingerprint);
            check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Incomplete,
                "Package destination remains unpublished pending import validation");
            await ThrowsAsync<InvalidOperationException>(() => new RelationalStateStore(new(fixture.DestinationConnectionString)).ReadSettingsAsync(),
                check, "Unvalidated package destination refuses application reads");
            await fixture.VerifySourceUnchangedAsync(check);
        }
    }

    private static async Task VerifyMalformedFormatMarkersAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await fixture.DestinationSqlAsync("EXEC(N'CREATE SCHEMA surf');");
        await fixture.DestinationSqlAsync("""
CREATE TABLE surf.StorageFormatInfo (
 Singleton tinyint NOT NULL, FormatIdentifier nvarchar(64) NOT NULL,
 SchemaVersion int NOT NULL, MinimumReaderVersion int NOT NULL, MinimumWriterVersion int NOT NULL,
 State varchar(24) NOT NULL, DatabaseIdentity uniqueidentifier NOT NULL);
INSERT surf.StorageFormatInfo VALUES(0,N'Surf2.Relational',1,1,1,'Ready',NEWID());
""");
        var probe = new PersistenceFormatProbe();
        check((await probe.ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Unsupported,
            "A forged Ready marker with another singleton cannot enable reads");
        await fixture.DestinationSqlAsync("""
UPDATE surf.StorageFormatInfo SET Singleton=1;
CREATE TABLE surf.SchemaMigration(SchemaVersion int NOT NULL, ScriptChecksum binary(32) NOT NULL);
INSERT surf.SchemaMigration VALUES(1,CONVERT(binary(32),0));
""");
        check((await probe.ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Unsupported,
            "A Ready marker with an unknown installed-schema checksum cannot enable reads");
        await fixture.DestinationSqlAsync("ALTER TABLE surf.StorageFormatInfo ALTER COLUMN FormatIdentifier nvarchar(max) NOT NULL;");
        check((await probe.ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Unsupported,
            "Unexpected marker LOB fields are rejected before value materialization");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

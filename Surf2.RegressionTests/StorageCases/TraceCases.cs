using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunRelationalTraceChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        var connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_Trace_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var seeded = await SeedQueryIntegrationAsync(fixture, session, content, new RelationalCaptureStore(session),
            new RelationalStateStore(session, content));
        var unrelated = new SqlDatabaseObject
        {
            Kind = SqlDatabaseObjectKind.StoredProcedure, SchemaName = "dbo", ObjectName = "UnrelatedProcedure",
            Definition = "SELECT N'" + new string('x', 65536) + "';"
        };
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            await writer.InsertCurrentObjectAsync(seeded.SnapshotKey, unrelated, 10);
            await writer.PublishSnapshotAsync(seeded.SnapshotKey, null);
        });
        var store = new RelationalSnapshotStore(session, content);
        await session.RequireReadyAsync();
        using (var readiness = new StateAccessSqlTrace(connection))
        {
            await session.RequireReadyAsync();
            check(readiness.Commands.Count <= 2 && readiness.Commands.Any(c => c.Text.Contains("M.SCRIPTCHECKSUM=@CHECKSUM", StringComparison.Ordinal)),
                "Pinned Ready checks retain identity and checksum validation without repeated format-metadata inspection");
        }
        await fixture.DestinationSqlAsync("UPDATE surf.StorageFormatInfo SET State='Migrating';");
        bool stateRejected = false;
        try { await session.RequireReadyAsync(); }
        catch (InvalidOperationException) { stateRejected = true; }
        check(stateRejected, "Pinned Ready checks reject a same-identity database moved back to Migrating");
        await fixture.DestinationSqlAsync("UPDATE surf.StorageFormatInfo SET State='Ready',MinimumReaderVersion=2;");
        bool versionRejected = false;
        try { await session.RequireReadyAsync(); }
        catch (InvalidOperationException) { versionRejected = true; }
        check(versionRejected, "Pinned Ready checks reject a reader-version change after initial validation");
        await fixture.DestinationSqlAsync("UPDATE surf.StorageFormatInfo SET MinimumReaderVersion=1;");
        await fixture.DestinationSqlAsync("UPDATE surf.SchemaMigration SET ScriptChecksum=HASHBYTES('SHA2_256',N'fixture changed checksum') WHERE SchemaVersion=1;");
        bool checksumRejected = false;
        try { await session.RequireReadyAsync(); }
        catch (InvalidOperationException) { checksumRejected = true; }
        check(checksumRejected, "Pinned Ready checks reject a schema-checksum change after initial validation");
        await fixture.DestinationSqlAsync("UPDATE surf.SchemaMigration SET ScriptChecksum=@Checksum WHERE SchemaVersion=1;",
            RelationalSession.Parameter("@Checksum", System.Data.SqlDbType.Binary,
                RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts()), 32));
        await session.RequireReadyAsync();
        var service = new SqlTraceService();
        const string rootSql = "SELECT * FROM dbo.Evidence; EXEC dbo.NeedleProc;";
        var expected = service.BuildTrace("Root.sql", rootSql, new DatabaseSnapshotLibrary
        {
            Snapshots = [new DatabaseMetadataSnapshot
            {
                SnapshotId = "query-snapshot", DisplayName = "Query snapshot",
                Objects = [seeded.ProcedureValue, unrelated]
            }]
        });
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var actual = await service.BuildRelationalTraceAsync("Root.sql", rootSql, store);
            check(actual == expected, "Relational SQL trace preserves existing CRUD, call ordering and report semantics");
            var definitions = trace.Commands.Where(c => c.Number("@Revision").HasValue &&
                c.Text.Contains("FROM SURF.DATABASEOBJECTREVISION O JOIN SURF.TEXTCONTENT", StringComparison.Ordinal)).ToArray();
            check(definitions.Length == 1 && definitions[0].Number("@Revision") == seeded.Procedure.RevisionKey,
                "SQL trace fetches only the followed procedure definition, not unrelated code or table data");
            StateAccessNoReads(trace.Commands, check, "SQL trace", "Asset", "DataValueException", "DiagramObject", "DocumentWindowState");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { await service.BuildRelationalTraceAsync("Root.sql", rootSql, store, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Cancelled SQL trace does not publish a partial report");
        }
        bool altered = false;
        using (var trace = new StateAccessSqlTrace(connection, command =>
        {
            if (altered || !command.Parameters.Contains("@Revision")) return;
            altered = true;
            using var fixtureConnection = new SqlConnection(fixture.DestinationConnectionString);
            fixtureConnection.Open();
            using var mutation = fixtureConnection.CreateCommand();
            mutation.CommandText = "UPDATE surf.DatabaseSnapshot SET DisplayName=DisplayName WHERE SnapshotKey=@Key;";
            mutation.Parameters.AddWithValue("@Key", seeded.SnapshotKey);
            mutation.ExecuteNonQuery();
        }))
        {
            bool rejected = false;
            try { await service.BuildRelationalTraceAsync("Root.sql", rootSql, store); }
            catch (SnapshotConcurrencyException) { rejected = true; }
            check(altered && rejected, "SQL trace rejects a current-head change before publishing its report");
        }
        var options = SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString);
        check(!await RelationalDatabaseBootstrap.CanProveDatabaseAbsentAsync(options),
            "New-database bootstrap never treats an existing fixture as absent");
        string absent = "Surf2_Regression_" + Guid.NewGuid().ToString("N");
        var absentOptions = SqlServerConnectionOptions.FromConnectionString(new SqlConnectionStringBuilder(connection)
        { InitialCatalog = absent }.ConnectionString);
        check(await RelationalDatabaseBootstrap.CanProveDatabaseAbsentAsync(absentOptions),
            "Privileged read-only bootstrap can prove a generated missing database absent without creating it");
        check((await new PersistenceFormatProbe().ProbeAsync(absentOptions.ConnectionString)).Format == PersistenceFormat.Unavailable,
            "Absence inspection leaves the generated missing database uncreated");
    }
}

using System.Data;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Packages;

public static partial class StorageRegressionSuite
{
    public static async Task RunPersistenceTransferChecksAsync(Action<bool, string> check)
    {
        foreach (string label in await LegacyPersistenceExportChecks.RunAsync()) check(true, label);
        var models = MigrationModels();
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        var request = MigrationRequest(fixture);
        await new RelationalMigrator().ConvertAsync(request);
        string local = Path.Combine(fixture.OwnedDirectory, "transfer-local");
        Directory.CreateDirectory(local);
        await File.WriteAllBytesAsync(Path.Combine(local, "notes.txt"), "fixture notes"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(local, "connection-settings.json"), "credential-canary"u8.ToArray());
        string legacyPackage = Path.Combine(fixture.OwnedDirectory, "legacy.surf2db.zip");
        var legacy = await LegacyPersistenceExport.ExportAsync(legacyPackage, fixture.SourceConnectionString, local,
            allowBlockingConsistentFallback: true);
        check(legacy.DocumentCount == 6 && legacy.LocalFileCount == 1,
            "Legacy UI exporter streams all six roots but excludes connection credentials");
        check((await RelationalPersistenceTransfer.InspectPackageAsync(legacyPackage)).Kind == PersistencePackageKind.Legacy,
            "Transfer inspection recognizes the UI legacy export");
        using (var archive = ZipFile.OpenRead(legacyPackage))
            check(archive.GetEntry("local-files/notes.txt") != null && archive.GetEntry("local-files/connection-settings.json") == null,
                "Legacy UI export carries selected local evidence without saved connection settings");
        byte[] legacyHash = SHA256.HashData(await File.ReadAllBytesAsync(legacyPackage));
        await ThrowsAsync<IOException>(() => LegacyPersistenceExport.ExportAsync(legacyPackage,
            fixture.SourceConnectionString, local, allowBlockingConsistentFallback: true), check,
            "Legacy UI export refuses replacement without consent");
        check(SHA256.HashData(await File.ReadAllBytesAsync(legacyPackage)).SequenceEqual(legacyHash),
            "Refused legacy export preserves the completed package");

        var current = SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString);
        var exporter = new RelationalPackageExporter(new RelationalSession(current.ConnectionString));
        string relationalPackage = Path.Combine(fixture.OwnedDirectory, "relational.surf2db.zip");
        await exporter.ExportAsync(relationalPackage, new(AllowBlockingConsistentFallback: true));
        check((await RelationalPersistenceTransfer.InspectPackageAsync(relationalPackage)).Kind == PersistencePackageKind.Relational,
            "Transfer inspection recognizes the Ready relational UI package");

        var ownedNames = new HashSet<string>(StringComparer.Ordinal);
        string AllocateName()
        {
            string name = "Surf2_Regression_" + Guid.NewGuid().ToString("N");
            ownedNames.Add(name); return name;
        }
        var masterOptions = new SqlConnectionStringBuilder(current.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var master = new SqlConnection(masterOptions.ConnectionString);
        await master.OpenAsync();
        async Task<bool> ExistsAsync(string name)
        {
            await using var command = master.CreateCommand();
            command.CommandText = "SELECT CASE WHEN DB_ID(@Name) IS NULL THEN 0 ELSE 1 END;";
            command.Parameters.Add(RelationalSession.Parameter("@Name", SqlDbType.NVarChar, name, 128));
            return Equals(await command.ExecuteScalarAsync(), 1);
        }
        var transfer = new RelationalPersistenceTransfer();
        try
        {
            await ThrowsAsync<SqlException>(() => transfer.ImportIntoNewDatabaseAsync(relationalPackage,
                current, new SqlConnectionStringBuilder(fixture.SourceConnectionString).InitialCatalog, local), check,
                "UI import refuses an existing database rather than overwriting its legacy contents");
            string bad = Path.Combine(fixture.OwnedDirectory, "malformed.zip");
            using (var archive = ZipFile.Open(bad, ZipArchiveMode.Create)) archive.CreateEntry("not-a-manifest");
            string missing = AllocateName();
            await ThrowsAsync<InvalidDataException>(() => transfer.ImportIntoNewDatabaseAsync(bad, current, missing, local),
                check, "UI transfer rejects a malformed package before creating its target");
            check(!await ExistsAsync(missing), "Malformed UI import leaves its proposed destination uncreated");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); string cancelled = AllocateName();
                await ThrowsAsync<OperationCanceledException>(() => transfer.ImportIntoNewDatabaseAsync(relationalPackage,
                    current, cancelled, local, ct: cancellation.Token), check, "Pre-cancelled UI import does not create its destination");
                check(!await ExistsAsync(cancelled), "Pre-cancelled UI import leaves its generated target absent");
            }
            bool leaseProven = false;
            var progress = new TransferProgress(message =>
            {
                if (!message.StartsWith("Importing", StringComparison.Ordinal)) return;
                try
                {
                    using var write = new FileStream(relationalPackage, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException) { leaseProven = true; }
            });
            string relationalName = AllocateName();
            var imported = await transfer.ImportIntoNewDatabaseAsync(relationalPackage, current, relationalName, local, progress);
            var probe = await new PersistenceFormatProbe().ProbeAsync(imported.Destination.ConnectionString);
            check(leaseProven && imported.DatabaseName == relationalName && probe.Format == PersistenceFormat.Relational &&
                probe.DatabaseIdentity == imported.DatabaseIdentity && imported.RecoveryDirectory == null,
                "UI relational transfer pins the immutable package and returns a separate validated Ready database");
            await VerifyMigratedModelsAsync(new RelationalSession(imported.Destination.ConnectionString), models, check);
            string legacyName = AllocateName();
            var converted = await transfer.ImportIntoNewDatabaseAsync(legacyPackage, current, legacyName, local);
            check(converted.DatabaseName == legacyName && converted.RecoveryDirectory != null &&
                (await new PersistenceFormatProbe().ProbeAsync(converted.Destination.ConnectionString)).Format == PersistenceFormat.Relational,
                "UI legacy transfer converts into a separate Ready target with preserved recovery evidence");
            await VerifyMigratedModelsAsync(new RelationalSession(converted.Destination.ConnectionString), models, check);
            check(current.ConnectionString == fixture.DestinationConnectionString &&
                (await new PersistenceFormatProbe().ProbeAsync(current.ConnectionString)).Format == PersistenceFormat.Relational,
                "Both UI transfer formats leave their original active connection and database unchanged");
            await fixture.VerifySourceUnchangedAsync(check);
        }
        finally
        {
            foreach (string name in ownedNames)
            {
                if (!name.StartsWith("Surf2_Regression_", StringComparison.Ordinal) || name.Length != 49 ||
                    !Guid.TryParseExact(name[17..], "N", out _))
                    throw new InvalidOperationException("Refusing cleanup of a non-owned transfer fixture.");
                if (!await ExistsAsync(name)) continue;
                SqlConnection.ClearPool(new SqlConnection(new SqlConnectionStringBuilder(current.ConnectionString) { InitialCatalog = name }.ConnectionString));
                await using var command = master.CreateCommand(); command.CommandTimeout = 120;
                command.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
                await command.ExecuteNonQueryAsync();
            }
        }
    }
    private sealed class TransferProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

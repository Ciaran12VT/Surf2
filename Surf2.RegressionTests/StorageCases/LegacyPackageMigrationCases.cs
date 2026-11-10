using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunLegacyPackageMigrationChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        var models = MigrationModels();
        int imageOrdinal = models.Diagrams.Diagrams[0].Objects.FindIndex(item => item.ObjectType == DiagramObjectType.Image);
        models.Diagrams.Diagrams[0].Objects[imageOrdinal].PastedImageFileName = "package-portable.png";
        byte[] png = Png(87);
        var roots = models.Documents();
        var databaseRoots = roots.Where(item => item.Key != "app-settings").ToDictionary();
        string package = Path.Combine(fixture.OwnedDirectory, "legacy-package.zip");
        Directory.CreateDirectory(fixture.OwnedDirectory);
        var localFiles = new Dictionary<string, byte[]>
        {
            ["settings.json"] = System.Text.Encoding.UTF8.GetBytes(roots["app-settings"]),
            ["database-snapshots.json"] = System.Text.Encoding.UTF8.GetBytes("not authoritative: deliberately invalid JSON"),
            ["PastedDiagramImages/package-portable.png"] = png,
            ["connection-settings.json"] = System.Text.Encoding.UTF8.GetBytes("credential-canary-never-restored")
        };
        await WriteLegacyFixturePackageAsync(package, databaseRoots, localFiles);
        byte[] originalHash = await LegacySourceStage.HashFileAsync(package);
        string forbiddenLocal = Path.Combine(fixture.OwnedDirectory, "must-not-read-or-write");
        string staging = Path.Combine(fixture.OwnedDirectory, "package-staging");
        var request = new RelationalMigrationRequest("not a SQL connection string", fixture.DestinationConnectionString,
            forbiddenLocal, staging, SourcePackagePath: package);
        var result = await new RelationalMigrator().ConvertAsync(request);
        check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational,
            "Legacy package conversion publishes without any SQL source connection");
        check(!Directory.Exists(forbiddenLocal), "Legacy package migration never reads or restores global AppData");
        await using (var stage = await LegacySourceStage.ReopenAsync(result.StageDirectory))
        {
            check(stage.SourceOrigin == "Package" && stage.StagedDocuments.Values.All(item => item.SourceKind == "Package"),
                "Package manifest freezes database documents and package-local fallback roots");
            foreach (var (key, json) in roots)
            {
                check(await File.ReadAllTextAsync(stage.StagedDocuments[key].FilePath) == json,
                    "Package decodes the authoritative JSON string exactly: " + key);
            }
            check(!Directory.EnumerateFiles(stage.DirectoryPath, "connection-settings.json", SearchOption.AllDirectories).Any(),
                "Package credentials are never extracted into staging");
            await stage.VerifyUnchangedAsync();
        }
        await VerifyMigratedModelsAsync(new(fixture.DestinationConnectionString), models, check);
        byte[] finalHash = await LegacySourceStage.HashFileAsync(package);
        check(CryptographicOperations.FixedTimeEquals(originalHash, finalHash),
            "Legacy package migration preserves the original archive bytes");
        var repeated = await new RelationalMigrator().ConvertAsync(request with { ResumeStageDirectory = result.StageDirectory });
        check(repeated.AlreadyCompleted, "Matching completed package migration can be verified idempotently");
        var state = new RelationalStateStore(new(fixture.DestinationConnectionString));
        long diagramKey = (await state.ListDiagramsAsync()).Items.Single().Token.Key;
        long workbenchKey = (await state.ListWorkbenchesAsync()).Items.Single().Token.Key;
        File.Delete(package);
        check((await state.ReadDiagramAsync(diagramKey))!.Value.PastedImages[imageOrdinal].SequenceEqual(png) &&
            (await state.ReadWorkbenchAsync(workbenchKey))!.Value.PastedImages[imageOrdinal].SequenceEqual(png),
            "Converted diagram/workbench images do not depend on the legacy archive or its source device");
        await fixture.VerifySourceUnchangedAsync(check);

        await using var rejectedFixture = await SqlFixture.CreateAsync();
        Directory.CreateDirectory(rejectedFixture.OwnedDirectory);
        string malicious = Path.Combine(rejectedFixture.OwnedDirectory, "traversal.zip");
        await WriteLegacyFixturePackageAsync(malicious, databaseRoots,
            new Dictionary<string, byte[]> { ["../outside.png"] = png });
        Exception? rejection = null;
        try
        {
            await new RelationalMigrator().ConvertAsync(new("not a SQL connection string", rejectedFixture.DestinationConnectionString,
                forbiddenLocal, Path.Combine(rejectedFixture.OwnedDirectory, "staging"), SourcePackagePath: malicious));
        }
        catch (InvalidDataException error) { rejection = error; }
        check(rejection != null, "Legacy ZIP path traversal is rejected during source preflight");
        check((await new PersistenceFormatProbe().ProbeAsync(rejectedFixture.DestinationConnectionString)).Format == PersistenceFormat.Empty,
            "Rejected legacy package cannot initialize or publish the destination");
        await rejectedFixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task WriteLegacyFixturePackageAsync(string path, IReadOnlyDictionary<string, string> roots,
        IReadOnlyDictionary<string, byte[]> localFiles)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await using (var entry = zip.CreateEntry("manifest.json").Open())
            await JsonSerializer.SerializeAsync(entry, new
            {
                FormatVersion = 1, ExportedAtUtc = EvidenceTime.ToUniversalTime(), AppName = "Surf2", AppVersion = "fixture",
                DatabaseName = "fixture-only", DocumentCount = roots.Count, LocalFileCount = localFiles.Count
            });
        await using (var entry = zip.CreateEntry("database/surf2-documents.json").Open())
            await JsonSerializer.SerializeAsync(entry, roots.Select(item => new
            {
                DocumentKey = item.Key, PayloadJson = item.Value,
                UpdatedAtUtc = DateTime.SpecifyKind(EvidenceTime.UtcDateTime, DateTimeKind.Unspecified)
            }));
        foreach (var (name, bytes) in localFiles)
        {
            await using var entry = zip.CreateEntry("local-files/" + name).Open();
            await entry.WriteAsync(bytes);
        }
    }
}

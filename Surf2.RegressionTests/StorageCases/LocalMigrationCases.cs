using System.IO;
using System.Security.Cryptography;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunLocalMigrationChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        var models = MigrationModels();
        var diagram = models.Diagrams.Diagrams[0];
        int imageOrdinal = diagram.Objects.FindIndex(item => item.ObjectType == DiagramObjectType.Image);
        if (imageOrdinal < 0) throw new InvalidOperationException("Missing local image fixture.");
        diagram.Objects[imageOrdinal].PastedImageFileName = "local-portable.png";
        byte[] imageBytes = Png(73);
        string local = Path.Combine(fixture.OwnedDirectory, "local-origin");
        string staging = Path.Combine(fixture.OwnedDirectory, "local-staging");
        string imagePath = Path.Combine(local, "PastedDiagramImages", "local-portable.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, json) in models.Documents())
        {
            string file = Path.Combine(local, LegacySourceStage.Documents[key].FileName);
            await File.WriteAllTextAsync(file, json);
            hashes.Add(file, await LegacySourceStage.HashFileAsync(file));
        }
        var request = new RelationalMigrationRequest("not a SQL connection string", fixture.DestinationConnectionString,
            local, staging, UseLocalSource: true);
        var result = await new RelationalMigrator().ConvertAsync(request);
        check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational,
            "Local-only conversion never parses a SQL source and publishes after field validation");
        await using (var source = await LegacySourceStage.ReopenAsync(result.StageDirectory))
        {
            check(source.SourceOrigin == "Local" && source.StagedDocuments.Values.All(item => item.SourceKind == "Local"),
                "Local recovery manifest pins all six local authoritative roots");
            await source.VerifyUnchangedAsync();
        }
        await VerifyMigratedModelsAsync(new(fixture.DestinationConnectionString), models, check);
        foreach (var (file, hash) in hashes)
        {
            byte[] actual = await LegacySourceStage.HashFileAsync(file);
            check(CryptographicOperations.FixedTimeEquals(hash, actual),
                "Local conversion preserves source bytes: " + Path.GetFileName(file));
        }
        check((await File.ReadAllBytesAsync(imagePath)).SequenceEqual(imageBytes), "Local conversion preserves the original pasted PNG");

        var state = new RelationalStateStore(new(fixture.DestinationConnectionString));
        long diagramKey = (await state.ListDiagramsAsync()).Items.Single().Token.Key;
        long workbenchKey = (await state.ListWorkbenchesAsync()).Items.Single().Token.Key;
        File.Delete(imagePath); // Only this fixture-owned image; verify database portability independently of the source PNG.
        check((await state.ReadDiagramAsync(diagramKey))!.Value.PastedImages[imageOrdinal].SequenceEqual(imageBytes) &&
            (await state.ReadWorkbenchAsync(workbenchKey))!.Value.PastedImages[imageOrdinal].SequenceEqual(imageBytes),
            "Diagram and saved-workbench pasted bytes remain readable after the source PNG is absent");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

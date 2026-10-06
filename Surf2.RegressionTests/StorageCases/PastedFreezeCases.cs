using System.IO;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunPastedFreezeChecksAsync(Action<bool, string> check)
    {
        await VerifyPastedFreezeRaceAsync(false, check);
        await VerifyPastedFreezeRaceAsync(true, check);
    }

    private static async Task VerifyPastedFreezeRaceAsync(bool initiallyMissing, Action<bool, string> check)
    {
        var models = MigrationModels();
        var diagram = models.Diagrams.Diagrams[0];
        int ordinal = diagram.Objects.FindIndex(item => item.ObjectType == DiagramObjectType.Image);
        string leaf = initiallyMissing ? "frozen-missing.png" : "frozen-present.png";
        byte[] original = Png(79);
        byte[] changed = Png(113);
        diagram.Objects[ordinal].PastedImageFileName = leaf;
        diagram.Objects[ordinal].ImageDataBase64 = Convert.ToBase64String(original);
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        var request = MigrationRequest(fixture);
        string pngPath = Path.Combine(request.LocalAppDataDirectory, "PastedDiagramImages", leaf);
        if (!initiallyMissing) await File.WriteAllBytesAsync(pngPath, original);
        bool mutated = false;
        RelationalMigrationException? rejection = null;
        try
        {
            await new RelationalMigrator().ConvertAsync(request, new PastedFreezeProgress(message =>
            {
                if (mutated || !message.StartsWith("Converting captured databases", StringComparison.Ordinal)) return;
                string stageDirectory = Directory.GetDirectories(request.StagingRoot).Single();
                using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(stageDirectory, "manifest.json")));
                check(manifest.RootElement.GetProperty("PastedImageFreeze").GetProperty("Completed").GetBoolean(),
                    "PNG present/absent source inventory is frozen before snapshot destination writes");
                File.WriteAllBytes(pngPath, changed);
                mutated = true;
            }));
        }
        catch (RelationalMigrationException error) { rejection = error; }
        check(mutated && rejection != null &&
            (await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Incomplete,
            initiallyMissing ? "A newly appeared source PNG prevents Ready publication" : "A changed source PNG prevents Ready publication");
        check((await File.ReadAllBytesAsync(pngPath)).SequenceEqual(changed),
            "Migration does not overwrite an external writer's changed source PNG");
        await ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var rejected = await LegacySourceStage.ReopenAsync(rejection!.StageDirectory);
        }, check, "Recovery refuses the stage while its original image inventory differs");
        if (initiallyMissing) File.Delete(pngPath);
        else await File.WriteAllBytesAsync(pngPath, original);
        await using (var stage = await LegacySourceStage.ReopenAsync(rejection!.StageDirectory))
        {
            check(stage.PastedImagesFrozen && stage.StagedPastedImageReferences.Single().IsMissing == initiallyMissing,
                "Recovery retains its original present/absent PNG decision");
            if (initiallyMissing)
                await ThrowsAsync<FileNotFoundException>(() => stage.ReadFrozenPngAsync(pngPath), check,
                    "Frozen-missing PNG reads retain the embedded fallback decision");
            else
                check((await stage.ReadFrozenPngAsync(pngPath))!.SequenceEqual(original),
                    "Frozen-present PNG reads retain the original staged bytes");
        }
        await new RelationalMigrator().ConvertAsync(request with { ResumeStageDirectory = rejection.StageDirectory });
        var state = new RelationalStateStore(new(fixture.DestinationConnectionString));
        long key = (await state.ListDiagramsAsync()).Items.Single().Token.Key;
        var restored = (await state.ReadDiagramAsync(key))!.Value;
        check(restored.Document.Objects[ordinal].PastedImageFileName == leaf &&
            (initiallyMissing ? restored.PastedImageFallbacks![ordinal].Bytes : restored.PastedImages[ordinal]).SequenceEqual(original),
            "After source recovery, publication preserves the frozen PNG bytes/fallback and original filename");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private sealed class PastedFreezeProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

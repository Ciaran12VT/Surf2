using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunRelationalV2StorageChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels();
        byte[] png = Png(92);
        string encoded = Convert.ToBase64String(png);
        var image = models.Diagrams.Diagrams[0].Objects.First(item => item.ObjectType == DiagramObjectType.Image);
        image.ImageDataBase64 = encoded;
        image.PastedImageFileName = "v2-pasted.png";
        models.Settings.DiagramImages.Images[0].ImageDataBase64 = encoded;
        models.Snapshots.Snapshots[0].FullDataTableNames.Add("[dbo].[V2Table] WHERE [Id] > 0");
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        var request = MigrationRequest(fixture);
        string imagePath = Path.Combine(request.LocalAppDataDirectory, "PastedDiagramImages", image.PastedImageFileName);
        await File.WriteAllBytesAsync(imagePath, png);
        var migrator = new RelationalMigrator();
        var result = await migrator.ConvertAsync(request);
        check(Equals(await fixture.DestinationSqlAsync("SELECT FormatIdentifier FROM surf.StorageFormatInfo;"),
            "Surf2.RelationalV2"), "V2 migration publishes a distinct storage identity");
        check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational,
            "The completed V2 migration can be opened by this runtime");
        check(Equals(await fixture.DestinationSqlAsync("""
SELECT COUNT_BIG(*) FROM surf.Asset WHERE Bytes=@Bytes;
""", RelationalSession.Parameter("@Bytes", SqlDbType.VarBinary, png, -1)), 1L),
            "Library, inline, pasted and workbench occurrences share one immutable binary asset");
        check(Equals(await fixture.DestinationSqlAsync("""
SELECT COUNT_BIG(*) FROM surf.TextContent WHERE CONVERT(varbinary(max),Text)=CONVERT(varbinary(max),@Encoded);
""", RelationalSession.Parameter("@Encoded", SqlDbType.NVarChar, encoded, -1)), 0L),
            "Canonical Base64 does not duplicate binary images in the text store");
        check(Equals(await fixture.DestinationSqlAsync("""
SELECT COUNT_BIG(*) FROM surf.FullDataTableSelection WHERE OriginalTableName=N'[dbo].[V2Table] WHERE [Id] > 0';
"""), 1L), "Current master's full-data selections survive migration verbatim");

        File.Delete(imagePath);
        var session = new RelationalSession(fixture.DestinationConnectionString);
        var store = new RelationalStateStore(session);
        var summaries = await store.ListDiagramsAsync();
        var restored = (await store.ReadDiagramAsync(summaries.Items[0].Token.Key))!;
        int ordinal = restored.Value.Document.Objects.FindIndex(item => item.ObjectType == DiagramObjectType.Image);
        check(restored.Value.Document.Objects[ordinal].ImageDataBase64 == encoded &&
            restored.Value.PastedImages[ordinal].SequenceEqual(png),
            "Migrated images reopen from SQL after the local source PNG is removed");
        check(restored.Value.Document.Objects.Where((_, i) => i != ordinal).All(item => item.ImageDataBase64.EndsWith("\r\n", StringComparison.Ordinal)),
            "Noncanonical legacy Base64 formatting remains exact");
        var choices = await new StatePreferenceAccess(session).ReadImageChoicesAsync();
        check(choices.Count == 1 && choices[0].ImageDataBase64 == encoded,
            "The Change Image picker reads and reconstructs SQL image choices");

        var original = restored.Value;
        var changed = JsonSerializer.Deserialize<DiagramDocument>(JsonSerializer.Serialize(original.Document, ModelJson), ModelJson)!;
        changed.Objects[ordinal].ImageDataBase64 = Convert.ToBase64String(Png(113));
        changed.Objects[ordinal].PastedImageFileName = "";
        var saved = await store.SaveDiagramAsync(new(changed, new Dictionary<int, byte[]>()), restored.Token, Guid.NewGuid());
        var reopened = (await store.ReadDiagramAsync(saved.Key))!;
        check(reopened.Value.Document.Objects[ordinal].ImageDataBase64 == changed.Objects[ordinal].ImageDataBase64,
            "Replacing an image persists its selected bytes");
        var historical = await store.ReadDiagramRevisionAsync(summaries.Items[0].RevisionKey);
        check(historical.PastedImages[ordinal].SequenceEqual(png),
            "Replacing one image preserves previous diagram revision assets");

        byte[] concurrent = Png(146);
        async Task<long> PutAsync()
        {
            await using var connection = new SqlConnection(fixture.DestinationConnectionString);
            await connection.OpenAsync();
            return await new RelationalContentStore().PutAssetAsync(connection, null, concurrent, "image/png");
        }
        long[] keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PutAsync()));
        check(keys.Distinct().Count() == 1, "Concurrent image imports reuse one asset identity");
        check(RelationalPackageFormat.Identifier == "Surf2.RelationalV2.Package", "V2 packages identify their storage contract");
        await fixture.VerifySourceUnchangedAsync(check);
        await VerifyRelationalV2EditorAsync(check);
    }
}

using System.IO;
using Surf2.Models;
using Surf2.Storage;

public static partial class StorageRegressionSuite
{
    public static async Task RunBootstrapChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        string directory = Path.Combine(fixture.OwnedDirectory, "bootstrap-only");
        string path = Path.Combine(directory, "connection-settings.json");
        var store = new LocalConnectionSettingsStore(path);
        check(store.Load().ConnectionString == new PersistenceConnectionSettings().ConnectionString,
            "A genuinely absent bootstrap keeps the existing default-selection contract");
        string first = "Server=fixture-only;Database=first;Integrated Security=True";
        string second = "Server=fixture-only;Database=second;Integrated Security=True";
        store.Save(new() { ConnectionString = first });
        byte[] original = await File.ReadAllBytesAsync(path);
        using (var pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool refused = false;
            try { store.Save(new() { ConnectionString = second }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
            check(refused, "Failed atomic bootstrap replacement preserves the previous selection");
        }
        check((await File.ReadAllBytesAsync(path)).SequenceEqual(original) &&
            !Directory.EnumerateFiles(directory, "*.pending").Any(),
            "Failed bootstrap save cleans only its own pending file and retains old bytes");
        using (var oldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            store.Save(new() { ConnectionString = second });
            using var buffered = new MemoryStream();
            await oldReader.CopyToAsync(buffered);
            check(buffered.ToArray().SequenceEqual(original) && store.Load().ConnectionString == second,
                "Concurrent bootstrap readers see one complete old/new version");
        }
        await File.WriteAllTextAsync(path, "null");
        await ThrowsAsync<InvalidDataException>(() => Task.Run(() => store.Load()), check,
            "A null bootstrap is an error, not permission to select another database");
        File.Delete(path);
        Directory.CreateDirectory(path);
        await ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => store.Load()), check,
            "An unavailable bootstrap is not treated as an absent file");
        Directory.Delete(path);
        using (var huge = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            huge.SetLength(1024 * 1024 + 1);
        await ThrowsAsync<InvalidDataException>(() => Task.Run(() => store.Load()), check,
            "Bootstrap size is checked before JSON materialization");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

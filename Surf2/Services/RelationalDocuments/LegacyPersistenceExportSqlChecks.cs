using System.Data;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;

namespace Surf2.Services.RelationalDocuments;

/// <summary>
/// Read-only SQL fixture hook. The parent creates and disposes the small exclusive
/// legacy database and AppData fixture; this hook writes only the selected new package.
/// It does not initialize, import, activate, mutate SQL, or delete fixture files.
/// </summary>
public static class LegacyPersistenceExportSqlChecks
{
    private const int FixtureBytes = 16 * 1024 * 1024;

    public static async Task<IReadOnlyList<string>> RunAsync(string legacyConnectionString,
        string fixtureAppDataDirectory, string newPackagePath, bool allowBlockingConsistentFallback = false,
        CancellationToken ct = default)
    {
        string root = Path.GetFullPath(fixtureAppDataDirectory), package = Path.GetFullPath(newPackagePath);
        if (File.Exists(package) || Directory.Exists(package))
            throw new InvalidOperationException("The SQL export fixture requires a new package path.");
        var probe = await new PersistenceFormatProbe().ProbeAsync(legacyConnectionString, ct);
        if (probe.Format != PersistenceFormat.Legacy) throw new InvalidOperationException("The SQL export fixture requires a known legacy source.");
        var before = await ReadSourceAsync(legacyConnectionString, ct);
        var locals = await ReadLocalFilesAsync(root, package, ct);
        var exported = await LegacyPersistenceExport.ExportAsync(package, legacyConnectionString, root,
            allowBlockingConsistentFallback: allowBlockingConsistentFallback, ct: ct);
        var after = await ReadSourceAsync(legacyConnectionString, ct);
        var passed = new List<string>();
        Check(before.ObjectCount == after.ObjectCount && before.Documents.SequenceEqual(after.Documents),
            "SQL legacy export preserves source schema count, raw UTF-16 payload hashes, IDs and timestamps", passed);
        var finalProbe = await new PersistenceFormatProbe().ProbeAsync(legacyConnectionString, ct);
        Check(finalProbe.Format == PersistenceFormat.Legacy && PersistenceFormatProbe.SameDatabase(probe, finalProbe),
            "SQL legacy export leaves source identity and format unchanged without initialization", passed);

        await using var file = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("The fixture export has no manifest.");
        using (var manifest = await ReadJsonAsync(manifestEntry, ct))
        {
            var value = manifest.RootElement;
            Check(value.GetProperty("FormatVersion").GetInt32() == 1 && value.GetProperty("AppName").GetString() == "Surf2" &&
                value.GetProperty("DatabaseName").GetString() == probe.DatabaseName &&
                value.GetProperty("DocumentCount").GetInt32() == before.Documents.Count &&
                value.GetProperty("LocalFileCount").GetInt32() == locals.Count &&
                exported.DocumentCount == before.Documents.Count && exported.LocalFileCount == locals.Count,
                "SQL legacy format-1 manifest and result counts match selected documents and allowed files", passed);
        }
        var documentEntry = archive.GetEntry("database/surf2-documents.json") ?? throw new InvalidDataException("The fixture export has no document stream.");
        using (var documents = await ReadJsonAsync(documentEntry, ct))
        {
            var restored = new List<DocumentFingerprint>();
            foreach (var value in documents.RootElement.EnumerateArray())
            {
                string payload = value.GetProperty("PayloadJson").GetString() ?? throw new InvalidDataException("A fixture payload is null.");
                byte[] utf16 = new UnicodeEncoding(false, false, true).GetBytes(payload);
                restored.Add(new(value.GetProperty("DocumentKey").GetString() ?? throw new InvalidDataException("A fixture ID is null."),
                    utf16.LongLength, Convert.ToHexString(SHA256.HashData(utf16)), value.GetProperty("UpdatedAtUtc").GetDateTime().Ticks));
            }
            Check(restored.SequenceEqual(before.Documents),
                "SQL legacy rows round-trip through the exported format-1 document envelope losslessly", passed);
        }
        var localEntries = archive.Entries.Where(e => e.FullName.StartsWith("local-files/", StringComparison.Ordinal)).ToArray();
        Check(archive.Entries.Count == 2 + locals.Count && localEntries.Length == locals.Count,
            "Legacy archive includes exactly the declared authoritative streams and selected local files", passed);
        foreach (var entry in localEntries)
        {
            string? allowed = LegacyPersistenceExport.LocalEntry(entry.FullName["local-files/".Length..]);
            if (allowed != entry.FullName || !locals.TryGetValue(entry.FullName, out var expected))
                throw new InvalidOperationException("The legacy archive contains an excluded or undeclared local file.");
            await using var input = entry.Open();
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            if (entry.Length != expected.Length || hash != expected.Hash)
                throw new InvalidOperationException("A local fixture file did not round-trip losslessly.");
        }
        passed.Add("Legacy local files, including PNG assets, retain exact lengths and bytes");
        passed.Add("Legacy export omits credential settings/backups, InternalLogs, Migrations and partial artifacts");
        var currentLocals = await ReadLocalFilesAsync(root, package, ct);
        Check(currentLocals.Count == locals.Count && currentLocals.All(p => locals.TryGetValue(p.Key, out var original) && original == p.Value),
            "Legacy export leaves selected local source files unchanged", passed);
        return passed.AsReadOnly();
    }

    private sealed record DocumentFingerprint(string Key, long Bytes, string Hash, long UpdatedTicks);
    private sealed record SourceFingerprint(long ObjectCount, IReadOnlyList<DocumentFingerprint> Documents);
    private sealed record LocalFingerprint(long Length, string Hash);

    private static async Task<SourceFingerprint> ReadSourceAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
IF (SELECT COALESCE(SUM(CONVERT(bigint,DATALENGTH(PayloadJson))),0) FROM app.Surf2Documents) > 16777216
    THROW 50000, 'The SQL export fixture exceeds its text budget.', 1;
SELECT COUNT_BIG(*) FROM sys.objects WHERE is_ms_shipped=0;
SELECT CONVERT(bigint,DATALENGTH(DocumentKey)),DocumentKey,CONVERT(bigint,DATALENGTH(PayloadJson)),
       HASHBYTES('SHA2_256',CONVERT(varbinary(max),PayloadJson)),UpdatedAtUtc
FROM app.Surf2Documents ORDER BY DocumentKey;
""";
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException("The source fixture schema could not be read.");
        long objects = reader.GetInt64(0);
        if (!await reader.NextResultAsync(ct)) throw new InvalidDataException("The source fixture documents could not be read.");
        var documents = new List<DocumentFingerprint>();
        while (await reader.ReadAsync(ct))
        {
            if (documents.Count >= 10_000 || reader.GetInt64(0) > 256)
                throw new InvalidDataException("The source fixture catalogue exceeds its budget.");
            documents.Add(new(reader.GetString(1), reader.GetInt64(2), Convert.ToHexString(reader.GetFieldValue<byte[]>(3)), reader.GetDateTime(4).Ticks));
        }
        return new(objects, documents.AsReadOnly());
    }

    private static async Task<Dictionary<string, LocalFingerprint>> ReadLocalFilesAsync(string root, string package, CancellationToken ct)
    {
        var result = new Dictionary<string, LocalFingerprint>(StringComparer.Ordinal);
        foreach (string path in LegacyPersistenceExport.EnumerateLocalFiles(root, package, package, ct))
        {
            string entry = LegacyPersistenceExport.LocalEntry(Path.GetRelativePath(root, path)) ??
                throw new InvalidDataException("The local fixture selection changed.");
            if (result.Count >= 4094) throw new InvalidDataException("The local fixture catalogue exceeds its budget.");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
            long length = file.Length;
            var fingerprint = new LocalFingerprint(length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct)));
            result.Add(entry, fingerprint);
        }
        return result;
    }

    private static async Task<JsonDocument> ReadJsonAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        if (entry.Length > FixtureBytes) throw new InvalidDataException("The exported fixture JSON exceeds its verification budget.");
        await using var input = entry.Open(); using var bytes = new MemoryStream();
        var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (read > FixtureBytes - bytes.Length) throw new InvalidDataException("The exported fixture JSON exceeds its verification budget.");
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length != entry.Length) throw new InvalidDataException("The exported fixture JSON length changed.");
        bytes.Position = 0; return await JsonDocument.ParseAsync(bytes, cancellationToken: ct);
    }

    private static void Check(bool valid, string label, List<string> passed)
    {
        if (!valid) throw new InvalidOperationException(label);
        passed.Add(label);
    }
}

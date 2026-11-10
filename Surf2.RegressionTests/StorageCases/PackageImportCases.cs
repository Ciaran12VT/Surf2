using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunPackageImportChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var models = PackageImportModels();
        await using var source = await SqlFixture.CreateAsync(models.Documents());
        var migration = await new RelationalMigrator().ConvertAsync(MigrationRequest(source));
        var sourceSession = new RelationalSession(source.DestinationConnectionString);
        await VerifyMigratedModelsAsync(sourceSession, models, check);
        await VerifyPackageHistoryOwnersAsync(sourceSession, models.Snapshots, check, "export source");
        byte[] sourceEvidence = await PackageImportSqlEvidenceAsync(sourceSession);
        byte[] stageHash = await PackageFileHashAsync(Path.Combine(migration.StageDirectory, "manifest.json"));
        string directory = Path.Combine(source.OwnedDirectory, "package-import");
        Directory.CreateDirectory(directory);
        string package = Path.Combine(directory, "roundtrip.surf2.zip");
        var limits = new RelationalPackageLimits { ChunkBytes = 1024 };
        var exporter = new RelationalPackageExporter(sourceSession, limits);
        byte[] localPng = Png(91);
        var selected = new RelationalPackageLocalFile("images/fixture.png", localPng.Length,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(localPng, writable: false)));
        var exported = await exporter.ExportAsync(package,
            new(LocalFiles: PackageSelectedFiles([selected]), AllowBlockingConsistentFallback: true));
        byte[] packageHash = await PackageFileHashAsync(package);

        await using (var restored = await SqlFixture.CreateAsync())
        {
            Guid identity = Guid.NewGuid();
            var options = PackageImportOptions(exported.ManifestSha256, identity);
            var imported = await new RelationalPackageImporter(restored.DestinationConnectionString, limits).ImportAsync(package, options);
            await VerifyPackageImportPublishedAsync(restored, imported, exported, identity, check);
            check(!Directory.Exists(restored.OwnedDirectory) && imported.VerifiedSkippedLocalFiles == 1 &&
                imported.LocalFilePolicy == "VerifyAndSkip-v1" && !imported.Activated,
                "Package import verifies/skips the selected local PNG and creates no extraction directory or activation");
            var session = new RelationalSession(restored.DestinationConnectionString);
            await VerifyMigratedModelsAsync(session, models, check);
            await VerifyPackageHistoryOwnersAsync(session, models.Snapshots, check, "restored target");
            await VerifyPackageImportCaptureAsync(sourceSession, session, check);
            await VerifyPackageImportPngAsync(source, restored, check);
            await VerifyPackageReexportAsync(restored, exported, limits, check);
            await ThrowsAsync<RelationalPackageImportException>(() =>
                new RelationalPackageImporter(restored.DestinationConnectionString, limits).ImportAsync(package, options), check,
                "Importer refuses a second import into the already Ready target");
            await restored.VerifySourceUnchangedAsync(check);
        }
        await CheckPackageImportInputsUnchangedAsync(source, sourceSession, sourceEvidence, package, packageHash,
            migration.StageDirectory, stageHash, check, "successful typed round trip");

        string badChecksum = await PackageImportVariantAsync(source, package, "bad-checksum.zip", manifest =>
        {
            var streams = manifest.Tables.ToArray();
            int index = Array.FindIndex(streams, x => x.Table.Schema == "surf" && x.Table.Name == "DatabaseSnapshot");
            streams[index] = streams[index] with { Sha256 = new string('0', 64) };
            return manifest with { Tables = streams };
        });
        await VerifyRejectedPackageImportAsync(badChecksum, limits, PersistenceFormat.Incomplete, check,
            "Tampered authoritative table checksum is rejected after schema installation");
        string unknown = await PackageImportVariantAsync(source, package, "unknown-table.zip", manifest =>
        {
            var streams = manifest.Tables.ToArray();
            streams[0] = streams[0] with { Table = streams[0].Table with { Name = "UnexpectedVerificationTable" } };
            return manifest with { Tables = streams };
        });
        await VerifyRejectedPackageImportAsync(unknown, limits, PersistenceFormat.Empty, check,
            "Unknown authoritative table is rejected before destination installation");
        await VerifyPackageImportCancellationAsync(package, exported.ManifestSha256, limits, check);
        await CheckPackageImportInputsUnchangedAsync(source, sourceSession, sourceEvidence, package, packageHash,
            migration.StageDirectory, stageHash, check, "checksum/unknown-table rejection and cancellation");

        // Invalid SQL UTF-16 code units cannot be staged through legacy JSON. Seed them only into the
        // owned, already validated SQL source, then check a separate real export/import without text decoders.
        var raw = await SeedPackageRawContentAsync(source, check);
        sourceEvidence = await PackageImportSqlEvidenceAsync(sourceSession);
        string rawPackage = Path.Combine(directory, "raw-utf16.surf2.zip");
        var rawExport = await exporter.ExportAsync(rawPackage, new(AllowBlockingConsistentFallback: true));
        byte[] rawPackageHash = await PackageFileHashAsync(rawPackage);
        await using (var restored = await SqlFixture.CreateAsync())
        {
            Guid identity = Guid.NewGuid();
            var imported = await new RelationalPackageImporter(restored.DestinationConnectionString, limits)
                .ImportAsync(rawPackage, PackageImportOptions(rawExport.ManifestSha256, identity));
            await VerifyPackageImportPublishedAsync(restored, imported, rawExport, identity, check);
            var bytes = (byte[])Required(await restored.DestinationSqlAsync(
                "SELECT CONVERT(varbinary(max),Text) FROM surf.TextContent WHERE ContentKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, raw.Key)), "restored raw SQL UTF-16");
            check(bytes.SequenceEqual(raw.Bytes) && DecodePackageUtf16Units(bytes) == raw.CodeUnits,
                "Actual SQL package import preserves raw UTF-16 NUL/unpaired surrogates/split pair/trailing whitespace without replacement");
            check(Convert.ToInt64(await restored.DestinationSqlAsync("""
                SELECT COUNT_BIG(*) FROM surf.TextContent WHERE ContentKey=@Key AND ContentHash=@Hash
                    AND ByteCount=@Bytes AND CharacterCount=@Characters AND LineCount=2;
                """, RelationalSession.Parameter("@Key", SqlDbType.BigInt, raw.Key),
                RelationalSession.Parameter("@Hash", SqlDbType.Binary, SHA256.HashData(raw.Bytes), 32),
                RelationalSession.Parameter("@Bytes", SqlDbType.BigInt, (long)raw.Bytes.Length),
                RelationalSession.Parameter("@Characters", SqlDbType.BigInt, (long)raw.CodeUnits.Length))) == 1,
                "Raw UTF-16 import retains exact SQL content identity, SHA256 and text metrics");
            var session = new RelationalSession(restored.DestinationConnectionString);
            await VerifyPackageImportCaptureAsync(sourceSession, session, check);
            await VerifyPackageImportPngAsync(source, restored, check);
            await VerifyPackageReexportAsync(restored, rawExport, limits, check);
            await restored.VerifySourceUnchangedAsync(check);
        }
        await CheckPackageImportInputsUnchangedAsync(source, sourceSession, sourceEvidence, rawPackage, rawPackageHash,
            migration.StageDirectory, stageHash, check, "raw UTF-16 round trip");
    }

    private static RelationalPackageImportOptions PackageImportOptions(string? hash = null, Guid? identity = null) =>
        new() { ExpectedManifestSha256 = hash, ImportIdentity = identity, BatchRows = 2, BatchBytes = 4 * 1024 * 1024 };

    private static MigrationFixture PackageImportModels()
    {
        var models = MigrationModels();
        models.Snapshots.Snapshots[0].TableDataSets[0].Rows.Add(Json(
            "{\"current-a1\":1,\"CURRENT-A1\":2,\"current-a1\":3,\"unlisted\":{\"nested\":[null,1.2300]}}"));
        var first = models.Snapshots.Histories[0];
        foreach ((string id, string definition) in new[] { ("tie-first", "tie-first-definition"), ("tie-second", "tie-second-definition") })
            first.Versions.Add(new() { VersionId = id, VersionName = id, VersionNumber = 12, CreatedAtUtc = EvidenceTime.AddTicks(13),
                Changes = [MixedChange(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Modified, "q",
                    new() { Kind = DatabaseVersionedResourceKind.StoredProcedure, DatabaseObject = MixedObject("q", definition) })] });
        var second = JsonSerializer.Deserialize<DatabaseSnapshotHistory>(JsonSerializer.Serialize(first, ModelJson), ModelJson)
            ?? throw new InvalidDataException("Could not clone the small duplicate-history fixture.");
        second.SnapshotId = first.SnapshotId.ToUpperInvariant();
        second.NextVersionNumber = 50;
        second.Versions.Single(x => x.VersionId == "mixed-latest").Changes.Last().PreviousPayload!.DatabaseObject!.Definition = "second-history-only";
        models.Snapshots.Histories.Add(second);
        return models;
    }

    private static async Task VerifyPackageImportPublishedAsync(SqlFixture target, RelationalPackageImportResult actual,
        RelationalPackageExportResult exported, Guid identity, Action<bool, string> check)
    {
        check(actual.ImportIdentity == identity && actual.DatabaseIdentity != Guid.Empty &&
            actual.DatabaseIdentity != actual.SourceDatabaseIdentity && actual.SourceDatabaseIdentity == exported.Manifest.SourceDatabaseIdentity &&
            actual.ManifestSha256 == exported.ManifestSha256 && actual.ImportedRows == exported.Manifest.Tables.Sum(x => x.RowCount) &&
            actual.CapturedRows == exported.Manifest.Tables.Where(x => x.Table.Schema == "capture").Sum(x => x.RowCount) &&
            actual.VerifiedUncompressedBytes == exported.UncompressedBytes,
            "Production package import preserves pinned source identity/hash/counts and creates an independent destination identity");
        check((await new PersistenceFormatProbe().ProbeAsync(target.DestinationConnectionString)).Format == PersistenceFormat.Relational &&
            Convert.ToInt64(await target.DestinationSqlAsync("""
                SELECT COUNT_BIG(*) FROM surf.StorageFormatInfo f JOIN surf.MigrationRun r ON r.MigrationIdentity=f.CompletedMigrationIdentity
                JOIN surf.SourceProvenance p ON p.MigrationIdentity=r.MigrationIdentity
                WHERE f.State='Ready' AND f.DatabaseIdentity=@Database AND r.MigrationIdentity=@Import AND r.Status='Complete'
                    AND r.FinishedAtUtc IS NOT NULL AND p.SourceKind='Package' AND p.DocumentKey=N'package-manifest';
                """, RelationalSession.Parameter("@Database", SqlDbType.UniqueIdentifier, actual.DatabaseIdentity),
                RelationalSession.Parameter("@Import", SqlDbType.UniqueIdentifier, identity))) == 1,
            "Importer itself atomically publishes Ready/completed identity/package provenance after validation");
        check(Convert.ToInt64(await target.DestinationSqlAsync("""
            SELECT (SELECT COUNT_BIG(*) FROM sys.foreign_keys WHERE is_disabled=1 OR is_not_trusted=1)
                +(SELECT COUNT_BIG(*) FROM sys.check_constraints WHERE is_disabled=1 OR is_not_trusted=1);
            """)) == 0, "Every restored SQL foreign/check constraint is enabled and trusted before runtime reads");
    }

    private static async Task VerifyPackageHistoryOwnersAsync(RelationalSession session, DatabaseSnapshotLibrary expected,
        Action<bool, string> check, string label)
    {
        var snapshots = new RelationalSnapshotStore(session, new RelationalContentStore());
        var capture = new RelationalCaptureStore(session);
        long key = (await snapshots.ListSnapshotsAsync()).Items.Single().SnapshotKey;
        var histories = await SnapshotPagesAsync(cursor => snapshots.ListHistoriesAsync(key, 1, cursor), check, label + " duplicate histories");
        check(histories.Count == 2 && histories.Select(x => x.SnapshotId).SequenceEqual(expected.Histories.Select(x => x.SnapshotId)) &&
            histories.Select(x => x.NextVersionNumber).SequenceEqual(expected.Histories.Select(x => x.NextVersionNumber)),
            label + ": actual SQL preserves duplicate history owners, original ID casing and distinct next-version counters");
        for (int i = 0; i < histories.Count; i++)
        {
            var versions = await SnapshotPagesAsync(cursor => snapshots.ListHistoryVersionsAsync(key, histories[i].HistoryKey, 1, cursor),
                check, label + " history " + i + " versions");
            check(versions.Select(x => x.VersionId).SequenceEqual(expected.Histories[i].Versions.Select(x => x.VersionId)) &&
                versions.Select(x => x.VersionNumber).SequenceEqual(expected.Histories[i].Versions.Select(x => x.VersionNumber)) &&
                versions.Count(x => x.VersionNumber == 12) == 3,
                label + ": duplicate version numbers keep source collection order within history " + i);
            var selected = new DatabaseSnapshotLibrary { Snapshots = expected.Snapshots,
                Histories = new(expected.Histories.Where((_, j) => j == i).Concat(expected.Histories.Where((_, j) => j != i))) };
            await VerifyHistoryOracleAsync(session, snapshots, capture, selected, key,
                versions.ToDictionary(x => x.VersionId, x => x.VersionKey, StringComparer.Ordinal), check);
        }
        var first = await snapshots.GetHistoryAsync(key);
        var defaultVersions = await snapshots.ListVersionsAsync(key);
        check(first?.HistoryKey == histories[0].HistoryKey &&
            defaultVersions.Items.Select(x => x.VersionId).SequenceEqual(expected.Histories[0].Versions.Select(x => x.VersionId)),
            label + ": unqualified history/version reads retain first-history semantics after duplicate-owner round trip");
    }

    private static async Task VerifyPackageImportCaptureAsync(RelationalSession source, RelationalSession target, Action<bool, string> check)
    {
        var left = new RelationalCaptureStore(source);
        var right = new RelationalCaptureStore(target);
        await using var connection = await source.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DataSetKey FROM surf.DataSet ORDER BY DataSetKey;";
        var keys = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) keys.Add(reader.GetInt64(0));
        foreach (long key in keys)
        {
            var a = await left.GetDescriptorAsync(key);
            var b = await right.GetDescriptorAsync(key);
            SameModel(a.Summary, b.Summary, check, "Package import preserves exact dataset/layout/revision/count/time identity: " + key);
            SameModel(a.Columns, b.Columns, check, "Package import preserves ordered capture column descriptors: " + key);
            check(a.Epoch == source.Epoch && b.Epoch == target.Epoch && a.Epoch != b.Epoch,
                "Restored capture descriptors use their independent runtime session epoch: " + key);
            await using var leftRows = left.StreamRowsAsync(key).GetAsyncEnumerator();
            await using var rightRows = right.StreamRowsAsync(key).GetAsyncEnumerator();
            long rows = 0;
            while (await leftRows.MoveNextAsync())
            {
                if (!await rightRows.MoveNextAsync()) throw new InvalidDataException("Restored capture ends before the source.");
                check(leftRows.Current.RowOrdinal == rightRows.Current.RowOrdinal &&
                    leftRows.Current.Value.GetRawText() == rightRows.Current.Value.GetRawText(),
                    "Package capture round trip retains duplicate/case/nested/null/numeric-token evidence and stable row ordinal: " + key + "/" + rows);
                rows++;
            }
            check(!await rightRows.MoveNextAsync() && rows == a.Summary.ActualRowCount,
                "Package capture stream has exact actual row count: " + key);
        }
        check(keys.Count > 0, "Package importer exercises actual current and historical captured datasets");
    }

    private static async Task VerifyPackageImportPngAsync(SqlFixture source, SqlFixture target, Action<bool, string> check)
    {
        foreach (byte red in new byte[] { 17, 59 })
        {
            byte[] bytes = Png(red);
            var parameters = new[] { RelationalSession.Parameter("@Hash", SqlDbType.Binary, SHA256.HashData(bytes), 32) };
            var key = await source.DestinationSqlAsync("SELECT AssetKey FROM surf.Asset WHERE ContentHash=@Hash;", parameters);
            var restored = (byte[])Required(await target.DestinationSqlAsync("SELECT Bytes FROM surf.Asset WHERE AssetKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, key!)), "restored PNG asset");
            check(restored.SequenceEqual(bytes), "Package importer retains independent diagram/settings PNG asset identities and exact streams: " + red);
        }
    }

    private static async Task VerifyPackageReexportAsync(SqlFixture target, RelationalPackageExportResult expected,
        RelationalPackageLimits limits, Action<bool, string> check)
    {
        Directory.CreateDirectory(target.OwnedDirectory);
        string path = Path.Combine(target.OwnedDirectory, "reexport.surf2.zip");
        var actual = await new RelationalPackageExporter(new(target.DestinationConnectionString), limits)
            .ExportAsync(path, new(AllowBlockingConsistentFallback: true));
        var byEntry = actual.Manifest.Tables.ToDictionary(x => x.Entry, StringComparer.Ordinal);
        check(byEntry.Count == expected.Manifest.Tables.Count && expected.Manifest.Tables.All(x =>
            byEntry.TryGetValue(x.Entry, out var y) && x.RowCount == y.RowCount && x.ByteCount == y.ByteCount && x.Sha256 == y.Sha256),
            "Re-export of restored SQL matches every authoritative/capture stream byte-for-byte, excluding deliberate rowversion restamps");
        check(actual.Manifest.LocalFiles.Count == 0 && actual.Manifest.SourceDatabaseIdentity != expected.Manifest.SourceDatabaseIdentity,
            "Restored database has its own identity and no implicitly restored local files");
    }

    private static async Task VerifyRejectedPackageImportAsync(string package, RelationalPackageLimits limits,
        PersistenceFormat expected, Action<bool, string> check, string label)
    {
        await using var target = await SqlFixture.CreateAsync();
        bool rejected = false;
        try { await new RelationalPackageImporter(target.DestinationConnectionString, limits).ImportAsync(package, PackageImportOptions()); }
        catch (Exception error) when (error is InvalidDataException or RelationalPackageImportException) { rejected = true; }
        check(rejected, label);
        await CheckPackageTargetUnpublishedAsync(target, expected, check, label);
        await target.VerifySourceUnchangedAsync(check);
    }

    private static async Task VerifyPackageImportCancellationAsync(string package, string hash, RelationalPackageLimits limits, Action<bool, string> check)
    {
        await using var target = await SqlFixture.CreateAsync();
        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => new RelationalPackageImporter(target.DestinationConnectionString, limits)
                .ImportAsync(package, PackageImportOptions(hash), preCancelled.Token), check, "Pre-cancelled package import leaves an Empty target");
        }
        await CheckPackageTargetUnpublishedAsync(target, PersistenceFormat.Empty, check, "pre-cancellation");
        var builder = new SqlConnectionStringBuilder(target.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_ImportCancel_" + Guid.NewGuid().ToString("N") };
        using var cancellation = new CancellationTokenSource();
        using (var checkpoint = new PackageImportCancellationCheckpoint(builder.InitialCatalog, builder.ApplicationName, cancellation))
        {
            await ThrowsAsync<OperationCanceledException>(() => new RelationalPackageImporter(builder.ConnectionString, limits)
                .ImportAsync(package, PackageImportOptions(hash), cancellation.Token), check,
                "Real SQL import cancellation after a committed table never publishes Ready");
            check(checkpoint.Triggered, "Cancellation diagnostic checkpoint reaches the owned importer after DatabaseSnapshot batches commit");
        }
        await CheckPackageTargetUnpublishedAsync(target, PersistenceFormat.Incomplete, check, "SQL-checkpoint cancellation");
        check(Convert.ToInt64(await target.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DatabaseSnapshot;")) == 1,
            "Cancelled package destination retains its committed snapshot batch while remaining unreadable at runtime");
        await ThrowsAsync<RelationalPackageImportException>(() => new RelationalPackageImporter(target.DestinationConnectionString, limits)
            .ImportAsync(package, PackageImportOptions(hash)), check, "Cancelled installed destination is not silently resumed or activated");
        await target.VerifySourceUnchangedAsync(check);
    }

    private static async Task CheckPackageTargetUnpublishedAsync(SqlFixture target, PersistenceFormat expected,
        Action<bool, string> check, string label)
    {
        check((await new PersistenceFormatProbe().ProbeAsync(target.DestinationConnectionString)).Format == expected,
            label + ": destination is exactly " + expected + ", never Ready");
        if (expected == PersistenceFormat.Incomplete)
            check(Convert.ToInt64(await target.DestinationSqlAsync("""
                SELECT COUNT_BIG(*) FROM surf.StorageFormatInfo WHERE State='Migrating' AND CompletedMigrationIdentity IS NULL;
                """)) == 1 && Convert.ToInt64(await target.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationRun WHERE Status='Complete';")) == 0,
                label + ": no completed migration/import identity is published");
        var session = new RelationalSession(target.DestinationConnectionString);
        await ThrowsAsync<InvalidOperationException>(() => new RelationalSnapshotStore(session, new RelationalContentStore()).ListSnapshotsAsync(),
            check, label + ": normal runtime snapshot reads refuse the unpublished target");
        await ThrowsAsync<InvalidOperationException>(() => new RelationalStateStore(session).ReadSettingsAsync(),
            check, label + ": normal runtime state reads refuse the unpublished target");
    }

    private static async Task<string> PackageImportVariantAsync(SqlFixture owner, string source, string name,
        Func<RelationalPackageManifest, RelationalPackageManifest> mutate)
    {
        if (Path.GetFileName(name) != name) throw new InvalidOperationException("Variant filename must remain fixture-owned.");
        string path = Path.Combine(owner.OwnedDirectory, "package-import", name);
        using var input = ZipFile.OpenRead(source);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in input.Entries)
        {
            var copy = archive.CreateEntry(entry.FullName, CompressionLevel.Fastest);
            await using var read = entry.Open();
            await using var write = copy.Open();
            if (entry.FullName == RelationalPackageFormat.ManifestEntry)
            {
                var manifest = await JsonSerializer.DeserializeAsync<RelationalPackageManifest>(read)
                    ?? throw new InvalidDataException("Fixture variant has no manifest.");
                await JsonSerializer.SerializeAsync(write, mutate(manifest));
            }
            else await read.CopyToAsync(write);
        }
        return path;
    }

    private static async Task<byte[]> PackageImportSqlEvidenceAsync(RelationalSession session)
    {
        // This fixture graph is intentionally small. SQL converts text to binary before JSON formatting,
        // so the evidence digest includes raw code units rather than a lossy JSON string representation.
        await using var connection = await session.OpenAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT * FROM surf.StorageFormatInfo FOR JSON PATH,INCLUDE_NULL_VALUES);
            SELECT (SELECT ContentKey,ContentHash,CharacterCount,ByteCount,LineCount,CONVERT(varbinary(max),Text) AS Raw
                FROM surf.TextContent ORDER BY ContentKey FOR JSON PATH,INCLUDE_NULL_VALUES);
            SELECT (SELECT * FROM surf.Asset ORDER BY AssetKey FOR JSON PATH,INCLUDE_NULL_VALUES);
            SELECT (SELECT * FROM surf.DatabaseSnapshot ORDER BY SnapshotKey FOR JSON PATH,INCLUDE_NULL_VALUES);
            SELECT (SELECT * FROM surf.SnapshotHistory ORDER BY HistoryKey FOR JSON PATH,INCLUDE_NULL_VALUES);
            SELECT (SELECT * FROM surf.SnapshotVersion ORDER BY VersionKey FOR JSON PATH,INCLUDE_NULL_VALUES);
            SELECT (SELECT * FROM surf.DataSet ORDER BY DataSetKey FOR JSON PATH,INCLUDE_NULL_VALUES);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (!await reader.ReadAsync()) throw new InvalidDataException("Missing source evidence result.");
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)));
        } while (await reader.NextResultAsync());
        await reader.DisposeAsync();
        command.CommandText = "SELECT DataSetKey FROM surf.DataSet ORDER BY DataSetKey;";
        var datasets = new List<long>();
        await using (var keys = await command.ExecuteReaderAsync())
            while (await keys.ReadAsync()) datasets.Add(keys.GetInt64(0));
        var capture = new RelationalCaptureStore(session);
        foreach (long key in datasets)
        {
            hash.AppendData(BitConverter.GetBytes(key));
            await foreach (var row in capture.StreamRowsAsync(key))
            {
                byte[] raw = System.Text.Encoding.UTF8.GetBytes(row.Value.GetRawText());
                hash.AppendData(BitConverter.GetBytes(row.RowOrdinal));
                hash.AppendData(BitConverter.GetBytes(row.EstimatedBytes));
                hash.AppendData(BitConverter.GetBytes(raw.Length));
                hash.AppendData(raw);
            }
        }
        return hash.GetHashAndReset();
    }

    private static async Task CheckPackageImportInputsUnchangedAsync(SqlFixture source, RelationalSession session,
        byte[] evidence, string package, byte[] packageHash, string stage, byte[] stageHash, Action<bool, string> check, string label)
    {
        check((await PackageImportSqlEvidenceAsync(session)).SequenceEqual(evidence) &&
            (await PackageFileHashAsync(package)).SequenceEqual(packageHash) &&
            (await PackageFileHashAsync(Path.Combine(stage, "manifest.json"))).SequenceEqual(stageHash),
            "Importer leaves original relational SQL evidence, package and frozen source manifest unchanged: " + label);
        await using var reopened = await LegacySourceStage.ReopenAsync(stage);
        await reopened.VerifyUnchangedAsync(source.SourceConnectionString);
        await source.VerifySourceUnchangedAsync(check);
    }

    private sealed class PackageImportCancellationCheckpoint : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database, _application;
        private readonly CancellationTokenSource _cancellation;
        private readonly object _gate = new();
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _listeners;
        private int _triggered;
        private bool _disposed;
        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public PackageImportCancellationCheckpoint(string database, string application, CancellationTokenSource cancellation)
        {
            _database = database; _application = application; _cancellation = cancellation;
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "SqlClientDiagnosticListener") return;
            lock (_gate)
                if (!_disposed) _subscriptions.Add(listener.Subscribe(this, name => name == SqlClientCommandBefore.Name));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not SqlClientCommandBefore before || before.Command.Connection is not { } connection ||
                connection.Database != _database || new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationName != _application ||
                !before.Command.CommandText.StartsWith("CREATE TABLE #PackageRows([ResourceKey] ", StringComparison.Ordinal)) return;
            if (Interlocked.Exchange(ref _triggered, 1) == 0) _cancellation.Cancel();
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            _listeners.Dispose();
            lock (_gate)
            {
                _disposed = true;
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();
            }
        }
    }
}

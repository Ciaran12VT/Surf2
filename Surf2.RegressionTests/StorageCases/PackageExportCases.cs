using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Packages;

public static partial class StorageRegressionSuite
{
    public static async Task RunPackageExportChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var models = MigrationModels();
        const string definition = "create procedure dbo.p as\r\nselect N'fixture \u00e9' as Value;\r\n  ";
        models.Snapshots.Snapshots[0].Objects[0].Definition = definition;
        var roots = models.Documents();
        await using var fixture = await SqlFixture.CreateAsync(roots);
        var migrated = await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        var session = new RelationalSession(fixture.DestinationConnectionString);
        await VerifyMigratedModelsAsync(session, models, check);
        check(migrated.StageDirectory.StartsWith(fixture.OwnedDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) &&
            (await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational,
            "Package fixture uses production migration and Ready publication inside its owned staging root");

        // The exporter must not change database options. Only this generated fixture destination is altered here.
        await fixture.DestinationSqlAsync("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
        check(Equals(await fixture.DestinationSqlAsync(
            "SELECT CONVERT(int,snapshot_isolation_state) FROM sys.databases WHERE database_id=DB_ID();"), 1),
            "Package fixture explicitly enables snapshot isolation on its owned destination only");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("""
            SELECT (SELECT COUNT_BIG(*) FROM surf.DatabaseSnapshot WHERE IsPublished=0)
                +(SELECT COUNT_BIG(*) FROM surf.SnapshotResourceRevision WHERE IsSealed=0)
                +(SELECT COUNT_BIG(*) FROM surf.DataSet WHERE State<>'Ready');
            """)) == 0, "Completed package fixture has no unpublished snapshots, unsealed revisions or partial datasets");
        var raw = await SeedPackageRawContentAsync(fixture, check);
        const string secret = "fixture-only-password-canary-DoNotPackage";
        await fixture.DestinationSqlAsync("""
            EXEC(N'CREATE SCHEMA fixturePackage');
            CREATE TABLE fixturePackage.Unselected (Id int PRIMARY KEY, ConnectionString nvarchar(max), LegacyRoot nvarchar(max));
            INSERT fixturePackage.Unselected VALUES(1,@Connection,@Root);
            """, RelationalSession.Parameter("@Connection", SqlDbType.NVarChar,
                "Server=fixture-only;User ID=fixture-user;Password=" + secret, -1),
            RelationalSession.Parameter("@Root", SqlDbType.NVarChar, roots["database-snapshots"], -1));

        string directory = Path.Combine(fixture.OwnedDirectory, "package-export");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "fixture.surf2.zip");
        byte[] png = Png(91);
        byte[] notes = Encoding.UTF8.GetBytes("Explicit fixture notes\r\n\u00e9\n");
        var limits = new RelationalPackageLimits { ChunkBytes = 1024 };
        var exporter = new RelationalPackageExporter(session, limits);
        var local = new[]
        {
            new RelationalPackageLocalFile("images/fixture.png", png.Length,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(png, writable: false))),
            new RelationalPackageLocalFile("notes\\fixture.txt", notes.Length,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(notes, writable: false)))
        };
        var result = await exporter.ExportAsync(destination,
            new(LocalFiles: PackageSelectedFiles(local)));
        check(result.Destination == destination && result.Manifest.Consistency == "SqlSnapshot-v1" &&
            result.Manifest.LocalFilePolicy == "ExplicitCallerSelection-v1",
            "Production exporter writes a pinned SQL snapshot and explicitly selected local streams");
        await VerifyPackageArchiveAsync(fixture, session, result, roots.Values, secret, definition, raw, png, notes, limits, check);

        byte[] existingHash = await PackageFileHashAsync(destination);
        await ThrowsAsync<IOException>(() => exporter.ExportAsync(destination), check,
            "Package export refuses an existing destination without replacement consent");
        await CheckPackagePreservedAsync(directory, destination, existingHash, check, "no replacement consent");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => exporter.ExportAsync(destination,
                new(ReplaceExisting: true), cancelled.Token), check, "Pre-cancelled export refuses publication");
        }
        await CheckPackagePreservedAsync(directory, destination, existingHash, check, "pre-cancellation");

        using (var cancellation = new CancellationTokenSource())
        {
            bool opened = false;
            var selected = new RelationalPackageLocalFile("cancelled.bin", 1, token =>
            {
                opened = true;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult<Stream>(new MemoryStream(new byte[] { 1 }, writable: false));
            });
            await ThrowsAsync<OperationCanceledException>(() => exporter.ExportAsync(destination,
                new(ReplaceExisting: true, LocalFiles: PackageSelectedFiles([selected])), cancellation.Token), check,
                "Cancellation after SQL streaming leaves the existing package intact");
            check(opened, "Mid-export cancellation reaches the local hook after authoritative SQL streams finish");
        }
        await CheckPackagePreservedAsync(directory, destination, existingHash, check, "mid-export cancellation");
        var shortFile = new RelationalPackageLocalFile("short.bin", 2,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[] { 1 }, writable: false)));
        await ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(destination,
            new(ReplaceExisting: true, LocalFiles: PackageSelectedFiles([shortFile]))), check,
            "Pinned local length mismatch fails rather than publishing a truncated package");
        await CheckPackagePreservedAsync(directory, destination, existingHash, check, "short local stream");
        var constrained = new RelationalPackageExporter(session, limits with { MaxCellBytes = 1024 });
        await ThrowsAsync<RelationalPackageLimitException>(() => constrained.ExportAsync(destination,
            new(ReplaceExisting: true)), check, "SQL cell budget failure never replaces a completed destination");
        await CheckPackagePreservedAsync(directory, destination, existingHash, check, "SQL streaming budget failure");

        foreach (string unsafePath in new[]
        {
            "../escape.bin", "..\\escape.bin", "/absolute.bin", "C:\\fixture.bin", "safe//empty.bin",
            "connection-settings.json", "safe/CON.txt", "safe/trailing. ", "safe/\ud800.bin"
        })
        {
            bool opened = false;
            var selected = new RelationalPackageLocalFile(unsafePath, 1, _ =>
            {
                opened = true;
                return ValueTask.FromResult<Stream>(new MemoryStream(new byte[] { 1 }, writable: false));
            });
            string label = JsonSerializer.Serialize(unsafePath);
            await ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(destination,
                new(ReplaceExisting: true, LocalFiles: PackageSelectedFiles([selected]))), check,
                "Actual package local hook rejects unsafe path " + label);
            check(!opened, "Unsafe local path is rejected before opening its stream: " + label);
            await CheckPackagePreservedAsync(directory, destination, existingHash, check, "unsafe local path " + label);
        }
        check(((byte[])Required(await fixture.DestinationSqlAsync(
            "SELECT CONVERT(varbinary(max),Text) FROM surf.TextContent WHERE ContentKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, raw.Key)), "raw fixture text")).SequenceEqual(raw.Bytes),
            "Successful and failed exports leave exact fixture SQL UTF-16 bytes unchanged");
        await using (var stage = await LegacySourceStage.ReopenAsync(migrated.StageDirectory))
        {
            await stage.VerifyUnchangedAsync(fixture.SourceConnectionString);
            check(stage.StagedDocuments.Count == 6 && stage.StagedDocuments.Values.All(x => x.SourceKind == "Sql"),
                "Package export retains the production migration's six-root frozen SQL manifest");
        }
        await fixture.VerifySourceUnchangedAsync(check);
        check(true, "Package export SQL checks complete; generated database names and owned ZIP/staging files are disposed by this fixture");
    }

    private sealed record PackageRawText(long Key, byte[] Bytes, string CodeUnits);

    private static async Task<PackageRawText> SeedPackageRawContentAsync(SqlFixture fixture, Action<bool, string> check)
    {
        var units = Enumerable.Repeat('x', 1400).ToArray();
        units[0] = '\0'; units[1] = '\ud800'; units[2] = 'A'; units[3] = '\udfff';
        units[511] = '\ud83d'; units[512] = '\ude80'; // Valid pair split across the 1024-byte export chunks.
        units[1023] = '\r'; units[1024] = '\n'; units[^1] = ' ';
        byte[] bytes = new byte[checked(units.Length * 2)];
        for (int i = 0; i < units.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), units[i]);
        // A parameterized varbinary -> nvarchar conversion bypasses .NET's surrogate replacement/rejection.
        // Change an existing referenced fixture content row, not production writers or Ready publication.
        long key = Convert.ToInt64(await fixture.DestinationSqlAsync("""
            DECLARE @Key bigint=(SELECT DescriptionContentKey FROM surf.Scope WHERE ScopeId=N'migration-scope');
            UPDATE surf.TextContent SET Text=CONVERT(nvarchar(max),@Raw),ContentHash=@Hash,
                CharacterCount=@Characters,ByteCount=@Bytes,LineCount=2 WHERE ContentKey=@Key;
            SELECT @Key;
            """, RelationalSession.Parameter("@Raw", SqlDbType.VarBinary, bytes, -1),
            RelationalSession.Parameter("@Hash", SqlDbType.Binary, SHA256.HashData(bytes), 32),
            RelationalSession.Parameter("@Characters", SqlDbType.BigInt, (long)units.Length),
            RelationalSession.Parameter("@Bytes", SqlDbType.BigInt, (long)bytes.Length)));
        var stored = (byte[])Required(await fixture.DestinationSqlAsync(
            "SELECT CONVERT(varbinary(max),Text) FROM surf.TextContent WHERE ContentKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, key)), "raw SQL UTF-16");
        check(key > 0 && stored.SequenceEqual(bytes), "Owned SQL raw content seed preserves NUL, unpaired surrogates, split pair and trailing whitespace");
        return new(key, bytes, new string(units));
    }

    private static async IAsyncEnumerable<RelationalPackageLocalFile> PackageSelectedFiles(
        IEnumerable<RelationalPackageLocalFile> files, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
        await Task.CompletedTask;
    }

    private static async Task VerifyPackageArchiveAsync(SqlFixture fixture, RelationalSession session,
        RelationalPackageExportResult result, IEnumerable<string> roots, string secret, string definition, PackageRawText raw,
        byte[] png, byte[] notes, RelationalPackageLimits limits, Action<bool, string> check)
    {
        using var archive = ZipFile.OpenRead(result.Destination);
        var manifestEntry = archive.GetEntry(RelationalPackageFormat.ManifestEntry)
            ?? throw new InvalidDataException("Export has no manifest entry.");
        RelationalPackageManifest manifest;
        await using (var input = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<RelationalPackageManifest>(input)
                ?? throw new InvalidDataException("Export manifest is null.");
        SameModel(result.Manifest, manifest, check, "ZIP manifest exactly matches the production export result");
        await using (var input = manifestEntry.Open())
            check(string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input)), result.ManifestSha256, StringComparison.OrdinalIgnoreCase),
                "ZIP manifest checksum independently matches its actual entry bytes");
        check(manifest.PackageIdentifier == RelationalPackageFormat.Identifier && manifest.PackageVersion == 2 &&
            manifest.RowEncodingVersion == 1 && manifest.DatabaseSchemaVersion == PersistenceFormatProbe.SupportedSchemaVersion &&
            manifest.DatabaseFormatIdentifier == PersistenceFormatProbe.FormatIdentifier &&
            Equals(await fixture.DestinationSqlAsync("SELECT DatabaseIdentity FROM surf.StorageFormatInfo;"), manifest.SourceDatabaseIdentity),
            "Package manifest carries exact supported package/database schema and generated source identity");
        string manifestJson = JsonSerializer.Serialize(manifest);
        check(!manifestJson.Contains(secret, StringComparison.Ordinal) &&
            !manifestJson.Contains(new SqlConnectionStringBuilder(fixture.SourceConnectionString).InitialCatalog, StringComparison.Ordinal) &&
            !manifestJson.Contains(new SqlConnectionStringBuilder(fixture.DestinationConnectionString).InitialCatalog, StringComparison.Ordinal) &&
            !manifestJson.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase) &&
            !manifestJson.Contains(fixture.OwnedDirectory, StringComparison.OrdinalIgnoreCase),
            "Manifest contains no fixture connection credentials, SQL database names or staging paths");
        var expectedEntries = manifest.Tables.Select(x => x.Entry).Concat(manifest.LocalFiles.Select(x => x.Entry))
            .Append(RelationalPackageFormat.ManifestEntry).Order(StringComparer.Ordinal).ToArray();
        check(archive.Entries.Select(x => x.FullName).Order(StringComparer.Ordinal).SequenceEqual(expectedEntries) &&
            expectedEntries.Distinct(StringComparer.OrdinalIgnoreCase).Count() == expectedEntries.Length &&
            archive.Entries.Sum(x => x.Length) == result.UncompressedBytes,
            "ZIP has exactly declared unique entries and exact total uncompressed byte count");
        var names = manifest.Tables.Select(x => x.Table.Schema + "." + x.Table.Name).ToHashSet(StringComparer.Ordinal);
        check(PackageExpectedTables.All(names.Contains) && names.Count == PackageExpectedTables.Length +
            manifest.Tables.Count(x => x.Table.Schema == "capture") &&
            names.All(x => PackageExpectedTables.Contains(x, StringComparer.Ordinal) ||
                x.StartsWith("capture.Data_", StringComparison.Ordinal)),
            "Package includes authoritative graph/history/state/content tables and generated captures only");
        check(!archive.Entries.Any(x => x.FullName.Contains("Surf2Documents", StringComparison.OrdinalIgnoreCase) ||
            x.FullName.Contains("connection-settings", StringComparison.OrdinalIgnoreCase) || x.FullName.Contains("fixturePackage", StringComparison.Ordinal)),
            "ZIP excludes legacy whole-root JSON, bootstrap connection settings and the credential-canary table");

        bool foundRaw = false, foundDefinition = false, foundInlineImageText = false;
        var assetHashes = new HashSet<string>(StringComparer.Ordinal);
        var legacyRoots = roots.ToHashSet(StringComparer.Ordinal);
        long capturedRows = 0;
        await using var connection = await session.OpenAsync();
        foreach (var stream in manifest.Tables)
        {
            ValidatePackageFixtureTable(stream.Table);
            var entry = archive.GetEntry(stream.Entry) ?? throw new InvalidDataException("Missing declared table stream.");
            await using (var input = entry.Open())
                check(entry.Length == stream.ByteCount && string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input)), stream.Sha256, StringComparison.OrdinalIgnoreCase),
                    "Package entry checksum/length matches actual ZIP bytes: " + stream.Entry);
            await VerifyPackageSchemaAsync(connection, stream.Table, check);
            long sqlRows = await PackageSelectedSqlCountAsync(connection, stream.Table);
            long rows = 0;
            var columns = stream.Table.Columns.Where(x => x.StreamOrdinal.HasValue).OrderBy(x => x.StreamOrdinal).ToArray();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            await using (var input = entry.Open())
            using (var lines = new StreamReader(input, new UTF8Encoding(false, true)))
            {
                while (await lines.ReadLineAsync() is { } line)
                {
                    using var json = JsonDocument.Parse(line);
                    JsonElement row = json.RootElement;
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columns.Length)
                        throw new InvalidDataException("Package NDJSON row width differs from schema.");
                    string key = string.Join("|", stream.Table.PrimaryKey.Select(name =>
                        row[columns.Single(x => x.Name == name).StreamOrdinal!.Value].GetRawText()));
                    if (!keys.Add(key)) throw new InvalidDataException("Duplicate stable primary key in exported stream.");
                    for (int i = 0; i < columns.Length; i++)
                    {
                        if (row[i].ValueKind == JsonValueKind.Null)
                        {
                            if (!columns[i].Nullable) throw new InvalidDataException("Package has NULL in a required SQL column.");
                            continue;
                        }
                        if (columns[i].Encoding is not ("utf16le-chunks-v1" or "binary-chunks-v1" or "sql-codepage-chunks-v1")) continue;
                        byte[] bytes = DecodePackageCell(row[i], limits.ChunkBytes);
                        if (columns[i].Encoding == "utf16le-chunks-v1")
                        {
                            string text = DecodePackageUtf16Units(bytes);
                            check(!text.Contains(secret, StringComparison.Ordinal) && !legacyRoots.Contains(text),
                                "Authoritative text cell excludes credential canary and whole legacy JSON root: " + stream.Table.Name + "." + columns[i].Name);
                            foundDefinition |= stream.Table.Name == "TextContent" && columns[i].Name == "Text" && text == definition;
                            foundInlineImageText |= stream.Table.Name == "TextContent" && columns[i].Name == "Text" && text == Convert.ToBase64String(Png(17)) + "\r\n";
                            if (stream.Table.Name == "TextContent" && columns[i].Name == "Text" &&
                                row[columns.Single(x => x.Name == "ContentKey").StreamOrdinal!.Value].GetString() == raw.Key.ToString(CultureInfo.InvariantCulture))
                            {
                                foundRaw = bytes.SequenceEqual(raw.Bytes) && text == raw.CodeUnits && row[i].GetProperty("chunks").GetArrayLength() >= 3;
                            }
                        }
                        if (stream.Table.Name == "Asset" && columns[i].Name == "Bytes")
                            assetHashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                        if ((stream.Table.Name == "TextContent" && columns[i].Name == "Text") ||
                            (stream.Table.Name == "Asset" && columns[i].Name == "Bytes"))
                        {
                            byte[] hash = DecodePackageCell(row[columns.Single(x => x.Name == "ContentHash").StreamOrdinal!.Value], limits.ChunkBytes);
                            long declaredBytes = long.Parse(row[columns.Single(x => x.Name == "ByteCount").StreamOrdinal!.Value].GetString()!, CultureInfo.InvariantCulture);
                            check(declaredBytes == bytes.Length && hash.SequenceEqual(SHA256.HashData(bytes)),
                                "Exported content/asset row hash and length match its decoded payload: " + stream.Table.Name);
                        }
                    }
                    rows++;
                }
            }
            check(rows == stream.RowCount && rows == sqlRows,
                "Package NDJSON row count agrees with independent fixture SQL selection: " + stream.Entry);
            if (stream.Table.Schema == "capture") capturedRows += rows;
        }
        check(foundRaw, "Exported raw UTF-16 chunks preserve exact code units without replacement, including a pair split between chunks");
        check(foundDefinition && foundInlineImageText &&
            assetHashes.Contains(Convert.ToHexString(SHA256.HashData(Png(17)))) &&
            assetHashes.Contains(Convert.ToHexString(SHA256.HashData(Png(59)))),
            "Package text and binary asset streams retain SQL definitions, inline PNG text and both diagram/settings PNG bytes");
        check(capturedRows > 0 && capturedRows == Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT SUM(ActualRowCount) FROM surf.DataSet;")),
            "Package generated capture streams cover current and reverse mixed metadata/data history rows");
        foreach ((string name, byte[] expected) in new[] { ("local-files/images/fixture.png", png), ("local-files/notes/fixture.txt", notes) })
        {
            var selected = manifest.LocalFiles.Single(x => x.Entry == name);
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing explicit local stream.");
            await using var input = entry.Open();
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            check(copy.ToArray().SequenceEqual(expected) && selected.ByteCount == expected.Length &&
                string.Equals(selected.Sha256, Convert.ToHexString(SHA256.HashData(expected)), StringComparison.OrdinalIgnoreCase),
                "Explicit local PNG/text stream preserves exact bytes, safe path, length and checksum: " + name);
        }
        check(manifest.LocalFiles.Count == 2, "Package includes no implicitly crawled or user-directory local files");
    }

    private static readonly string[] PackageExpectedTables =
    [
        "surf.UserProfile", "surf.DatabaseSnapshot", "surf.SnapshotResource", "surf.SnapshotResourceRevision",
        "surf.DatabaseObjectRevision", "surf.TableMetadataRevision", "surf.TableDataRevision", "surf.TableDataRevisionMetadata",
        "surf.TableColumnRevision", "surf.PrimaryKeyConstraint", "surf.PrimaryKeyColumn", "surf.SnapshotCurrentColumn",
        "surf.SnapshotCurrentPrimaryKey", "surf.FullDataTableSelection", "surf.SnapshotHistory", "surf.SnapshotVersion",
        "surf.SnapshotChange", "surf.DataSet", "surf.DataLayout", "surf.DataColumn", "surf.DataValueException",
        "surf.ScopeCatalogueState", "surf.Scope", "surf.ScopeResource", "surf.VirtualFolder", "surf.VirtualFolderMember",
        "surf.Diagram", "surf.Workbench", "surf.DiagramRevision", "surf.DiagramObject", "surf.Workflow", "surf.WorkflowItem",
        "surf.DiagramWorkflowBinding", "surf.WorkflowItemMarker", "surf.DiagramPortalTarget", "surf.QueryItem",
        "surf.WorkspaceSession", "surf.DocumentWindowState", "surf.DocumentWindowFilter", "surf.WorkspaceUnloadedResource",
        "surf.ReferenceConnectionLine", "surf.ApplicationPreference", "surf.ExtensionAppearance", "surf.ReferenceStyle",
        "surf.DiagramImageDefinition", "surf.TextContent", "surf.Asset"
    ];

    private static void ValidatePackageFixtureTable(RelationalPackageTable table)
    {
        if (PackageExpectedTables.Contains(table.Schema + "." + table.Name, StringComparer.Ordinal)) return;
        if (table.Schema == "capture" && table.Name.StartsWith("Data_", StringComparison.Ordinal) &&
            long.TryParse(table.Name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out long key) &&
            key > 0 && table.Name == "Data_" + key.ToString(CultureInfo.InvariantCulture)) return;
        throw new InvalidDataException("Unexpected SQL table in fixture export manifest.");
    }

    private static string PackageSqlTable(RelationalPackageTable table) =>
        "[" + table.Schema + "].[" + table.Name + "]"; // Identifiers are restricted by ValidatePackageFixtureTable.

    private static async Task<long> PackageSelectedSqlCountAsync(SqlConnection connection, RelationalPackageTable table)
    {
        string predicate = table.Name switch
        {
            "TextContent" => "ContentKey IN (" + PackageContentReferenceSql(false) + ")",
            "Asset" => "AssetKey IN (" + PackageContentReferenceSql(true) + ")",
            _ => "1=1"
        };
        // This fully completed fixture has no unpublished snapshots, partial revisions or incomplete captures.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM " + PackageSqlTable(table) + " WHERE " + predicate + ";";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string PackageContentReferenceSql(bool asset)
    {
        (string Table, string[] Columns)[] references = asset
            ? [("DiagramObject", ["ImageAssetKey", "PastedAssetKey", "PastedFallbackAssetKey"]), ("DiagramImageDefinition", ["AssetKey"])]
            : [("DatabaseObjectRevision", ["DefinitionContentKey"]), ("Scope", ["DescriptionContentKey"]),
                ("ScopeResource", ["DetailsOverrideContentKey"]), ("DiagramObject", ["ImageDataBase64ContentKey", "LabelTextContentKey", "DocumentationXamlContentKey"]),
                ("WorkflowItem", ["ItemDescriptionContentKey", "ItemDocumentationXamlContentKey"]), ("QueryItem", ["QueryDescriptionContentKey"]),
                ("DiagramImageDefinition", ["RegexContentKey", "NameRegexContentKey", "ContentRegexContentKey", "ImageDataBase64ContentKey"])];
        return string.Join(" UNION ", references.SelectMany(x => x.Columns.Select(column => "SELECT [" + column + "] FROM surf.[" + x.Table + "]")));
    }

    private static async Task VerifyPackageSchemaAsync(SqlConnection connection, RelationalPackageTable table, Action<bool, string> check)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.column_id,c.name,TYPE_NAME(c.system_type_id),CONVERT(int,c.max_length),c.precision,c.scale,
                c.is_nullable,c.is_identity,c.is_computed,c.collation_name,
                CONVERT(int,COLLATIONPROPERTY(c.collation_name,'CodePage')),
                CONVERT(nvarchar(100),i.seed_value),CONVERT(nvarchar(100),i.increment_value),CONVERT(nvarchar(100),i.last_value),cc.definition
            FROM sys.columns c LEFT JOIN sys.identity_columns i ON i.object_id=c.object_id AND i.column_id=c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            WHERE c.object_id=OBJECT_ID(@Table,'U') ORDER BY c.column_id;
            SELECT c.name FROM sys.key_constraints pk JOIN sys.index_columns k ON k.object_id=pk.parent_object_id AND k.index_id=pk.unique_index_id
            JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
            WHERE pk.parent_object_id=OBJECT_ID(@Table,'U') AND pk.type='PK' AND k.key_ordinal>0 ORDER BY k.key_ordinal;
            SELECT fk.name,OBJECT_SCHEMA_NAME(fk.referenced_object_id),OBJECT_NAME(fk.referenced_object_id),pc.name,rc.name
            FROM sys.foreign_keys fk JOIN sys.foreign_key_columns k ON k.constraint_object_id=fk.object_id
            JOIN sys.columns pc ON pc.object_id=k.parent_object_id AND pc.column_id=k.parent_column_id
            JOIN sys.columns rc ON rc.object_id=k.referenced_object_id AND rc.column_id=k.referenced_column_id
            WHERE fk.parent_object_id=OBJECT_ID(@Table,'U') ORDER BY fk.name COLLATE Latin1_General_100_BIN2,k.constraint_column_id;
            """;
        command.Parameters.Add(RelationalSession.Parameter("@Table", SqlDbType.NVarChar, PackageSqlTable(table), 256));
        await using var reader = await command.ExecuteReaderAsync();
        bool matches = true;
        int count = 0, ordinal = 0;
        while (await reader.ReadAsync())
        {
            if (count >= table.Columns.Count) { matches = false; continue; }
            var c = table.Columns[count++];
            bool rowVersion = reader.GetString(2) is "timestamp" or "rowversion";
            bool included = !rowVersion && !reader.GetBoolean(8);
            matches &= c.SqlColumnId == reader.GetInt32(0) && c.Name == reader.GetString(1) && c.SqlType == reader.GetString(2) &&
                c.MaxLengthBytes == reader.GetInt32(3) && c.Precision == reader.GetByte(4) && c.Scale == reader.GetByte(5) &&
                c.Nullable == reader.GetBoolean(6) && c.Identity == reader.GetBoolean(7) && c.Computed == reader.GetBoolean(8) &&
                c.Collation == (reader.IsDBNull(9) ? null : reader.GetString(9)) && c.CodePage == (reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10)) &&
                c.IdentitySeed == (reader.IsDBNull(11) ? null : reader.GetString(11)) &&
                c.IdentityIncrement == (reader.IsDBNull(12) ? null : reader.GetString(12)) &&
                c.IdentityLastValue == (reader.IsDBNull(13) ? null : reader.GetString(13)) &&
                c.ComputedDefinition == (reader.IsDBNull(14) ? null : reader.GetString(14)) &&
                c.RowVersion == rowVersion && c.StreamOrdinal == (included ? ordinal++ : (int?)null) &&
                c.RestorePolicy == (rowVersion ? "RestampRowVersion" : c.Computed ? "RegenerateComputed" : c.Identity ? "PreserveIdentityAndReseed" : "PreserveValue");
            string? expectedEncoding = !included ? null : c.SqlType switch
            {
                "nvarchar" or "nchar" => "utf16le-chunks-v1",
                "varchar" or "char" => "sql-codepage-chunks-v1",
                "binary" or "varbinary" => "binary-chunks-v1",
                "bigint" or "int" or "smallint" or "tinyint" => "integer-string-v1",
                "bit" => "boolean-v1",
                "float" when c.Precision <= 24 => "float32-bits-v1",
                "float" => "float64-bits-v1",
                "uniqueidentifier" => "guid-string-v1",
                "datetimeoffset" => "datetimeoffset-string-v1",
                _ => throw new InvalidDataException("Unexpected SQL type in the bounded package fixture: " + c.SqlType)
            };
            matches &= c.Encoding == expectedEncoding;
        }
        matches &= count == table.Columns.Count;
        await reader.NextResultAsync();
        var primaryKey = new List<string>();
        while (await reader.ReadAsync()) primaryKey.Add(reader.GetString(0));
        matches &= primaryKey.SequenceEqual(table.PrimaryKey);
        await reader.NextResultAsync();
        var relationships = new List<string>();
        while (await reader.ReadAsync()) relationships.Add(string.Join("|", Enumerable.Range(0, 5).Select(reader.GetString)));
        var declared = table.ForeignKeys.OrderBy(x => x.Name, StringComparer.Ordinal).SelectMany(fk =>
            fk.Columns.Zip(fk.ReferencedColumns, (from, to) => string.Join("|", fk.Name, fk.ReferencedSchema, fk.ReferencedTable, from, to)));
        matches &= relationships.SequenceEqual(declared);
        check(matches, "Package schema matches actual SQL columns/identity/rowversion policies/primary and foreign keys: " + table.Schema + "." + table.Name);
    }

    private static byte[] DecodePackageCell(JsonElement cell, int chunkLimit)
    {
        long length = long.Parse(cell.GetProperty("byteLength").GetString()!, CultureInfo.InvariantCulture);
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("Unexpectedly large small-fixture package cell.");
        using var output = new MemoryStream();
        foreach (var chunk in cell.GetProperty("chunks").EnumerateArray())
        {
            byte[] bytes = chunk.GetBytesFromBase64();
            if (bytes.Length == 0 || bytes.Length > chunkLimit) throw new InvalidDataException("Package cell exceeds its structural chunk budget.");
            output.Write(bytes);
        }
        if (output.Length != length) throw new InvalidDataException("Package chunk bytes do not match the declared cell length.");
        return output.ToArray();
    }

    private static string DecodePackageUtf16Units(byte[] bytes)
    {
        if (bytes.Length % 2 != 0) throw new InvalidDataException("SQL UTF-16 cell has an odd byte length.");
        var units = new char[bytes.Length / 2];
        for (int i = 0; i < units.Length; i++) units[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return new string(units); // Deliberately no Encoding decoder: even invalid SQL code units must survive.
    }

    private static async Task<byte[]> PackageFileHashAsync(string path)
    {
        await using var input = File.OpenRead(path);
        return await SHA256.HashDataAsync(input);
    }

    private static async Task CheckPackagePreservedAsync(string directory, string destination, byte[] hash,
        Action<bool, string> check, string reason)
    {
        check((await PackageFileHashAsync(destination)).SequenceEqual(hash), "Existing destination remains byte-identical after " + reason);
        check(Directory.GetFiles(directory, ".*.pending", SearchOption.TopDirectoryOnly).Length == 0,
            "Failed export removes its own pending ZIP after " + reason);
    }
}

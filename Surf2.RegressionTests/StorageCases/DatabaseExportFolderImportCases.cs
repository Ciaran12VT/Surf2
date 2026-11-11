using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private static DatabaseMetadataSnapshot ExportFolderModel() => new()
    {
        DisplayName = "Export fixture", DatabaseName = "Original database", ImportedAtUtc = EvidenceTime,
        Objects = [
            new() { SchemaName = "reporting.v2", ObjectName = "Re]calculate", Kind = SqlDatabaseObjectKind.StoredProcedure,
                Definition = "CREATE OR ALTER PROCEDURE [reporting.v2].[Re]]calculate] AS SELECT 1;\r\n" },
            new() { SchemaName = "dbo", ObjectName = "Labels", Kind = SqlDatabaseObjectKind.View,
                Definition = "CREATE VIEW [dbo].[Labels] AS SELECT [Label] FROM [audit.v2].[Items.rows];" },
            new() { SchemaName = "dbo", ObjectName = "Answer", Kind = SqlDatabaseObjectKind.Function,
                Definition = "CREATE FUNCTION [dbo].[Answer]() RETURNS int AS BEGIN RETURN 42; END;" },
            new() { SchemaName = "dbo", ObjectName = "Changed", Kind = SqlDatabaseObjectKind.Trigger,
                Definition = "CREATE TRIGGER [dbo].[Changed] ON [audit.v2].[Items.rows] AFTER INSERT AS SELECT 1;" } ],
        Tables = [new() { SchemaName = "audit.v2", TableName = "Items.rows", HasFullData = true, FullDataRowCount = 3 },
            new() { SchemaName = "dbo", TableName = "Blank", HasFullData = true, FullDataRowCount = 1 }],
        Columns = [new() { SchemaName = "audit.v2", TableName = "Items.rows", ColumnName = "Id", DataType = "varchar", MaxLength = 20, Ordinal = 3, IsIdentity = true },
            new() { SchemaName = "audit.v2", TableName = "Items.rows", ColumnName = "La]bel", DataType = "nvarchar", MaxLength = 80, IsNullable = true, Ordinal = 8 },
            new() { SchemaName = "audit.v2", TableName = "Items.rows", ColumnName = "Amount", DataType = "decimal", NumericPrecision = 10, NumericScale = 2, Ordinal = 9 },
            new() { SchemaName = "dbo", TableName = "Blank", ColumnName = "Value", DataType = "int", Ordinal = 1 }],
        PrimaryKeys = [new() { SchemaName = "audit.v2", TableName = "Items.rows", ColumnName = "Id", ConstraintName = "PK_Source", KeyOrdinal = 1 }],
        TableDataSets = [new() { SchemaName = "audit.v2", TableName = "Items.rows", RowCount = 3, ImportedAtUtc = EvidenceTime, Rows = [
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Id"] = "001", ["La]bel"] = "comma, quote\" and\r\nnewline", ["Amount"] = "1.20" }),
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Id"] = "", ["La]bel"] = "", ["Amount"] = "" }),
            JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Id"] = 7, ["La]bel"] = null, ["Amount"] = true }) ] },
            new() { SchemaName = "dbo", TableName = "Blank", RowCount = 1, ImportedAtUtc = EvidenceTime, Rows = [JsonSerializer.SerializeToElement(new { Value = "" })] }],
        FullDataTableNames = ["[audit.v2].[Items.rows] TOP 3", "[dbo].[Blank] WHERE Value IS NOT NULL"]
    };

    private static void VerifyImportedFolder(DatabaseMetadataSnapshot source, DatabaseExportFolderImportResult result, Action<bool, string> check, string label)
    {
        var snapshot = result.Snapshot;
        check(source.Objects.All(expected => snapshot.Objects.Any(actual => actual.SchemaName == expected.SchemaName && actual.ObjectName == expected.ObjectName &&
            actual.Kind == expected.Kind && actual.Definition == expected.Definition)), label + " preserves all four object kinds, exact definitions and escaped/dotted names");
        var trigger = snapshot.Objects.Single(x => x.Kind == SqlDatabaseObjectKind.Trigger);
        var columns = snapshot.Columns.Where(x => x.TableName == "Items.rows").ToList();
        check(trigger.ParentSchemaName == "audit.v2" && trigger.ParentObjectName == "Items.rows" && columns.Count == 3 &&
            columns[0].ColumnName == "Id" && columns[0].IsIdentity && !columns[0].IsNullable && columns[0].MaxLength == 20 &&
            columns[1].ColumnName == "La]bel" && columns[1].MaxLength == 80 && columns[1].IsNullable &&
            columns[2].NumericPrecision == 10 && columns[2].NumericScale == 2, label + " preserves available column metadata and qualified trigger parent");
        var rows = snapshot.GetTableDataSet("audit.v2", "Items.rows")!.Rows;
        check(rows.Count == 3 && rows[0].GetProperty("Id").GetString() == "001" && rows[0].GetProperty("La]bel").GetString() == "comma, quote\" and\r\nnewline" &&
            rows[1].EnumerateObject().All(x => x.Value.GetString() == "") && rows[2].GetProperty("Id").ValueKind == JsonValueKind.String &&
            rows[2].GetProperty("Id").GetString() == "7" && rows[2].GetProperty("La]bel").GetString() == "" && rows[2].GetProperty("Amount").GetString() == "True" &&
            snapshot.GetTableDataSet("dbo", "Blank")!.Rows.Single().GetProperty("Value").GetString() == "",
            label + " reads quoted commas/quotes/newlines, leading zeros, all-empty records and display-only CSV types");
        check(snapshot.DatabaseName == "" && snapshot.ImportedAtUtc != source.ImportedAtUtc && snapshot.PrimaryKeys.Single().ConstraintName == "" &&
            snapshot.PrimaryKeys.Single().KeyOrdinal == 0 && columns.Select(x => x.Ordinal).SequenceEqual(new[] { 1, 2, 3 }) &&
            snapshot.FullDataTableNames.All(x => !x.Contains(" TOP ") && !x.Contains(" WHERE ")) && result.Warnings.Count >= 3,
            label + " reports missing export metadata without inventing source timestamps, PK names/order, predicates or JSON types");
    }

    public static async Task RunDatabaseExportFolderImportChecksAsync(Action<bool, string> check)
    {
        string name = "Surf2_ExportImport_" + Guid.NewGuid().ToString("N");
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(directory);
        try
        {
            var model = ExportFolderModel(); var importer = new DatabaseExportFolderImportService();
            string archive = Path.Combine(directory, "master.zip");
            new DatabaseExportService(new DatabaseDocumentService()).Export(model, archive);
            string Extract(string suffix) { string folder = Path.Combine(directory, suffix); ZipFile.ExtractToDirectory(archive, folder); return folder; }
            string valid = Extract("valid");
            var before = Directory.GetFiles(valid, "*", SearchOption.AllDirectories).ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(x)));
            var imported = await importer.ImportAsync(valid, "Selected label");
            VerifyImportedFolder(model, imported, check, "Master export-folder roundtrip");
            check(imported.Snapshot.DisplayName == "Selected label" && before.All(x => SHA256.HashData(File.ReadAllBytes(x.Key)).SequenceEqual(x.Value)),
                "Folder import uses the requested label and leaves every source file byte-for-byte unchanged");
            foreach (var (suffix, content) in new[] {
                ("unclosed", "Id,Value\r\n1,\"missing close"), ("stray", "Id,Value\n1,bad\"quote\n"),
                ("afterquote", "Id,Value\n1,\"closed\"junk\n"), ("width", "Id,Value\n1,2,3\n"),
                ("duplicate", "Id,id\n1,2\n"), ("emptyheader", "Id,\n1,2\n") })
            {
                string folder = Extract(suffix);
                await File.WriteAllTextAsync(Path.Combine(folder, "Table Data", "audit.v2.Items.rows.csv"), content, new UTF8Encoding(false));
                await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(folder), check, "Folder import rejects malformed CSV: " + suffix);
            }
            string missingData = Extract("missing-data"); File.Delete(Path.Combine(missingData, "Table Data", "dbo.Blank.csv"));
            await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(missingData), check, "Folder import refuses a missing CSV for a table marked as exported full data");
            string unknown = Extract("unknown"); await File.WriteAllTextAsync(Path.Combine(unknown, "unrelated.txt"), "unrelated");
            await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(unknown), check, "Folder import rejects unrelated files instead of silently ignoring them");
            string orphan = Extract("orphan"); File.Delete(Path.Combine(orphan, "Tables", "dbo.Blank.txt"));
            await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(orphan), check, "Named CSV without identity-bearing table document is refused");
            string duplicate = Extract("duplicate-table"); File.Copy(Path.Combine(duplicate, "Tables", "dbo.Blank.txt"), Path.Combine(duplicate, "Tables", "copy.txt"));
            await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(duplicate), check, "Folder import rejects duplicate table identity even under distinct filenames");
            var collisions = new DatabaseMetadataSnapshot { Tables = [new() { TableName = "same?" }, new() { TableName = "same*" }],
                TableDataSets = [new() { TableName = "same?", Rows = [JsonSerializer.SerializeToElement(new { Id = 1 })] }] };
            string collisionZip = Path.Combine(directory, "collision.zip"); new DatabaseExportService(new DatabaseDocumentService()).Export(collisions, collisionZip);
            string collisionFolder = Path.Combine(directory, "collision"); ZipFile.ExtractToDirectory(collisionZip, collisionFolder);
            await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(collisionFolder), check, "Sanitized filename collisions cannot silently attach CSV rows to the wrong table");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => importer.ImportAsync(valid, ct: cancellation.Token), check, "Folder import supports cancellation without publishing a partial snapshot");
        }
        finally
        {
            // Only the absolute temporary directory created by this method may be removed.
            if (Path.GetDirectoryName(directory) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) && Path.GetFileName(directory) == name)
                Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task RunRelationalExportFolderImportChecksAsync(Action<bool, string> check)
    {
        var models = MigrationModels(); var model = ExportFolderModel();
        model.SnapshotId = models.Snapshots.Snapshots[0].SnapshotId;
        models.Snapshots.Snapshots.Clear(); models.Snapshots.Snapshots.Add(model); models.Snapshots.Histories.Clear();
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        Directory.CreateDirectory(fixture.OwnedDirectory);
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(fixture.DestinationConnectionString));
        var exporter = new RelationalSnapshotHistoryService(runtime); var importer = new DatabaseExportFolderImportService();
        var summary = (await runtime.Snapshots.ListSnapshotsAsync()).Items.Single();
        long opaqueVersionKey = 0;
        check(summary.CurrentVersionKey is null, "Migrated no-history fixture exports a nullable current VersionKey");
        foreach (bool historical in new[] { false, true })
        {
            string label = historical ? "history" : "current";
            string zip = Path.Combine(fixture.OwnedDirectory, label + ".zip"), folder = Path.Combine(fixture.OwnedDirectory, label);
            if (historical)
            {
                var versions = await runtime.Snapshots.ListVersionsAsync(summary.SnapshotKey);
                long initial = versions.Items.Single(x => x.IsInitial).VersionKey;
                await exporter.ExportVersionAsync(summary.SnapshotKey, initial, zip);
            }
            else await exporter.ExportCurrentAsync(new(runtime.Session.Epoch, summary.SnapshotKey, summary.RowVersion), zip);
            ZipFile.ExtractToDirectory(zip, folder);
            var result = await importer.ImportAsync(folder);
            VerifyImportedFolder(model, result, check, "V2 " + label + " export-folder roundtrip");
            if (!historical)
            {
                long scopeKey = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT ScopeKey FROM surf.Scope WHERE ScopeId=N'migration-scope';"));
                var scope = (await runtime.StateStore.ReadScopeAsync(scopeKey))!;
                var saved = await new RelationalSnapshotEditorService(runtime).SaveAsync(result.Snapshot, scope.Value, scope.Token, null);
                var persistedScope = Required(await runtime.StateStore.ReadScopeAsync(scopeKey), "imported database scope");
                var membership = persistedScope.Value.Resources.Single(resource => resource.Path == saved.Snapshot.SnapshotId);
                check(saved.Snapshot.DatabaseName.Length == 0 && membership.DisplayName == label && membership.Details == label,
                    "An export-folder snapshot without a database name displays its folder title in both scope columns");
                var reopened = await new RelationalSnapshotEditorService(runtime).ReadAsync(saved.Snapshot);
                check(reopened.Objects.Count == 4 && reopened.GetTableDataSet("dbo", "Blank")!.Rows.Count == 1 && saved.Snapshot.SnapshotKey != summary.SnapshotKey,
                    "Imported display export can be published as a distinct SQL snapshot with blank rows intact");
                var editedSource = model.Clone(); editedSource.Objects[0].Definition += "-- edited after export\r\n";
                editedSource.Objects.Add(new() { SchemaName = "dbo", ObjectName = "EmptyView", Kind = SqlDatabaseObjectKind.View, Definition = "" });
                editedSource.Objects.Add(new() { SchemaName = "dbo", ObjectName = "OpaqueProc", Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = "-- exported original text without a SQL declaration\r\n" });
                opaqueVersionKey = (await new RelationalSnapshotEditorService(runtime).SaveAsync(editedSource, saved.UpdatedScope, saved.UpdatedScopeToken,
                    new(runtime.Session.Epoch, summary.SnapshotKey, summary.RowVersion))).VersionKey;
                string metadata = Directory.GetFiles(Path.Combine(folder, "resources"), "*.json").First();
                string originalMetadata = await File.ReadAllTextAsync(metadata);
                var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalMetadata)!;
                value["TextEncoding"] = JsonSerializer.SerializeToElement("unknown");
                await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(value));
                await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(folder), check, "V2 export import rejects unsupported sidecar encoding");
                value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalMetadata)!;
                value["VersionKey"] = JsonSerializer.SerializeToElement(long.MaxValue);
                await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(value));
                await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(folder), check, "V2 export import rejects resource sidecars mixed from different versions");
                await File.WriteAllTextAsync(metadata, originalMetadata);
                string sql = Directory.GetFiles(Path.Combine(folder, "resources"), "*.sql").First();
                byte[] originalSql = await File.ReadAllBytesAsync(sql);
                await File.WriteAllBytesAsync(sql, originalSql[..^1]);
                await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(folder), check, "V2 export import rejects damaged raw UTF-16 without replacing malformed bytes");
            }
            else
            {
                foreach (string metadata in Directory.GetFiles(Path.Combine(folder, "resources"), "*.json"))
                {
                    var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(metadata))!;
                    value.Remove("ObjectKind"); await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(value));
                }
                var legacy = await importer.ImportAsync(folder);
                check(model.Objects.All(expected => legacy.Snapshot.Objects.Any(actual => actual.Kind == expected.Kind && actual.ObjectName == expected.ObjectName && actual.Definition == expected.Definition)),
                    "Older historical sidecars without ObjectKind still recover all four kinds from parseable declarations");
                string body = Directory.GetFiles(Path.Combine(folder, "resources"), "*.sql").First(); File.Delete(body);
                await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(folder), check, "V2 export import refuses a missing resource body");
            }
        }
        string opaqueZip = Path.Combine(fixture.OwnedDirectory, "opaque-history.zip"), opaqueFolder = Path.Combine(fixture.OwnedDirectory, "opaque-history");
        await exporter.ExportVersionAsync(summary.SnapshotKey, opaqueVersionKey, opaqueZip);
        ZipFile.ExtractToDirectory(opaqueZip, opaqueFolder);
        var opaque = await importer.ImportAsync(opaqueFolder);
        check(opaque.Snapshot.Objects.Single(x => x.ObjectName == "EmptyView") is { Kind: SqlDatabaseObjectKind.View, Definition.Length: 0 } &&
            opaque.Snapshot.Objects.Single(x => x.ObjectName == "OpaqueProc") is { Kind: SqlDatabaseObjectKind.StoredProcedure, Definition: "-- exported original text without a SQL declaration\r\n" },
            "Genuine historical exports retain authoritative object kinds for empty and opaque SQL definitions");
        string firstMetadata = Path.Combine(opaqueFolder, "resources", "00000000.json");
        var firstValue = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(firstMetadata))!;
        firstValue["ObjectKind"] = JsonSerializer.SerializeToElement(99);
        await File.WriteAllTextAsync(firstMetadata, JsonSerializer.Serialize(firstValue));
        await ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(opaqueFolder), check, "Historical import refuses invalid authoritative ObjectKind values");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

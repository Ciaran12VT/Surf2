using System.Data;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Packages;

public static partial class StorageRegressionSuite
{
    private static async Task VerifySchemaOnePackageImportAsync(SqlFixture source,
        RelationalPackageExportResult currentPackage, RelationalPackageLimits limits, Action<bool, string> check)
    {
        var session = new RelationalSession(source.DestinationConnectionString);
        var capture = new RelationalCaptureStore(session);
        var datasetKeys = new List<long>();
        await using var connection = await session.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DataSetKey FROM surf.DataSet ORDER BY DataSetKey;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) datasetKeys.Add(reader.GetInt64(0));
        }
        var layouts = new Dictionary<long, PackageTableSpec>();
        foreach (long key in datasetKeys)
        {
            var descriptor = await capture.GetDescriptorAsync(key);
            var layout = new CaptureLayout(descriptor.Columns);
            long layoutKey = descriptor.Summary.LayoutKey;
            if (layouts.TryAdd(layoutKey, PackageTableCatalogue.Capture(layoutKey, layout.Columns.Length)))
                await source.DestinationSqlAsync(layout.CreateTableSql(layoutKey));
            var values = new List<JsonElement>();
            await foreach (var row in capture.StreamRowsAsync(key)) values.Add(row.Value);
            long ordinal = 0;
            var handle = new CaptureWriteHandle(session.Epoch, key, layoutKey);
            var captureLimits = new CaptureLimits();
            foreach (JsonElement[] rows in values.Chunk(64))
            {
                using var batch = new CaptureBulkBatch(handle, layout,
                    rows.Select(row => CaptureRowCodec.Encode(layout, row, captureLimits)).ToArray(), ordinal, captureLimits);
                foreach (var (data, destination) in new[]
                {
                    (batch.Rows, CaptureLayout.QualifiedTable(layoutKey)), (batch.Exceptions, "surf.DataValueException")
                })
                {
                    using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null)
                        { DestinationTableName = destination };
                    foreach (DataColumn column in data.Columns) copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    await copy.WriteToServerAsync(data);
                }
                ordinal += rows.Length;
            }
        }

        string packagePath = Path.Combine(source.OwnedDirectory, "schema-one-captures.zip");
        var exporter = new RelationalPackageExporter(session, limits);
        var budget = new PackageBudget(limits);
        var streams = new List<RelationalPackageTableStream>();
        // Exercise the legacy wire representation using the actual SQL layout rows.
        // Private serializers avoid reopening a legacy database through the runtime guard.
        var metadataMethod = typeof(RelationalPackageExporter).GetMethod("ReadMetadataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rowsMethod = typeof(RelationalPackageExporter).GetMethod("WriteTableAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using var frozen=await SqlFixture.CreateAsync();
        foreach(string script in RelationalSchemaInstaller.ReadScripts(1))await frozen.DestinationSqlAsync(script);
        await using var frozenConnection=new SqlConnection(frozen.DestinationConnectionString);await frozenConnection.OpenAsync();
        await using var frozenTransaction=(SqlTransaction)await frozenConnection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var file = File.Create(packagePath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            foreach (var spec in PackageTableCatalogue.ForSchema(1).Concat(layouts.Values.OrderBy(spec => spec.CaptureLayoutKey)))
            {
                var table = await (Task<RelationalPackageTable>)metadataMethod.Invoke(exporter,
                    [spec.CaptureLayoutKey.HasValue?connection:frozenConnection, spec.CaptureLayoutKey.HasValue?transaction:frozenTransaction, spec, budget, CancellationToken.None])!;
                streams.Add(await (Task<RelationalPackageTableStream>)rowsMethod.Invoke(exporter,
                    [connection, transaction, spec, table, archive, budget, CancellationToken.None])!);
            }
            await transaction.CommitAsync();
            var manifest = currentPackage.Manifest with
            {
                PackageVersion = 2, DatabaseSchemaVersion = 1, MinimumReaderVersion = 1, MinimumWriterVersion = 1,
                Tables = streams, LocalFiles = [], Consistency = "SqlSerializableBlocking-v1"
            };
            await using var output = archive.CreateEntry(RelationalPackageFormat.ManifestEntry).Open();
            await JsonSerializer.SerializeAsync(output, manifest);
        }

        await using var restored = await SqlFixture.CreateAsync();
        var imported = await new RelationalPackageImporter(restored.DestinationConnectionString, limits)
            .ImportAsync(packagePath);
        var restoredSession = new RelationalSession(restored.DestinationConnectionString);
        await VerifyPackageImportCaptureAsync(session, restoredSession, check, compareJsonEvidence: true);
        check(imported.CapturedRows > 0 && imported.CapturedRows ==
            Convert.ToInt64(await source.DestinationSqlAsync("SELECT SUM(ActualRowCount) FROM surf.DataSet;")),
            "Schema 1 package restores and validates all captured current and historical rows");
        check(Equals(await restored.DestinationSqlAsync("SELECT SchemaVersion FROM surf.StorageFormatInfo WHERE State='Ready';"), 3) &&
            Convert.ToInt64(await restored.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.FullTableData;")) == datasetKeys.Count &&
            Convert.ToInt64(await restored.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'capture');")) == 0,
            "Schema 1 package publishes schema 3 with JSON captures and removes generated capture tables");
        await restored.VerifySourceUnchangedAsync(check);

        foreach (long layoutKey in layouts.Keys)
            await source.DestinationSqlAsync("DROP TABLE " + CaptureLayout.QualifiedTable(layoutKey) + ";");
        await source.DestinationSqlAsync("DELETE FROM surf.DataValueException;");
    }
}

using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Access.State;
using Surf2.Services.RelationalExplorer;

int passed = 0;
if (args.Contains("--scope-ui", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunScopePickerUiChecksAsync(Check);
    if (args.Length == 1) { Console.WriteLine($"Passed {passed} scope UI scenarios."); return; }
}
if (args.Contains("--scope-dirty", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunRuntimeScopeSwitchDirtyChecksAsync(Check);
    if (args.Length == 1) { Console.WriteLine($"Passed {passed} scope change checks."); return; }
}
if (args.Contains("--migration-progress", StringComparer.Ordinal))
{
    StorageRegressionSuite.RunMigrationProgressChecks(Check);
    if (args.Length == 1)
    {
        Console.WriteLine($"Passed {passed} migration progress checks.");
        return;
    }
}
foreach (string name in SnapshotContractChecks.Run()) Check(true, name);
foreach (string name in await SnapshotContractChecks.RunHistoryAsync()) Check(true, name);
foreach (string name in await IndexContractChecks.RunAsync()) Check(true, name);
Check(!PersistenceFormatProbe.SameDatabase(new(PersistenceFormat.Empty, "A"), new(PersistenceFormat.Empty, "A")),
    "Unavailable identity is not proof of equality");
Check(PersistenceFormatProbe.SameDatabase(new(PersistenceFormat.Legacy, "A", "Server", 7),
    new(PersistenceFormat.Empty, "A", "server", 7)), "Physical database identity comparison");
Check(!PersistenceFormatProbe.SameDatabase(new(PersistenceFormat.Legacy, "A", "Server", 7),
    new(PersistenceFormat.Empty, "A", "Other", 7)), "Database IDs are scoped to server");

string nestedJson = "{\"SchemaVersion\":2,\"Snapshots\":[{\"Name\":\"A\\\"B\",\"Rows\":[{\"a\":1.2300,\"a\":null,\"b\":\"\",\"c\":true},{}]}]}";
await using (var cursor = new StreamingJsonCursor(new ChunkedReadStream(Encoding.UTF8.GetBytes(nestedJson), 1)))
{
    Check(await cursor.MoveNextAsync(), "Streamed JSON initial token");
    long rows = 0;
    await cursor.ReadObjectAsync(async (name, root, ct) =>
    {
        if (name == "Snapshots")
        {
            await root.ReadArrayAsync(async (_, snapshot, ct) =>
            {
                await snapshot.ReadObjectAsync(async (property, value, ct) =>
                {
                    if (property == "Rows")
                    {
                        await value.ReadArrayAsync(async (ordinal, item, ct) =>
                        {
                            JsonElement row = await item.ReadValueAsync(ct);
                            if (ordinal == 0)
                            {
                                Check(row.GetRawText().Contains("1.2300", StringComparison.Ordinal), "Exact numeric token preservation");
                                Check(row.EnumerateObject().Count() == 4, "Duplicate data-row properties preserved");
                            }

                            rows++;
                        }, ct);
                    }
                    else
                    {
                        await value.SkipValueAsync(ct);
                    }
                }, ct);
            }, ct);
        }
        else
        {
            await root.SkipValueAsync(ct);
        }
    });
    Check(rows == 2 && !await cursor.MoveNextAsync(), "Nested arrays streamed without retaining the library");
}

await using (var cursor = new StreamingJsonCursor(new ChunkedReadStream([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"Text\":\"é😀\"}")], 1)))
{
    await cursor.MoveNextAsync();
    JsonElement value = await cursor.ReadValueAsync();
    Check(value.GetProperty("Text").GetString() == "é😀", "BOM and split multibyte UTF-8 tokens");
}

if (args.Contains("--sql", StringComparer.Ordinal))
{
    string databaseName = "Surf2_Regression_" + Guid.NewGuid().ToString("N");
    string connectionString = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
    await using var master = new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=True;TrustServerCertificate=True");
    await master.OpenAsync();
    await using var provision = master.CreateCommand();
    provision.CommandText = $"CREATE DATABASE [{databaseName}];";
    await provision.ExecuteNonQueryAsync();
    try
    {
        var probe = new PersistenceFormatProbe();
        var emptyProbe = await probe.ProbeAsync(connectionString);
        Console.WriteLine($"Isolated probe: {emptyProbe.Format}: {emptyProbe.Reason}");
        Check(emptyProbe.Format == PersistenceFormat.Empty, "Read-only empty probe");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0;";
        Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0, "Probe does not initialize tables");
        command.CommandText = """
CREATE SCHEMA app;
""";
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
CREATE TABLE app.Surf2Documents (
    DocumentKey nvarchar(128) NOT NULL PRIMARY KEY,
    PayloadJson nvarchar(max) NOT NULL,
    UpdatedAtUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
""";
        await command.ExecuteNonQueryAsync();
        Check((await probe.ProbeAsync(connectionString)).Format == PersistenceFormat.Legacy, "Legacy schema detection");
        command.CommandText = "SELECT COUNT_BIG(*) FROM app.Surf2Documents;";
        Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0, "Legacy probe writes no fallback data");
        command.CommandText = "ALTER TABLE app.Surf2Documents ADD Unexpected int NULL;";
        await command.ExecuteNonQueryAsync();
        Check((await probe.ProbeAsync(connectionString)).Format == PersistenceFormat.Unsupported, "Unknown legacy shape rejected");
    }
    finally
    {
        SqlConnection.ClearAllPools();
        if (!databaseName.StartsWith("Surf2_Regression_", StringComparison.Ordinal) || databaseName.Length != 49)
        {
            throw new InvalidOperationException("Refusing to clean a non-test database.");
        }

        provision.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        await provision.ExecuteNonQueryAsync();
    }
}

if (args.Contains("--storage", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunAsync(Check);
    await StorageRegressionSuite.RunNewDatabaseChecksAsync(Check);
}

if (args.Contains("--performance", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunPerformanceAsync(Check);
}

if (args.Contains("--new", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunNewDatabaseChecksAsync(Check);
}

if (args.Contains("--local", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunLocalMigrationChecksAsync(Check);
}

if (args.Contains("--visual", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunMigrationVisualChecksAsync(Check,
        Path.Combine(AppContext.BaseDirectory, "migration-visual-checks"));
}

if (args.Contains("--package-export", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunPackageExportChecksAsync(Check);
    foreach (string name in await PackageRowDecodingChecks.RunAsync()) Check(true, name);
}

if (args.Contains("--legacy-package", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunLegacyPackageMigrationChecksAsync(Check);
}

if (args.Contains("--migration-validation", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunMigrationValidationChecksAsync(Check);
}

if (args.Contains("--migration-mapping", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunMigrationMappingChecksAsync(Check);
    await StorageRegressionSuite.RunMigrationMappingSqlChecksAsync(Check);
}

if (args.Contains("--bootstrap", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunBootstrapChecksAsync(Check);
}

if (args.Contains("--package-import", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunPackageImportChecksAsync(Check);
}

if (args.Contains("--access", StringComparer.Ordinal))
{
    foreach (string name in await SnapshotContractChecks.RunReadLimitsAsync()) Check(true, name);
    foreach (string name in await AccessContractChecks.RunAsync()) Check(true, name);
    foreach (string name in await StateAccessContractChecks.RunAsync()) Check(true, name);
    foreach (string name in await RelationalExplorerContractChecks.RunPureAsync()) Check(true, name);
    foreach (string name in await Surf2.Services.RelationalDocuments.RelationalDocumentContractChecks.RunAsync()) Check(true, name);
}

if (args.Contains("--pasted-freeze", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunPastedFreezeChecksAsync(Check);
}

if (args.Contains("--state-access", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunStateAccessChecksAsync(Check);
}

if (args.Contains("--query-integration", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunQueryIntegrationChecksAsync(Check);
}

if (args.Contains("--scope-index", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunScopeIndexChecksAsync(Check);
}

if (args.Contains("--saved-targets", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunSavedTargetChecksAsync(Check);
}

if (args.Contains("--trace", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunRelationalTraceChecksAsync(Check);
}

if (args.Contains("--metrics", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunSqlMetricsChecksAsync(Check);
}

if (args.Contains("--scratch", StringComparer.Ordinal)) ScratchChecks.Run(Check);
if (args.Contains("--raw-text", StringComparer.Ordinal)) await StorageRegressionSuite.RunRawTextChecksAsync(Check);

if (args.Contains("--runtime-wpf", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunRuntimeWpfChecksAsync(Check, Path.Combine(AppContext.BaseDirectory, "runtime-wpf-checks"));
}

if (args.Contains("--runtime-capture", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunRuntimeCaptureChecksAsync(Check);
}

if (args.Contains("--runtime-state", StringComparer.Ordinal))
    await StorageRegressionSuite.RunRuntimeStateCommandChecksAsync(Check);

if (args.Contains("--runtime-comparison", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunRuntimeComparisonChecksAsync(Check);
}

if (args.Contains("--transfer", StringComparer.Ordinal))
{
    await StorageRegressionSuite.RunPersistenceTransferChecksAsync(Check);
}

Console.WriteLine($"Passed {passed} regression checks.");

void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAILED: " + name);
    }

    passed++;
    Console.WriteLine("PASS: " + name);
}

sealed class ChunkedReadStream(byte[] data, int chunkSize) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
}

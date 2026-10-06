using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private sealed record RuntimeComparisonSnapshot(long Key, SnapshotRevisionHandle Procedure,
        long PreviousProcedure, SnapshotRevisionHandle Table, long DataResource, long DataRevision,
        long PreviousDataRevision, long Version, CaptureWriteHandle Data, CaptureWriteHandle PreviousData);

    // Parent opts in explicitly. Accepts no database/connection supplied by a caller.
    public static async Task RunRuntimeComparisonChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_Comparison_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var capture = new RelationalCaptureStore(session, new CaptureLimits
        { WriteBatchRows = 2, WriteBatchBytes = 128 * 1024, ReadBatchRows = 2, ReadBatchBytes = 32 * 1024 });
        string[] left = ["{\"Id\":\"x\",\"Value\":null}", "{\"Id\":\"X\",\"Value\":\"duplicate\"}",
            "{\"Id\":\"x #2\",\"Value\":\"literal suffix\"}", "{\"Id\":null,\"Value\":\"blank\"}",
            "{\"Value\":\"blank duplicate\"}", "{\"Id\":1.2300,\"id\":999,\"Value\":true}", "null", "{}"];
        string[] right = ["{\"Id\":\"x\",\"Value\":\"\"}", "{\"Id\":\"X\",\"Value\":\"changed\"}",
            "{\"Id\":\"x #2\",\"Value\":\"literal suffix\"}", "{\"Id\":null,\"Value\":\"blank\"}",
            "{\"Value\":\"blank duplicate\"}", "{\"Id\":1.2300,\"Value\":true}", "42", "{}"];
        string[] historicalRows = ["{\"Id\":\"old\",\"Value\":\"historical only\"}", "null"];
        var a = await SeedRuntimeComparisonSnapshotAsync(session, content, capture, "compare-a", left, historicalRows, 0, 65);
        var b = await SeedRuntimeComparisonSnapshotAsync(session, content, capture, "compare-b", right, historicalRows, 1, 0);
        var unrelated = await SeedRuntimeComparisonSnapshotAsync(session, content, capture, "unrelated", ["{\"Id\":\"unrelated\",\"Value\":\"unrelated payload\"}"],
            historicalRows, 2, 0);
        await fixture.MarkTestFixtureReadyAsync();
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        await runtime.Session.RequireReadyAsync();
        var state = runtime.StateStore;
        var scopeToken = await state.CreateScopeAsync(new Scope { ScopeId = "compare-scope", Name = "Compare scope",
            Resources = new ObservableCollection<ScopedResource>
            {
                new() { ResourceId = "a", Kind = ResourceKind.DatabaseSnapshot, Path = "compare-a" },
                new() { ResourceId = "b", Kind = ResourceKind.DatabaseSnapshot, Path = "compare-b" }
            } }, 0, Guid.NewGuid());
        await InTransactionAsync(runtime.Session, async (c, t) =>
            scopeToken = await state.ResolveScopeResourceTargetsAsync(c, t, scopeToken,
                [new(0, a.Key, null), new(1, b.Key, null)], Guid.NewGuid()));
        var scope = await runtime.Explorer.OpenScopeAsync(scopeToken.Key, sortCultureName: "en-US");
        var service = new RelationalComparisonService(runtime, new ComparisonLimits
        { StagingDirectory = fixture.OwnedDirectory, PageSize = 2 });
        var currentA = Required(await runtime.Snapshots.ResolveResourceAsync(a.Procedure.ResourceKey), "comparison A procedure");
        var currentB = Required(await runtime.Snapshots.ResolveResourceAsync(b.Procedure.ResourceKey), "comparison B procedure");
        var targetA = RelationalComparisonService.FromResource(currentA, runtime.Session.Epoch);
        var targetB = RelationalComparisonService.FromResource(currentB, runtime.Session.Epoch);
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var candidates = await service.GetCandidatesAsync(targetA, scope);
            check(candidates.Complete && candidates.Items.Count == 66 &&
                candidates.Items.All(x => x.SnapshotKey == a.Key || x.SnapshotKey == b.Key),
                "Comparison candidates use only loaded scope memberships and retain all metadata pages");
            StateAccessNoReads(trace.Commands, check, "Comparison candidate discovery", "TextContent", "Asset", "DataValueException",
                "DiagramObject", "Workflow", "DocumentWindowState");
            check(trace.Commands.All(x => !x.Text.Contains("FROM CAPTURE.", StringComparison.Ordinal)),
                "Comparison candidate metadata discovery reads no captured row payloads");
            var pages = trace.Commands.Where(c => c.Text.Contains("TOP (@TAKE)", StringComparison.Ordinal) &&
                c.Text.Contains("SURF.SNAPSHOTRESOURCE", StringComparison.Ordinal) && c.Number("@Take") == 65).ToArray();
            check(pages.Length > 2 && pages.Any(p => p.Number("@AfterKey") > 0 || p.Number("@AfterOrder") > -1),
                "Actual comparison metadata SQL uses bounded 64-row seek pages with continuation");
        }
        using (var trace = new StateAccessSqlTrace(connection))
        {
            check(await service.ReadTextAsync(targetA) == "current compare-a\r\nSELECT 1.2300;" &&
                await service.ReadTextAsync(targetB) == "current compare-b\r\nSELECT 1.2300;",
                "Current comparison reads exact selected definitions and raw numeric/newline spelling");
            var definitions = StateAccessReads(trace.Commands, "TextContent");
            check(definitions.Count == 2 && definitions.All(c => c.Number("@Revision") == a.Procedure.RevisionKey ||
                c.Number("@Revision") == b.Procedure.RevisionKey), "Current pair SQL fetches only its two immutable definition revisions");
            StateAccessNoReads(trace.Commands, check, "Current comparison pair", "Asset", "DataSet", "DataValueException", "DiagramObject", "TableColumnRevision");
            check(!definitions.Any(c => c.Number("@Revision") == unrelated.Procedure.RevisionKey),
                "Current selected pair never materializes unrelated snapshot definitions");
        }
        var old = Required(await runtime.Snapshots.ResolveResourceAsync(a.Procedure.ResourceKey, a.Version), "historical comparison procedure");
        check(old.RevisionKey == a.PreviousProcedure, "History comparison resolves its selected reverse-history revision");
        using (var trace = new StateAccessSqlTrace(connection))
        {
            check(await service.ReadTextAsync(RelationalComparisonService.FromResource(old, runtime.Session.Epoch, a.Version)) == "old compare-a",
                "Historical code comparison reads the immutable previous definition, not the current head");
            var definitions = StateAccessReads(trace.Commands, "TextContent");
            check(definitions.Count == 1 && definitions[0].Number("@Revision") == a.PreviousProcedure,
                "Selected historical text fetch has one previous-revision payload projection");
        }
        var dataA = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(a.DataResource), "comparison A data"), runtime.Session.Epoch);
        var dataB = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(b.DataResource), "comparison B data"), runtime.Session.Epoch);
        ComparisonTableInput inputA, inputB;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            inputA = await service.DescribeTableAsync(dataA); inputB = await service.DescribeTableAsync(dataB);
            check(inputA.Headers.SequenceEqual(new[] { "Id", "Value" }) && inputA.PreferredKeys.SequenceEqual(new[] { "Id" }) &&
                inputA.MaximumRows == left.Length, "Comparison table descriptor preserves selected columns, preferred key and actual count");
            StateAccessNoReads(trace.Commands, check, "Selected comparison table descriptors", "TextContent", "Asset", "DataValueException", "DiagramObject");
            check(trace.Commands.All(x => !x.Text.Contains("FROM CAPTURE.", StringComparison.Ordinal)),
                "Describing comparison tables does not read their rows");
        }
        using (var trace = new StateAccessSqlTrace(connection))
        await using (var result = await service.CompareTablesAsync(inputA, inputB, ["Id"]))
        {
            var actual = await RuntimeComparisonAllRowsAsync(result);
            var expected = RuntimeComparisonLegacyTableDiff(left, right);
            check(actual.Select(x => (x.Key, x.Status, x.ChangedColumns, x.LeftPreview, x.RightPreview))
                .SequenceEqual(expected.Rows.Select(x => (x.Key, x.Status.ToString(), x.ChangedColumns, x.LeftPreview, x.RightPreview))),
                "Actual SQL-fed table comparison retains legacy null/duplicate/suffix/case/raw-token behavior");
            var payloads = trace.Commands.Where(c => c.Text.Contains("FROM CAPTURE.", StringComparison.Ordinal) ||
                c.Text.Contains("FROM SURF.DATAVALUEEXCEPTION", StringComparison.Ordinal)).ToArray();
            check(payloads.Length != 0 && payloads.All(c => c.Number("@DataSet") == a.Data.DataSetKey || c.Number("@DataSet") == b.Data.DataSetKey),
                "Table comparison SQL streams only the selected two datasets, never unrelated or historical rows");
            string csv = Path.Combine(fixture.OwnedDirectory, "runtime-comparison.csv");
            await result.ExportAsync(csv);
            check(CsvGridParser.Parse(await File.ReadAllTextAsync(csv)).Rows.Count == actual.Count && actual.Count > result.PageSize,
                "Runtime SQL comparison export contains every matching row beyond its viewport page");
            File.Delete(csv);
        }
        var oldDataSummary = Required(await runtime.Snapshots.ResolveResourceAsync(a.DataResource, a.Version), "historical table data");
        var oldData = RelationalComparisonService.FromResource(oldDataSummary, runtime.Session.Epoch, a.Version);
        var historicalInput = await service.DescribeTableAsync(oldData);
        var currentInput = await service.DescribeTableAsync(dataA);
        using (var trace = new StateAccessSqlTrace(connection))
        await using (var result = await service.CompareTablesAsync(historicalInput, currentInput, ["Id"]))
        {
            check((await RuntimeComparisonAllRowsAsync(result)).Any(x => x.LeftPreview.Contains("historical only", StringComparison.Ordinal)),
                "Selected historical table comparison streams its preserved prior rows");
            var payloads = trace.Commands.Where(c => c.Text.Contains("FROM CAPTURE.", StringComparison.Ordinal)).ToArray();
            check(payloads.Length != 0 && payloads.All(c => c.Number("@DataSet") == a.PreviousData.DataSetKey || c.Number("@DataSet") == a.Data.DataSetKey),
                "Historical/current comparison streams only the chosen historical and current datasets");
        }
        var collectionA = RuntimeComparisonCollection(runtime.Session.Epoch, a.Key);
        var collectionB = RuntimeComparisonCollection(runtime.Session.Epoch, b.Key);
        using (var trace = new StateAccessSqlTrace(connection))
        await using (var result = await service.CompareCollectionsAsync(collectionA, collectionB, scope, new()))
        {
            var rows = await RuntimeComparisonAllRowsAsync(result);
            check(rows.Single(r => r.Key == "Stored Procedures/dbo.Proc.sql").Status == "Different" &&
                rows.Single(r => r.Key == "Tables/dbo.Evidence.sql").Status == "Identical" &&
                rows.Single(r => r.Key == "Full Table Data/dbo.Evidence.csv").Status == "Different",
                "Relational collection comparison handles selected code, table metadata and streamed captured CSV");
            check(StateAccessReads(trace.Commands, "TextContent").All(c => c.Number("@Revision") != unrelated.Procedure.RevisionKey) &&
                trace.Commands.Where(c => c.Text.Contains("FROM CAPTURE.", StringComparison.Ordinal))
                    .All(c => c.Number("@DataSet") == a.Data.DataSetKey || c.Number("@DataSet") == b.Data.DataSetKey),
                "Collection comparison never reads payloads from an unrelated snapshot or historical dataset");
            StateAccessNoReads(trace.Commands, check, "Collection comparison", "Asset", "DiagramObject", "DocumentWindowState", "Workflow");
        }
        await using (var historical = await service.CompareCollectionsAsync(collectionA with { VersionKey = a.Version }, collectionA, scope, new()))
        {
            var rows = await RuntimeComparisonAllRowsAsync(historical);
            check(rows.Single(r => r.Key == "Stored Procedures/dbo.Proc.sql").Left!.RevisionKey == a.PreviousProcedure &&
                rows.Single(r => r.Key == "Tables/dbo.Evidence.sql").Left!.HistoricalEntryKey.HasValue,
                "Historical collection results retain selected revision and projected table-entry identities for later opening");
        }
        check(!Directory.EnumerateDirectories(fixture.OwnedDirectory).Any(), "All actual SQL comparison spools are removed after disposal");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task<RuntimeComparisonSnapshot> SeedRuntimeComparisonSnapshotAsync(RelationalSession session,
        RelationalContentStore content, RelationalCaptureStore capture, string name, string[] rows, string[] previousRows, long order, int additional)
    {
        long key = 0, oldProcedure = 0, dataResource = 0, dataRevision = 0, oldDataRevision = 0, version = 0, latest = 0;
        SnapshotRevisionHandle procedure = null!, table = null!;
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            key = await writer.CreateSnapshotAsync(new(name, name, name, EvidenceTime, order));
            procedure = await writer.InsertCurrentObjectAsync(key, new SqlDatabaseObject { SchemaName = "dbo", ObjectName = "Proc",
                Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = "current " + name + "\r\nSELECT 1.2300;" }, 0);
            oldProcedure = await writer.InsertObjectRevisionAsync(procedure.ResourceKey, new SqlDatabaseObject
            { SchemaName = "dbo", ObjectName = "Proc", Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = "old " + name });
            await writer.SealRevisionAsync(oldProcedure);
            for (int i = 0; i < additional; i++) await writer.InsertCurrentObjectAsync(key, new SqlDatabaseObject
            { SchemaName = "dbo", ObjectName = "Metadata" + i.ToString("D3"), Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = new string('x', 4096) }, i + 3);
            table = await writer.InsertCurrentTableAsync(key, new SqlTable { SchemaName = "dbo", TableName = "Evidence",
                HasFullData = true, FullDataRowCount = 999, FullDataImportedAtUtc = EvidenceTime }, 1);
            await writer.InsertColumnAsync(key, table.RevisionKey, new SqlColumn { SchemaName = "dbo", TableName = "Evidence",
                ColumnName = "Id", DataType = "nvarchar", Ordinal = 1, MaxLength = 128, IsNullable = true }, 0, 0);
            await writer.InsertColumnAsync(key, table.RevisionKey, new SqlColumn { SchemaName = "dbo", TableName = "Evidence",
                ColumnName = "Value", DataType = "nvarchar", Ordinal = 2, MaxLength = 128, IsNullable = true }, 1, 1);
            await writer.InsertPrimaryKeyAsync(key, table.RevisionKey, new SqlPrimaryKeyColumn { SchemaName = "dbo", TableName = "Evidence",
                ConstraintName = "PK_Evidence", ColumnName = "Id", KeyOrdinal = 1 }, 0, 0);
            await writer.SealRevisionAsync(table.RevisionKey);
            dataResource = await writer.CreateResourceAsync(key, DatabaseVersionedResourceKind.TableData, "dbo", "Evidence", "data:dbo.Evidence", 2);
            dataRevision = await writer.InsertTableDataRevisionAsync(dataResource); oldDataRevision = await writer.InsertTableDataRevisionAsync(dataResource);
            long history = await writer.InsertHistoryAsync(key, name, 3, order);
            version = await writer.InsertVersionAsync(history, new("old-" + name, "Old", 1, EvidenceTime, true, 0));
            latest = await writer.InsertVersionAsync(history, new("new-" + name, "New", 2, EvidenceTime.AddTicks(1), false, 1));
            await writer.InsertChangeAsync(latest, procedure.ResourceKey, new(DatabaseVersionedResourceKind.StoredProcedure,
                DatabaseSnapshotResourceChangeKind.Modified, "StoredProcedure|dbo.Proc", "Proc", "Stored Procedures/dbo.Proc.sql", 0, oldProcedure));
            await writer.InsertChangeAsync(latest, dataResource, new(DatabaseVersionedResourceKind.TableData,
                DatabaseSnapshotResourceChangeKind.Modified, "TableData|dbo.Evidence", "Evidence", "Full Table Data/dbo.Evidence.csv", 1, oldDataRevision));
        });
        CaptureColumnDefinition[] columns = [new("Id"), new("Value")];
        var data = await capture.CreateDataSetAsync(new(dataRevision, columns, 999, EvidenceTime, rows.Length));
        await capture.AppendRowsAsync(data, JsonRows(rows.Select(Json)), 0); await capture.CompleteDataSetAsync(data);
        var old = await capture.CreateDataSetAsync(new(oldDataRevision, columns, 2, EvidenceTime, previousRows.Length));
        await capture.AppendRowsAsync(old, JsonRows(previousRows.Select(Json)), 0); await capture.CompleteDataSetAsync(old);
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            await writer.SealRevisionAsync(dataRevision); await writer.SealRevisionAsync(oldDataRevision);
            await writer.SetCurrentRevisionAsync(dataResource, dataRevision, 2); await writer.PublishSnapshotAsync(key, latest);
        });
        return new(key, procedure, oldProcedure, table, dataResource, dataRevision, oldDataRevision, version, data, old);
    }
    private static RelationalComparisonTarget RuntimeComparisonCollection(Guid epoch, long snapshot) => new(
        new(snapshot.ToString(), "Database Snapshot", snapshot.ToString(), ComparisonResourceKind.DatabaseSnapshot,
            "DatabaseSnapshot", true, false, false, "snapshot:" + snapshot), epoch, snapshot);
    private static async Task<List<ComparisonResultRow>> RuntimeComparisonAllRowsAsync(ComparisonResultStore result)
    {
        var rows = new List<ComparisonResultRow>(); long offset = 0;
        while (true)
        {
            var page = await result.ReadPageAsync(offset); rows.AddRange(page.Rows);
            if (!page.HasNext) return rows;
            if (page.Rows.Count == 0) throw new InvalidOperationException("Comparison fixture failed to advance.");
            offset += page.Rows.Count;
        }
    }
    private static TableDataDiffResult RuntimeComparisonLegacyTableDiff(string[] left, string[] right)
    {
        var library = new DatabaseSnapshotLibrary();
        ComparisonResource Resource(string id) => new(id, "Table Data", id, ComparisonResourceKind.TableData,
            "TableData", false, false, true, id, SnapshotId: id, TableSchemaName: "dbo", TableName: "Evidence");
        foreach (var pair in new[] { (Id: "left", Rows: left), (Id: "right", Rows: right) })
            library.Snapshots.Add(new() { SnapshotId = pair.Id, Tables = [new() { SchemaName = "dbo", TableName = "Evidence", HasFullData = true }],
                Columns = [new() { SchemaName = "dbo", TableName = "Evidence", ColumnName = "Id", Ordinal = 1 },
                    new() { SchemaName = "dbo", TableName = "Evidence", ColumnName = "Value", Ordinal = 2 }],
                TableDataSets = [new() { SchemaName = "dbo", TableName = "Evidence", Rows = pair.Rows.Select(Json).ToList() }] });
        return new ResourceComparisonService().BuildTableDataDiff(Resource("left"), Resource("right"), ["Id"], library);
    }
}

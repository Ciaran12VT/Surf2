using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalGrid;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private sealed record QueryIntegrationFixture(long SnapshotKey, SnapshotRevisionHandle Procedure,
        SnapshotRevisionHandle Table, long DataResourceKey, long DataRevisionKey, long PreviousDataRevisionKey,
        CaptureWriteHandle Data, CaptureWriteHandle PreviousData, JsonElement[] Rows, SqlDatabaseObject ProcedureValue,
        DiagramState Diagram, long DiagramKey, StateToken ScopeToken, long OtherScopeKey);

    public static async Task RunQueryIntegrationChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        // Reuse the fixture-filtered diagnostic observer from the state integration cases.
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_Query_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var snapshots = new RelationalSnapshotStore(session, content);
        var capture = new RelationalCaptureStore(session, new CaptureLimits
        {
            WriteBatchRows = 2, WriteBatchBytes = 128 * 1024,
            ReadBatchRows = 2, ReadBatchBytes = 32 * 1024, MaxPageRows = 2, MaxPageBytes = 32 * 1024
        });
        var state = new RelationalStateStore(session, content);
        var seeded = await SeedQueryIntegrationAsync(fixture, session, content, capture, state);
        var limits = new ExplorerLimits(PageSize: 1);
        var index = new RelationalIndexStore(session, content);
        var metadata = new RelationalExplorerMetadataAdapter(session, index, limits);
        var explorer = new RelationalExplorerService(metadata, limits: limits);
        var sources = new RelationalExplorerSearchSources(session, index, snapshots, capture, limits);
        var references = new RelationalReferenceService(session, index, limits);

        var scope = await QueryIntegrationObservedAsync(connection,
            () => explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US"), commands =>
        {
            StateAccessNoReads(commands, check, "Explorer selected metadata", "Asset", "TextContent", "DatabaseObjectRevision",
                "TableColumnRevision", "DataValueException", "DiagramObject", "Workflow", "DocumentWindowState");
            check(commands.All(c => !c.Text.Contains("BASE64", StringComparison.Ordinal) && !c.Text.Contains("BYTES", StringComparison.Ordinal)),
                "Explorer scope metadata SQL contains no asset bytes or base64 projection");
        }, check, "selected scope metadata");
        check(scope.Resources.Length == 3 && scope.Resources.Count(r => r.Kind == ResourceKind.DatabaseSnapshot) == 2 &&
            scope.Resources.Where(r => r.Kind == ResourceKind.DatabaseSnapshot).All(r => r.Snapshot?.SnapshotKey == seeded.SnapshotKey) &&
            scope.Resources.Select(r => r.ScopeResourceKey).Distinct().Count() == 3,
            "Concrete metadata adapter returns only the selected scope's aliases and diagram header");
        var aliases = scope.Resources.Where(r => r.Kind == ResourceKind.DatabaseSnapshot).OrderBy(r => r.SortOrdinal).ToArray();
        var roots = await QueryIntegrationObservedAsync(connection, () => explorer.GetRootsAsync(scope), commands =>
            StateAccessNoReads(commands, check, "Explorer roots", "TextContent", "Asset", "DatabaseObjectRevision", "TableColumnRevision", "DiagramObject"),
            check, "lazy explorer roots");
        var firstRoot = roots.Single(r => r.ScopeResourceKey == aliases[0].ScopeResourceKey);
        var secondRoot = roots.Single(r => r.ScopeResourceKey == aliases[1].ScopeResourceKey);
        check(firstRoot.OccurrenceKey != secondRoot.OccurrenceKey && firstRoot.NodeKey == secondRoot.NodeKey &&
            firstRoot.ChildrenState == ExplorerChildrenState.Unloaded && firstRoot.Name == "First alias" && secondRoot.Name == "Second alias",
            "Explorer roots retain independent alias occurrences, shared legacy move keys and unloaded child state");
        var categories = await QueryIntegrationObservedAsync(connection, () => QueryIntegrationChildrenAsync(explorer, scope, firstRoot, check), commands =>
            StateAccessNoReads(commands, check, "Database category headers", "DatabaseObjectRevision", "TableColumnRevision", "Asset", "TextContent", "DataValueException"),
            check, "category headers without definitions");
        check(categories.Count == 5 && categories.Select(n => n.Category).Distinct().Count() == 5,
            "Expanding one database root enumerates exactly five category headers without object graphs");
        var procedureNodes = await QueryIntegrationChildrenAsync(explorer, scope, categories.Single(c => c.Category == ExplorerCategory.Procedures), check);
        var tableNodes = await QueryIntegrationChildrenAsync(explorer, scope, categories.Single(c => c.Category == ExplorerCategory.Tables), check);
        var procedureNode = procedureNodes.Single();
        var tableNode = tableNodes.Single();
        check(procedureNode.SourceRevisionKey == seeded.Procedure.RevisionKey && tableNode.SourceRevisionKey == seeded.Table.RevisionKey &&
            tableNode.HasFullData && tableNode.ReportedRowCount == 999 && tableNode.ScopeResourceId == "",
            "Concrete child metadata pins typed object/table revisions and retains reported counts separately from captured rows");
        var secondCategories = await QueryIntegrationChildrenAsync(explorer, scope, secondRoot, check);
        var secondProcedure = (await QueryIntegrationChildrenAsync(explorer, scope,
            secondCategories.Single(c => c.Category == ExplorerCategory.Procedures), check)).Single();
        var secondTable = (await QueryIntegrationChildrenAsync(explorer, scope,
            secondCategories.Single(c => c.Category == ExplorerCategory.Tables), check)).Single();
        var diagramNode = (await QueryIntegrationChildrenAsync(explorer, scope, roots.Single(r => r.Role == ExplorerNodeRole.DiagramRoot), check)).Single();

        var canonical = ExplorerCompatibility.DatabaseLocator(aliases[0].Snapshot!, ExplorerCategory.Procedures, "dbo", "NeedleProc", true);
        var address = Required(await explorer.ResolveAddressAsync(scope, canonical, aliases[1].ScopeResourceKey), "preferred alias address");
        var readable = Required(await explorer.ResolveAddressAsync(scope, address.ReadableLocator, aliases[1].ScopeResourceKey), "readable alias address");
        check(address.Node.ScopeResourceKey == aliases[1].ScopeResourceKey && address.RevisionKey == seeded.Procedure.RevisionKey &&
            readable.Node.SnapshotResourceKey == address.Node.SnapshotResourceKey && address.Hierarchy[0] == "Second alias" &&
            address.TextFileNameSeed!.StartsWith("Second alias_", StringComparison.Ordinal),
            "Canonical/readable address resolution preserves selected alias ancestry and logical TXT seed");
        var dataLocator = ExplorerCompatibility.DatabaseLocator(aliases[0].Snapshot!, ExplorerCategory.Tables, "dbo", "Evidence", true, true);
        var dataAddress = Required(await explorer.ResolveAddressAsync(scope, dataLocator, aliases[0].ScopeResourceKey), "table-data address");
        var dataResource = Required(await sources.ResolveTableDataAsync(dataAddress.Node), "selected table data companion");
        check(dataAddress.IsTableData && dataAddress.RevisionKey == null && dataAddress.Node.SourceRevisionKey == seeded.Table.RevisionKey &&
            dataResource.RevisionKey == seeded.DataRevisionKey && dataResource.ResourceKey == seeded.DataResourceKey &&
            dataResource.RevisionKey != seeded.Table.RevisionKey,
            "Table-data locator binds metadata identity first, then resolves the independent immutable captured revision");
        check(await sources.ReadCurrentContentAsync(procedureNode, 4096, default) == seeded.ProcedureValue.Definition,
            "Concrete code provider loads the exact selected definition, including newline and raw Unicode spelling");
        var tableCode = await index.PrepareTableCodeAsync(seeded.Table.RevisionKey, tableNode.FullPath, "fixture");
        check(tableCode.Text.Contains("[Id]", StringComparison.Ordinal) && tableCode.Text.Contains("[Value]", StringComparison.Ordinal) &&
            tableCode.Text.Contains("[Flag]", StringComparison.Ordinal) && tableCode.SourceRevisionKey == seeded.Table.RevisionKey,
            "Concrete table-code renderer uses the selected typed metadata and column ordinals");

        var emptyPaint = await references.LoadPaintAsync(scope);
        check(!emptyPaint.Coverage.FullyPublished && emptyPaint.Coverage.Documents == 0,
            "An installed empty index is not represented as a published reference catalogue");
        var unindexed = await QueryIntegrationSearchAsync(explorer, sources, scope, "code-only-marker", false, IndexSearchTarget.Content, check);
        check(unindexed.Hits.Count == 2 && unindexed.Hits.All(h => h.Node.SnapshotResourceKey == seeded.Procedure.ResourceKey &&
            h.CurrentSourceEvaluated && h.IndexFreshness == IndexFreshness.Unindexed),
            "Unindexed explorer content search evaluates actual selected definitions and retains both alias occurrences");
        var names = await QueryIntegrationSearchAsync(explorer, sources, scope, "NeedleProc", false, IndexSearchTarget.Name, check);
        var suffix = await QueryIntegrationSearchAsync(explorer, sources, scope, "999 rows", false, IndexSearchTarget.Name, check);
        check(names.Hits.Count == 2 && names.Hits.All(h => !h.ContentMatched) && suffix.Hits.Count == 0,
            "Concrete explorer name search matches legacy object names, not reported-row-count display suffixes");
        var diagramSearch = await QueryIntegrationSearchAsync(explorer, sources, scope, "diagram-only-marker", false, IndexSearchTarget.Content, check);
        check(diagramSearch.Hits.Count == 1 && diagramSearch.Hits[0].Node.Role == ExplorerNodeRole.Diagram &&
            diagramSearch.Hits[0].Node.DiagramRevisionKey == diagramNode.DiagramRevisionKey,
            "Concrete diagram search evaluates the selected type/label/image-name projection without a global diagram model");
        var cells = await QueryIntegrationSearchAsync(explorer, sources, scope, "cell-only-marker %_[", false, IndexSearchTarget.Content, check);
        check(cells.Hits.Count == 2 && cells.Hits.All(h => h.Node.SnapshotResourceKey == seeded.Table.ResourceKey &&
            !h.ContentMatched && h.MatchingColumnIndexes.SequenceEqual(new[] { 1 })),
            "Captured-cell-only search creates one table result per alias and highlights only the matching metadata column");
        var code = await QueryIntegrationSearchAsync(explorer, sources, scope, "[Value]", false, IndexSearchTarget.Content, check);
        check(code.Hits.Count == 2 && code.Hits.All(h => h.ContentMatched && h.MatchingColumnIndexes.IsEmpty),
            "Selected table metadata code can match independently of captured-cell filters");
        var regex = await QueryIntegrationSearchAsync(explorer, sources, scope, "1\\.2300", true, IndexSearchTarget.Content, check);
        check(regex.Hits.Count == 2 && regex.Hits.All(h => h.MatchingColumnIndexes.SequenceEqual(new[] { 0 })),
            "Concrete SQL capture search preserves first duplicate/case property display and exact numeric tokens");
        var unloaded = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, new HashSet<string>(StringComparer.Ordinal) { "DB-A" }, "en-US");
        check(unloaded.Resources.Single(r => r.ResourceId == "db-a").IsLoaded,
            "Explorer preserves the caller's case-sensitive unloaded-ID comparer");
        unloaded = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DB-A" }, "en-US");
        var loadedOnly = await QueryIntegrationSearchAsync(explorer, sources, unloaded, "code-only-marker", false, IndexSearchTarget.Content, check);
        check(loadedOnly.Hits.Count == 1 && loadedOnly.Hits[0].Node.ScopeResourceKey == aliases[1].ScopeResourceKey,
            "Workspace unloading suppresses only the chosen alias occurrence, not its shared database document");
        await VerifyQueryCapturedGridAsync(fixture, connection, capture, seeded, check);

        // Publish through the concrete index API; the tests neither parse nor provide searchable source models.
        long procedureDocument = await QueryIntegrationPublishAsync(index,
            new SnapshotDocumentOwner(seeded.SnapshotKey, seeded.Procedure.ResourceKey, false),
            [procedureNode, secondProcedure], await index.PrepareDefinitionAsync(seeded.Procedure.RevisionKey, canonical, "fixture"),
            new("catalogue-v1", "raw-v1", "query-integration-v1"));
        await QueryIntegrationPublishAsync(index, new SnapshotDocumentOwner(seeded.SnapshotKey, seeded.Table.ResourceKey, true),
            [tableNode, secondTable], tableCode, new("catalogue-v1", RelationalIndexStore.TableRendererVersion, "query-integration-v1"));
        await QueryIntegrationPublishAsync(index, new DiagramDocumentOwner(diagramNode.DiagramRevisionKey!.Value),
            [diagramNode], await index.PrepareDiagramAsync(diagramNode.DiagramRevisionKey.Value),
            new("catalogue-v1", RelationalIndexStore.DiagramRendererVersion, "query-integration-v1"));
        await index.MarkDiscoveryReconciledAsync(await index.CaptureContextAsync(seeded.ScopeToken.Key));
        scope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        var bindings = await sources.BindIndexDocumentsAsync(scope, [procedureNode, secondProcedure], default);
        check(bindings.Length == 2 && bindings.All(b => b.DocumentKey == procedureDocument && b.Freshness == IndexFreshness.Indexed) &&
            bindings.Select(b => b.ScopeResourceKey).Distinct().Count() == 2,
            "Concrete TVP binding maps two occurrences to one published document without losing alias ownership");
        var indexedSearch = await QueryIntegrationSearchAsync(explorer, sources, scope, "code-only-marker", false, IndexSearchTarget.Content, check);
        check(indexedSearch.Hits.Count == 2 && indexedSearch.Hits.All(h => h.IndexFreshness == IndexFreshness.Indexed) &&
            indexedSearch.Coverage.IndexFullyPublished,
            "Published index search retains actual aliases and distinguishes completed current coverage from unindexed fallback");
        var paint = await QueryIntegrationObservedAsync(connection, () => references.LoadPaintAsync(scope), commands =>
            StateAccessNoReads(commands, check, "Reference paint metadata", "TextContent", "Asset", "DatabaseObjectRevision", "DiagramObject", "TableColumnRevision"),
            check, "compact reference paint metadata");
        check(paint.Coverage.FullyPublished && paint.Paint.TryGetStyle("NeedleProc", "SQL Server", out var style) &&
            style?.Kind == ReferenceEntityKind.StoredProcedure,
            "Actual persisted reference paint catalogue exposes the published SQL name/language/kind");
        using (var trace = new StateAccessSqlTrace(connection))
        {
            for (int i = 0; i < 100; i++) paint.Paint.TryGetStyle("needleproc", "sql server", out _);
            check(trace.Commands.Count == 0, "Immutable reference paint lookups execute no SQL, parser, file or lazy-provider work");
        }
        var navigation = await references.ResolveAsync(scope.Context,
            [new ReferenceQuery("NeedleProc"), new ReferenceQuery("dbo.NeedleProc"), new ReferenceQuery("not-a-symbol")]);
        check(navigation.Length == 3 && navigation[0].Candidates.Length == 1 && navigation[1].Candidates.Length == 1 &&
            navigation[2].Candidates.IsEmpty && navigation[0].Candidates[0].DocumentKey == procedureDocument &&
            navigation[0].Candidates[0].Freshness == IndexFreshness.Indexed &&
            navigation[0].Candidates[0].Definition.Locator == canonical,
            "Concrete paged reference navigation completes name/qualified lookup and returns selected published revision/locator");
        var outsideScope = await explorer.OpenScopeAsync(seeded.OtherScopeKey, sortCultureName: "en-US");
        var outside = await references.ResolveWithCoverageAsync(outsideScope, [new ReferenceQuery("NeedleProc")]);
        check(outside.Targets.Single().Candidates.IsEmpty && !outside.Coverage.FullyPublished,
            "Provisional reference lookup does not leak globally published symbols into an unrelated unindexed scope");
        await ThrowsAsync<ReferenceIndexNotReadyException>(
            () => references.ResolveAsync(outsideScope, [new ReferenceQuery("NeedleProc")]), check,
            "Strict navigation does not report an authoritative empty result for an unindexed scope");
        var oldScope = scope;
        var changedObject = new SqlDatabaseObject
        {
            SchemaName = "dbo", ObjectName = "NeedleProc", Kind = SqlDatabaseObjectKind.StoredProcedure,
            Definition = "CREATE PROCEDURE [dbo].[NeedleProc] AS SELECT N'new-current-only-marker';\r\n"
        };
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            long revision = await writer.InsertObjectRevisionAsync(seeded.Procedure.ResourceKey, changedObject);
            await writer.SealRevisionAsync(revision);
            await writer.SetCurrentRevisionAsync(seeded.Procedure.ResourceKey, revision, 0);
            await writer.PublishSnapshotAsync(seeded.SnapshotKey, null);
        });
        var invalidated = await QueryIntegrationSearchAsync(explorer, sources, oldScope, "code-only-marker", false,
            IndexSearchTarget.Content, check, expectGenerationChanged: true);
        check(invalidated.Hits.Count == 0 && invalidated.Coverage.GenerationChanged && !invalidated.Coverage.Completed,
            "An actual snapshot publication invalidates the old explorer generation without publishing stale hits");
        scope = await explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        var fallback = await QueryIntegrationSearchAsync(explorer, sources, scope, "new-current-only-marker", false, IndexSearchTarget.Content, check);
        var noOld = await QueryIntegrationSearchAsync(explorer, sources, scope, "code-only-marker", false, IndexSearchTarget.Content, check);
        check(fallback.Hits.Count == 2 && fallback.Hits.All(h => h.CurrentSourceEvaluated && h.IndexFreshness == IndexFreshness.Stale) &&
            noOld.Hits.Count == 0 && fallback.Coverage.IndexStaleOrUnindexed > 0 && !fallback.Coverage.IndexFullyPublished,
            "A stale persisted index cannot suppress a new current-source hit or leak an obsolete content hit");
        var staleTargets = await references.ResolveWithCoverageAsync(scope, [new ReferenceQuery("NeedleProc")]);
        check(staleTargets.Targets.Single().Candidates.Single().Freshness == IndexFreshness.Stale && !staleTargets.Coverage.FullyPublished,
            "Provisional reference lookup carries incomplete coverage and stale persisted source locations");
        await ThrowsAsync<ReferenceIndexNotReadyException>(
            () => references.ResolveAsync(scope, [new ReferenceQuery("NeedleProc")]), check,
            "Strict navigation rejects stale reference coverage instead of treating it as current");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in explorer.SearchAsync(new(scope, "", false, IndexSearchTarget.Content, Guid.NewGuid()), sources, cancelled.Token)) { }
        }, check, "Pre-cancelled concrete explorer search does not publish a successful empty result");
        var descriptor = Required(await capture.GetForRevisionAsync(seeded.DataRevisionKey), "unchanged query capture");
        var rows = new List<CaptureRow>();
        await foreach (var row in capture.StreamRowsAsync(descriptor.Summary.DataSetKey)) rows.Add(row);
        check(rows.Count == seeded.Rows.Length && rows.Select((r, i) => JsonEvidenceEquals(seeded.Rows[i], r.Value)).All(x => x),
            "Explorer/search/grid/reference operations and sparse edits leave every authoritative captured row unchanged");
        SameModel(seeded.Diagram.Document, Required(await state.ReadDiagramAsync(seeded.DiagramKey), "unchanged query diagram").Value.Document,
            check, "Query providers leave the authoritative diagram, fonts, PNG text and assets unchanged");
        check(true, $"Query SQL fixture: ScopeKey={seeded.ScopeToken.Key}; OtherScopeKey={seeded.OtherScopeKey}; SnapshotKey={seeded.SnapshotKey}; " +
            $"ProcedureResourceKey={seeded.Procedure.ResourceKey}; TableMetadataRevisionKey={seeded.Table.RevisionKey}; " +
            $"TableDataRevisionKey={seeded.DataRevisionKey}; DataSetKey={seeded.Data.DataSetKey}; canonical={JsonSerializer.Serialize(canonical)}; data={JsonSerializer.Serialize(dataLocator)}.");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task<QueryIntegrationFixture> SeedQueryIntegrationAsync(SqlFixture fixture, RelationalSession session,
        RelationalContentStore content, RelationalCaptureStore capture, RelationalStateStore state)
    {
        long snapshot = 0, dataResource = 0, dataRevision = 0, previousRevision = 0;
        SnapshotRevisionHandle procedure = null!, table = null!;
        var procedureValue = new SqlDatabaseObject
        {
            SchemaName = "dbo", ObjectName = "NeedleProc", Kind = SqlDatabaseObjectKind.StoredProcedure,
            Definition = "CREATE PROCEDURE [dbo].[NeedleProc] AS SELECT N'code-only-marker \u00e9';\r\nSELECT 7;\n"
        };
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            snapshot = await writer.CreateSnapshotAsync(new("query-snapshot", "Query snapshot", "fixture database", EvidenceTime, 0));
            procedure = await writer.InsertCurrentObjectAsync(snapshot, procedureValue, 0);
            table = await writer.InsertCurrentTableAsync(snapshot, new SqlTable
            { SchemaName = "dbo", TableName = "Evidence", HasFullData = true, FullDataRowCount = 999, FullDataImportedAtUtc = EvidenceTime }, 1);
            var columns = new[] { ("Id", "decimal"), ("Value", "nvarchar"), ("Flag", "bit") };
            for (int i = 0; i < columns.Length; i++)
                await writer.InsertColumnAsync(snapshot, table.RevisionKey, new SqlColumn
                { SchemaName = "dbo", TableName = "Evidence", ColumnName = columns[i].Item1, DataType = columns[i].Item2,
                    Ordinal = i + 1, MaxLength = i == 1 ? 128 : 8, IsNullable = true }, i, i);
            await writer.InsertPrimaryKeyAsync(snapshot, table.RevisionKey, new SqlPrimaryKeyColumn
            { SchemaName = "dbo", TableName = "Evidence", ConstraintName = "PK_Evidence", ColumnName = "Id", KeyOrdinal = 1 }, 0, 0);
            await writer.SealRevisionAsync(table.RevisionKey);
            dataResource = await writer.CreateResourceAsync(snapshot, DatabaseVersionedResourceKind.TableData, "dbo", "Evidence", "data:dbo.Evidence", 2);
            dataRevision = await writer.InsertTableDataRevisionAsync(dataResource);
            previousRevision = await writer.InsertTableDataRevisionAsync(dataResource);
        });
        JsonElement[] rows =
        [
            Json("{\"Id\":1.2300,\"id\":99,\"Id\":false,\"Value\":\"cell-only-marker %_[\",\"Flag\":true,\"unlisted\":{\"x\":[null,1e+02]}}"),
            Json("null"), Json("{\"Id\":2,\"Value\":null,\"Flag\":false}"), Json("[1,false]"),
            Json("{\"Id\":10,\"Value\":\"alpha\",\"Flag\":true}"), Json("{}"), Json("\"not an object\""), Json("false")
        ];
        CaptureColumnDefinition[] layout = [new("Id"), new("Value"), new("Flag")];
        var data = await capture.CreateDataSetAsync(new(dataRevision, layout, 999, EvidenceTime, rows.Length));
        await capture.AppendRowsAsync(data, JsonRows(rows), 0); await capture.CompleteDataSetAsync(data);
        var previous = await capture.CreateDataSetAsync(new(previousRevision, layout, -7, EvidenceTime.AddTicks(-1), 1));
        await capture.AppendRowsAsync(previous, JsonRows([Json("{\"Id\":-0,\"Value\":\"historical-only\",\"Flag\":false}")]), 0);
        await capture.CompleteDataSetAsync(previous);
        await InTransactionAsync(session, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            await writer.SealRevisionAsync(dataRevision); await writer.SealRevisionAsync(previousRevision);
            await writer.SetCurrentRevisionAsync(dataResource, dataRevision, 2); await writer.PublishSnapshotAsync(snapshot, null);
        });
        // Explicit storage-unit Ready callback; no migration or runtime activation is claimed.
        await fixture.MarkTestFixtureReadyAsync();
        var diagram = new DiagramState(new DiagramDocument
        {
            DiagramId = "query-diagram", Name = "Query diagram", CreatedAtUtc = EvidenceTime, UpdatedAtUtc = EvidenceTime,
            Objects = [new() { Id = "image-object", ObjectType = DiagramObjectType.Image, LabelText = "diagram-only-marker",
                LabelFontSize = 23.75, ImageName = "fixture image", ImageDataBase64 = Convert.ToBase64String(Png(17)) + "\r\n" }]
        }, new Dictionary<int, byte[]>());
        var diagramToken = await state.CreateDiagramAsync(diagram, 0, Guid.NewGuid());
        var scope = new Scope
        {
            ScopeId = "query-scope", Name = "Query scope", Resources = new ObservableCollection<ScopedResource>
            {
                new() { ResourceId = "db-a", Kind = ResourceKind.DatabaseSnapshot, Path = "query-snapshot", DisplayNameOverride = "First alias", AddedAtUtc = EvidenceTime },
                new() { ResourceId = "db-b", Kind = ResourceKind.DatabaseSnapshot, Path = "query-snapshot", DisplayNameOverride = "Second alias", AddedAtUtc = EvidenceTime },
                new() { ResourceId = "diagram", Kind = ResourceKind.Diagram, Path = "query-diagram", AddedAtUtc = EvidenceTime }
            }
        };
        var scopeToken = await state.CreateScopeAsync(scope, 0, Guid.NewGuid());
        var other = await state.CreateScopeAsync(new Scope { ScopeId = "excluded-scope", Name = "Excluded scope" }, 1, Guid.NewGuid());
        await InTransactionAsync(session, async (c, t) =>
        {
            scopeToken = await state.ResolveScopeResourceTargetsAsync(c, t, scopeToken,
                [new(0, snapshot, null), new(1, snapshot, null), new(2, null, diagramToken.Key)], Guid.NewGuid());
        });
        return new(snapshot, procedure, table, dataResource, dataRevision, previousRevision, data, previous,
            rows, procedureValue, diagram, diagramToken.Key, scopeToken, other.Key);
    }

    private static async Task<List<ExplorerNodeSummary>> QueryIntegrationChildrenAsync(RelationalExplorerService service,
        ExplorerScope scope, ExplorerNodeSummary parent, Action<bool, string> check)
    {
        var nodes = new List<ExplorerNodeSummary>(); int batches = 0; bool completed = false;
        await foreach (var batch in service.GetChildrenAsync(scope, parent))
        {
            if (++batches > 20) throw new InvalidOperationException("Fixture explorer child enumeration failed to terminate.");
            check(batch.Nodes.Length <= 1 && batch.FailureCode == null && batch.State != ExplorerChildrenState.Failed,
                "Concrete explorer children honor the one-node batch bound: " + parent.Role);
            if (batches == 1) check(batch.State == ExplorerChildrenState.Loading && !batch.Completed && batch.Nodes.IsEmpty,
                "Concrete explorer emits an explicit initial Loading batch");
            nodes.AddRange(batch.Nodes); completed |= batch.Completed;
        }
        check(completed, "Concrete explorer emits an explicit completed child batch: " + parent.Role);
        return nodes;
    }

    private sealed record QueryIntegrationSearch(List<ExplorerSearchHit> Hits, ExplorerSearchCoverage Coverage);
    private static async Task<QueryIntegrationSearch> QueryIntegrationSearchAsync(RelationalExplorerService service,
        RelationalExplorerSearchSources sources, ExplorerScope scope, string text, bool regex, IndexSearchTarget target,
        Action<bool, string> check, bool expectGenerationChanged = false)
    {
        var request = new ExplorerSearchRequest(scope, text, regex, target, Guid.NewGuid());
        var builder = new ExplorerSearchResultBuilder(scope, request.RequestIdentity, new ExplorerLimits(PageSize: 1));
        var hits = new List<ExplorerSearchHit>(); ExplorerSearchCoverage? coverage = null; int batches = 0;
        await foreach (var batch in service.SearchAsync(request, sources))
        {
            if (++batches > 100) throw new InvalidOperationException("Fixture explorer search failed to terminate.");
            check(batch.RequestIdentity == request.RequestIdentity && batch.Hits.Length <= 1 && batch.Outcomes.Length <= 1,
                "Concrete explorer search pins request identity and bounded hit/outcome pages");
            hits.AddRange(batch.Hits); coverage = batch.Coverage; builder.Append(batch);
        }
        var final = Required(coverage, "terminal explorer search coverage");
        check(expectGenerationChanged ? final.GenerationChanged && !final.Completed : final.Completed && !final.GenerationChanged,
            "Concrete explorer reports terminal completion separately from generation invalidation");
        if (!expectGenerationChanged)
        {
            check(builder.Completed && QueryIntegrationResultMatches(builder.Snapshot()) == hits.Count,
                "Concrete result builder retains every match and alias ancestor without source documents");
            check(hits.All(h => h.AncestorsAreNatural), "SQL-backed search hits carry natural ancestry for post-filter assembly");
        }
        return new(hits, final);
    }

    private static int QueryIntegrationResultMatches(IEnumerable<ExplorerResultNode> nodes) =>
        nodes.Sum(n => (n.Match == null ? 0 : 1) + QueryIntegrationResultMatches(n.Children));

    private static async Task<long> QueryIntegrationPublishAsync(RelationalIndexStore index, DocumentOwner owner,
        ImmutableArray<ExplorerNodeSummary> nodes, PreparedIndexSource source, IndexPolicy policy)
    {
        var document = await index.RegisterAsync(new(owner, nodes[0].MatchName, source.Language));
        await index.SetMembershipAsync(document.DocumentKey,
            nodes.Select(n => new DocumentMembership(n.ScopeResourceKey!.Value, n.MatchName, n.FullPath, n.NodeKey, n.ParentNodeKey, n.SourceOrdinal)).ToImmutableArray(),
            nodes.Select(n => new IndexLocator(n.ScopeResourceKey!.Value, 1, n.FullPath)).ToImmutableArray());
        var lease = await index.BeginWorkAsync(await index.GetHandleAsync(document.DocumentKey), Guid.NewGuid(), source.Fingerprint, policy);
        await index.PublishAsync(new(lease, source.Text, source.Language, source.Symbols, source.SourceRevisionKey));
        return document.DocumentKey;
    }

    private static async Task<T> QueryIntegrationObservedAsync<T>(string connection, Func<Task<T>> action,
        Action<IReadOnlyList<StateAccessCommand>> verify, Action<bool, string> check, string name)
    {
        using var trace = new StateAccessSqlTrace(connection); var result = await action();
        var commands = trace.Commands;
        check(commands.Count > 0, "Actual fixture SQL observed for query integration: " + name);
        verify(commands);
        check(true, "Query SQL evidence " + name + ": " + commands.Count + " commands including identity/readiness probes");
        return result;
    }

    private static async Task VerifyQueryCapturedGridAsync(SqlFixture fixture, string connection,
        RelationalCaptureStore capture, QueryIntegrationFixture seeded, Action<bool, string> check)
    {
        string staging = Path.Combine(fixture.OwnedDirectory, "query-grid-cache");
        var limits = new GridLimits
        {
            StagingDirectory = staging, MaxColumns = 3, MaxCellCharacters = 128, MaxHeaderBytes = 1024,
            MaxRowBytes = 1024, PageRows = 1, PageBytes = 2048, CachePages = 1, CacheBytes = 4096,
            MaxPendingPageReads = 4, SortRunRows = 2, SortRunBytes = 4096, MergeFanIn = 2,
            MaxSortRuns = 8, MaxOwnedFiles = 32, MaxDiskBytes = 2 * 1024 * 1024, MaxClipboardBytes = 1024
        };
        GridColumn[] projection = [new(0, "Id", "Id"), new(1, "Context value", "Value"), new(2, "Flag", "Flag")];
        await using (var source = await CapturedGridSource.OpenAsync(capture, seeded.Data.DataSetKey, limits, displayColumns: projection))
        {
            await using var view = await source.CreateQueryAsync();
            check(source.Descriptor.ReportedRowCount == 999 && source.Descriptor.SourceRowCount == 8 &&
                source.Descriptor.Columns.SequenceEqual(projection),
                "Concrete captured grid pins contextual display headers and reported/raw counts without generated CSV");
            check(await view.Completion == 3 && view.Count == new GridCount(3, true),
                "Captured grid suppresses blank display records and nonobjects without changing eight raw captured rows");
            var firstRequest = new GridPageRequest(0, 1, 2048);
            var first = await view.ReadPageAsync(firstRequest);
            var rows = await QueryIntegrationGridRowsAsync(view, check);
            check(rows.Select(r => r.RowOrdinal).SequenceEqual(new long[] { 0, 2, 4 }) &&
                rows[0].Cells.SequenceEqual(new[] { "1.2300", "cell-only-marker %_[", "True" }) &&
                rows[1].Cells.SequenceEqual(new[] { "2", "", "False" }) && rows[2].Cells.SequenceEqual(new[] { "10", "alpha", "True" }),
                "SQL-backed grid preserves visible raw ordinals, first duplicate/case display, null and boolean formatting without a phantom empty-object row");
            using (var trace = new StateAccessSqlTrace(connection))
            {
                _ = source.Descriptor; _ = view.Descriptor; _ = view.Count; _ = view.CacheStatistics;
                view.TryGetCachedPage(firstRequest, out _);
                await view.ReadPageAsync(new GridPageRequest(2, 1, 2048));
                check(trace.Commands.Count == 0, "Grid cached properties and deep positional pages perform no further SQL after one staging scan");
            }
            await using (var sorted = await source.CreateQueryAsync(new GridQuery(Sorts: [new(0, true)], SortCultureName: "en-US")))
            {
                var sortedRows = await QueryIntegrationGridRowsAsync(sorted, check);
                check(sortedRows.Select(r => r.RowOrdinal).SequenceEqual(new long[] { 2, 4, 0 }),
                    "Concrete disk-backed grid sorts displayed strings, not reinterpreted numbers or SQL collation");
            }
            await using (var tied = await source.CreateQueryAsync(new GridQuery(Sorts: [new(2, true)], SortCultureName: "en-US")))
                check((await QueryIntegrationGridRowsAsync(tied, check)).Select(r => r.RowOrdinal).SequenceEqual(new long[] { 0, 4, 2 }),
                    "Descending display sort retains ascending raw ordinals as its stable tie-breaker");
            await using (var filtered = await source.CreateQueryAsync(new GridQuery(Filters: [new(1, "%_[")])))
                check((await QueryIntegrationGridRowsAsync(filtered, check)).Single().RowOrdinal == 0,
                    "SQL-backed grid treats percent, underscore and bracket filter characters literally");
            await using (var any = await source.CreateQueryAsync(new GridQuery(
                Filters: [new(0, "2"), new(1, "cell-only-marker"), new(2, "unrelated impossible filter")], AnyMatchColumnOrdinals: [0, 1])))
                check((await QueryIntegrationGridRowsAsync(any, check)).Select(r => r.RowOrdinal).SequenceEqual(new long[] { 0, 2 }),
                    "Search-generated OR column group takes precedence over unrelated AND filters");
            source.SetCell(first.Rows.Single(), 1, "edit,\r\n\"quoted\"");
            await using var edited = await source.CreateQueryAsync();
            var editedRows = await QueryIntegrationGridRowsAsync(edited, check);
            check(edited.OverlayGeneration > view.OverlayGeneration && editedRows[0].Cells[1] == "edit,\r\n\"quoted\"" &&
                (await view.ReadPageAsync(firstRequest)).Rows[0].Cells[1] == "cell-only-marker %_[",
                "Sparse captured-grid edits are session-only and operation queries retain their pinned old overlay");
            await using var output = new MemoryStream();
            long written = await GridOutput.WriteDelimitedAsync(edited, output, [1, 0]);
            string csv = Encoding.UTF8.GetString(output.ToArray()); string newline = Environment.NewLine;
            string expected = "Context value,Id" + newline + "\"edit,\r\n\"\"quoted\"\"\",1.2300" + newline +
                ",2" + newline + "alpha,10" + newline;
            check(written == 3 && output.CanWrite && csv == expected,
                "Concrete grid export streams every result, reorders visible columns and quotes embedded CR/LF and quotes exactly");
            var copy = await GridOutput.CopyAsync(edited, [1, 0], 1024);
            check(copy.RowCount == 3 && copy.ColumnCount == 2 && copy.Text.Contains("alpha\t10", StringComparison.Ordinal),
                "Concrete grid copy includes off-page rows rather than only the one-row viewport");
            await ThrowsAsync<GridLimitException>(() => GridOutput.CopyAsync(edited, [1, 0], 16), check,
                "Clipboard output rejects an explicit small byte budget rather than silently truncating");
            string destination = Path.Combine(fixture.OwnedDirectory, "query-grid-export.csv");
            byte[] existing = Encoding.UTF8.GetBytes("existing fixture destination");
            await File.WriteAllBytesAsync(destination, existing);
            await ThrowsAsync<IOException>(() => GridOutput.ExportAsync(edited, destination, [1, 0]), check,
                "Grid output does not replace an existing fixture destination without explicit permission");
            using var cancellation = new CancellationTokenSource();
            await ThrowsAsync<OperationCanceledException>(() => GridOutput.ExportAsync(edited, destination, [1, 0], true,
                new QueryIntegrationProgress(_ => cancellation.Cancel()), cancellation.Token), check,
                "Cancellation after streaming output aborts grid destination publication");
            check((await File.ReadAllBytesAsync(destination)).SequenceEqual(existing) &&
                !Directory.EnumerateFiles(fixture.OwnedDirectory, "*.grid-pending").Any(),
                "Failed/cancelled grid exports preserve existing bytes and remove only their own pending files");
            var export = await GridOutput.ExportAsync(edited, destination, [1, 0], true);
            check(export.RowCount == 3 && export.ColumnCount == 2 && await File.ReadAllTextAsync(destination) == expected,
                "Explicit successful grid export atomically replaces only its fixture-owned destination");
            source.ClearEdits();
            check(source.OverlayBytes == 0 && (await edited.ReadPageAsync(firstRequest)).Rows[0].Cells[1] == "edit,\r\n\"quoted\"",
                "Clearing current session edits does not mutate a pinned output query's captured overlay");
        }
        await using (var historical = await CapturedGridSource.OpenAsync(capture, seeded.PreviousData.DataSetKey, limits))
        {
            await using var query = await historical.CreateQueryAsync();
            var rows = await QueryIntegrationGridRowsAsync(query, check);
            check(rows.Count == 1 && rows[0].Cells.SequenceEqual(new[] { "-0", "historical-only", "False" }) &&
                historical.Descriptor.ReportedRowCount == -7,
                "Concrete grid opens the explicitly selected older dataset without substituting current rows or reported counts");
        }
        check(!Directory.EnumerateFileSystemEntries(staging).Any(),
            "Logical captured-grid close awaits work and cleans its generated staging files/directories");
    }

    private static async Task<List<GridRow>> QueryIntegrationGridRowsAsync(IGridQuerySession query, Action<bool, string> check)
    {
        long count = await query.Completion; var rows = new List<GridRow>(); long position = 0;
        while (position < count)
        {
            var page = await query.ReadPageAsync(new GridPageRequest(position, 1, 2048));
            check(page.Rows.Count == 1 && page.EstimatedBytes <= 2048 && page.Rows.All(r => r.EstimatedBytes <= 1024) &&
                page.SourceId == query.Descriptor.SourceId && page.QueryGeneration == query.Generation &&
                page.OverlayGeneration == query.OverlayGeneration && page.IsRangeComplete,
                "Concrete SQL-backed grid page honors row/byte bounds and pinned source/query/overlay generations");
            if (page.Rows.Count == 0) throw new InvalidOperationException("Completed fixture grid failed to advance.");
            rows.AddRange(page.Rows); position += page.Rows.Count;
        }
        var stream = new List<GridRow>(); await foreach (var row in query.StreamAsync()) stream.Add(row);
        check(stream.Select(r => r.RowOrdinal).SequenceEqual(rows.Select(r => r.RowOrdinal)) &&
            stream.Zip(rows).All(pair => pair.First.Cells.SequenceEqual(pair.Second.Cells)),
            "Concrete full-grid stream and bounded positional pages produce the same complete results");
        return rows;
    }

    private sealed class QueryIntegrationProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

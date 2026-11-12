using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    public static async Task RunSnapshotMemoryChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_SnapshotMemory_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var seed = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var capture = new RelationalCaptureStore(seed);
        string[] leftRows = ["{\"Id\":1,\"Value\":\"same\"}", "{\"Id\":2,\"Value\":\"before\"}", "{\"Id\":2,\"Value\":\"duplicate\"}"];
        string[] rightRows = ["{\"Id\":1,\"Value\":\"same\"}", "{\"Id\":2,\"Value\":\"after\"}", "{\"Id\":3,\"Value\":\"added\"}"];
        string[] oldRows = ["{\"Id\":0,\"Value\":\"historical only\"}"];
        var a = await SeedRuntimeComparisonSnapshotAsync(seed, content, capture, "compare-a", leftRows, oldRows, 0, 4);
        var b = await SeedRuntimeComparisonSnapshotAsync(seed, content, capture, "compare-b", rightRows, oldRows, 1, 4);
        var unrelated = await SeedRuntimeComparisonSnapshotAsync(seed, content, capture, "unrelated", rightRows, oldRows, 2, 0);
        var mixedLibrary = MixedHistoryLibrary();
        var mixed = await SeedMixedHistoryAsync(seed, content, capture, mixedLibrary);
        long syntheticKey = 0;
        await InTransactionAsync(seed, async (c, t) =>
        {
            var writer = new RelationalSnapshotWriter(c, t, content);
            syntheticKey = await writer.CreateSnapshotAsync(new("memory-unversioned", "Unversioned", "fixture", EvidenceTime, 30));
            await writer.InsertCurrentObjectAsync(syntheticKey, new SqlDatabaseObject { SchemaName = "dbo", ObjectName = "Unversioned",
                Kind = SqlDatabaseObjectKind.StoredProcedure, Definition = "SELECT 1;\r\nSELECT 2;" }, 0);
            await writer.PublishSnapshotAsync(syntheticKey, null);
        });
        await fixture.MarkTestFixtureReadyAsync();
        var baseline = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var scopeToken = await baseline.StateStore.CreateScopeAsync(new Scope
        {
            ScopeId = "memory-scope", Name = "Memory scope", Resources = new ObservableCollection<ScopedResource>
            {
                new() { ResourceId = "a", Kind = ResourceKind.DatabaseSnapshot, Path = "compare-a" },
                new() { ResourceId = "b", Kind = ResourceKind.DatabaseSnapshot, Path = "compare-b" },
                new() { ResourceId = "mixed", Kind = ResourceKind.DatabaseSnapshot, Path = mixedLibrary.Snapshots[0].SnapshotId },
                new() { ResourceId = "synthetic", Kind = ResourceKind.DatabaseSnapshot, Path = "memory-unversioned" }
            }
        }, 0, Guid.NewGuid());
        await InTransactionAsync(baseline.Session, async (c, t) => scopeToken = await baseline.StateStore.ResolveScopeResourceTargetsAsync(
            c, t, scopeToken, [new(0, a.Key, null), new(1, b.Key, null), new(2, mixed.Key, null), new(3, syntheticKey, null)], Guid.NewGuid()));

        // Independent SQL route supplies expected history text/counts before the new runtime exists.
        var expectedHistory = new Dictionary<(long Version, string Resource), (string Text, int Added, int Removed)>();
        var historyBaseline = new RelationalSnapshotHistoryService(baseline);
        var headA = Required(await baseline.Snapshots.GetSnapshotAsync(a.Key), "memory snapshot A");
        foreach (var version in await historyBaseline.GetDisplayedVersionsAsync(headA))
        {
            var files = await historyBaseline.ListFileRowsAsync(version);
            foreach (var file in files)
            {
                var count = await historyBaseline.CountFileLinesAsync(file);
                expectedHistory.Add((version.VersionKey, file.OriginalResourceKey),
                    (await historyBaseline.ReadFileTextAsync(file), count.Added, count.Removed));
            }
        }
        var ambiguousVersions = new HashSet<long>();
        var baselineMixed = Required(await baseline.Snapshots.GetSnapshotAsync(mixed.Key), "mixed SQL baseline");
        foreach (var version in await historyBaseline.GetDisplayedVersionsAsync(baselineMixed))
        {
            try { await historyBaseline.ListFileRowsAsync(version); }
            catch (InvalidOperationException error) when (error.Message.Contains("ambiguous duplicate file identities", StringComparison.Ordinal))
            { ambiguousVersions.Add(version.VersionKey); }
        }
        var runtime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        var scope = await runtime.Explorer.OpenScopeAsync(scopeToken.Key, sortCultureName: "en-US");
        long[] selected = [a.Key, b.Key, mixed.Key, syntheticKey];
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => runtime.PreloadSnapshotMemoryAsync(selected, ct: canceled.Token), check,
                "Canceled preload publishes no partial snapshot data");
            check(!runtime.IsSnapshotMemoryReady(selected), "Canceled preload is never reported as ready");
        }
        await runtime.PreloadSnapshotMemoryAsync(selected);
        check(runtime.IsSnapshotMemoryReady(selected) && !runtime.Session.SnapshotMemory.ContainsSnapshot(unrelated.Key),
            "Preload retains only selected scope snapshots and completes all history/count preparation");
        var history = new RelationalSnapshotHistoryService(runtime);
        string blockedScratch = Path.Combine(fixture.OwnedDirectory, "no-scratch");
        Directory.CreateDirectory(fixture.OwnedDirectory);
        File.WriteAllText(blockedScratch, "Resident comparisons must not access scratch storage.");
        var comparison = new RelationalComparisonService(runtime, new() { StagingDirectory = blockedScratch, PageSize = 2,
            SortBufferBytes = 1024, MaximumDiskBytes = 1024 });
        long sqlBefore = runtime.Session.Metrics.Snapshot().Started;
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var snapshot = Required(await runtime.Snapshots.GetSnapshotAsync(a.Key), "resident snapshot A");
            var versions = await history.GetDisplayedVersionsAsync(snapshot);
            check(versions.Count == 2 && versions[0].VersionNumber == 2, "Resident history retains newest-first version order");
            foreach (var version in versions)
            {
                var files = await history.ListFileRowsAsync(version);
                check(await history.CountVersionFilesAsync(snapshot, version) == files.Count,
                    "Resident version counts agree with visible file rows");
                var counts = new List<RelationalHistoryFileLineCount>();
                await foreach (var count in history.CountFileLinesAsync(files)) counts.Add(count);
                foreach (var file in files)
                {
                    var expected = expectedHistory[(version.VersionKey, file.OriginalResourceKey)];
                    var count = counts.Single(value => value.File.OriginalResourceKey == file.OriginalResourceKey);
                    check(count.Error == null && count.Added == expected.Added && count.Removed == expected.Removed &&
                        await history.ReadFileTextAsync(file) == expected.Text,
                        "Resident history file text and line counts match SQL for " + version.VersionName + " " + file.DisplayName);
                    if (file.Entry == null) continue;
                    var target = await history.FileTargetAsync(file);
                    await using var context = await history.OpenVersionAsync(a.Key, version.VersionKey);
                    var preview = await history.ReadPreviewAsync(context, file.Entry);
                    check(target.VersionKey == version.VersionKey && (preview.Text != null || preview.CapturedData != null),
                        "Resident history preview and comparison target retain selected version identity");
                    if (preview.CapturedData != null)
                    {
                        var page = await runtime.CapturedData.ReadPageAsync(preview.CapturedData.Summary.DataSetKey);
                        check(page.Rows.Count > 0, "Historical captured-data preview reads resident rows");
                    }
                }
            }
            var currentA = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(a.Procedure.ResourceKey), "resident A procedure"), runtime.Session.Epoch);
            var currentB = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(b.Procedure.ResourceKey), "resident B procedure"), runtime.Session.Epoch);
            await using (var documents = new RelationalDocumentService(runtime))
            {
                var resource = Required(await runtime.Snapshots.ResolveResourceAsync(a.Procedure.ResourceKey), "resolver procedure");
                string readable = ExplorerCompatibility.DatabaseLocator(snapshot, ExplorerCategory.Procedures, "dbo", "Proc", true, false);
                string identity = RelationalDocumentService.IdentityPath(runtime.Session.Epoch, resource, ExplorerCategory.Procedures, false);
                check((await documents.ResolveComparisonResourceAsync(readable, scope)).ResourceKey == resource.ResourceKey &&
                    (await documents.ResolveComparisonResourceAsync(identity, scope)).ResourceKey == resource.ResourceKey,
                    "Resident comparison resolution handles both readable paths and typed identities");
                string missing = RelationalDocumentService.IdentityPath(runtime.Session.Epoch, resource with { ResourceKey = long.MaxValue }, ExplorerCategory.Procedures, false);
                await ThrowsAsync<DocumentTargetException>(() => documents.ResolveComparisonResourceAsync(missing, scope), check,
                    "Missing typed comparison identities fail from resident scope data without SQL fallback");
                await ThrowsAsync<DocumentTargetException>(() => documents.ResolveComparisonResourceAsync(
                    ExplorerCompatibility.DatabaseLocator(snapshot, ExplorerCategory.Procedures, "dbo", "MissingProcedure", true, false), scope), check,
                    "Missing readable comparison paths fail from resident scope data without SQL fallback");
            }
            var candidates = await comparison.GetCandidatesAsync(currentA, scope);
            check(candidates.Complete && candidates.Items.Any(value => value.ResourceKey == b.Procedure.ResourceKey),
                "Resident comparison candidates use the loaded scope catalogue");
            var pair = await comparison.ReadTextPairAsync(currentA, currentB);
            check(pair.Left.StartsWith("current compare-a", StringComparison.Ordinal) && pair.Right.StartsWith("current compare-b", StringComparison.Ordinal),
                "Resident text comparisons read exact current definitions");
            var historical = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(a.Procedure.ResourceKey, a.Version), "resident old procedure"), runtime.Session.Epoch, a.Version);
            check(await comparison.ReadTextAsync(historical) == "old compare-a", "Resident historical text stays distinct from the head");
            var collectionA = RuntimeComparisonCollection(runtime.Session.Epoch, a.Key);
            var collectionB = RuntimeComparisonCollection(runtime.Session.Epoch, b.Key);
            var snapshotChoices = await comparison.GetCandidatesAsync(collectionA, scope);
            var categorySource = collectionA with { Category = ExplorerCategory.Procedures,
                Resource = collectionA.Resource with { Kind = ComparisonResourceKind.DatabaseObjectFolder, ComparisonTypeKey = "DatabaseFolder:Stored Procedures" } };
            var categoryChoices = await comparison.GetCandidatesAsync(categorySource, scope);
            check(snapshotChoices.Complete && snapshotChoices.Items.Any(item => item.SnapshotKey == b.Key && item.Resource.Kind == ComparisonResourceKind.DatabaseSnapshot) &&
                categoryChoices.Complete && categoryChoices.Items.Any(item => item.SnapshotKey == b.Key && item.Category == ExplorerCategory.Procedures),
                "Resident whole-snapshot and category comparison pickers use only loaded catalogue data");
            foreach (var options in new[] { new ComparisonOptions(), new ComparisonOptions(true, true) })
            {
                await using var result = await comparison.CompareCollectionsAsync(collectionA, collectionB, scope, options);
                var rows = await RuntimeComparisonAllRowsAsync(result);
                check(rows.Single(row => row.Key == "Stored Procedures/dbo.Proc.sql").Status == "Different" &&
                    rows.Single(row => row.Key == "Full Table Data/dbo.Evidence.csv").Status == "Different",
                    "Resident collection comparison covers definitions and full table data with changed options");
                check((await result.ReadDocumentChoicesAsync()).Complete && (await result.ReadPageAsync(differencesOnly: true)).MatchingCount > 0,
                    "Resident comparison result paging, filtering and document choices require no scratch reads");
            }
            await using (var result = await comparison.CompareCollectionsAsync(collectionA with { VersionKey = a.Version }, collectionA, scope, new()))
                check((await RuntimeComparisonAllRowsAsync(result)).Single(row => row.Key == "Stored Procedures/dbo.Proc.sql").Left!.RevisionKey == a.PreviousProcedure,
                    "Resident historical collection comparison pins previous immutable revisions");
            var dataA = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(a.DataResource), "resident A data"), runtime.Session.Epoch);
            var dataB = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(b.DataResource), "resident B data"), runtime.Session.Epoch);
            var inputA = await comparison.DescribeTableAsync(dataA); var inputB = await comparison.DescribeTableAsync(dataB);
            check(inputA.IsMemoryResident && inputB.IsMemoryResident, "Resident table inputs select the in-memory comparison engine");
            await using (var result = await comparison.CompareTablesAsync(inputA, inputB, ["Id"]))
            {
                var rows = await RuntimeComparisonAllRowsAsync(result);
                var expected = RuntimeComparisonLegacyTableDiff(leftRows, rightRows);
                check(rows.Select(row => (row.Key, row.Status, row.ChangedColumns, row.LeftPreview, row.RightPreview)).SequenceEqual(
                    expected.Rows.Select(row => (row.Key, row.Status.ToString(), row.ChangedColumns, row.LeftPreview, row.RightPreview))),
                    "Resident captured-data comparison matches the independent master model");
                string output = Path.Combine(fixture.OwnedDirectory, "resident-export.csv");
                await result.ExportAsync(output);
                check(CsvGridParser.Parse(await File.ReadAllTextAsync(output)).Rows.Count == rows.Count, "Resident comparison CSV includes every page");
                File.Delete(output);
            }
            var oldData = RelationalComparisonService.FromResource(Required(await runtime.Snapshots.ResolveResourceAsync(a.DataResource, a.Version), "resident old data"), runtime.Session.Epoch, a.Version);
            await using (var result = await comparison.CompareTablesAsync(await comparison.DescribeTableAsync(oldData), inputA, ["Id"]))
                check((await RuntimeComparisonAllRowsAsync(result)).Any(row => row.LeftPreview.Contains("historical only", StringComparison.Ordinal)),
                    "Resident table comparison includes prior captured rows");
            var mixedHead = Required(await runtime.Snapshots.GetSnapshotAsync(mixed.Key), "resident mixed history");
            var mixedOracle = new DatabaseSnapshotHistoryService(new DatabaseDocumentService());
            foreach (var version in await history.GetDisplayedVersionsAsync(mixedHead))
            {
                if (ambiguousVersions.Contains(version.VersionKey))
                {
                    await ThrowsAsync<InvalidOperationException>(() => history.ListFileRowsAsync(version), check,
                        "Resident history preserves the existing duplicate-file guard for " + version.VersionName);
                    continue;
                }
                var legacyVersion = mixedLibrary.Histories[0].Versions.Single(value => value.VersionId == version.VersionId);
                var expected = mixedOracle.CreateVersionSummary(mixedLibrary, mixedLibrary.Histories[0], legacyVersion).Changes;
                var files = await history.ListFileRowsAsync(version);
                check(files.Count == expected.Count && files.All(file => expected.Any(value => value.ResourceKey == file.OriginalResourceKey && value.ChangeKind == file.ChangeKind)),
                    "Resident mixed history preserves removed resource identities and version gaps in " + version.VersionName);
                await foreach (var count in history.CountFileLinesAsync(files))
                {
                    var original = expected.Single(value => value.ResourceKey == count.File.OriginalResourceKey);
                    check(count.Error == null && count.Added == original.AddedLines && count.Removed == original.RemovedLines,
                        "Resident mixed history counts match master for " + version.VersionName + " " + count.File.OriginalResourceKey);
                }
            }
            foreach (string version in new[] { "mixed-initial", "mixed-middle" })
            {
                var old = mixedOracle.ReconstructSnapshot(mixedLibrary, mixedLibrary.Snapshots[0].SnapshotId, version);
                var current = mixedLibrary.Snapshots[0].Clone();
                old.SnapshotId = old.DisplayName = "oracle-left"; current.SnapshotId = current.DisplayName = "oracle-right";
                var pairLibrary = new DatabaseSnapshotLibrary(); pairLibrary.Snapshots.Add(old); pairLibrary.Snapshots.Add(current);
                var left = RuntimeComparisonCollection(runtime.Session.Epoch, mixed.Key) with { VersionKey = mixed.Versions[version] };
                var right = RuntimeComparisonCollection(runtime.Session.Epoch, mixed.Key);
                var expected = new ResourceComparisonService().BuildCollectionDiff(left.Resource with { SnapshotId = old.SnapshotId },
                    right.Resource with { SnapshotId = current.SnapshotId }, pairLibrary);
                await using var result = await comparison.CompareCollectionsAsync(left, right, scope, new());
                var rows = await RuntimeComparisonAllRowsAsync(result);
                check(rows.Select(row => (row.Key.ToUpperInvariant(), row.Status)).SequenceEqual(expected.Rows.Select(row =>
                    (row.RelativePath.ToUpperInvariant(), row.Status switch { ResourceComparisonStatus.MissingLeft => "Missing left",
                        ResourceComparisonStatus.MissingRight => "Missing right", _ => row.Status.ToString() }))),
                    "Resident mixed collection comparison matches master despite duplicate legacy history identities in " + version);
            }
            var currentColumns = await SnapshotPagesAsync(cursor => runtime.Snapshots.ListCurrentColumnsAsync(mixed.Key, 1, cursor), check,
                "Resident current columns with duplicate SQL ordinals");
            check(currentColumns.Select(value => value.Column.ColumnName).SequenceEqual(mixedLibrary.Snapshots[0].Columns.Select(value => value.ColumnName)),
                "Resident current metadata paging preserves root column order including orphan children");
            var syntheticHead = Required(await runtime.Snapshots.GetSnapshotAsync(syntheticKey), "resident unversioned snapshot");
            var syntheticVersion = (await history.GetDisplayedVersionsAsync(syntheticHead)).Single();
            var syntheticFiles = await history.ListFileRowsAsync(syntheticVersion);
            var syntheticCount = await history.CountFileLinesAsync(syntheticFiles.Single());
            check(syntheticVersion.VersionKey == 0 && syntheticVersion.IsInitial &&
                await history.CountVersionFilesAsync(syntheticHead, syntheticVersion) == 1 &&
                syntheticCount.Added == 2 && syntheticCount.Removed == 0 &&
                await history.ReadFileTextAsync(syntheticFiles.Single()) == "SELECT 1;\r\nSELECT 2;",
                "Synthetic V1 without saved history retains its resident file text and line counts");
            check(trace.Commands.Count == 0 && runtime.Session.Metrics.Snapshot().Started == sqlBefore,
                "All warmed history, previews, candidates and text/collection/table comparisons execute zero SQL commands");
        }
        File.Delete(blockedScratch);
        var cachedObject = await runtime.Snapshots.ReadObjectAsync(a.Procedure.RevisionKey);
        cachedObject.Definition = "mutated caller object";
        check((await runtime.Snapshots.ReadObjectAsync(a.Procedure.RevisionKey)).Definition != cachedObject.Definition,
            "Editing a returned object cannot mutate resident immutable definitions");
        var cachedTable = await runtime.Snapshots.ReadTableMetadataAsync(a.Table.RevisionKey);
        cachedTable.Table.TableName = "mutated caller table";
        check((await runtime.Snapshots.ReadTableMetadataAsync(a.Table.RevisionKey)).Table.TableName == "Evidence",
            "Editing returned table metadata cannot mutate resident immutable tables");

        var expectedHead = Required(await runtime.Snapshots.GetSnapshotAsync(a.Key), "head before restore");
        await using var staleContext = await history.OpenVersionAsync(a.Key, a.Version);
        await history.RestoreVersionAsync(new(runtime.Session.Epoch, a.Key, expectedHead.RowVersion), a.Version);
        check(!runtime.IsSnapshotMemoryReady([a.Key]) && runtime.IsSnapshotMemoryReady([b.Key]),
            "Publishing a restored version invalidates its snapshot while keeping unrelated resident snapshots");
        await ThrowsAsync<SnapshotConcurrencyException>(() => staleContext.ReadPageAsync(HistoricalCollection.Objects), check,
            "A context from an invalidated resident snapshot cannot continue serving stale projected history");
        await runtime.PreloadSnapshotMemoryAsync(selected);
        var restored = Required(await runtime.Snapshots.ResolveResourceAsync(a.Procedure.ResourceKey), "restored procedure");
        check(runtime.IsSnapshotMemoryReady(selected) && (await runtime.Snapshots.ReadObjectAsync(restored.RevisionKey)).Definition == "old compare-a",
            "Preload after publication exposes the new head and retains historical data");
        runtime.RetainSnapshotMemory([a.Key]);
        check(runtime.IsSnapshotMemoryReady([a.Key]) && !runtime.Session.SnapshotMemory.ContainsSnapshot(b.Key),
            "Changing scope releases snapshots that are no longer loaded and keeps shared snapshots ready");
        await using var unloadedContext = await history.OpenVersionAsync(a.Key, a.Version);
        runtime.InvalidateSnapshotMemory();
        check(!runtime.Session.SnapshotMemory.ContainsSnapshot(a.Key) && !runtime.Session.SnapshotMemory.ContainsSnapshot(b.Key),
            "Unloading the scope releases all resident snapshot reads");
        await ThrowsAsync<SnapshotConcurrencyException>(() => unloadedContext.ReadPageAsync(HistoricalCollection.Objects), check,
            "A context from an unloaded scope is fenced from later history reads");
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

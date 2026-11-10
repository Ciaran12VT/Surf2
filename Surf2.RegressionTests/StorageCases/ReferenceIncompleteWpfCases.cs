using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Index;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckIncompleteReferenceNavigationAsync(Surf2.MainWindow main, string connection,
        QueryIntegrationFixture seeded, Action<bool, string> check)
    {
        main.Dispatcher.VerifyAccess();
        await RuntimeWpfDrainReferenceRefreshAsync(main);
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var original = RuntimeWpfField<ExplorerScope>(main, "_relationalReferenceScope");
        var baselineCode = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows");
        var baselineGrids = RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows");
        var documents = new List<IndexedDocumentSummary>();
        SearchCursor? cursor = null;
        bool exhausted;
        do
        {
            var page = await runtime.Index.ReadDocumentsPageAsync(original.Context, cursor);
            documents.AddRange(page.Items); cursor = page.Next; exhausted = page.Exhausted;
        } while (!exhausted);
        var procedure = documents.First(d => d.SourceRevisionKey == seeded.Procedure.RevisionKey);
        var table = documents.First(d => d.SourceRevisionKey == seeded.Table.RevisionKey);
        string expectedTableText = (await runtime.Index.PrepareTableCodeAsync(seeded.Table.RevisionKey, table.Locator, "fixture")).Text;
        var prepared = await runtime.Index.PrepareDefinitionAsync(seeded.Procedure.RevisionKey, procedure.Locator, "fixture");
        var lease = await runtime.Index.BeginWorkAsync(await runtime.Index.GetHandleAsync(procedure.DocumentKey),
            Guid.NewGuid(), prepared.Fingerprint, procedure.Policy!);
        try
        {
            var scope = await runtime.Explorer.OpenScopeAsync(original.Context.ScopeKey, sortCultureName: original.SortCultureName);
            var catalogue = await runtime.References.LoadPaintAsync(scope);
            RuntimeWpfAssert(!catalogue.Coverage.FullyPublished && catalogue.Coverage.StaleOrUnindexed == 2 &&
                catalogue.Paint.Names.ContainsKey("Evidence"), check,
                "The actual WPF fixture has an unfinished procedure index, both stale aliases, and a highlighted current table");
            // Match InstallAsync's published incomplete view, with the tracked worker already drained.
            RuntimeWpfSetField(main, "_relationalExplorerScope", scope);
            RuntimeWpfSetField(main, "_relationalReferenceScope", scope);
            RuntimeWpfSetField(main, "_relationalReferenceCatalogue", catalogue);
            RuntimeWpfSetField(main, "_relationalPhysicalVerification", false);
            string sourcePath = DatabaseDocumentService.CreateCanonicalObjectDocumentPath(
                new DatabaseMetadataSnapshot { SnapshotId = "query-snapshot", DisplayName = "Query snapshot" }, seeded.ProcedureValue);
            var request = new ReferenceNavigationRequestedEventArgs("[dbo].[Evidence]", 1, 1, null);
            SymbolSummary selectedTable;
            using (var trace = new StateAccessSqlTrace(connection))
            {
                var resolution = await RuntimeWpfResolveReferenceAsync(main, request, sourcePath);
                RuntimeWpfAssert(resolution.Candidates.Length == 1 && resolution.Candidates[0].Freshness == IndexFreshness.Indexed &&
                    resolution.Candidates[0].Definition.Kind == ReferenceEntityKind.Table, check,
                    "MainWindow's actual resolver returns a verified table while unrelated procedure indexing is incomplete");
                selectedTable = resolution.Candidates[0];
                StateAccessNoReads(trace.Commands, check, "Incomplete-index table lookup", "TextContent", "TableColumnRevision", "Asset");
            }
            await RuntimeWpfInvokeTaskAsync(main, "PreviewRelationalReferenceAsync", request, sourcePath);
            string preview = RuntimeWpfNamed<ICSharpCode.AvalonEdit.TextEditor>(main, "PreviewEditor").Text;
            RuntimeWpfAssert(preview == expectedTableText &&
                main.StatusText.Contains("index is incomplete", StringComparison.OrdinalIgnoreCase), check,
                "Actual table preview renders exact selected code and explicitly qualifies incomplete coverage");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", request, sourcePath, null);
            var opened = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Where(w => !baselineCode.Contains(w)).ToArray();
            RuntimeWpfAssert(opened.Length == 1 && opened[0].State.BoundResourceKey == seeded.Table.ResourceKey &&
                opened[0].State.BoundSnapshotKey == seeded.SnapshotKey && opened[0].Text == expectedTableText,
                check, "Actual incomplete-index navigation opens the exact captured table in a real code window");
            RuntimeWpfAssert(RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Any(w =>
                w.State.BoundResourceKey == seeded.DataResourceKey && w.UsesGridProvider), check,
                "Table navigation still opens its related captured data through the paged grid presenter");
            RuntimeWpfAssert(main.StatusText.Contains("Opened verified reference", StringComparison.OrdinalIgnoreCase) &&
                main.StatusText.Contains("index is incomplete", StringComparison.OrdinalIgnoreCase) &&
                !RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue").Coverage.FullyPublished,
                check, "Positive navigation does not claim complete readiness or hide incomplete coverage");

            var access = RuntimeWpfField<RelationalDocumentService>(main, "_relationalDocuments");
            var address = await access.ResolveResourceAsync(seeded.SnapshotKey, seeded.Table.ResourceKey, scope);
            var plan = await access.DescribeTextAsync(address);
            var tableSource = await runtime.Index.PrepareTableCodeAsync(seeded.Table.RevisionKey, table.Locator, "fixture");
            var tableLease = await runtime.Index.BeginWorkAsync(await runtime.Index.GetHandleAsync(table.DocumentKey),
                Guid.NewGuid(), tableSource.Fingerprint, table.Policy!);
            await runtime.Index.PublishAsync(new(tableLease, tableSource.Text, tableSource.Language, tableSource.Symbols, tableSource.SourceRevisionKey));
            var republished = await runtime.Explorer.OpenScopeAsync(original.Context.ScopeKey, sortCultureName: original.SortCultureName);
            RuntimeWpfSetField(main, "_relationalExplorerScope", republished);
            RuntimeWpfSetField(main, "_relationalReferenceScope", republished);
            RuntimeWpfSetField(main, "_relationalReferenceCatalogue", await runtime.References.LoadPaintAsync(republished));
            int publications = 0;
            Action published = () => publications++;
            await RuntimeWpfInvokeTaskAsync(main, "TryOpenRelationalFileCoreAsync", address.DocumentPath, null, null, null,
                RelationalDocumentService.Reference(selectedTable, address.DocumentPath), null, false, CancellationToken.None,
                plan.Key, republished.Context, address, selectedTable, published);
            RuntimeWpfAssert(publications == 0 && RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").SequenceEqual(baselineCode.Concat(opened)),
                check, "A republished symbol with identical source text cannot acknowledge stale reference navigation or create another window");
            RuntimeWpfAssert((await access.DescribeTextAsync(address)).Key == plan.Key && opened[0].Text == expectedTableText,
                check, "The stale-symbol check is independent of unchanged source/text identity and preserves the existing editor");
            var miss = new ReferenceNavigationRequestedEventArgs("NoSuchIncompleteTarget", 1, 1, null);
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", miss, sourcePath, null);
            RuntimeWpfAssert(main.StatusText.Contains("does not mean the target is absent", StringComparison.OrdinalIgnoreCase) &&
                RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").SequenceEqual(baselineCode.Concat(opened)), check,
                "Missing references on an incomplete index are explicitly inconclusive and open no window");
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync",
                new ReferenceNavigationRequestedEventArgs("NeedleProc", 1, 1, null), sourcePath, null);
            RuntimeWpfAssert(main.StatusText.Contains("index is incomplete", StringComparison.OrdinalIgnoreCase) &&
                !RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(w => w.State.BoundResourceKey == seeded.Procedure.ResourceKey),
                check, "Actual navigation never opens the stale procedure even though its old highlight metadata remains available");
            RuntimeWpfSetField(main, "_relationalReferenceScope", null!);
            await RuntimeWpfInvokeTaskAsync(main, "NavigateRelationalReferenceAsync", request, sourcePath, null);
            RuntimeWpfAssert(main.StatusText.Contains("indexing is not ready yet", StringComparison.OrdinalIgnoreCase), check,
                "A click before catalogue publication now reports readiness instead of silently doing nothing");
            RuntimeWpfCheckEmptyLibraries(main, check);
        }
        finally
        {
            await runtime.Index.PublishAsync(new(lease, prepared.Text, prepared.Language, prepared.Symbols, prepared.SourceRevisionKey));
            foreach (var window in RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Where(w => !baselineCode.Contains(w)))
                await RuntimeWpfInvokeTaskAsync(main, "CloseOpenWindowAsync", window);
            foreach (var grid in RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").Where(w => !baselineGrids.Contains(w)))
                await RuntimeWpfInvokeTaskAsync(main, "CloseSpreadsheetWindowAsync", grid);
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalLastActiveScopeAsync");
            await RuntimeWpfDrainReferenceRefreshAsync(main);
            RuntimeWpfAssert(RuntimeWpfField<ReferenceCatalogue>(main, "_relationalReferenceCatalogue").Coverage.FullyPublished &&
                RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").SequenceEqual(baselineCode) &&
                RuntimeWpfWindows<FloatingSpreadsheetWindow>(main, "_openSpreadsheetWindows").SequenceEqual(baselineGrids),
                check, "Incomplete-reference checks restore readiness and close only their own windows before subsequent scenarios");
        }
    }
}

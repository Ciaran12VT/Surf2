using System.Collections.Immutable;
using System.IO;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyLoadedBrowsePresentationAsync(Surf2.MainWindow main, SqlFixture fixture,
        string connection, string folder, Action<bool, string> check)
    {
        var runtime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
        var scope = RuntimeWpfField<ExplorerScope>(main, "_relationalExplorerScope");
        var indexedScope = scope with { Context = scope.Context with { CatalogueGeneration = scope.Context.CatalogueGeneration + 1 } };
        check(runtime.IsSelectedExplorerScope(scope) && runtime.IsSelectedExplorerScope(indexedScope),
            "Publishing a derived index retains the selected scope's browsing identity");
        check(!RelationalRuntime.SameExplorerSelection(scope, scope with
            { Resources = scope.Resources.Select((resource, i) => i == 0 ? resource with { IsLoaded = !resource.IsLoaded } : resource).ToImmutableArray() }) &&
            !RelationalRuntime.SameExplorerSelection(scope, scope with { Context = scope.Context with { ScopeKey = scope.Context.ScopeKey + 1 } }),
            "Changing the scope or loaded membership still invalidates an old open request");

        var sqlWindow = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows")
            .First(window => window.State.FilePath.StartsWith("db://@", StringComparison.OrdinalIgnoreCase));
        string tooltip = (string)RuntimeWpfInvoke(main, "GetOpenDocumentToolTip", sqlWindow.State.FilePath)!;
        check(tooltip.Contains("Resource:", StringComparison.Ordinal) && tooltip.Contains("db://", StringComparison.Ordinal) &&
            tooltip.Contains(sqlWindow.State.DisplayName, StringComparison.Ordinal) && !tooltip.Contains("db://@", StringComparison.Ordinal) &&
            !tooltip.Contains(runtime.Session.Epoch.ToString("N"), StringComparison.Ordinal),
            "SQL tab tooltips show the database and qualified object name while window keys retain typed identity");

        string path = Path.Combine(folder, "IndexPublicationRace.cs");
        await File.WriteAllTextAsync(path, "class IndexPublicationRace { }\n");
        var opening = (Task<bool>)RuntimeWpfInvoke(main, "TryOpenRelationalFileAsync", path)!;
        check(!opening.IsCompleted, "The fixture holds a resource open across a derived scope publication");
        RuntimeWpfSetField(main, "_relationalExplorerScope", indexedScope);
        await opening;
        check(RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Any(window => window.State.FilePath == path),
            "An in-progress file open survives index publication in the same scope");

        // Reproduce idle catalogue drift without changing the selected scope or its sources.
        await fixture.DestinationSqlAsync("UPDATE surf.SnapshotCatalogueHead SET UserKey=UserKey WHERE UserKey=1; " +
            "UPDATE surf.StateCatalogueGeneration SET PublicationId=NEWID() WHERE ProfileKey=1 AND Kind=1;");
        await using var documents = new RelationalDocumentService(runtime);
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var roots = await runtime.Explorer.GetRootsAsync(indexedScope);
            var physical = roots.Single(node => node.FullPath == folder);
            var children = await V2CatalogueChildrenAsync(runtime.Explorer, indexedScope, physical);
            var selected = children.Single(node => node.FullPath == path);
            var address = await documents.ResolveExplorerNodeAsync(indexedScope, selected);
            await documents.RequireCurrentAsync(indexedScope);
            var database = roots.First(node => node.ResourceKind == ResourceKind.DatabaseSnapshot);
            var categories = await V2CatalogueChildrenAsync(runtime.Explorer, indexedScope, database);
            var procedures = await V2CatalogueChildrenAsync(runtime.Explorer, indexedScope,
                categories.Single(node => node.Category == ExplorerCategory.Procedures));
            check(address.DocumentPath == path && procedures.Length > 0,
                "Local folders and cached database names remain available after unrelated catalogue changes");
            check(trace.Commands.Count == 0,
                "Loaded folder browsing, file resolution and SQL-name expansion issue no database commands");
        }
        runtime.SelectExplorerScope(null);
        check(!runtime.IsSelectedExplorerScope(scope), "Retiring a scope releases its in-memory browsing selection");
        runtime.SelectExplorerScope(indexedScope);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            await documents.RequireCurrentAsync(indexedScope, cancelled.Token);
            throw new InvalidOperationException("A cancelled browse request remained active.");
        }
        catch (OperationCanceledException) { check(true, "Cached browsing still respects cancellation"); }
    }
}

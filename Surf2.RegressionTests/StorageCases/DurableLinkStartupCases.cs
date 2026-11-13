using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckDurableLinkStartupAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_DurableStartup_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var state = new RelationalStateStore(session);
        var settings = new AppSettings { LoadMostRecentWorkbenchOnStartup = true,
            Diagnostics = new() { EnableInternalLogging = false } };
        settings.EnsureDefaults();
        await InTransactionAsync(session, async (c, t) =>
        {
            await state.ImportSettingsAsync(c, t, settings, Guid.NewGuid());
            await state.ImportWorkspaceAsync(c, t, new WorkspaceState { CanvasZoom = 1 }, Guid.NewGuid());
            await state.ImportScopeSelectionAsync(c, t, 2, "query-scope", Guid.NewGuid());
        });
        var seeded = await SeedQueryIntegrationAsync(fixture, session, new RelationalContentStore(), new RelationalCaptureStore(session), state);
        var initialRuntime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        await using var initialDocuments = new RelationalDocumentService(initialRuntime);
        var procedure = await initialDocuments.ResolveResourceAsync(seeded.SnapshotKey, seeded.Procedure.ResourceKey, null);
        var table = await initialDocuments.ResolveResourceAsync(seeded.SnapshotKey, seeded.Table.ResourceKey, null);
        string durable = procedure.PersistentDocumentPath!;
        var diagram = LinkDiagram("startup-durable-diagram", new Dictionary<string, string> { ["startup-reference"] = durable + ":2" });
        var libraryToken = await state.CreateDiagramAsync(new(diagram, new Dictionary<int, byte[]>()), 1, Guid.NewGuid());
        var workbench = new WorkbenchState
        {
            WorkbenchId = "startup-durable-workbench", Name = "Startup durable workbench", IsDefaultForScope = true,
            ScopeId = "query-scope", ScopeName = "Query scope", IsCodeViewVisible = true, IsDiagramViewVisible = true,
            ActiveWorkspaceView = "Code", CodeViewMode = "Tabs", ActiveDocumentPath = durable,
            ActiveDiagramSnapshot = diagram, ActiveDiagramId = diagram.DiagramId, ActiveDiagramName = diagram.Name,
            OpenDocuments = [BoundWindow(durable, seeded.SnapshotKey, seeded.Procedure.ResourceKey),
                BoundWindow(table.PersistentDocumentPath!, seeded.SnapshotKey, seeded.Table.ResourceKey)]
        };
        var workbenchToken = await state.CreateWorkbenchAsync(new(workbench, new Dictionary<int, byte[]>()), 0, Guid.NewGuid());
        // Simulate records left by the previous app. The current writer would correctly reject this expired epoch.
        string expiredProcedure = RelationalDocumentService.IdentityPath(Guid.NewGuid(),
            (await initialRuntime.Snapshots.ResolveResourceAsync(seeded.Procedure.ResourceKey))!, ExplorerCategory.Procedures, false);
        string expiredTable = RelationalDocumentService.IdentityPath(Guid.NewGuid(),
            (await initialRuntime.Snapshots.ResolveResourceAsync(seeded.Table.ResourceKey))!, ExplorerCategory.Tables, false);
        await fixture.DestinationSqlAsync("""
UPDATE surf.DiagramObject SET MetadataLink=@Link WHERE Id=N'startup-reference';
UPDATE surf.DocumentWindowState SET FilePath=CASE WHEN BoundResourceKey=@Procedure THEN @Path ELSE @TablePath END WHERE TargetState=1;
UPDATE surf.Workbench SET ActiveDocumentPath=@Path WHERE WorkbenchKey=@Workbench;
""", RelationalSession.Parameter("@Link", SqlDbType.NVarChar, expiredProcedure + ":2", -1),
            RelationalSession.Parameter("@Path", SqlDbType.NVarChar, expiredProcedure, -1),
            RelationalSession.Parameter("@TablePath", SqlDbType.NVarChar, expiredTable, -1),
            RelationalSession.Parameter("@Procedure", SqlDbType.BigInt, seeded.Procedure.ResourceKey),
            RelationalSession.Parameter("@Workbench", SqlDbType.BigInt, workbenchToken.Key));
        var before = Required(await state.ReadWorkbenchAsync(workbenchToken.Key), "startup old workbench").Value.Workbench;
        RuntimeWpfAssert(before.ActiveDiagramSnapshot!.Objects.Single().Metadata.Link == expiredProcedure + ":2" &&
            before.OpenDocuments[0].FilePath == expiredProcedure, check,
            "Startup fixture begins with expired session links in its saved diagram and workbench windows");

        var main = new Surf2.MainWindow(initialRuntime.Options);
        var root = new Grid { DataContext = main };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(main.Resources);
        var content = (FrameworkElement)main.Content; main.Content = null; root.Children.Add(content);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 durable startup offscreen fixture")
        { PositionX = -32000, PositionY = -32000, Width = 1280, Height = 820,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        host.RootVisual = root; RuntimeWpfArrange(root, 1280, 820);
        try
        {
            RuntimeWpfAssert(await ((Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!).WaitAsync(TimeSpan.FromSeconds(90)),
                check, "Actual MainWindow startup automatically repairs saved links and restores the default workbench");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            var restartedRuntime = RuntimeWpfField<RelationalRuntime>(main, "_relational");
            RuntimeWpfAssert(restartedRuntime.Session.Epoch != initialRuntime.Session.Epoch, check,
                "Restoring the saved workbench runs in a new application session");
            var windows = RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows");
            var procedureWindow = windows.Single(window => window.State.BoundResourceKey == seeded.Procedure.ResourceKey);
            RuntimeWpfAssert(windows.Count() == 2 && procedureWindow.Text == seeded.ProcedureValue.Definition &&
                procedureWindow.State.FilePath.Contains(restartedRuntime.Session.Epoch.ToString("N"), StringComparison.Ordinal), check,
                "Startup restores both saved documents under the new epoch with the original SQL content");
            RuntimeWpfAssert(ReferenceEquals(RuntimeWpfOptionalField<FloatingCodeWindow>(main, "_activeCodeWindow"), procedureWindow), check,
                "The saved active document remains selected even though another tab restored afterwards");
            var shape = RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramShapeControl>().Single();
            RuntimeWpfAssert(shape.Metadata.Link == durable + ":2", check,
                "The embedded diagram is restored with its repaired durable reference already applied");
            var repairedWorkbench = Required(await state.ReadWorkbenchAsync(workbenchToken.Key), "startup repaired workbench").Value.Workbench;
            var repairedLibrary = Required(await state.ReadDiagramAsync(libraryToken.Key), "startup repaired library diagram").Value.Document;
            RuntimeWpfAssert(repairedWorkbench.ActiveDocumentPath == durable && repairedWorkbench.OpenDocuments[0].FilePath == durable &&
                repairedWorkbench.ActiveDiagramSnapshot!.Objects.Single().Metadata.Link == durable + ":2" &&
                repairedLibrary.Objects.Single().Metadata.Link == durable + ":2", check,
                "The startup hook repairs stored workbench and library references before restoring the UI");
            RuntimeWpfAssert(await (Task<bool>)RuntimeWpfInvoke(main, "OpenMetadataLinkAsync", shape.Metadata.Link)! &&
                ReferenceEquals(RuntimeWpfOptionalField<FloatingCodeWindow>(main, "_activeCodeWindow"), procedureWindow) &&
                RuntimeWpfWindows<FloatingCodeWindow>(main, "_openWindows").Count() == 2, check,
                "The restored diagram reference opens the same target without creating a duplicate tab");
        }
        finally
        {
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            try { await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync"); }
            finally
            {
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                host.RootVisual = null; root.Children.Clear(); main.Close();
            }
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

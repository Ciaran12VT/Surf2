using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Data.SqlClient;
using Surf2.Controls;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private static async Task RuntimeWpfCheckDiagramOpenAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_DiagramOpen_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var session = new RelationalSession(connection);
        var state = new RelationalStateStore(session);
        const string scopeId = "diagram-open-fixture";
        var settings = new AppSettings { LoadMostRecentWorkbenchOnStartup = false,
            Diagnostics = new() { EnableInternalLogging = false } };
        settings.EnsureDefaults();
        const string imageId = "diagram-open-shared-image";
        settings.DiagramImages.Images.Add(new() { Id = imageId, Name = "Shared diagram image", SortOrder = 1,
            ImageDataBase64 = Convert.ToBase64String(Png(59)) });
        await InTransactionAsync(session, async (c, t) =>
        {
            await state.ImportSettingsAsync(c, t, settings, Guid.NewGuid());
            await state.ImportWorkspaceAsync(c, t, new WorkspaceState { CanvasZoom = 1 }, Guid.NewGuid());
            await state.ImportScopeSelectionAsync(c, t, 2, scopeId, Guid.NewGuid());
        });
        await fixture.MarkTestFixtureReadyAsync();
        var scope = new Scope { ScopeId = scopeId, Name = "Explorer diagram fixture" };
        string[] labels = ["First diagram", "Blocked diagram", "Latest diagram"];
        var diagramKeys = new List<long>();
        foreach (string label in labels)
        {
            string diagramId = Guid.NewGuid().ToString("N");
            var diagram = new DiagramDocument
            {
                DiagramId = diagramId, Name = label, Objects =
                [new() { Id = "shape", ObjectType = DiagramObjectType.Shape, ShapeKind = DiagramShapeKind.Rectangle,
                    LabelText = label, Left = 100, Top = 100, Width = 160, Height = 100 }]
            };
            if (diagramKeys.Count == 0)
                for (int image = 0; image < 2; image++)
                    diagram.Objects.Add(new() { Id = "image-" + image, ObjectType = DiagramObjectType.Image,
                        ImageDefinitionId = imageId, ImageName = "Shared diagram image", Left = 300 + image * 50,
                        Top = 100, Width = 40, Height = 40 });
            var token = await state.CreateDiagramAsync(new(diagram, new Dictionary<int, byte[]>()), diagramKeys.Count, Guid.NewGuid());
            diagramKeys.Add(token.Key);
            scope.Resources.Add(new() { ResourceId = diagramId, Kind = ResourceKind.Diagram, Path = diagramId });
        }
        var scopeToken = await state.CreateScopeAsync(scope, 0, Guid.NewGuid());
        var targets = diagramKeys.Select((key, ordinal) => new ScopeResourceTarget(ordinal, null, key)).ToArray();
        await InTransactionAsync(session, (c, t) => state.ResolveScopeResourceTargetsAsync(c, t, scopeToken, targets, Guid.NewGuid()));

        var main = new Surf2.MainWindow(SqlServerConnectionOptions.FromConnectionString(connection));
        var root = new Grid { DataContext = main };
        root.Resources.MergedDictionaries.Add(Application.Current.Resources);
        root.Resources.MergedDictionaries.Add(main.Resources);
        var content = (FrameworkElement)main.Content;
        main.Content = null;
        root.Children.Add(content);
        using var host = new HwndSource(new HwndSourceParameters("Surf2 diagram open offscreen fixture")
        {
            PositionX = -32000, PositionY = -32000, Width = 1280, Height = 820,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080
        });
        host.RootVisual = root;
        RuntimeWpfArrange(root, 1280, 820);
        try
        {
            RuntimeWpfAssert(await ((Task<bool>)RuntimeWpfInvoke(main, "TryLoadRelationalPersistenceAsync")!).WaitAsync(TimeSpan.FromSeconds(60)),
                check, "Diagram open fixture restores its selected scope");
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            await RuntimeWpfDrainReferenceRefreshAsync(main);
            await RuntimeWpfField<Task>(main, "_relationalImageMenuTask");
            int savePrompts = 0;
            MessageBoxResult saveChoice = MessageBoxResult.No;
            typeof(Surf2.MainWindow).GetProperty("RelationalStateSwitchConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(main, (Func<MessageBoxResult>)(() => { savePrompts++; return saveChoice; }));
            var diagramRoot = main.RootNodes.Single(node => node.FullPath == DiagramDocumentService.DiagramRootPath);
            await RuntimeWpfInvokeTaskAsync(main, "LoadRelationalChildrenAsync", diagramRoot);

            Directory.CreateDirectory(fixture.OwnedDirectory);
            string metadataFile = Path.Combine(fixture.OwnedDirectory, "diagram-reference.cs");
            await File.WriteAllTextAsync(metadataFile, "class DiagramReference { public void Open() { } }");
            RuntimeWpfAssert(await (Task<bool>)RuntimeWpfInvoke(main, "OpenMetadataLinkAsync", metadataFile)!, check,
                "A diagram metadata link reports success after its code window exists");

            using (var firstTrace = new StateAccessSqlTrace(connection))
            {
                await ClickAsync(0);
                await RuntimeWpfWaitAsync(() => IsShowing(0), "first Explorer diagram renders its saved shape");
                RuntimeWpfAssert(RuntimeWpfField<object>(main, "_activeWorkspaceView").ToString() == "Diagram", check,
                    "First Explorer double-click publishes the diagram and selects its workspace view");
                var reads = StateAccessReads(firstTrace.Commands, "Diagram");
                RuntimeWpfAssert(reads.Count > 0 && reads.All(command => command.Number("@Key") == diagramKeys[0]), check,
                    "The selected Explorer occurrence reads only its typed diagram key");
                RuntimeWpfAssert(savePrompts == 0, check,
                    "Opening a diagram keeps existing code windows without prompting to replace the workspace");
                var imageControls = RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramImageControl>().ToArray();
                RuntimeWpfAssert(imageControls.Length == 2 && ReferenceEquals(imageControls[0].ImageSource, imageControls[1].ImageSource), check,
                    "Repeated image definitions reuse the same validated decoded bitmap when opening a diagram");
                int imageCatalogueReads = firstTrace.Commands.Count(command => command.Text
                    .Contains("FROM SURF.DIAGRAMIMAGEDEFINITION S", StringComparison.Ordinal));
                RuntimeWpfAssert(imageCatalogueReads == 1, check,
                    "Opening a diagram scans image definition metadata once for all repeated occurrences (observed " + imageCatalogueReads + ")");
            }

            RuntimeWpfAssert(await (Task<bool>)RuntimeWpfInvoke(main, "OpenMetadataLinkAsync", metadataFile)!, check,
                "An already open diagram reference can be revealed again");
            RuntimeWpfAssert(RuntimeWpfField<object>(main, "_activeWorkspaceView").ToString() == "Code", check,
                "A diagram reference activates the Code workspace instead of leaving its window hidden behind Diagram");
            RuntimeWpfAssert(!await (Task<bool>)RuntimeWpfInvoke(main, "OpenMetadataLinkAsync",
                "db://00000000000000000000000000000000/object/StoredProcedure/dbo.missing.sql")! &&
                main.StatusText.StartsWith("Could not open", StringComparison.Ordinal), check,
                "An unavailable database reference keeps its failure status and does not report opened");

            var originalShape = RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramShapeControl>().Single();
            originalShape.ApplyDetails("Unsaved first diagram", originalShape.OutlineColorText, originalShape.BackColorText);
            using (var reselectTrace = new StateAccessSqlTrace(connection))
            {
                RuntimeWpfAssert(await (Task<bool>)RuntimeWpfInvoke(main, "OpenRelationalDiagramFromUiAsync",
                    DiagramDocumentService.CreateDiagramDocumentPath(scope.Resources[0].Path), diagramKeys[0])!, check,
                    "Reselecting the current saved diagram succeeds without replacing its canvas");
                RuntimeWpfAssert(savePrompts == 0 && originalShape.LabelText == "Unsaved first diagram" &&
                    ReferenceEquals(originalShape, RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramShapeControl>().Single()), check,
                    "Reselecting the same diagram retains unsaved edits without a save prompt");
                RuntimeWpfAssert(!reselectTrace.Commands.Any(command => command.Text
                    .Contains("FROM SURF.DIAGRAMOBJECT", StringComparison.Ordinal)), check,
                    "Reselecting the same revision does not reload the diagram graph");
            }
            saveChoice = MessageBoxResult.Cancel;
            RuntimeWpfAssert(!await (Task<bool>)RuntimeWpfInvoke(main, "OpenRelationalDiagramFromUiAsync",
                DiagramDocumentService.CreateDiagramDocumentPath(scope.Resources[1].Path), diagramKeys[1])! && savePrompts == 1 &&
                ReferenceEquals(originalShape, RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramShapeControl>().Single()), check,
                "Switching away from a changed diagram still prompts and Cancel retains its canvas");
            originalShape.ApplyDetails(labels[0], originalShape.OutlineColorText, originalShape.BackColorText);
            saveChoice = MessageBoxResult.No;

            await using var blocker = await session.OpenAsync();
            await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.Serializable);
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT_BIG(*) FROM surf.DiagramObject WITH (TABLOCKX,HOLDLOCK);";
                await command.ExecuteScalarAsync();
            }
            var graphReadEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var trace = new StateAccessSqlTrace(connection, command =>
            {
                if (StateAccessSql(command.CommandText).Contains("FROM SURF.DIAGRAMOBJECT", StringComparison.Ordinal))
                    graphReadEntered.TrySetResult(true);
            });
            await ClickAsync(1);
            await graphReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var blockedRequest = RuntimeWpfField<object>(main, "_relationalSelectionLifetime");
            var blockedCompletion = (Task)blockedRequest.GetType().GetProperty("Completed")!.GetValue(blockedRequest)!;
            RuntimeWpfAssert(!blockedCompletion.IsCompleted && IsShowing(0), check,
                "A blocked diagram read keeps the previous canvas visible");

            await ClickAsync(2);
            await blockedCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            RuntimeWpfAssert(IsShowing(0), check, "A newer Explorer click cancels the blocked read before it can replace the canvas");
            await transaction.RollbackAsync();
            await RuntimeWpfWaitAsync(() => IsShowing(2), "latest Explorer diagram renders after the read lock clears");
            RuntimeWpfAssert(IsShowing(2), check, "The latest Explorer selection publishes its own diagram and shape");

            async Task ClickAsync(int index)
            {
                var node = diagramRoot.Children.Single(n => n.ScopeResourceId == scope.Resources[index].ResourceId);
                var item = await RuntimeWpfExplorerItemAsync(main, root, [diagramRoot, node]);
                var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Control.PreviewMouseDoubleClickEvent, Source = item };
                RuntimeWpfNamed<TreeView>(main, "ObjectExplorer").RaiseEvent(click);
                RuntimeWpfAssert(click.Handled, check, "The real Object Explorer handler accepts diagram double-click " + (index + 1));
            }

            bool IsShowing(int index) =>
                RuntimeWpfOptionalField<StateEditSession<DiagramState>>(main, "_relationalDiagramEdit")?.SubjectKey == diagramKeys[index] &&
                RuntimeWpfNamed<Canvas>(main, "DiagramCanvas").Children.OfType<DiagramShapeControl>().SingleOrDefault()?.LabelText == labels[index];
        }
        finally
        {
            RuntimeWpfField<TaskCompletionSource<bool>>(main, "_startupReadyCompletion").TrySetResult(true);
            try { await RuntimeWpfInvokeTaskAsync(main, "DisposeRelationalPersistenceAsync"); }
            finally
            {
                RuntimeWpfSetField(main, "_shutdownSaveCompleted", true);
                host.RootVisual = null;
                root.Children.Clear();
                main.Close();
            }
        }
        await fixture.VerifySourceUnchangedAsync(check);
    }
}

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private sealed record StateFixture(AppSettings Settings, StateToken SettingsToken,
        WorkspaceState Workspace, StateToken WorkspaceToken, Scope Scope, StateToken ScopeToken,
        StateToken OtherScopeToken, StateToken SelectionToken);

    private static async Task<StateFixture> SeedStateAsync(RelationalSession session, RelationalStateStore store, Action<bool, string> check)
    {
        var settings = new AppSettings
        {
            LoadMostRecentWorkbenchOnStartup = true,
            Appearance = new() { Theme = " Raw custom theme " },
            Diagnostics = new() { EnableInternalLogging = true },
            ResourceComparison = new() { IgnoreCaseByDefault = true, IgnoreWhitespaceByDefault = false },
            CodeWindows = new() { DefaultBackcolor = " raw color ", BackcolorsByExtension =
            [
                new() { Extension = ".SQL ", Backcolor = "custom one", Language = "unrecognized language " },
                new() { Extension = ".SQL ", Backcolor = "custom two", Language = "SQL Server" }
            ] },
            KeyboardShortcuts = new()
            {
                Version = 2, EnableCanvasCtrlMousePanning = false, EnableTabCtrlMouseScrolling = true,
                EnableTabCtrlShiftMouseAutoscrolling = false, EnableCodeShiftMouseAutoscrolling = true,
                EnableCodeCtrlShiftMouseScrollbarLockedScrolling = false, EnableCodeCanvasShiftMousePanning = true,
                EnableCodeCanvasCtrlShiftMouseZooming = false, EnableCodeViewCtrlPlusMinusNavigation = true,
                EnableCtrlNumberViewSwitching = false, EnableCodeTabCtrlASNavigation = true,
                EnableDiagramCtrlQSidebarToggle = false, EnableDiagramCtrlWWorkflowSidebar = true,
                EnableDiagramShiftMousePanning = false, EnableDiagramCtrlShiftMouseZooming = true
            },
            ReferenceHighlights = new() { Styles =
            [
                new() { Language = " custom ", Kind = ReferenceEntityKind.Class, Foreground = "raw foreground", IsBold = true, IsItalic = false, IsUnderline = true },
                new() { Language = " custom ", Kind = ReferenceEntityKind.Class, Foreground = "other foreground", IsBold = false, IsItalic = true, IsUnderline = false }
            ] },
            DiagramImages = new() { Images =
            [
                new() { Id = "duplicate image", Name = "first ", Regex = "legacy.*", MatchTarget = "raw-target ",
                    NameRegex = "", ContentRegex = "", ResourceTypeFilter = "unrecognized type ", SortOrder = -3,
                    OriginalFileName = "original.png", ImageDataBase64 = Convert.ToBase64String(Png(17)) + "\r\n" },
                new() { Id = "duplicate image", Name = "second", Regex = "other", MatchTarget = "Content",
                    NameRegex = "n.+", ContentRegex = "c.+", ResourceTypeFilter = "File", SortOrder = 9,
                    OriginalFileName = "empty.png", ImageDataBase64 = "" }
            ] }
        };
        var workspace = new WorkspaceState
        {
            LastFolderPath = null, CanvasZoom = 1.375, ViewportHorizontalOffset = -2.25, ViewportVerticalOffset = 13.625,
            UnloadedResourceIds = ["same ", "same ", ""], OpenDocuments = [Window("duplicate path "), Window("duplicate path ")]
        };
        var scope = new Scope
        {
            ScopeId = "duplicate scope ", Name = " Scope \u00e9 ", Description = "description\r\n\ud83d\ude00 ",
            Resources = new ObservableCollection<ScopedResource>(Enum.GetValues<ResourceKind>().Select((kind, i) => new ScopedResource
            {
                ResourceId = "duplicate resource", Kind = kind, Path = "fixture-only-unresolved-path-" + i,
                DisplayNameOverride = "name " + i, DetailsOverride = "details\r\n" + i,
                AddedAtUtc = EvidenceTime.AddTicks(i), IncludeChildren = i % 2 == 0
            })),
            VirtualFolders =
            [
                new() { VirtualFolderId = "duplicate folder", Name = "folder one", ParentNodeKey = "raw-parent ", ChildNodeKeys = ["node ", "node ", "missing-node"] },
                new() { VirtualFolderId = "duplicate folder", Name = "folder two", ParentNodeKey = "other-parent", ChildNodeKeys = [] }
            ]
        };
        StateFixture? result = null;
        await InTransactionAsync(session, async (connection, transaction) =>
        {
            var settingsToken = await store.ImportSettingsAsync(connection, transaction, settings, Guid.NewGuid());
            var workspaceToken = await store.ImportWorkspaceAsync(connection, transaction, workspace, Guid.NewGuid());
            var scopeToken = await store.ImportScopeAsync(connection, transaction, scope, 4, Guid.NewGuid());
            var otherScopeToken = await store.ImportScopeAsync(connection, transaction,
                new Scope { ScopeId = scope.ScopeId, Name = "other scope" }, 4, Guid.NewGuid());
            var selectionToken = await store.ImportScopeSelectionAsync(connection, transaction, 37, "unresolved-last-scope ", Guid.NewGuid());
            result = new(settings, settingsToken, workspace, workspaceToken, scope, scopeToken, otherScopeToken, selectionToken);
        });
        // Caller-owned import transactions must not be committed by the domain store.
        await using (var connection = await session.OpenAsync())
        await using (var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync())
        {
            await store.ImportScopeAsync(connection, transaction, new Scope { ScopeId = "rolled-back", Name = "not persisted" }, 0, Guid.NewGuid());
            await transaction.RollbackAsync();
        }
        check(result != null, "State imports stage settings, workspace, scopes and selection during Migrating");
        return Required(result, "state seed");
    }

    private static async Task VerifyStateAsync(RelationalSession session, RelationalStateStore store, StateFixture f, Action<bool, string> check)
    {
        var settings = Required(await store.ReadSettingsAsync(), "settings");
        SameModel(f.Settings, settings.Value, check, "Settings preserve every writable field, raw theme/language/regex, duplicate children and original image base64");
        var preferences = Required(await store.ReadPreferenceSummaryAsync(), "preferences");
        check(preferences.Theme == f.Settings.Appearance.Theme && preferences.EnableInternalLogging &&
            preferences.DefaultBackcolor == f.Settings.CodeWindows.DefaultBackcolor && preferences.Token.Key == settings.Token.Key,
            "Preference summary is usable without loading image definitions");
        SameModel(f.Settings.KeyboardShortcuts, preferences.KeyboardShortcuts, check, "Preference summary preserves version and each keyboard toggle without defaults");
        var workspace = Required(await store.ReadWorkspaceAsync(), "workspace");
        SameModel(f.Workspace, workspace.Value, check, "Workspace preserves null folder, duplicate unloaded IDs, duplicate windows, font size and filter ordinals");
        var scope = Required(await store.ReadScopeAsync(f.ScopeToken.Key), "scope");
        SameModel(f.Scope, scope.Value, check, "Scope preserves all resource kinds, duplicate identities, raw paths and ordered virtual-folder members");
        var scopes = await store.ListScopesAsync(pageSize: 1);
        var scopeCursor = Required(scopes.Next, "scope cursor");
        var secondScope = await store.ListScopesAsync(pageSize: 1, after: scopeCursor);
        check(scopes.Items.Count == 1 && scopes.Items[0].Token.Key == f.ScopeToken.Key && secondScope.Items.Count == 1 &&
            secondScope.Items[0].Token.Key == f.OtherScopeToken.Key && secondScope.Next == null,
            "State catalogue pages retain duplicate IDs and tied ordinal without exposing rolled-back imports");
        var selection = Required(await store.ReadScopeSelectionAsync(), "scope selection");
        check(selection.Value.SchemaVersion == 37 && selection.Value.LastActiveScopeId == "unresolved-last-scope ",
            "Scope selection retains schema version and unresolved original ID");
        var selectionToken = await store.SaveScopeSelectionAsync(new(38, null), selection.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.SaveScopeSelectionAsync(new(99, "stale"), selection.Token, Guid.NewGuid()), check,
            "Scope selection rejects stale token");
        check((await store.ReadScopeSelectionAsync())!.Value == new ScopeSelection(38, null) && !selectionToken.Version.SequenceEqual(selection.Token.Version),
            "Scope selection preserves null and advances token without accepting stale state");

        var originalDiagram = Diagram("published diagram");
        var diagramToken = await store.CreateDiagramAsync(originalDiagram, 3, Guid.NewGuid());
        var otherDiagramToken = await store.CreateDiagramAsync(Diagram("other diagram"), 3, Guid.NewGuid());
        var diagram = Required(await store.ReadDiagramAsync(diagramToken.Key), "diagram");
        SameModel(originalDiagram.Document, diagram.Value.Document, check, "Diagram preserves all subtype fields, geometry, font, portals, workflows, documentation and ordered queries");
        SameImages(originalDiagram.PastedImages, diagram.Value.PastedImages, check, "Pasted images are lossless and bound to ordinal despite duplicate object IDs and filenames");
        var diagrams = await store.ListDiagramsAsync(pageSize: 1);
        var diagramCursor = Required(diagrams.Next, "diagram cursor");
        var nextDiagram = await store.ListDiagramsAsync(pageSize: 1, after: diagramCursor);
        check(diagrams.Items.Count == 1 && nextDiagram.Items.Count == 1 && nextDiagram.Items[0].Token.Key == otherDiagramToken.Key && nextDiagram.Next == null,
            "Diagram catalogue remains bounded with tied order");
        long oldDiagramRevision = diagrams.Items[0].RevisionKey;

        var workbench = new WorkbenchState
        {
            WorkbenchId = "same-workbench-id ", Name = "selected workbench", IsDefaultForScope = true,
            CreatedAtUtc = EvidenceTime, UpdatedAtUtc = EvidenceTime.AddTicks(1), SavedAtUtc = EvidenceTime.AddTicks(2),
            ScopeId = f.Scope.ScopeId, ScopeName = "scope display ", IsCodeViewVisible = false, IsDiagramViewVisible = true,
            ActiveWorkspaceView = "raw view ", WorkspaceSplitOrientation = "raw split ", CodeViewMode = "raw mode ",
            PinnedExplorerDetailTab = "raw detail ", ReferenceConnectionLinesEnabled = true, CodeCanvasZoom = 0.625,
            CodeViewportHorizontalOffset = -10.25, CodeViewportVerticalOffset = 20.375,
            UnloadedResourceIds = ["same", "same", ""], OpenDocuments = [Window("same.csv"), Window("same.csv")],
            ActiveDocumentPath = "unresolved document ", ActiveDiagramId = originalDiagram.Document.DiagramId,
            ActiveDiagramName = "embedded-only title", ActiveDiagramSnapshot = originalDiagram.Document,
            IsDiagramLocked = false, DiagramCanvasZoom = 1.75, DiagramViewportHorizontalOffset = 13.125, DiagramViewportVerticalOffset = -9.25,
            ReferenceConnectionLines =
            [
                new() { ConnectionId = "duplicate-connection", SourceFilePath = "source ", SourceLineNumber = 7,
                    SourceStartColumnNumber = 2, SourceEndColumnNumber = 19, TargetFilePath = "target ", TargetLineNumber = 3,
                    TargetStartColumnNumber = 1, TargetEndColumnNumber = 17 },
                new() { ConnectionId = "duplicate-connection", SourceFilePath = "other", SourceLineNumber = -1,
                    SourceStartColumnNumber = 0, SourceEndColumnNumber = 0, TargetFilePath = "unresolved", TargetLineNumber = 0 }
            ]
        };
        var workbenchAggregate = new WorkbenchAggregate(workbench, originalDiagram.PastedImages);
        var workbenchToken = await store.CreateWorkbenchAsync(workbenchAggregate, 2, Guid.NewGuid());
        var otherWorkbenchToken = await store.CreateWorkbenchAsync(new(new WorkbenchState
        {
            WorkbenchId = workbench.WorkbenchId, Name = "other workbench", CreatedAtUtc = EvidenceTime,
            UpdatedAtUtc = EvidenceTime, SavedAtUtc = EvidenceTime, ActiveDiagramSnapshot = null
        }, new Dictionary<int, byte[]>()), 2, Guid.NewGuid());
        var workbenchRead = Required(await store.ReadWorkbenchAsync(workbenchToken.Key), "workbench");
        SameModel(workbench, workbenchRead.Value.Workbench, check, "Workbench preserves every field, connection lines, windows, filters and embedded diagram graph");
        SameImages(originalDiagram.PastedImages, workbenchRead.Value.PastedImages, check, "Workbench embedded copy preserves independent pasted-image bytes");
        var workbenches = await store.ListWorkbenchesAsync(pageSize: 1);
        var nextWorkbench = await store.ListWorkbenchesAsync(pageSize: 1, after: Required(workbenches.Next, "workbench cursor"));
        check(nextWorkbench.Items.Count == 1 && nextWorkbench.Items[0].Token.Key == otherWorkbenchToken.Key && nextWorkbench.Next == null &&
            workbenches.Items[0].EmbeddedDiagramRevisionKey != oldDiagramRevision && nextWorkbench.Items[0].EmbeddedDiagramRevisionKey == null,
            "Workbench catalogue uses independent embedded revisions and preserves absent copies");

        var changedDiagram = Diagram("edited catalogue diagram");
        changedDiagram.Document.Objects[0].LabelText = "new label";
        var newDiagramToken = await store.SaveDiagramAsync(changedDiagram, diagram.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.SaveDiagramAsync(originalDiagram, diagram.Token, Guid.NewGuid()), check,
            "Stale diagram save cannot overwrite a newer immutable revision");
        SameModel(originalDiagram.Document, (await store.ReadDiagramRevisionAsync(oldDiagramRevision)).Document, check, "Old diagram revision remains readable after current head changes");
        SameModel(workbench, (await store.ReadWorkbenchAsync(workbenchToken.Key))!.Value.Workbench, check, "Catalogue diagram edits do not mutate embedded workbench copy");
        await ThrowsAsync<StateConflictException>(() => store.ListDiagramsAsync(pageSize: 1, after: diagramCursor), check,
            "Diagram publication invalidates an in-flight catalogue cursor");
        var invalidDiagram = Diagram("must roll back");
        invalidDiagram.Document.Objects[0].ImageDataBase64 = "invalid-base64";
        await ThrowsAsync<InvalidDataException>(() => store.SaveDiagramAsync(invalidDiagram, newDiagramToken, Guid.NewGuid()), check,
            "Invalid inline image fails the whole diagram save");
        var afterInvalid = Required(await store.ReadDiagramAsync(diagramToken.Key), "diagram after failed save");
        check(afterInvalid.Token.Version.SequenceEqual(newDiagramToken.Version), "Image validation failure rolls back diagram head token");
        SameModel(changedDiagram.Document, afterInvalid.Value.Document, check, "Image validation failure omits no object and preserves the old graph");
        await ThrowsAsync<InvalidDataException>(() => store.CreateDiagramAsync(new(originalDiagram.Document, new Dictionary<int, byte[]>()), 1, Guid.NewGuid()), check,
            "Missing pasted-image bytes are rejected without consulting user files");
        var rowLimited = new RelationalStateStore(session, limits: new StateLimits { MaximumRows = 2 });
        await ThrowsAsync<InvalidDataException>(() => rowLimited.ReadDiagramAsync(diagramToken.Key), check,
            "Selected diagram read rejects row budget overflow instead of returning a truncated graph");
        var assetLimited = new RelationalStateStore(session, limits: new StateLimits { MaximumAssetBytes = 1 });
        await ThrowsAsync<InvalidDataException>(() => assetLimited.ReadDiagramAsync(diagramToken.Key), check,
            "Selected diagram read rejects asset budget overflow");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListWorkbenchesAsync(pageSize: 501), check,
            "State catalogue maximum page size is enforced");
        await ThrowsAsync<ArgumentException>(() => store.ListScopesAsync(pageSize: 1, after: scopeCursor with { Epoch = Guid.NewGuid() }), check,
            "State catalogue cursor is bound to its connection epoch");

        byte[] defensive = settings.Token.Version;
        defensive[0] ^= 0xff;
        check(!defensive.SequenceEqual(settings.Token.Version), "State token exposes a defensive rowversion copy");
        f.Settings.CodeWindows.DefaultBackcolor = "new color ";
        var savedSettings = await store.SaveSettingsAsync(f.Settings, settings.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.SaveSettingsAsync(f.Settings, settings.Token, Guid.NewGuid()), check,
            "Settings reject stale optimistic token");
        await ThrowsAsync<ArgumentException>(() => store.SaveSettingsAsync(f.Settings,
            new(savedSettings.Key, Guid.NewGuid(), savedSettings.Version, savedSettings.PublicationId), Guid.NewGuid()), check,
            "State save rejects foreign connection epoch");
        SameModel(f.Settings, (await store.ReadSettingsAsync())!.Value, check, "Targeted settings save preserves all unloaded child fields");
        f.Workspace.LastFolderPath = "raw-folder-that-is-not-opened ";
        var newWorkspace = await store.SaveWorkspaceAsync(f.Workspace, workspace.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.SaveWorkspaceAsync(f.Workspace, workspace.Token, Guid.NewGuid()), check,
            "Workspace rejects stale optimistic token");
        SameModel(f.Workspace, (await store.ReadWorkspaceAsync())!.Value, check, "Workspace save preserves font sizes, filter insertion order and duplicate windows");
        f.Scope.Name = "changed scope";
        var firstResource = f.Scope.Resources[0];
        f.Scope.Resources.RemoveAt(0);
        f.Scope.Resources.Add(firstResource);
        await store.SaveScopeAsync(f.Scope, scope.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.SaveScopeAsync(f.Scope, scope.Token, Guid.NewGuid()), check,
            "Scope rejects stale optimistic token");
        SameModel(f.Scope, (await store.ReadScopeAsync(scope.Token.Key))!.Value, check, "Scope save preserves duplicate child IDs while reordering resources");
        await ThrowsAsync<StateConflictException>(() => store.ListScopesAsync(pageSize: 1, after: scopeCursor), check,
            "Scope mutation invalidates catalogue generation");
        workbench.Name = "changed workbench";
        await store.SaveWorkbenchAsync(new(workbench, originalDiagram.PastedImages), workbenchRead.Token, Guid.NewGuid());
        await ThrowsAsync<StateConflictException>(() => store.SaveWorkbenchAsync(workbenchAggregate, workbenchRead.Token, Guid.NewGuid()), check,
            "Workbench rejects stale optimistic token");
        SameModel(workbench, (await store.ReadWorkbenchAsync(workbenchToken.Key))!.Value.Workbench, check, "Workbench save preserves its complete embedded copy");
        check((await store.ReadScopeAsync(f.OtherScopeToken.Key))!.Value.Name == "other scope" &&
            (await store.ReadDiagramAsync(otherDiagramToken.Key))!.Value.Document.Name == "other diagram" &&
            (await store.ReadWorkbenchAsync(otherWorkbenchToken.Key))!.Value.Workbench.Name == "other workbench" &&
            !newWorkspace.Version.SequenceEqual(workspace.Token.Version), "Targeted state mutations preserve unrelated aggregates");
    }

    private static OpenDocumentState Window(string path) => new()
    {
        FilePath = path, DisplayName = "raw title ", Left = -12.25, Top = 17.125, Width = 701.5, Height = 431.75,
        FontSize = 17.375, HorizontalOffset = 8.125, VerticalOffset = 9.625,
        SpreadsheetFilters = new() { [7] = "last-column-first ", [-1] = "", [2] = "\u00e9\r\nquoted \"value\"" }
    };

    private static DiagramState Diagram(string name)
    {
        byte[] inline = Png(17);
        var document = new DiagramDocument
        {
            DiagramId = "duplicate-diagram-id ", Name = name, CreatedAtUtc = EvidenceTime,
            UpdatedAtUtc = EvidenceTime.AddTicks(3), CanvasZoom = 1.625,
            ViewportHorizontalOffset = -13.5, ViewportVerticalOffset = 25.125
        };
        foreach (var type in Enum.GetValues<DiagramObjectType>())
        {
            int i = document.Objects.Count;
            document.Objects.Add(new()
            {
                Id = "duplicate-object-id", ObjectType = type, ZIndex = 7 - i, WorkflowId = "same-workflow", WorkflowItemId = "same-item",
                PortalName = "portal ", PairedPortalDiagramId = "unresolved-diagram ", PairedPortalObjectId = "unresolved-object ",
                ShapeKind = (DiagramShapeKind)(i % 2), ImageDefinitionId = "duplicate image", ImageName = "inline name ",
                ImageDataBase64 = Convert.ToBase64String(inline) + "\r\n", PastedImageFileName = i is 1 or 3 ? "same.png" : "",
                LabelText = "Label \u00e9 " + i, LabelFontSize = 21.75 + i, OutlineColorText = "raw outline", BackColorText = "raw fill",
                HasEndArrow = true, IsLineLoose = true, LineStartX = -1.25, LineStartY = 2.5, LineEndX = 3.75, LineEndY = -4.125,
                IsTethered = false, LabelAnchorX = 5.5, LabelAnchorY = -6.625, LabelBoxLeft = -7.75, LabelBoxTop = 8.875,
                LabelBoxWidth = 91.25, LabelBoxHeight = 19.5, Left = -15.25, Top = 24.75, Width = 47.125, Height = 53.625,
                Metadata = new() { Link = "fixture:unresolved-link ", DocumentationXaml = "<FlowDocument FontFamily=\"Calibri\" FontSize=\"18.5\"><Paragraph>raw\r\ntext</Paragraph></FlowDocument>",
                    Queries = [Query(2, QueryStatus.Irrelevant), Query(1, QueryStatus.Active), Query(2, QueryStatus.Resolved)] }
            });
        }
        for (int i = 0; i < 2; i++) document.Workflows.Add(new()
        {
            WorkflowId = "same-workflow", WorkflowName = "Workflow " + i, CreatedAtUtc = EvidenceTime.AddTicks(i),
            UpdatedAtUtc = EvidenceTime.AddTicks(i + 5), AreMarkersVisible = i == 0,
            Items =
            [
                new() { WorkflowItemId = "same-item", MarkerDiagramObjectId = "duplicate-object-id", ItemNumber = 9,
                    ItemDescription = "description\r\n", ItemDocumentationXaml = "<Paragraph FontSize=\"13.25\">item</Paragraph>", Queries = [Query(7, QueryStatus.Resolved), Query(3, QueryStatus.Active)] },
                new() { WorkflowItemId = "same-item", MarkerDiagramObjectId = "missing-marker", ItemNumber = -1, ItemDescription = "second item", Queries = [] }
            ]
        });
        return new(document, new Dictionary<int, byte[]> { [1] = Png(33), [3] = Png(77) });
    }

    private static QueryItem Query(int number, QueryStatus status) => new()
    {
        QueryId = "duplicate-query", QueryNumber = number, Status = status,
        CreatedDateUtc = EvidenceTime.AddTicks(number), QueryDescription = "description \u00e9\r\n" + status
    };

    private static void SameImages(IReadOnlyDictionary<int, byte[]> expected, IReadOnlyDictionary<int, byte[]> actual,
        Action<bool, string> check, string name) => check(expected.Count == actual.Count && expected.All(pair =>
            actual.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes)), name);

    // Generate valid, distinct one-pixel PNG fixtures in memory; no external image or user file is read.
    private static byte[] Png(byte red)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(new byte[] { 0, red, 127, 231, 255 });
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();

        void Chunk(string name, byte[] bytes)
        {
            byte[] type = Encoding.ASCII.GetBytes(name);
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)bytes.Length);
            png.Write(integer);
            png.Write(type);
            png.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (byte b in type.Concat(bytes))
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
            png.Write(integer);
        }
    }
}

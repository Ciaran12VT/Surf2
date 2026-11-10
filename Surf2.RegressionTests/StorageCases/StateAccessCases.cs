using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunStateAccessChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        var connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        {
            ApplicationName = "Surf2_Regression_StateAccess_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var session = new RelationalSession(connection);
        var store = new RelationalStateStore(session);
        var seeded = await SeedStateAsync(session, store, check);
        var selected = new SelectedStateAccess(session);
        var preferences = new StatePreferenceAccess(session);
        var refused = await preferences.LoadStartupPreferencesAsync();
        check(refused.Status == StateLoadStatus.Failed && refused.Value == null,
            "State access offers no writable startup fallback while the fixture is Migrating");

        // This callback is only for an isolated storage-unit fixture, not a production Ready publisher.
        await fixture.MarkTestFixtureReadyAsync();
        var graph = Diagram("state access current diagram");
        var diagram = await store.CreateDiagramAsync(graph, 3, Guid.NewGuid());
        var otherGraph = Diagram("state access sibling diagram");
        var otherDiagram = await store.CreateDiagramAsync(otherGraph, 3, Guid.NewGuid());
        var workbench = new WorkbenchAggregate(new WorkbenchState
        {
            WorkbenchId = "duplicate-access-workbench", Name = "selected saved copy", ScopeId = seeded.Scope.ScopeId,
            ScopeName = seeded.Scope.Name, CreatedAtUtc = EvidenceTime.AddTicks(10),
            UpdatedAtUtc = default, SavedAtUtc = default, ActiveDiagramId = graph.Document.DiagramId,
            ActiveDiagramName = "saved independent title", ActiveDiagramSnapshot = graph.Document,
            OpenDocuments = [Window("same fixture window"), Window("same fixture window")],
            UnloadedResourceIds = ["same", "same", ""], CodeCanvasZoom = 1.375,
            PinnedExplorerDetailTab = "raw pinned tab", ReferenceConnectionLines =
            [new() { ConnectionId = "fixture line", SourceFilePath = "source", TargetFilePath = "target",
                SourceLineNumber = 8, SourceStartColumnNumber = 2, SourceEndColumnNumber = 11, TargetLineNumber = 4 }]
        }, graph.PastedImages);
        var workbenchToken = await store.CreateWorkbenchAsync(workbench, 2, Guid.NewGuid());
        var otherWorkbench = new WorkbenchAggregate(new WorkbenchState
        {
            WorkbenchId = workbench.Workbench.WorkbenchId, Name = "sibling saved fallback timestamp",
            CreatedAtUtc = EvidenceTime.AddTicks(9), UpdatedAtUtc = default, SavedAtUtc = EvidenceTime.AddTicks(10),
            OpenDocuments = [Window("other fixture window")], ActiveDiagramSnapshot = null
        }, new Dictionary<int, byte[]>());
        var otherWorkbenchToken = await store.CreateWorkbenchAsync(otherWorkbench, 2, Guid.NewGuid());

        await VerifyStateAccessPreferencesAsync(fixture, connection, session, store, preferences, seeded, check);
        SameModel(graph.Document, Required(await store.ReadDiagramAsync(diagram.Key), "unchanged access diagram").Value.Document,
            check, "Scalar/style/image/appearance saves preserve the unselected diagram, fonts, workflows and queries");
        SameImages(graph.PastedImages, Required(await store.ReadDiagramAsync(diagram.Key), "unchanged pasted assets").Value.PastedImages,
            check, "Preference image replacement does not replace immutable pasted assets or original inline diagram bytes");
        SameModel(workbench.Workbench, Required(await store.ReadWorkbenchAsync(workbenchToken.Key), "unchanged access workbench").Value.Workbench,
            check, "Targeted preference saves preserve saved embedded diagrams, windows, fonts and connection lines");
        SameModel(seeded.Workspace, Required(await store.ReadWorkspaceAsync(), "unchanged access workspace").Value,
            check, "Targeted preference saves preserve workspace fonts, geometry and ordered filters");

        await VerifyStateAccessAggregatesAsync(connection, session, store, selected, preferences, seeded,
            graph, diagram, otherGraph, otherDiagram, workbench, workbenchToken, otherWorkbench, otherWorkbenchToken, check);
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static async Task VerifyStateAccessPreferencesAsync(SqlFixture fixture, string connection,
        RelationalSession session, RelationalStateStore store, StatePreferenceAccess access, StateFixture seeded,
        Action<bool, string> check)
    {
        var expected = Required(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(seeded.Settings, ModelJson), ModelJson),
            "detached expected preferences");
        var childIdentities = await StateAccessChildIdentitiesAsync(fixture);
        var scalar = await StateAccessObservedAsync(connection, () => access.LoadStartupPreferencesAsync(), commands =>
        {
            check(StateAccessReads(commands, "ApplicationPreference").Count == 1,
                "Scalar startup executes one preference-head projection");
            StateAccessNoReads(commands, check, "Scalar startup", "Asset", "TextContent", "DiagramImageDefinition",
                "ReferenceStyle", "ExtensionAppearance", "Diagram", "Workbench", "DocumentWindowState");
            check(commands.All(c => !c.Text.Contains("BASE64", StringComparison.Ordinal) && !c.Text.Contains("REGEX", StringComparison.Ordinal)),
                "Actual scalar startup SQL excludes base64 and image regex projections");
        }, check, "scalar startup");
        await using var scalarEdit = StateAccessEdit(scalar, check, "scalar startup");
        var runtime = Required(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(expected, ModelJson), ModelJson), "runtime preferences");
        scalarEdit.Snapshot().ApplyToRuntime(runtime);
        SameModel(expected, runtime, check, "Startup scalar/input projection preserves all raw values and unqueried child collections");
        check(scalarEdit.ExpectedToken.Epoch == session.Epoch && scalarEdit.ExpectedToken.Key == 1 && !scalarEdit.IsDirty,
            "SQL startup load supplies a clean expected profile token in the active epoch");

        var images = await StateAccessObservedAsync(connection, () => access.ListImagesAsync(1), commands =>
        {
            var rows = StateAccessReads(commands, "DiagramImageDefinition");
            check(rows.Count == 1 && rows[0].Number("@Take") == 2,
                "Image summary SQL uses a one-row page plus one seek sentinel");
            check(rows.All(c => !c.Text.Contains("BASE64", StringComparison.Ordinal) && !c.Text.Contains("REGEX", StringComparison.Ordinal)),
                "Image catalogue projection excludes original base64 and regex text");
            StateAccessNoReads(commands, check, "Image summaries", "Asset", "TextContent", "ReferenceStyle", "ExtensionAppearance");
        }, check, "image summary page");
        var imagePage = Required(images.Value, "image summary page");
        var secondImages = Required((await access.ListImagesAsync(1, Required(imagePage.Next, "image cursor"))).Value, "second image page");
        check(imagePage.Items.Count == 1 && secondImages.Items.Count == 1 && secondImages.Next == null &&
            imagePage.Items[0].Id == secondImages.Items[0].Id && imagePage.Items[0].HasAsset && !secondImages.Items[0].HasAsset,
            "Image summary pages preserve duplicate IDs, original order and empty definitions without graph hydration");
        long imageKey = imagePage.Items[0].Key, emptyKey = secondImages.Items[0].Key;
        var styles = await StateAccessObservedAsync(connection, () => access.ListStylesAsync(1), commands =>
        {
            var rows = StateAccessReads(commands, "ReferenceStyle");
            check(rows.Count == 1 && rows[0].Number("@Take") == 2, "Style catalogue SQL is a bounded child projection");
            StateAccessNoReads(commands, check, "Style summaries", "Asset", "TextContent", "DiagramImageDefinition", "ExtensionAppearance");
        }, check, "style summary page");
        var stylePage = Required(styles.Value, "style summary page");
        var secondStyles = Required((await access.ListStylesAsync(1, Required(stylePage.Next, "style cursor"))).Value, "second style page");
        check(stylePage.Items.Count == 1 && secondStyles.Items.Count == 1 && secondStyles.Next == null &&
            stylePage.Items[0].Language == secondStyles.Items[0].Language && stylePage.Items[0].Kind == secondStyles.Items[0].Kind,
            "Style pages retain duplicate identities and independently ordered siblings");
        long styleKey = stylePage.Items[0].Key;
        var appearances = await StateAccessObservedAsync(connection, () => access.ListExtensionAppearanceAsync(1), commands =>
        {
            var rows = StateAccessReads(commands, "ExtensionAppearance");
            check(rows.Count == 1 && rows[0].Number("@Take") == 2, "Extension catalogue SQL is a bounded child projection");
            StateAccessNoReads(commands, check, "Extension summaries", "Asset", "TextContent", "DiagramImageDefinition", "ReferenceStyle");
        }, check, "extension summary page");
        var appearancePage = Required(appearances.Value, "extension summary page");
        check(appearancePage.Items.Count == 1 && appearancePage.Next != null,
            "Extension summary page remains bounded with duplicate extensions");
        long appearanceKey = appearancePage.Items[0].Key;

        var staleStyle = await access.LoadStyleAsync(styleKey);
        await using var staleStyleEdit = StateAccessEdit(staleStyle, check, "pre-scalar style");
        var changed = scalarEdit.Snapshot() with
        {
            Theme = " raw new theme ", DefaultBackcolor = " untouched siblings color ", IgnoreCaseByDefault = false,
            Input = scalarEdit.Snapshot().Input with { Version = 41, EnableDiagramShiftMousePanning = true }
        };
        scalarEdit.Replace(changed);
        StateToken oldScalarToken = scalarEdit.ExpectedToken;
        var scalarSave = await StateAccessObservedAsync(connection, () => scalarEdit.SaveAsync(), commands =>
        {
            check(commands.Count(c => c.Text.StartsWith("UPDATE SURF.APPLICATIONPREFERENCE ", StringComparison.Ordinal)) == 1,
                "Scalar save updates only the preference head once");
            check(commands.All(c => !c.Text.StartsWith("DELETE ", StringComparison.Ordinal)), "Scalar save issues no child deletion SQL");
            StateAccessNoReads(commands, check, "Scalar save", "Asset", "TextContent", "DiagramImageDefinition", "ReferenceStyle", "ExtensionAppearance");
        }, check, "scalar targeted save");
        StateAccessSaved(scalarEdit, oldScalarToken, scalarSave, check, "Scalar save");
        changed.ApplyToRuntime(expected);
        SameModel(expected, Required(await store.ReadSettingsAsync(), "post-scalar settings").Value, check,
            "Scalar-only SQL save preserves every style, extension, image, raw regex/base64 and child order");
        var stalePage = await access.ListImagesAsync(1, imagePage.Next);
        check(stalePage.Status == StateLoadStatus.Failed && stalePage.Value == null && stalePage.Error is StateConflictException,
            "Scalar publication invalidates an old image catalogue cursor without returning a partial page");
        var staleValue = staleStyleEdit.Snapshot(); staleValue.Foreground = "must not overwrite"; staleStyleEdit.Replace(staleValue);
        await ThrowsAsync<StateConflictException>(() => staleStyleEdit.SaveAsync(), check,
            "A style loaded before a scalar save rejects the stale shared owner token");
        check(staleStyleEdit.Status == StateEditStatus.Conflict && staleStyleEdit.PendingPublicationId == null,
            "Definite SQL optimistic conflict requires reload rather than unknown-outcome retry");
        SameModel(expected, Required(await store.ReadSettingsAsync(), "settings after stale style").Value, check,
            "Conflicted child save rolls back and preserves all existing preference rows");

        var styleLoad = await StateAccessObservedAsync(connection, () => access.LoadStyleAsync(styleKey), commands =>
        {
            StateAccessChosenRead(commands, "ReferenceStyle", styleKey, check, "Selected style");
            StateAccessNoReads(commands, check, "Selected style", "Asset", "TextContent", "DiagramImageDefinition", "ExtensionAppearance");
        }, check, "lazy selected style");
        await using var styleEdit = StateAccessEdit(styleLoad, check, "lazy style");
        var detached = styleEdit.Snapshot(); detached.Foreground = "detached unsaved mutation";
        check(styleEdit.Snapshot().Foreground == expected.ReferenceHighlights.Styles[0].Foreground && !styleEdit.IsDirty,
            "SQL-loaded style snapshots are detached and cannot mutate the loaded owner silently");
        var styleValue = styleEdit.Snapshot(); styleValue.Foreground = " selected foreground "; styleValue.IsBold = false;
        styleEdit.Replace(styleValue); styleValue.Foreground = "mutated after capture";
        var oldStyle = styleEdit.ExpectedToken;
        var styleSave = await StateAccessObservedAsync(connection, () => styleEdit.SaveAsync(), commands =>
        {
            StateAccessChosenUpdate(commands, "ReferenceStyle", styleKey, check);
            StateAccessNoReads(commands, check, "Style save", "Asset", "TextContent", "DiagramImageDefinition", "ExtensionAppearance");
            check(commands.All(c => !c.Text.StartsWith("DELETE ", StringComparison.Ordinal)), "Style save deletes no siblings");
        }, check, "style targeted save");
        StateAccessSaved(styleEdit, oldStyle, styleSave, check, "Style save");
        expected.ReferenceHighlights.Styles[0].Foreground = " selected foreground "; expected.ReferenceHighlights.Styles[0].IsBold = false;
        SameModel(expected, Required(await store.ReadSettingsAsync(), "post-style settings").Value, check,
            "Selected style save preserves duplicate style sibling, scalar flags, images and extension appearances");

        long assetKey = Convert.ToInt64(await fixture.DestinationSqlAsync(
            "SELECT AssetKey FROM surf.DiagramImageDefinition WHERE DiagramImageDefinitionKey=@Key;",
            RelationalSession.Parameter("@Key", SqlDbType.BigInt, imageKey)));
        // A valid FK can still be semantically inconsistent. Damage only the owned empty sibling, then repair it.
        await fixture.DestinationSqlAsync("UPDATE surf.DiagramImageDefinition SET AssetKey=@Asset WHERE DiagramImageDefinitionKey=@Key;",
            RelationalSession.Parameter("@Asset", SqlDbType.BigInt, assetKey), RelationalSession.Parameter("@Key", SqlDbType.BigInt, emptyKey));
        try
        {
            var unrelated = await access.LoadStartupPreferencesAsync();
            await using var unrelatedEdit = StateAccessEdit(unrelated, check, "startup with inconsistent unselected image");
            var imageLoad = await StateAccessObservedAsync(connection, () => access.LoadImageAsync(imageKey), commands =>
            {
                StateAccessChosenRead(commands, "DiagramImageDefinition", imageKey, check, "Selected image");
                StateAccessChosenRead(commands, "Asset", assetKey, check, "Selected image asset");
                StateAccessNoReads(commands, check, "Selected image", "ReferenceStyle", "ExtensionAppearance", "DiagramObject", "Workbench");
            }, check, "lazy selected image with inconsistent sibling");
            await using var imageEdit = StateAccessEdit(imageLoad, check, "lazy selected image");
            SameModel(expected.DiagramImages.Images[0], imageEdit.Snapshot(), check,
                "Selected image loads exact raw regex/base64 without loading an inconsistent sibling or global AppSettings");
            var bytesLoad = await access.ReadImageAssetAsync(imageKey, imageEdit.ExpectedToken);
            var bytes = Required(bytesLoad.Value, "selected image bytes");
            var copied = bytes.CopyBytes(); copied[0] ^= 0xff;
            check(bytesLoad.IsReady && bytes.MediaType == "image/png" && bytes.DefinitionKey == imageKey &&
                bytes.CopyBytes().SequenceEqual(Png(17)) && bytes.ByteCount == Png(17).LongLength,
                "Lazy selected asset bytes are verified, exact and defensively detached");
            var beforeFailure = Required(await store.ReadSettingsTokenAsync(), "pre-image-failure token");
            var bad = await access.LoadImageAsync(emptyKey);
            check(bad.Status == StateLoadStatus.Failed && bad.Value == null && bad.Error is InvalidDataException,
                "An inconsistent selected image returns Failed with no model or save capability");
            check(StateAccessSameToken(beforeFailure, Required(await store.ReadSettingsTokenAsync(), "post-image-failure token")),
                "Failed lazy image load changes no preference owner publication or rowversion");
        }
        finally
        {
            await fixture.DestinationSqlAsync("UPDATE surf.DiagramImageDefinition SET AssetKey=NULL WHERE DiagramImageDefinitionKey=@Key;",
                RelationalSession.Parameter("@Key", SqlDbType.BigInt, emptyKey));
        }
        await using (var empty = StateAccessEdit(await access.LoadImageAsync(emptyKey), check, "repaired empty image"))
            check(empty.Snapshot().ImageDataBase64 == "" && (await access.ReadImageAssetAsync(emptyKey, empty.ExpectedToken)).Status == StateLoadStatus.Missing,
                "Failed image loads are not cached; repaired empty definition loads without inventing an asset");
        var tiny = new StatePreferenceAccess(session, new StateLimits { MaximumAssetBytes = 1 });
        await using (var tinyStartup = StateAccessEdit(await tiny.LoadStartupPreferencesAsync(), check, "tiny-asset scalar startup"))
            check((await tiny.ListImagesAsync(1)).IsReady && (await tiny.LoadImageAsync(imageKey)).Status == StateLoadStatus.Failed,
                "Asset budget applies to the selected image, not scalar startup or summary catalogue reads");

        await using var replacement = StateAccessEdit(await access.LoadImageAsync(imageKey), check, "image replacement");
        var previousImageOwner = replacement.ExpectedToken;
        var imageValue = replacement.Snapshot(); imageValue.Name = "selected replacement";
        imageValue.ImageDataBase64 = Convert.ToBase64String(Png(99)) + "\r\n";
        replacement.Replace(imageValue);
        var imageSave = await StateAccessObservedAsync(connection, () => replacement.SaveAsync(), commands =>
        {
            var updates = commands.Where(c => c.Text.StartsWith("UPDATE SURF.DIAGRAMIMAGEDEFINITION ", StringComparison.Ordinal)).ToArray();
            check(updates.Length == 2 && updates.All(c => c.Number("@Key") == imageKey),
                "Image save updates only the chosen definition and its asset pointer");
            StateAccessNoReads(commands, check, "Image save", "ReferenceStyle", "ExtensionAppearance", "DiagramObject", "Workbench");
            check(commands.All(c => !c.Text.StartsWith("DELETE ", StringComparison.Ordinal)), "Image save does not delete unqueried definitions");
        }, check, "image targeted save");
        StateAccessSaved(replacement, previousImageOwner, imageSave, check, "Image save");
        expected.DiagramImages.Images[0].Name = imageValue.Name; expected.DiagramImages.Images[0].ImageDataBase64 = imageValue.ImageDataBase64;
        var staleAsset = await access.ReadImageAssetAsync(imageKey, previousImageOwner);
        check(staleAsset.Status == StateLoadStatus.Failed && staleAsset.Value == null && staleAsset.Error is StateConflictException,
            "Lazy image bytes reject a stale expected profile token after publication");
        check(Required((await access.ReadImageAssetAsync(imageKey, replacement.ExpectedToken)).Value, "replacement image bytes")
            .CopyBytes().SequenceEqual(Png(99)), "Fresh expected image token reads the new exact PNG bytes");
        SameModel(expected, Required(await store.ReadSettingsAsync(), "post-image settings").Value, check,
            "Image-targeted save preserves original empty sibling, raw regex fields, styles, appearances and scalar input flags");

        await using var appearance = StateAccessEdit(await access.LoadExtensionAppearanceAsync(appearanceKey), check, "selected appearance");
        var appearanceValue = appearance.Snapshot(); appearanceValue.Backcolor = " selected extension color "; appearance.Replace(appearanceValue);
        await appearance.SaveAsync(); expected.CodeWindows.BackcolorsByExtension[0].Backcolor = appearanceValue.Backcolor;
        await VerifyStateAccessRecoveryAsync(connection, store, access, styleKey, expected, check);
        SameModel(expected, Required(await store.ReadSettingsAsync(), "final targeted preferences").Value, check,
            "Recovery and selected appearance saves preserve all other settings, duplicates and collection order");
        check(childIdentities == await StateAccessChildIdentitiesAsync(fixture),
            "Targeted saves retain every preference child surrogate identity and source ordinal without delete/reinsert");
        var foreign = new StatePreferenceAccess(new RelationalSession(connection));
        await ThrowsAsync<ArgumentException>(() => foreign.ReadImageAssetAsync(imageKey, replacement.ExpectedToken), check,
            "Lazy asset reads reject owner tokens from a different connection epoch");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using (var trace = new StateAccessSqlTrace(connection))
        {
            var cancelledLoad = await access.LoadStartupPreferencesAsync(cancelled.Token);
            check(cancelledLoad.Status == StateLoadStatus.Cancelled && cancelledLoad.Value == null && trace.Commands.Count == 0,
                "Pre-cancelled startup load returns no writable fallback and issues no SQL");
        }
        check((await access.LoadStyleAsync(long.MaxValue)).Status == StateLoadStatus.Missing &&
            (await access.LoadImageAsync(long.MaxValue)).Status == StateLoadStatus.Missing,
            "Missing selected preference keys stay Missing rather than default writable children");
    }

    private static async Task VerifyStateAccessRecoveryAsync(string connection, RelationalStateStore store,
        StatePreferenceAccess access, long styleKey, AppSettings expected, Action<bool, string> check)
    {
        await using var edit = StateAccessEdit(await access.LoadStyleAsync(styleKey), check, "cancellation recovery style");
        var value = edit.Snapshot(); value.Foreground = "cancelled capture"; edit.Replace(value);
        var original = edit.ExpectedToken;
        using var cancellation = new CancellationTokenSource();
        bool triggered = false, cancelled = false;
        using (var trace = new StateAccessSqlTrace(connection, command =>
        {
            if (!triggered && StateAccessSql(command.CommandText).StartsWith("UPDATE SURF.REFERENCESTYLE ", StringComparison.Ordinal))
            {
                triggered = true; cancellation.Cancel();
            }
        }))
        {
            try { await edit.SaveAsync(cancellation.Token); }
            catch (Exception error) when (cancellation.IsCancellationRequested && error is OperationCanceledException or SqlException) { cancelled = true; }
            check(triggered && cancelled && trace.Commands.Any(c => c.Text.StartsWith("UPDATE SURF.APPLICATIONPREFERENCE ", StringComparison.Ordinal)),
                "Fixture-scoped cancellation reaches the selected child write after its transactional owner bump");
        }
        check(edit.Status == StateEditStatus.OutcomeUnknown && edit.PendingPublicationId.HasValue && edit.IsDirty,
            "Cancelled SQL save retains its captured generation and pending publication until recovery");
        await ThrowsAsync<InvalidOperationException>(() => edit.SaveAsync(), check,
            "Unknown SQL outcome prevents a blind save retry");
        check(StateAccessSameToken(original, Required(await store.ReadSettingsTokenAsync(), "rolled-back preference owner")),
            "Cancelled child transaction rolls back the owner bump and preserves the old expected token");
        var recovered = await StateAccessObservedAsync(connection, () => edit.RecoverAsync(), commands =>
        {
            var probes = StateAccessReads(commands, "ApplicationPreference");
            check(probes.Count == 1 && probes[0].Text.Contains("VERSION, PUBLICATIONID", StringComparison.Ordinal),
                "Unknown-outcome recovery uses one narrow owner publication/rowversion probe");
            StateAccessNoReads(commands, check, "Recovery probe", "Asset", "TextContent", "ReferenceStyle", "DiagramImageDefinition", "ExtensionAppearance");
        }, check, "cancelled save recovery");
        check(recovered == StateRecoveryDisposition.NotCommitted && edit.Status == StateEditStatus.Dirty &&
            edit.PendingPublicationId == null && StateAccessSameToken(original, edit.ExpectedToken),
            "SQL recovery proves the cancelled save did not commit and retains the unsaved capture");
        SameModel(expected, Required(await store.ReadSettingsAsync(), "post-cancellation settings").Value, check,
            "Cancelled write and recovery preserve every sibling and the selected persisted style");
        var save = await edit.SaveAsync(); expected.ReferenceHighlights.Styles[0].Foreground = value.Foreground;
        StateAccessSaved(edit, original, save, check, "Recovered style save");
        check(await edit.RecoverAsync() == StateRecoveryDisposition.NoPendingSave &&
            (await edit.SaveAsync()).Disposition == StateSaveDisposition.NoChanges,
            "Acknowledged recovered save requires no probe/retry and coalesces an unchanged generation");

        // A validation failure happens before SQL, but still requires the same conservative recovery protocol.
        await using var invalid = StateAccessEdit(await access.LoadImageAsync(
            Required((await access.ListImagesAsync(1)).Value, "invalid image page").Items[0].Key), check, "invalid image recovery");
        var invalidValue = invalid.Snapshot(); invalidValue.ImageDataBase64 = "not base64"; invalid.Replace(invalidValue);
        await ThrowsAsync<InvalidDataException>(() => invalid.SaveAsync(), check, "Invalid captured image fails instead of replacing stored bytes");
        await using var winner = StateAccessEdit(await access.LoadStartupPreferencesAsync(), check, "recovery competing scalar");
        var winningValue = winner.Snapshot() with { Theme = "competing publication after failed image" };
        winner.Replace(winningValue); await winner.SaveAsync(); winningValue.ApplyToRuntime(expected);
        check(await invalid.RecoverAsync() == StateRecoveryDisposition.Conflict && invalid.Status == StateEditStatus.Conflict,
            "Recovery reports Conflict if a different publication changed the shared profile while outcome was pending");
    }

    private static async Task VerifyStateAccessAggregatesAsync(string connection, RelationalSession session,
        RelationalStateStore store, SelectedStateAccess access, StatePreferenceAccess preferences, StateFixture seeded,
        DiagramState graph, StateToken diagram, DiagramState otherGraph, StateToken otherDiagram,
        WorkbenchAggregate workbench, StateToken workbenchToken, WorkbenchAggregate otherWorkbench,
        StateToken otherWorkbenchToken, Action<bool, string> check)
    {
        var scopes = await StateAccessObservedAsync(connection, () => access.ListScopesAsync(1), commands =>
        {
            StateAccessCatalogue(commands, "Scope", check);
            StateAccessNoReads(commands, check, "Scope summaries", "ScopeResource", "VirtualFolder", "TextContent", "Asset");
        }, check, "scope summary catalogue");
        var scopeNext = await access.ListScopesAsync(1, Required(scopes.Next, "scope access cursor"));
        check(scopes.Items.Count == 1 && scopeNext.Items.Count == 1 && scopeNext.Next == null &&
            scopes.Items[0].Token.Key == seeded.ScopeToken.Key && scopeNext.Items[0].Token.Key == seeded.OtherScopeToken.Key,
            "Selected-access scope catalogue preserves duplicate IDs and tied source order");
        var diagrams = await StateAccessObservedAsync(connection, () => access.ListDiagramsAsync(1), commands =>
        {
            StateAccessCatalogue(commands, "Diagram", check);
            StateAccessNoReads(commands, check, "Diagram summaries", "DiagramObject", "Workflow", "QueryItem", "Asset", "TextContent");
        }, check, "diagram summary catalogue");
        var diagramNext = await access.ListDiagramsAsync(1, Required(diagrams.Next, "diagram access cursor"));
        check(diagrams.Items.Count == 1 && diagramNext.Items.Count == 1 && diagramNext.Next == null &&
            diagrams.Items[0].Token.Key == diagram.Key && diagramNext.Items[0].Token.Key == otherDiagram.Key,
            "Selected-access diagram catalogue keeps duplicate IDs without hydrating revisions");
        var workbenches = await StateAccessObservedAsync(connection, () => access.ListWorkbenchesAsync(1), commands =>
        {
            StateAccessCatalogue(commands, "Workbench", check);
            StateAccessNoReads(commands, check, "Workbench summaries", "DiagramRevision", "DiagramObject", "DocumentWindowState", "Asset", "TextContent");
        }, check, "workbench summary catalogue");
        var workbenchNext = await access.ListWorkbenchesAsync(1, Required(workbenches.Next, "workbench access cursor"));
        check(workbenches.Items.Count == 1 && workbenchNext.Items.Count == 1 && workbenchNext.Next == null &&
            workbenches.Items[0].Token.Key == workbenchToken.Key && workbenchNext.Items[0].Token.Key == otherWorkbenchToken.Key &&
            workbenches.Items[0].EmbeddedDiagramRevisionKey != diagrams.Items[0].RevisionKey,
            "Workbench summaries identify the independent saved embedded revision without loading its graph");
        var recent = await StateAccessObservedAsync(connection, () => preferences.ReadMostRecentWorkbenchSummaryAsync(), commands =>
        {
            var rows = StateAccessReads(commands, "Workbench");
            check(rows.Count == 1 && rows[0].Text.Contains("TOP(1)", StringComparison.Ordinal),
                "Most-recent startup choice executes one TOP(1) summary query");
            StateAccessNoReads(commands, check, "Most-recent workbench summary", "DiagramRevision", "DiagramObject", "DocumentWindowState", "Asset", "TextContent");
        }, check, "most-recent workbench summary");
        check(recent.IsReady && recent.Value?.Token.Key == workbenchToken.Key,
            "Most-recent SQL summary honors Created/Saved fallback timestamps and source-order ties");

        var scopeLoad = await StateAccessObservedAsync(connection, () => access.LoadScopeAsync(scopes.Items[0]), commands =>
        {
            StateAccessChosenRead(commands, "Scope", seeded.ScopeToken.Key, check, "Selected scope");
            StateAccessNoReads(commands, check, "Selected scope", "Diagram", "Workbench", "Asset", "ApplicationPreference");
        }, check, "selected scope aggregate");
        await using var scopeEdit = StateAccessEdit(scopeLoad, check, "selected scope");
        SameModel(seeded.Scope, scopeEdit.Snapshot(), check, "Selected scope adapter preserves complete resources, raw paths and duplicate virtual-folder members");
        var scopeValue = scopeEdit.Snapshot(); scopeValue.Name = "only selected scope name"; scopeEdit.Replace(scopeValue);
        var oldScope = scopeEdit.ExpectedToken; StateAccessSaved(scopeEdit, oldScope, await scopeEdit.SaveAsync(), check, "Selected scope save");
        SameModel(scopeValue, Required(await store.ReadScopeAsync(oldScope.Key), "updated selected scope").Value, check,
            "Selected scope save preserves all uncaptured siblings within that full selected aggregate");
        check(Required(await store.ReadScopeAsync(seeded.OtherScopeToken.Key), "scope sibling").Value.Name == "other scope",
            "Selected scope save leaves a separate duplicate-ID owner untouched");
        var staleScope = await access.LoadScopeAsync(scopes.Items[0]);
        check(staleScope.Status == StateLoadStatus.Failed && staleScope.Value == null && staleScope.Error is StateConflictException,
            "A stale catalogue summary cannot become a writable selected aggregate");
        await ThrowsAsync<StateConflictException>(() => access.ListScopesAsync(1, scopes.Next), check,
            "Scope publication invalidates a selected-access catalogue cursor");

        var diagramLoad = await StateAccessObservedAsync(connection, () => access.LoadDiagramAsync(diagrams.Items[0]), commands =>
        {
            StateAccessChosenRead(commands, "Diagram", diagram.Key, check, "Selected diagram");
            var revisions = StateAccessReads(commands, "DiagramRevision");
            check(revisions.Count == 1 && revisions[0].Number("@Key") == diagrams.Items[0].RevisionKey,
                "Selected diagram hydrates only its explicitly selected immutable revision");
            StateAccessNoReads(commands, check, "Selected diagram", "Workbench", "WorkspaceSession", "ApplicationPreference", "DiagramImageDefinition");
        }, check, "selected diagram aggregate");
        await using var diagramEdit = StateAccessEdit(diagramLoad, check, "selected diagram");
        SameModel(graph.Document, diagramEdit.Snapshot().Document, check,
            "Selected diagram access preserves subtype fields, font sizes, exact XAML, workflows and queries");
        SameImages(graph.PastedImages, diagramEdit.Snapshot().PastedImages, check, "Selected diagram access preserves ordinal-bound pasted bytes");
        var diagramValue = diagramEdit.Snapshot(); diagramValue.Document.Name = "edited current, not saved copy";
        diagramValue.Document.Objects[0].LabelText = "selected label edit"; diagramEdit.Replace(diagramValue);
        var oldDiagram = diagramEdit.ExpectedToken;
        StateAccessSaved(diagramEdit, oldDiagram, await diagramEdit.SaveAsync(), check, "Selected diagram save");
        SameModel(diagramValue.Document, Required(await store.ReadDiagramAsync(diagram.Key), "updated diagram").Value.Document,
            check, "Selected diagram save retains all fonts, sibling objects, workflows and queries");
        SameImages(graph.PastedImages, Required(await store.ReadDiagramAsync(diagram.Key), "updated diagram pasted bytes").Value.PastedImages,
            check, "Selected diagram save retains pasted assets independent of preference image edits");
        SameModel(otherGraph.Document, Required(await store.ReadDiagramAsync(otherDiagram.Key), "diagram sibling").Value.Document,
            check, "Selected diagram edit does not overwrite a duplicate-ID catalogue sibling");

        var workbenchLoad = await StateAccessObservedAsync(connection, () => access.LoadWorkbenchAsync(workbenches.Items[0]), commands =>
        {
            var roots = StateAccessReads(commands, "Workbench");
            check(roots.Count == 2 && roots.All(c => c.Number("@Key") == workbenchToken.Key),
                "Selected workbench reads only its chosen head and embedded-revision pointer");
            var revisions = StateAccessReads(commands, "DiagramRevision");
            check(revisions.Count == 1 && revisions[0].Number("@Key") == workbenches.Items[0].EmbeddedDiagramRevisionKey,
                "Saved workbench loads its own immutable revision, not the edited current diagram");
            StateAccessNoReads(commands, check, "Selected workbench", "Diagram", "WorkspaceSession", "ApplicationPreference", "DiagramImageDefinition");
        }, check, "selected saved workbench aggregate");
        await using var workbenchEdit = StateAccessEdit(workbenchLoad, check, "selected workbench");
        SameModel(workbench.Workbench, workbenchEdit.Snapshot().Workbench, check,
            "Saved workbench adapter retains exact independent graph, window fonts, filters, geometry and lines");
        SameImages(workbench.PastedImages, workbenchEdit.Snapshot().PastedImages, check, "Saved embedded graph retains pasted assets by ordinal");
        var workbenchValue = workbenchEdit.Snapshot(); workbenchValue.Workbench.Name = "selected workbench new name";
        workbenchEdit.Replace(workbenchValue); var oldWorkbench = workbenchEdit.ExpectedToken;
        StateAccessSaved(workbenchEdit, oldWorkbench, await workbenchEdit.SaveAsync(), check, "Selected workbench save");
        SameModel(workbenchValue.Workbench, Required(await store.ReadWorkbenchAsync(workbenchToken.Key), "updated workbench").Value.Workbench,
            check, "Selected workbench save preserves embedded-copy fields, fonts and ordered query/layout children");
        SameModel(otherWorkbench.Workbench, Required(await store.ReadWorkbenchAsync(otherWorkbenchToken.Key), "workbench sibling").Value.Workbench,
            check, "Selected workbench save preserves a separate duplicate-ID owner and its windows");

        var workspaceLoad = await StateAccessObservedAsync(connection, () => access.LoadWorkspaceAsync(), commands =>
            StateAccessNoReads(commands, check, "Selected workspace", "Workbench", "Diagram", "DiagramRevision", "Asset", "TextContent", "ApplicationPreference"),
            check, "selected workspace descriptors");
        await using var workspace = StateAccessEdit(workspaceLoad, check, "workspace descriptors");
        var workspaceValue = workspace.Snapshot(); workspaceValue.CanvasZoom = 2.625; workspace.Replace(workspaceValue);
        await workspace.SaveAsync();
        SameModel(workspaceValue, Required(await store.ReadWorkspaceAsync(), "updated workspace descriptors").Value, check,
            "Workspace descriptor save preserves exact font sizes, duplicate windows, geometry, ordered filters and unloaded IDs");
        await using var selection = StateAccessEdit(await access.LoadScopeSelectionAsync(), check, "scope selection access");
        check(selection.Snapshot() == new ScopeSelection(37, "unresolved-last-scope "), "Scope-selection adapter preserves raw schema and unresolved selection");
        selection.Replace(new ScopeSelection(38, null)); await selection.SaveAsync();
        check(Required(await store.ReadScopeSelectionAsync(), "saved scope selection").Value == new ScopeSelection(38, null),
            "Scope-selection adapter saves only the selection head and retains null without normalizing");
        var foreign = new SelectedStateAccess(new RelationalSession(connection));
        await ThrowsAsync<ArgumentException>(() => foreign.LoadDiagramAsync(diagrams.Items[0]), check,
            "Selected summary loads reject a different session epoch before hydrating any graph");
        check((await access.LoadScopeAsync(long.MaxValue)).Status == StateLoadStatus.Missing,
            "Missing selected aggregate is not an empty writable scope model");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledLoad = await access.LoadWorkbenchAsync(workbenchToken.Key, cancelled.Token);
        check(cancelledLoad.Status == StateLoadStatus.Cancelled && cancelledLoad.Value == null,
            "Cancelled selected graph load returns no partial graph or writable edit session");
        check(scopeEdit.Epoch == session.Epoch && diagramEdit.Epoch == session.Epoch && workbenchEdit.Epoch == session.Epoch,
            "All selected SQL edit sessions remain bound to the fixture connection epoch");
    }

    private static StateEditSession<T> StateAccessEdit<T>(StateLoad<StateEditSession<T>> load,
        Action<bool, string> check, string name) where T : class
    {
        check(load.IsReady && load.Value != null && load.Error == null, "State access Ready edit session: " + name);
        return Required(load.Value, name + " edit session");
    }

    private static void StateAccessSaved<T>(StateEditSession<T> edit, StateToken before, StateSaveResult result,
        Action<bool, string> check, string name) where T : class =>
        check(result.Disposition == StateSaveDisposition.Saved && !result.HasNewerChanges && result.SavedGeneration == edit.Generation &&
            edit.Status == StateEditStatus.Clean && !edit.IsDirty && edit.PendingPublicationId == null &&
            result.Token.Key == before.Key && result.Token.Epoch == before.Epoch && result.Token.PublicationId != before.PublicationId &&
            !result.Token.Version.SequenceEqual(before.Version) && StateAccessSameToken(edit.ExpectedToken, result.Token),
            name + " publishes a new owner token and marks only its acknowledged capture clean");

    private static bool StateAccessSameToken(StateToken left, StateToken right) => left.Key == right.Key && left.Epoch == right.Epoch &&
        left.PublicationId == right.PublicationId && left.Version.SequenceEqual(right.Version);

    private static async Task<string> StateAccessChildIdentitiesAsync(SqlFixture fixture) =>
        (string)Required(await fixture.DestinationSqlAsync("""
SELECT (SELECT DiagramImageDefinitionKey,SortOrdinal FROM surf.DiagramImageDefinition ORDER BY SortOrdinal,DiagramImageDefinitionKey FOR JSON PATH) +
       (SELECT ReferenceStyleKey,SortOrdinal FROM surf.ReferenceStyle ORDER BY SortOrdinal,ReferenceStyleKey FOR JSON PATH) +
       (SELECT ExtensionAppearanceKey,SortOrdinal FROM surf.ExtensionAppearance ORDER BY SortOrdinal,ExtensionAppearanceKey FOR JSON PATH);
"""), "preference child identities");

    private static async Task<T> StateAccessObservedAsync<T>(string connection, Func<Task<T>> operation,
        Action<IReadOnlyList<StateAccessCommand>> verify, Action<bool, string> check, string name)
    {
        using var trace = new StateAccessSqlTrace(connection);
        var result = await operation();
        var commands = trace.Commands;
        check(commands.Count > 0, "Actual fixture-only SqlClient diagnostics observed: " + name);
        verify(commands);
        check(true, "State SQL evidence " + name + ": " + commands.Count + " commands observed including readiness/identity probes");
        return result;
    }

    private static List<StateAccessCommand> StateAccessReads(IReadOnlyList<StateAccessCommand> commands, string table) =>
        commands.Where(c => Regex.IsMatch(c.Text, @"\b(?:FROM|JOIN)\s+SURF\." + Regex.Escape(table.ToUpperInvariant()) + @"\b",
            RegexOptions.CultureInvariant)).ToList();

    private static void StateAccessNoReads(IReadOnlyList<StateAccessCommand> commands, Action<bool, string> check,
        string name, params string[] tables) => check(tables.All(t => StateAccessReads(commands, t).Count == 0),
        name + " issues no SQL reads of " + string.Join(", ", tables));

    private static void StateAccessCatalogue(IReadOnlyList<StateAccessCommand> commands, string table, Action<bool, string> check)
    {
        var rows = StateAccessReads(commands, table);
        check(rows.Count == 1 && rows[0].Number("@Take") == 2,
            table + " catalogue issues one bounded head projection with one seek sentinel");
    }

    private static void StateAccessChosenRead(IReadOnlyList<StateAccessCommand> commands, string table, long key,
        Action<bool, string> check, string name)
    {
        var rows = StateAccessReads(commands, table);
        check(rows.Count == 1 && rows[0].Number("@Key") == key && rows[0].Text.Contains("=@KEY", StringComparison.Ordinal),
            name + " executes only one chosen-key SQL projection");
    }

    private static void StateAccessChosenUpdate(IReadOnlyList<StateAccessCommand> commands, string table, long key, Action<bool, string> check)
    {
        var rows = commands.Where(c => c.Text.StartsWith("UPDATE SURF." + table.ToUpperInvariant() + " ", StringComparison.Ordinal)).ToArray();
        check(rows.Length == 1 && rows[0].Number("@Key") == key && rows[0].Text.Contains("=@KEY", StringComparison.Ordinal),
            table + " targeted save executes one chosen-key update");
    }

    private static string StateAccessSql(string sql) => Regex.Replace(sql.Replace("[", "").Replace("]", ""), @"\s+", " ")
        .Trim().ToUpperInvariant();

    private sealed record StateAccessCommand(string Text, IReadOnlyDictionary<string, long> Numbers)
    {
        public long? Number(string name) => Numbers.TryGetValue(name, out long value) ? value : null;
    }

    // Observe only this generated database/application pair. No user SQL, parameter text, credentials or paths are retained.
    private sealed class StateAccessSqlTrace : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database, _application;
        private readonly Action<SqlCommand>? _before;
        private readonly object _gate = new();
        private readonly List<IDisposable> _subscriptions = [];
        private readonly List<StateAccessCommand> _commands = [];
        private readonly IDisposable _listeners;
        private bool _disposed;
        public IReadOnlyList<StateAccessCommand> Commands { get { lock (_gate) return _commands.ToArray(); } }
        public StateAccessSqlTrace(string connection, Action<SqlCommand>? before = null)
        {
            var options = new SqlConnectionStringBuilder(connection);
            _database = options.InitialCatalog; _application = options.ApplicationName; _before = before;
            if (!_database.StartsWith("Surf2_Regression_", StringComparison.Ordinal) ||
                !_application.StartsWith("Surf2_Regression_StateAccess_", StringComparison.Ordinal))
                throw new InvalidOperationException("State SQL diagnostics require an owned regression fixture.");
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != "SqlClientDiagnosticListener") return;
            lock (_gate)
                if (!_disposed) _subscriptions.Add(listener.Subscribe(this, name => name == SqlClientCommandBefore.Name));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not SqlClientCommandBefore before || before.Command.Connection is not { } connection ||
                connection.Database != _database || new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationName != _application) return;
            var numbers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (SqlParameter parameter in before.Command.Parameters)
                if (parameter.Value is long or int or short) numbers[parameter.ParameterName] = Convert.ToInt64(parameter.Value);
            lock (_gate)
            {
                if (_disposed) return;
                _commands.Add(new(StateAccessSql(before.Command.CommandText), numbers));
            }
            _before?.Invoke(before.Command);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            _listeners.Dispose();
            lock (_gate)
            {
                _disposed = true;
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();
            }
        }
    }
}

using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Packages;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunDurableLinkChecksAsync(Action<bool, string> check)
    {
        await using var fixture = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(fixture.SourceConnectionString,
            fixture.DestinationConnectionString, fixture.MigrationIdentity, fixture.Fingerprint);
        string connection = new SqlConnectionStringBuilder(fixture.DestinationConnectionString)
        { ApplicationName = "Surf2_Regression_StateAccess_Durable_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        var seedSession = new RelationalSession(connection);
        var content = new RelationalContentStore();
        var seeded = await SeedQueryIntegrationAsync(fixture, seedSession, content,
            new RelationalCaptureStore(seedSession), new RelationalStateStore(seedSession, content));
        var first = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(connection));
        await first.Session.RequireReadyAsync();
        await using var documents = new RelationalDocumentService(first);
        var snapshot = Required(await first.Snapshots.GetSnapshotAsync(seeded.SnapshotKey), "durable snapshot");
        var resources = new[]
        {
            Required(await first.Snapshots.ResolveResourceAsync(seeded.Procedure.ResourceKey), "durable procedure"),
            Required(await first.Snapshots.ResolveResourceAsync(seeded.Table.ResourceKey), "durable metadata"),
            Required(await first.Snapshots.ResolveResourceAsync(seeded.DataResourceKey), "durable data")
        };
        var addresses = new List<RelationalDocumentAddress>();
        foreach (var resource in resources)
        {
            var address = await documents.ResolveResourceAsync(snapshot.SnapshotKey, resource.ResourceKey, null);
            string expected = DurableDocumentLink.Create(snapshot.PublicId, resource.PublicId, resource.Kind!.Value);
            check(address.PersistentDocumentPath == expected && address.DocumentPath.StartsWith("db://@", StringComparison.Ordinal),
                "A selected " + resource.Kind + " exposes a durable link separately from its live window identity");
            check(await first.StateStore.NormalizeDocumentLinkAsync(address.DocumentPath) == expected,
                "Saving a live " + resource.Kind + " address replaces the runtime epoch with persisted GUID identities");
            addresses.Add(address);
        }
        string procedure = addresses[0].PersistentDocumentPath!, table = addresses[1].PersistentDocumentPath!, data = addresses[2].PersistentDocumentPath!;
        const string suffix = ":17:[\"Account Id\"=\"contains: [a], \\\"quoted\\\"\",\"Value\"=\"α\\nβ\"]";
        string decorated = addresses[2].DocumentPath + suffix;
        check(await first.StateStore.NormalizeDocumentLinkAsync(decorated) == data + suffix,
            "Persistent-link normalization preserves line number and escaped table-filter suffix byte for byte");
        var search = new CodeSearchReference(addresses[0].DocumentPath, "Needle.*[a-z]", MatchCase: true, WholeWords: true, UseRegex: true);
        string normalizedSearch = await first.StateStore.NormalizeDocumentLinkAsync(CodeSearchReferenceService.Encode(search));
        check(CodeSearchReferenceService.TryDecode(normalizedSearch, out var decoded) && decoded == (search with { DocumentPath = procedure }),
            "Encoded search links preserve all search options while replacing only the document address");
        const string physical = @"C:\fixture\local.cs:17";
        check(await first.StateStore.NormalizeDocumentLinkAsync(physical) == physical &&
            await first.StateStore.NormalizeDocumentLinkAsync("https://example.invalid/docs?q=db://@literal") == "https://example.invalid/docs?q=db://@literal",
            "Physical paths and ordinary web links pass through unchanged");

        var newDiagram = new DiagramState(LinkDiagram("durable-new", new Dictionary<string, string>
        {
            ["procedure"] = addresses[0].DocumentPath,
            ["filtered"] = decorated,
            ["search"] = CodeSearchReferenceService.Encode(search)
        }), new Dictionary<int, byte[]>());
        var newToken = await first.StateStore.CreateDiagramAsync(newDiagram, 2, Guid.NewGuid());
        var stored = Required(await first.StateStore.ReadDiagramAsync(newToken.Key), "new durable diagram");
        check(stored.Value.Document.Objects[0].Metadata.Link == procedure && stored.Value.Document.Objects[1].Metadata.Link == data + suffix &&
            stored.Value.Document.Objects[2].Metadata.Link == normalizedSearch,
            "The SQL diagram write boundary prevents runtime links from escaping through any saved object metadata");
        check(newDiagram.Document.Objects[0].Metadata.Link == addresses[0].DocumentPath,
            "Saving normalizes a detached copy without changing the live diagram model");

        var restarted = new RelationalRuntime(first.Options);
        await restarted.Session.RequireReadyAsync();
        await using var reopened = new RelationalDocumentService(restarted);
        check(restarted.Session.Epoch != first.Session.Epoch, "The restart fixture uses an independent runtime epoch");
        for (int i = 0; i < resources.Length; i++)
        {
            var selected = await reopened.ResolveAsync(addresses[i].PersistentDocumentPath!, null);
            check(selected.Resource is { } selectedResource && selectedResource.ResourceKey == resources[i].ResourceKey && selectedResource.SnapshotKey == snapshot.SnapshotKey &&
                selected.DocumentPath.Contains(restarted.Session.Epoch.ToString("N"), StringComparison.Ordinal),
                "A saved " + resources[i].Kind + " link reopens the same resource in a new runtime");
        }
        var reopenedProcedure = await reopened.ResolveAsync(procedure, null);
        check((await reopened.ReadTextAsync(await reopened.DescribeTextAsync(reopenedProcedure))).Text == seeded.ProcedureValue.Definition,
            "Reopening the durable procedure reads its exact saved SQL definition");
        await ExpectDurableRejectionAsync(() => reopened.ResolveAsync(addresses[0].DocumentPath, null), check,
            "An arbitrary expired session URL remains rejected by ordinary link opening");
        await ExpectDurableRejectionAsync(() => restarted.StateStore.CreateDiagramAsync(newDiagram, 3, Guid.NewGuid()), check,
            "New diagram writes reject foreign-session addresses instead of silently binding their integer keys");
        await ExpectDurableRejectionAsync(() => reopened.ResolveAsync(
            DurableDocumentLink.Create(snapshot.PublicId, resources[0].PublicId, DatabaseVersionedResourceKind.TableMetadata), null), check,
            "A durable link with a mismatched object kind cannot reinterpret the same resource GUID");
        var loadedScope = await restarted.Explorer.OpenScopeAsync(seeded.ScopeToken.Key, sortCultureName: "en-US");
        restarted.SelectExplorerScope(loadedScope);
        await restarted.PreloadSnapshotMemoryAsync([snapshot.SnapshotKey]);
        using (var trace = new StateAccessSqlTrace(connection))
        {
            for (int i = 0; i < resources.Length; i++)
            {
                var loaded = await reopened.ResolveAsync(addresses[i].PersistentDocumentPath!, loadedScope);
                var comparison = await reopened.ResolveComparisonResourceAsync(addresses[i].PersistentDocumentPath!, loadedScope);
                check(loaded.Resource?.ResourceKey == resources[i].ResourceKey && comparison.ResourceKey == resources[i].ResourceKey,
                    "A durable " + resources[i].Kind + " link resolves from the loaded browsing and comparison catalogues");
            }
            check(trace.Commands.Count == 0, "Resolving loaded durable links and comparison targets performs no SQL reads");
        }

        await VerifyStoredLinkRepairAsync(fixture, first, restarted, seeded, addresses, procedure, table, data, suffix, check);

        string packagePath = Path.Combine(fixture.OwnedDirectory, "durable-link-copy.surf2.zip");
        var exported = await new RelationalPackageExporter(restarted.Session).ExportAsync(packagePath, new(AllowBlockingConsistentFallback: true));
        await using (var restored = await SqlFixture.CreateAsync())
        {
            var imported = await new RelationalPackageImporter(restored.DestinationConnectionString).ImportAsync(packagePath, PackageImportOptions(exported.ManifestSha256));
            var restoredRuntime = new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(restored.DestinationConnectionString));
            await using var restoredDocuments = new RelationalDocumentService(restoredRuntime);
            var restoredAddress = await restoredDocuments.ResolveAsync(procedure, null);
            check(imported.DatabaseIdentity != first.Session.DatabaseIdentity && restoredAddress.PersistentDocumentPath == procedure &&
                (await restoredDocuments.ReadTextAsync(await restoredDocuments.DescribeTextAsync(restoredAddress))).Text == seeded.ProcedureValue.Definition,
                "A package restored under a new database identity retains working durable object links");
        }

        await using var unrelated = await SqlFixture.CreateAsync();
        await new RelationalSchemaInstaller().InitializeDestinationAsync(unrelated.SourceConnectionString,
            unrelated.DestinationConnectionString, unrelated.MigrationIdentity, unrelated.Fingerprint);
        var unrelatedSession = new RelationalSession(unrelated.DestinationConnectionString);
        var otherSeed = await SeedQueryIntegrationAsync(unrelated, unrelatedSession, new RelationalContentStore(),
            new RelationalCaptureStore(unrelatedSession), new RelationalStateStore(unrelatedSession));
        check(otherSeed.SnapshotKey == seeded.SnapshotKey && otherSeed.Procedure.ResourceKey == seeded.Procedure.ResourceKey,
            "The unrelated database deliberately overlaps the original numeric resource keys");
        await using var otherDocuments = new RelationalDocumentService(new RelationalRuntime(SqlServerConnectionOptions.FromConnectionString(unrelated.DestinationConnectionString)));
        await ExpectDurableRejectionAsync(() => otherDocuments.ResolveAsync(procedure, null), check,
            "Durable links cannot resolve an unrelated database's object merely because its numeric keys overlap");
        await fixture.VerifySourceUnchangedAsync(check);
        await unrelated.VerifySourceUnchangedAsync(check);
    }

    private static async Task VerifyStoredLinkRepairAsync(SqlFixture fixture, RelationalRuntime first, RelationalRuntime restarted,
        QueryIntegrationFixture seeded, IReadOnlyList<RelationalDocumentAddress> addresses, string procedure, string table, string data,
        string suffix, Action<bool, string> check)
    {
        Guid expiredEpoch = Guid.NewGuid();
        string expired = addresses[0].DocumentPath.Replace(first.Session.Epoch.ToString("N"), expiredEpoch.ToString("N"), StringComparison.Ordinal);
        string expiredTable = addresses[1].DocumentPath.Replace(first.Session.Epoch.ToString("N"), expiredEpoch.ToString("N"), StringComparison.Ordinal);
        string expiredData = addresses[2].DocumentPath.Replace(first.Session.Epoch.ToString("N"), expiredEpoch.ToString("N"), StringComparison.Ordinal);
        string conflicting = expired.Replace(expiredEpoch.ToString("N"), Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        string missing = $"db://@{expiredEpoch:N}:{seeded.SnapshotKey}/object/StoredProcedure/9223372036854775807.sql";
        string malformed = "db://@not-an-epoch:1/object/StoredProcedure/1.sql";
        string wrongKind = $"db://@{expiredEpoch:N}:{seeded.SnapshotKey}/table/{seeded.Procedure.ResourceKey}.sql";
        string wrongOwner = $"db://@{expiredEpoch:N}:{seeded.SnapshotKey + 100}/object/StoredProcedure/{seeded.Procedure.ResourceKey}.sql";
        var originalLinks = new Dictionary<string, string>
        {
            ["repair-plain"] = expired, ["repair-line"] = expired + ":29", ["repair-filter"] = expiredData + suffix,
            ["repair-whitespace"] = " \t" + expired + ":5 \r\n",
            ["repair-search"] = CodeSearchReferenceService.Encode(new(expired, "find me", UseRegex: true)),
            ["repair-missing"] = missing, ["repair-malformed"] = malformed,
            ["repair-kind"] = wrongKind, ["repair-owner"] = wrongOwner
        };
        var expected = new Dictionary<string, string>(originalLinks)
        {
            ["repair-plain"] = procedure, ["repair-line"] = procedure + ":29", ["repair-filter"] = data + suffix,
            ["repair-whitespace"] = " \t" + procedure + ":5 \r\n",
            ["repair-search"] = CodeSearchReferenceService.Encode(new(procedure, "find me", UseRegex: true))
        };
        var placeholders = originalLinks.Keys.ToDictionary(key => key, _ => "https://example.invalid/placeholder");
        var diagram = new DiagramState(LinkDiagram("repair-library", placeholders), new Dictionary<int, byte[]>());
        var diagramToken = await first.StateStore.CreateDiagramAsync(diagram, 5, Guid.NewGuid());
        var current = Required(await first.StateStore.ReadDiagramAsync(diagramToken.Key), "repair first revision");
        current.Value.Document.Name = "repair second revision";
        await first.StateStore.SaveDiagramAsync(current.Value, current.Token, Guid.NewGuid());
        var workbench = new WorkbenchState
        {
            WorkbenchId = "repair-workbench", Name = "Repair workbench", ScopeId = "query-scope", ScopeName = "Query scope",
            ActiveDiagramSnapshot = LinkDiagram("repair-embedded", placeholders), ActiveDocumentPath = addresses[0].DocumentPath,
            OpenDocuments = [BoundWindow(addresses[0].DocumentPath, seeded.SnapshotKey, seeded.Procedure.ResourceKey),
                new() { FilePath = "https://example.invalid/placeholder", DisplayName = "explicit missing", TargetState = SavedDocumentTargetState.Missing },
                BoundWindow(addresses[1].DocumentPath, seeded.SnapshotKey, seeded.Table.ResourceKey)],
            ReferenceConnectionLines = [new() { ConnectionId = "repair-connection", SourceFilePath = addresses[0].DocumentPath,
                TargetFilePath = addresses[1].DocumentPath, SourceBoundSnapshotKey = seeded.SnapshotKey,
                SourceBoundResourceKey = seeded.Procedure.ResourceKey, SourceTargetState = SavedDocumentTargetState.Resolved,
                TargetBoundSnapshotKey = seeded.SnapshotKey, TargetBoundResourceKey = seeded.Table.ResourceKey,
                TargetTargetState = SavedDocumentTargetState.Resolved }]
        };
        var workbenchToken = await first.StateStore.CreateWorkbenchAsync(new(workbench, new Dictionary<int, byte[]>()), 0, Guid.NewGuid());
        var workspaceToken = await first.StateStore.CreateWorkspaceAsync(new WorkspaceState
        { OpenDocuments = [BoundWindow(addresses[2].DocumentPath, seeded.SnapshotKey, seeded.DataResourceKey)] }, Guid.NewGuid());
        var savedWorkbench = Required(await first.StateStore.ReadWorkbenchAsync(workbenchToken.Key), "new durable workbench").Value.Workbench;
        var savedWorkspace = Required(await first.StateStore.ReadWorkspaceAsync(), "new durable workspace").Value;
        check(savedWorkbench.OpenDocuments[0].FilePath == procedure && savedWorkspace.OpenDocuments[0].FilePath == data &&
            savedWorkbench.ReferenceConnectionLines[0].SourceFilePath == procedure && savedWorkbench.ReferenceConnectionLines[0].TargetFilePath == table &&
            savedWorkbench.ActiveDocumentPath == procedure,
            "Workspace and workbench saves normalize open-document, active-document and connection references");

        // Simulate records written by the old app, including non-current library revisions.
        foreach (var (id, link) in originalLinks)
            await fixture.DestinationSqlAsync("UPDATE surf.DiagramObject SET MetadataLink=@Link WHERE Id=@Id;",
                RelationalSession.Parameter("@Link", SqlDbType.NVarChar, link, -1), RelationalSession.Parameter("@Id", SqlDbType.NVarChar, id, -1));
        await fixture.DestinationSqlAsync("""
UPDATE surf.DocumentWindowState SET FilePath=CASE WHEN BoundResourceKey=@Data THEN @DataPath WHEN BoundResourceKey=@Table THEN @Conflict ELSE @Path END WHERE TargetState=1;
UPDATE surf.DocumentWindowState SET FilePath=@Conflict WHERE TargetState=2;
UPDATE surf.ReferenceConnectionLine SET SourceFilePath=@Path,TargetFilePath=@TablePath WHERE ConnectionId=N'repair-connection';
UPDATE surf.Workbench SET ActiveDocumentPath=@Path WHERE WorkbenchKey=@Workbench;
""", RelationalSession.Parameter("@Path", SqlDbType.NVarChar, expired, -1),
            RelationalSession.Parameter("@DataPath", SqlDbType.NVarChar, expiredData, -1),
            RelationalSession.Parameter("@TablePath", SqlDbType.NVarChar, expiredTable, -1),
            RelationalSession.Parameter("@Conflict", SqlDbType.NVarChar, conflicting, -1),
            RelationalSession.Parameter("@Data", SqlDbType.BigInt, seeded.DataResourceKey),
            RelationalSession.Parameter("@Table", SqlDbType.BigInt, seeded.Table.ResourceKey),
            RelationalSession.Parameter("@Workbench", SqlDbType.BigInt, workbenchToken.Key));
        var before = await ReadRepairObjectsAsync(restarted.Session);
        var repaired = await restarted.StateStore.RepairStoredDocumentLinksAsync();
        check(repaired.Repaired >= 17 && repaired.Unresolved >= 13,
            "Startup repair fixes resolvable persisted session references and reports malformed, missing and mismatched targets");
        var after = await ReadRepairObjectsAsync(restarted.Session);
        var targets = after.Where(row => originalLinks.ContainsKey(row.Id)).ToArray();
        check(targets.Length == originalLinks.Count * 3 && targets.All(row => row.Link == expected[row.Id]),
            "Repair covers both library diagram revisions and the embedded workbench diagram, preserving invalid originals");
        check(before.Select(row => (row.Key, row.Revision, row.Id, row.LabelKey, row.DocumentationKey))
            .SequenceEqual(after.Select(row => (row.Key, row.Revision, row.Id, row.LabelKey, row.DocumentationKey))),
            "Repair changes reference strings without replacing object identities, saved revisions, labels or documentation");
        var reopenedWorkbench = Required(await restarted.StateStore.ReadWorkbenchAsync(workbenchToken.Key), "repaired workbench");
        var reopenedWorkspace = Required(await restarted.StateStore.ReadWorkspaceAsync(), "repaired workspace");
        check(!workbenchToken.Version.SequenceEqual(reopenedWorkbench.Token.Version) &&
            !workspaceToken.Version.SequenceEqual(reopenedWorkspace.Token.Version),
            "Repair advances changed owner versions so pre-repair state cannot overwrite the repaired references");
        check(reopenedWorkbench.Value.Workbench.OpenDocuments[0].FilePath == procedure && reopenedWorkspace.Value.OpenDocuments[0].FilePath == data &&
            reopenedWorkbench.Value.Workbench.ReferenceConnectionLines[0].SourceFilePath == procedure &&
            reopenedWorkbench.Value.Workbench.ReferenceConnectionLines[0].TargetFilePath == table && reopenedWorkbench.Value.Workbench.ActiveDocumentPath == procedure,
            "Startup repair persists durable document, active-document and connection paths into workspaces and workbenches");
        check(reopenedWorkbench.Value.Workbench.OpenDocuments[1].TargetState == SavedDocumentTargetState.Missing &&
            reopenedWorkbench.Value.Workbench.OpenDocuments[1].FilePath == conflicting,
            "An explicitly unresolved saved target is retained for review rather than rebound from its numeric path");
        check(reopenedWorkbench.Value.Workbench.OpenDocuments[2].BoundResourceKey == seeded.Table.ResourceKey &&
            reopenedWorkbench.Value.Workbench.OpenDocuments[2].FilePath == conflicting,
            "A path that disagrees with its authoritative saved binding is preserved instead of redirected");
        var rerun = await restarted.StateStore.RepairStoredDocumentLinksAsync();
        var rerunWorkbench = Required(await restarted.StateStore.ReadWorkbenchAsync(workbenchToken.Key), "idempotent workbench");
        check(rerun.Repaired == 0 && rerun.Unresolved == repaired.Unresolved &&
            reopenedWorkbench.Token.Version.SequenceEqual(rerunWorkbench.Token.Version),
            "Restart repair is idempotent and does not republish unchanged owners");
        await using var reopenedDocuments = new RelationalDocumentService(new RelationalRuntime(restarted.Options));
        var repairedAddress = await reopenedDocuments.ResolveAsync(reopenedWorkbench.Value.Workbench.OpenDocuments[0].FilePath, null);
        check((await reopenedDocuments.ReadTextAsync(await reopenedDocuments.DescribeTextAsync(repairedAddress))).Text == seeded.ProcedureValue.Definition,
            "A repaired persisted document still opens the original SQL definition after another restart");
        var editable = Required(await restarted.StateStore.ReadDiagramAsync(diagramToken.Key), "diagram with retained unresolved references");
        editable.Value.Document.Objects[0].LabelText = "Unrelated diagram edit";
        var editedToken = await restarted.StateStore.SaveDiagramAsync(editable.Value, editable.Token, Guid.NewGuid());
        var edited = Required(await restarted.StateStore.ReadDiagramAsync(editedToken.Key), "saved unresolved reference diagram");
        check(edited.Value.Document.Objects[0].LabelText == "Unrelated diagram edit" &&
            edited.Value.Document.Objects.All(item => item.Metadata.Link == expected[item.Id]),
            "Existing unresolved links remain intact while unrelated edits to their diagram can still be saved");
        edited.Value.Document.Objects.Add(new() { Id = "new-foreign-link", ObjectType = DiagramObjectType.Shape,
            Width = 200, Height = 80, Metadata = new() { Link = expired + ":31" } });
        await ExpectDurableRejectionAsync(() => restarted.StateStore.SaveDiagramAsync(edited.Value, edited.Token, Guid.NewGuid()), check,
            "Preserving an existing unresolved link does not permit introducing another foreign-session link");
        await VerifyRetainedLayoutLinkEditsAsync(fixture, restarted, seeded, conflicting, missing, table, check);
    }

    private static async Task VerifyRetainedLayoutLinkEditsAsync(SqlFixture fixture, RelationalRuntime runtime,
        QueryIntegrationFixture seeded, string conflicting, string missing, string table, Action<bool, string> check)
    {
        var workspace = Required(await runtime.StateStore.ReadWorkspaceAsync(), "layout preservation workspace");
        var bound = BoundWindow(table, seeded.SnapshotKey, seeded.Table.ResourceKey);
        bound.DisplayName = "Retained resolved window";
        workspace.Value.OpenDocuments.Add(bound);
        workspace.Value.OpenDocuments.Add(new() { FilePath = "https://example.invalid/external", DisplayName = "Retained external window" });
        await runtime.StateStore.SaveWorkspaceAsync(workspace.Value, workspace.Token, Guid.NewGuid());
        var workbench = new WorkbenchState
        {
            WorkbenchId = "repair-retained-layout", Name = "Retained layout", ActiveDocumentPath = "https://example.invalid/active",
            OpenDocuments = [BoundWindow(table, seeded.SnapshotKey, seeded.Table.ResourceKey),
                new() { FilePath = "https://example.invalid/external", DisplayName = "Retained external window" }],
            ReferenceConnectionLines = [new()
            {
                ConnectionId = "retained-layout-connection", SourceFilePath = table, TargetFilePath = "https://example.invalid/external",
                SourceBoundSnapshotKey = seeded.SnapshotKey, SourceBoundResourceKey = seeded.Table.ResourceKey,
                SourceTargetState = SavedDocumentTargetState.Resolved
            }]
        };
        workbench.OpenDocuments[0].DisplayName = "Retained resolved window";
        var workbenchToken = await runtime.StateStore.CreateWorkbenchAsync(new(workbench, new Dictionary<int, byte[]>()), 10, Guid.NewGuid());
        await fixture.DestinationSqlAsync("""
UPDATE surf.DocumentWindowState SET FilePath=@Conflict WHERE DisplayName=N'Retained resolved window';
UPDATE surf.DocumentWindowState SET FilePath=@Missing WHERE DisplayName=N'Retained external window';
UPDATE surf.ReferenceConnectionLine SET SourceFilePath=@Conflict,TargetFilePath=@Missing WHERE ConnectionId=N'retained-layout-connection';
UPDATE surf.Workbench SET ActiveDocumentPath=@Missing WHERE WorkbenchKey=@Workbench;
""", RelationalSession.Parameter("@Conflict", SqlDbType.NVarChar, conflicting, -1),
            RelationalSession.Parameter("@Missing", SqlDbType.NVarChar, missing, -1),
            RelationalSession.Parameter("@Workbench", SqlDbType.BigInt, workbenchToken.Key));
        await runtime.StateStore.RepairStoredDocumentLinksAsync();
        workspace = Required(await runtime.StateStore.ReadWorkspaceAsync(), "retained repaired workspace");
        var selected = Required(await runtime.StateStore.ReadWorkbenchAsync(workbenchToken.Key), "retained repaired workbench");
        var retainedWorkbench = selected.Value.Workbench;
        var beforeWorkspace = workspace.Value.OpenDocuments.Select(LayoutWindowIdentity).ToArray();
        var beforeWorkbench = retainedWorkbench.OpenDocuments.Select(LayoutWindowIdentity).ToArray();
        var beforeConnections = retainedWorkbench.ReferenceConnectionLines.Select(LayoutConnectionIdentity).ToArray();
        check(workspace.Value.OpenDocuments.Any(window => window.FilePath == conflicting && window.TargetState == SavedDocumentTargetState.Resolved &&
                window.BoundResourceKey == seeded.Table.ResourceKey) &&
            retainedWorkbench.OpenDocuments[1].FilePath == missing && retainedWorkbench.OpenDocuments[1].TargetState == SavedDocumentTargetState.External &&
            retainedWorkbench.ReferenceConnectionLines[0].SourceFilePath == conflicting && retainedWorkbench.ReferenceConnectionLines[0].TargetFilePath == missing &&
            retainedWorkbench.ActiveDocumentPath == missing,
            "Repair preserves unresolvable external and mismatched resolved layout references with their original bindings");
        workspace.Value.CanvasZoom = 1.75;
        workspace.Value.OpenDocuments[^1].Left = 123;
        await runtime.StateStore.SaveWorkspaceAsync(workspace.Value, workspace.Token, Guid.NewGuid());
        retainedWorkbench.Name = "Unrelated workbench rename";
        retainedWorkbench.CodeCanvasZoom = 1.5;
        retainedWorkbench.ReferenceConnectionLines[0].SourceLineNumber = 7;
        await runtime.StateStore.SaveWorkbenchAsync(selected.Value, selected.Token, Guid.NewGuid());
        var savedWorkspace = Required(await runtime.StateStore.ReadWorkspaceAsync(), "edited retained workspace");
        var savedWorkbench = Required(await runtime.StateStore.ReadWorkbenchAsync(workbenchToken.Key), "edited retained workbench");
        check(savedWorkspace.Value.CanvasZoom == 1.75 && savedWorkspace.Value.OpenDocuments[^1].Left == 123 &&
            savedWorkspace.Value.OpenDocuments.Select(LayoutWindowIdentity).SequenceEqual(beforeWorkspace) &&
            savedWorkbench.Value.Workbench.Name == "Unrelated workbench rename" && savedWorkbench.Value.Workbench.CodeCanvasZoom == 1.5 &&
            savedWorkbench.Value.Workbench.ReferenceConnectionLines[0].SourceLineNumber == 7 &&
            savedWorkbench.Value.Workbench.OpenDocuments.Select(LayoutWindowIdentity).SequenceEqual(beforeWorkbench) &&
            savedWorkbench.Value.Workbench.ReferenceConnectionLines.Select(LayoutConnectionIdentity).SequenceEqual(beforeConnections) &&
            savedWorkbench.Value.Workbench.ActiveDocumentPath == missing,
            "Unrelated workspace and workbench edits retain existing broken window, connection and active-document references");
        check(workspace.Value.OpenDocuments.Select(LayoutWindowIdentity).SequenceEqual(beforeWorkspace) &&
            retainedWorkbench.OpenDocuments.Select(LayoutWindowIdentity).SequenceEqual(beforeWorkbench) &&
            retainedWorkbench.ReferenceConnectionLines.Select(LayoutConnectionIdentity).SequenceEqual(beforeConnections) && retainedWorkbench.ActiveDocumentPath == missing,
            "Preserving unresolved saved layout references never mutates the caller's reference descriptors");

        savedWorkspace.Value.OpenDocuments.Add(new() { FilePath = missing + ":21", DisplayName = "New foreign window" });
        await ExpectDurableRejectionAsync(() => runtime.StateStore.SaveWorkspaceAsync(savedWorkspace.Value, savedWorkspace.Token, Guid.NewGuid()), check,
            "Retained workspace references do not authorize a newly added foreign-session window");
        savedWorkspace.Value.OpenDocuments.RemoveAt(savedWorkspace.Value.OpenDocuments.Count - 1);
        savedWorkspace.Value.OpenDocuments[^1].FilePath = missing + ":22";
        await ExpectDurableRejectionAsync(() => runtime.StateStore.SaveWorkspaceAsync(savedWorkspace.Value, savedWorkspace.Token, Guid.NewGuid()), check,
            "Editing a retained unresolved window into another foreign-session address is rejected");
        var saved = savedWorkbench.Value.Workbench;
        var retainedConnection = saved.ReferenceConnectionLines[0];
        retainedConnection.ConnectionId = "new-foreign-connection";
        await ExpectDurableRejectionAsync(() => runtime.StateStore.SaveWorkbenchAsync(savedWorkbench.Value, savedWorkbench.Token, Guid.NewGuid()), check,
            "A new connection cannot borrow another saved connection's permission to retain an unresolved target");
        retainedConnection.ConnectionId = "retained-layout-connection";
        retainedConnection.TargetFilePath = missing + ":23";
        await ExpectDurableRejectionAsync(() => runtime.StateStore.SaveWorkbenchAsync(savedWorkbench.Value, savedWorkbench.Token, Guid.NewGuid()), check,
            "Editing a retained connection endpoint into another foreign-session address is rejected");
        retainedConnection.TargetFilePath = missing;
        saved.ActiveDocumentPath = missing + ":24";
        await ExpectDurableRejectionAsync(() => runtime.StateStore.SaveWorkbenchAsync(savedWorkbench.Value, savedWorkbench.Token, Guid.NewGuid()), check,
            "A retained active-document address does not authorize a different foreign-session address");
        saved.ActiveDocumentPath = missing;
        saved.WorkbenchId = "new-owner-with-foreign-references";
        await ExpectDurableRejectionAsync(() => runtime.StateStore.CreateWorkbenchAsync(savedWorkbench.Value, 11, Guid.NewGuid()), check,
            "Unresolved layout references may be retained only by their existing owner");
        saved.WorkbenchId = "repair-retained-layout";
        var unchanged = Required(await runtime.StateStore.ReadWorkbenchAsync(workbenchToken.Key), "layout after rejected foreign saves");
        check(unchanged.Token.Version.SequenceEqual(savedWorkbench.Token.Version) &&
            unchanged.Value.Workbench.ReferenceConnectionLines.Select(LayoutConnectionIdentity).SequenceEqual(beforeConnections) &&
            saved.ReferenceConnectionLines.Select(LayoutConnectionIdentity).SequenceEqual(beforeConnections),
            "Rejected foreign-reference edits leave both stored state and caller reference descriptors intact");
    }

    private static (string Path, long? Snapshot, long? Resource, SavedDocumentTargetState State) LayoutWindowIdentity(OpenDocumentState window) =>
        (window.FilePath, window.BoundSnapshotKey, window.BoundResourceKey, window.TargetState);

    private static (string Id, string Source, long? SourceSnapshot, long? SourceResource, SavedDocumentTargetState SourceState,
        string Target, long? TargetSnapshot, long? TargetResource, SavedDocumentTargetState TargetState) LayoutConnectionIdentity(ReferenceConnectionLineState line) =>
        (line.ConnectionId, line.SourceFilePath, line.SourceBoundSnapshotKey, line.SourceBoundResourceKey, line.SourceTargetState,
            line.TargetFilePath, line.TargetBoundSnapshotKey, line.TargetBoundResourceKey, line.TargetTargetState);

    private static OpenDocumentState BoundWindow(string path, long snapshot, long resource) => new()
    { FilePath = path, DisplayName = "Fixture document", BoundSnapshotKey = snapshot, BoundResourceKey = resource, TargetState = SavedDocumentTargetState.Resolved };

    private static DiagramDocument LinkDiagram(string id, IReadOnlyDictionary<string, string> links) => new()
    {
        DiagramId = id, Name = id, CreatedAtUtc = EvidenceTime, UpdatedAtUtc = EvidenceTime,
        Objects = links.Select(pair => new DiagramObjectSnapshot
        {
            Id = pair.Key, ObjectType = DiagramObjectType.Shape, ShapeKind = DiagramShapeKind.Rectangle,
            LabelText = pair.Key, Width = 200, Height = 80, Metadata = new() { Link = pair.Value }
        }).ToList()
    };

    private sealed record RepairObject(long Key, long Revision, string Id, string Link, long LabelKey, long DocumentationKey);
    private static async Task<IReadOnlyList<RepairObject>> ReadRepairObjectsAsync(RelationalSession session)
    {
        var result = new List<RepairObject>();
        await using var sql = await session.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = "SELECT DiagramObjectKey,DiagramRevisionKey,Id,MetadataLink,LabelTextContentKey,DocumentationXamlContentKey FROM surf.DiagramObject ORDER BY DiagramObjectKey;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5)));
        return result;
    }

    private static async Task ExpectDurableRejectionAsync(Func<Task> action, Action<bool, string> check, string description)
    {
        bool rejected = false;
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { rejected = true; }
        check(rejected, description);
    }
}

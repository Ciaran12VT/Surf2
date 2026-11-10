using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Services;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access.State;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    public static async Task RunSavedTargetChecksAsync(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var models = SavedTargetModels();
        var documents = models.Documents();
        check(documents.Values.All(json => !json.Contains("BoundSnapshotKey", StringComparison.Ordinal) &&
            !json.Contains("BoundResourceKey", StringComparison.Ordinal) && !json.Contains("TargetState", StringComparison.Ordinal)),
            "Saved-target fixture starts from six legacy SQL roots without relational-only DTO fields");
        await using var fixture = await SqlFixture.CreateAsync(documents);
        var result = await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture));
        check(!result.AlreadyCompleted &&
            (await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational &&
            Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo;"), "Ready"),
            "Saved-target production migration binds legacy layouts before validated Ready publication");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DocumentWindowState;")) == 14 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.ReferenceConnectionLine;")) == 5 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationCheckpoint;")) == 6 &&
            Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.SourceProvenance;")) == 6,
            "Saved-target resolution changes fields only: original fourteen windows, five lines and six root checkpoints/provenances");
        await fixture.VerifySourceUnchangedAsync(check);

        var session = new RelationalSession(fixture.DestinationConnectionString);
        var snapshots = new RelationalSnapshotStore(session, new RelationalContentStore());
        var state = new RelationalStateStore(session);
        var heads = await SnapshotPagesAsync(cursor => snapshots.ListSnapshotsAsync(pageSize: 1, cursor: cursor), check,
            "Saved-target snapshot headers");
        var a = heads.Single(x => x.SnapshotId == models.Snapshots.Snapshots[0].SnapshotId);
        var b = heads.Single(x => x.SnapshotId == "saved-target-b");
        var excluded = heads.Single(x => x.SnapshotId == "saved-target-outside");
        var aProcedure = (await snapshots.ListObjectsAsync(a.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items
            .Single(x => x.Resource.ObjectName == "p").Resource;
        var bProcedure = (await snapshots.ListObjectsAsync(b.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items.Single().Resource;
        var excludedProcedure = (await snapshots.ListObjectsAsync(excluded.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items.Single().Resource;
        var resolvedA = new SavedTargetEvidence(SavedDocumentTargetState.Resolved, a.SnapshotKey, aProcedure.ResourceKey);
        var resolvedB = new SavedTargetEvidence(SavedDocumentTargetState.Resolved, b.SnapshotKey, bProcedure.ResourceKey);
        var resolvedOutside = new SavedTargetEvidence(SavedDocumentTargetState.Resolved, excluded.SnapshotKey, excludedProcedure.ResourceKey);
        var ambiguous = new SavedTargetEvidence(SavedDocumentTargetState.Ambiguous, null, null);
        var missing = new SavedTargetEvidence(SavedDocumentTargetState.Missing, null, null);
        var external = new SavedTargetEvidence(SavedDocumentTargetState.External, null, null);
        SavedTargetEvidence[] workspaceBindings = [resolvedA, resolvedB, ambiguous, missing, resolvedOutside, external, missing];
        SavedTargetEvidence[] workbenchBindings = [resolvedA, resolvedB, ambiguous, missing, missing, external, missing];
        (SavedTargetEvidence Source, SavedTargetEvidence Target)[] referenceBindings =
            [(resolvedA, resolvedB), (ambiguous, resolvedA), (missing, external), (missing, missing), (resolvedB, ambiguous)];

        var workspace = Required(await state.ReadWorkspaceAsync(), "saved-target migrated workspace");
        var workbenchHead = (await state.ListWorkbenchesAsync()).Items.Single();
        var workbench = Required(await state.ReadWorkbenchAsync(workbenchHead.Token.Key), "saved-target migrated workbench");
        SavedTargetWindows(workspace.Value.OpenDocuments, workspaceBindings, check, "Migrated workspace");
        SavedTargetWindows(workbench.Value.Workbench.OpenDocuments, workbenchBindings, check, "Migrated scoped workbench");
        SavedTargetReferences(workbench.Value.Workbench.ReferenceConnectionLines, referenceBindings, check, "Migrated references");
        SameModel(models.Workspace, workspace.Value, check, "Binding preserves every original workspace path, font, geometry and filter");
        SameModel(models.Workbenches.Workbenches[0], workbench.Value.Workbench, check,
            "Binding preserves workbench paths, line coordinates and the independent embedded diagram graph");
        check(Convert.ToInt64(await fixture.DestinationSqlAsync("""
            SELECT COUNT_BIG(*) FROM surf.SnapshotResource
            WHERE SnapshotKey=@Snapshot AND ObjectName=N'retired-only' AND CurrentRevisionKey IS NULL;
            """, RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, a.SnapshotKey))) == 1,
            "Missing retired procedure is real history-only SQL evidence, not an absent fixture object");
        check((await snapshots.ListObjectsAsync(a.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items
            .All(x => x.Resource.ObjectName != "retired-only"), "Saved bindings never select a history-only procedure as current");

        var access = new SelectedStateAccess(session);
        await using (var edit = StateAccessEdit(await access.LoadWorkspaceAsync(), check, "saved-target workspace copy"))
        {
            var copy = edit.Snapshot();
            SavedTargetWindows(copy.OpenDocuments, workspaceBindings, check, "Detached workspace copy");
            copy.OpenDocuments[0].BoundSnapshotKey = excluded.SnapshotKey;
            SavedTargetWindows(edit.Snapshot().OpenDocuments, workspaceBindings, check, "Workspace copy isolation");
            copy = edit.Snapshot(); copy.CanvasZoom = 2.875;
            edit.Replace(copy); var before = edit.ExpectedToken;
            StateAccessSaved(edit, before, await edit.SaveAsync(), check, "Saved-target workspace save");
            workspace = Required(await state.ReadWorkspaceAsync(), "saved-target saved workspace");
            SavedTargetWindows(workspace.Value.OpenDocuments, workspaceBindings, check, "Saved/reloaded workspace");
            SameModel(copy, workspace.Value, check, "Typed workspace save retains binding siblings and exact legacy fields");
        }
        await using (var edit = StateAccessEdit(await access.LoadWorkbenchAsync(workbenchHead.Token.Key), check, "saved-target workbench copy"))
        {
            var copy = edit.Snapshot();
            SavedTargetWindows(copy.Workbench.OpenDocuments, workbenchBindings, check, "Detached workbench windows");
            SavedTargetReferences(copy.Workbench.ReferenceConnectionLines, referenceBindings, check, "Detached reference lines");
            copy.Workbench.ReferenceConnectionLines[0].SourceBoundSnapshotKey = excluded.SnapshotKey;
            SavedTargetReferences(edit.Snapshot().Workbench.ReferenceConnectionLines, referenceBindings, check, "Reference copy isolation");
            copy = edit.Snapshot(); copy.Workbench.Name = "saved binding roundtrip";
            copy.Workbench.OpenDocuments[1].FontSize = 19.625;
            edit.Replace(copy); var before = edit.ExpectedToken;
            StateAccessSaved(edit, before, await edit.SaveAsync(), check, "Saved-target workbench save");
            workbench = Required(await state.ReadWorkbenchAsync(workbenchHead.Token.Key), "saved-target saved workbench");
            SavedTargetWindows(workbench.Value.Workbench.OpenDocuments, workbenchBindings, check, "Saved/reloaded workbench windows");
            SavedTargetReferences(workbench.Value.Workbench.ReferenceConnectionLines, referenceBindings, check, "Saved/reloaded references");
            SameModel(copy.Workbench, workbench.Value.Workbench, check, "Typed workbench save retains fonts, filters, paths and embedded graph");
            SameImages(copy.PastedImages, workbench.Value.PastedImages, check, "Typed binding save preserves embedded pasted bytes");
        }

        long renamedRevision = 0;
        var renamed = MixedObject("renamed-p", "CREATE PROCEDURE [dbo].[renamed-p] AS SELECT N'renamed-current';");
        await snapshots.ExecuteMutationAsync(a.SnapshotKey, a.RowVersion, async (writer, ct) =>
        {
            await writer.UpdateSnapshotHeaderAsync(a.SnapshotKey, "renamed saved-target snapshot", a.DatabaseName, a.ImportedAtUtc, a.SortOrdinal, ct);
            renamedRevision = await writer.InsertObjectRevisionAsync(aProcedure.ResourceKey, renamed, ct);
            await writer.SealRevisionAsync(renamedRevision, ct);
            await writer.SetCurrentRevisionAsync(aProcedure.ResourceKey, renamedRevision, aProcedure.SortOrdinal, aProcedure.RowVersion, ct);
        });
        var renamedHead = Required(await snapshots.GetSnapshotAsync(a.SnapshotKey), "renamed binding snapshot");
        var renamedResource = Required(await snapshots.ResolveResourceAsync(aProcedure.ResourceKey), "renamed bound procedure");
        check(renamedHead.DisplayName == "renamed saved-target snapshot" && renamedResource.ResourceKey == aProcedure.ResourceKey &&
            renamedResource.SnapshotKey == a.SnapshotKey && renamedResource.ObjectName == renamed.ObjectName && renamedResource.RevisionKey == renamedRevision,
            "Production targeted rename publishes a new selected revision under the same durable owner/resource keys");
        SameModel(renamed, await snapshots.ReadObjectAsync(renamedResource.RevisionKey), check,
            "A saved numeric resource binding resolves the renamed current procedure definition");
        check((await snapshots.ReadObjectAsync(aProcedure.RevisionKey)).ObjectName == "p",
            "Rename preserves the previous immutable procedure revision");
        var renamedPath = DatabaseDocumentService.CreateCanonicalObjectDocumentPath(new DatabaseMetadataSnapshot
            { SnapshotId = renamedHead.SnapshotId, DisplayName = renamedHead.DisplayName, DatabaseName = renamedHead.DatabaseName }, renamed);
        check(renamedPath != workspace.Value.OpenDocuments[0].FilePath,
            "Rename test changes the canonical locator, so unchanged saved keys cannot be explained by unchanged text");
        await using (var edit = StateAccessEdit(await access.LoadWorkspaceAsync(), check, "workspace after rename"))
        {
            var copy = edit.Snapshot(); copy.ViewportHorizontalOffset = -13.875;
            edit.Replace(copy); await edit.SaveAsync();
        }
        await using (var edit = StateAccessEdit(await access.LoadWorkbenchAsync(workbenchHead.Token.Key), check, "workbench after rename"))
        {
            var copy = edit.Snapshot(); copy.Workbench.CodeCanvasZoom = 2.125;
            edit.Replace(copy); await edit.SaveAsync();
        }
        workspace = Required(await state.ReadWorkspaceAsync(), "workspace durable keys after rename");
        workbench = Required(await state.ReadWorkbenchAsync(workbenchHead.Token.Key), "workbench durable keys after rename");
        SavedTargetWindows(workspace.Value.OpenDocuments, workspaceBindings, check, "Workspace save/load after rename");
        SavedTargetWindows(workbench.Value.Workbench.OpenDocuments, workbenchBindings, check, "Workbench save/load after rename");
        SavedTargetReferences(workbench.Value.Workbench.ReferenceConnectionLines, referenceBindings, check, "Reference save/load after rename");
        check(workspace.Value.OpenDocuments.Select(x => x.FilePath).SequenceEqual(models.Workspace.OpenDocuments.Select(x => x.FilePath)) &&
            workbench.Value.Workbench.ReferenceConnectionLines.Select(x => (x.SourceFilePath, x.TargetFilePath))
                .SequenceEqual(models.Workbenches.Workbenches[0].ReferenceConnectionLines.Select(x => (x.SourceFilePath, x.TargetFilePath))),
            "Typed rename/save retains original raw locators rather than silently retargeting or rewriting them");

        await VerifySavedTargetWrongOwnerAsync(fixture, access, state, excluded.SnapshotKey, workbenchHead.Token.Key, check);
        SavedTargetWindows(Required(await state.ReadWorkspaceAsync(), "workspace after rejected targets").Value.OpenDocuments,
            workspaceBindings, check, "Workspace after FK rejection");
        var intact = Required(await state.ReadWorkbenchAsync(workbenchHead.Token.Key), "workbench after rejected targets");
        SavedTargetWindows(intact.Value.Workbench.OpenDocuments, workbenchBindings, check, "Workbench after FK rejection");
        SavedTargetReferences(intact.Value.Workbench.ReferenceConnectionLines, referenceBindings, check, "References after FK rejection");
        check(Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo;"), "Ready"),
            "Rejected targeted writes do not damage destination readiness");
        check(true, $"Saved-target IDs: {a.SnapshotId}/{a.SnapshotKey}/{aProcedure.ResourceKey}, {b.SnapshotId}/{b.SnapshotKey}/{bProcedure.ResourceKey}; " +
            $"outside-scope {excluded.SnapshotId}/{excluded.SnapshotKey}; canonical {models.Workspace.OpenDocuments[0].FilePath}; " +
            $"ambiguous retained {models.Workspace.OpenDocuments[2].FilePath}");
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private static MigrationFixture SavedTargetModels()
    {
        var models = MigrationModels();
        var a = models.Snapshots.Snapshots[0]; a.DisplayName = "same saved-target display";
        var b = new DatabaseMetadataSnapshot { SnapshotId = "saved-target-b", DisplayName = a.DisplayName,
            DatabaseName = "binding database B", ImportedAtUtc = EvidenceTime, Objects = [MixedObject("p", "current-B")] };
        var excluded = new DatabaseMetadataSnapshot { SnapshotId = "saved-target-outside", DisplayName = "outside scope",
            DatabaseName = "binding database outside", ImportedAtUtc = EvidenceTime, Objects = [MixedObject("p", "outside-current")] };
        models.Snapshots.Snapshots.Add(b); models.Snapshots.Snapshots.Add(excluded);
        var retired = MixedObject("retired-only", "historical-only-procedure");
        models.Snapshots.Histories[0].Versions.Single(x => x.VersionId == "mixed-latest").Changes.Add(
            MixedChange(DatabaseVersionedResourceKind.StoredProcedure, DatabaseSnapshotResourceChangeKind.Deleted, retired.ObjectName,
                new() { Kind = DatabaseVersionedResourceKind.StoredProcedure, DatabaseObject = retired }));
        models.Scopes.Scopes[0].Resources.Add(new() { ResourceId = "binding-snapshot-b", Kind = ResourceKind.DatabaseSnapshot,
            Path = b.SnapshotId, AddedAtUtc = EvidenceTime, IncludeChildren = true });
        string[] paths =
        [
            DatabaseDocumentService.CreateCanonicalObjectDocumentPath(a, a.Objects[0]),
            DatabaseDocumentService.CreateCanonicalObjectDocumentPath(b, b.Objects[0]),
            DatabaseDocumentService.CreateObjectDocumentPath(a, a.Objects[0]),
            DatabaseDocumentService.CreateCanonicalObjectDocumentPath(a, retired),
            DatabaseDocumentService.CreateCanonicalObjectDocumentPath(excluded, excluded.Objects[0]),
            "fixture-only-binding.sql",
            "db://malformed"
        ];
        models.Workspace.OpenDocuments = new(paths.Select(Window));
        var workbench = models.Workbenches.Workbenches[0]; workbench.OpenDocuments = paths.Select(Window).ToList();
        workbench.ReferenceConnectionLines =
        [
            SavedTargetLine("canonical pair", paths[0], paths[1]),
            SavedTargetLine("ambiguous source", paths[2], paths[0]),
            SavedTargetLine("historical and external", paths[3], paths[5]),
            SavedTargetLine("outside and malformed", paths[4], paths[6]),
            SavedTargetLine("ambiguous target", paths[1], paths[2])
        ];
        return models;
    }

    private static ReferenceConnectionLineState SavedTargetLine(string id, string source, string target) => new()
    {
        ConnectionId = id, SourceFilePath = source, TargetFilePath = target,
        SourceLineNumber = 11, SourceStartColumnNumber = 2, SourceEndColumnNumber = 19,
        TargetLineNumber = 23, TargetStartColumnNumber = 4, TargetEndColumnNumber = 29
    };

    private sealed record SavedTargetEvidence(SavedDocumentTargetState State, long? SnapshotKey, long? ResourceKey);

    // JsonIgnore is intentional in legacy DTOs; compare durable fields explicitly instead of using SameModel.
    private static void SavedTargetWindows(IReadOnlyList<OpenDocumentState> windows, IReadOnlyList<SavedTargetEvidence> expected,
        Action<bool, string> check, string name)
    {
        check(windows.Count == expected.Count, name + " retains exact window count/order");
        for (int i = 0; i < expected.Count; i++)
            check(new SavedTargetEvidence(windows[i].TargetState, windows[i].BoundSnapshotKey, windows[i].BoundResourceKey) == expected[i],
                name + " window " + i + " exact state/owner/resource: " + expected[i].State);
    }

    private static void SavedTargetReferences(IReadOnlyList<ReferenceConnectionLineState> lines,
        IReadOnlyList<(SavedTargetEvidence Source, SavedTargetEvidence Target)> expected, Action<bool, string> check, string name)
    {
        check(lines.Count == expected.Count, name + " retains exact reference count/order");
        for (int i = 0; i < expected.Count; i++)
        {
            var line = lines[i];
            check(new SavedTargetEvidence(line.SourceTargetState, line.SourceBoundSnapshotKey, line.SourceBoundResourceKey) == expected[i].Source &&
                new SavedTargetEvidence(line.TargetTargetState, line.TargetBoundSnapshotKey, line.TargetBoundResourceKey) == expected[i].Target,
                name + " line " + i + " independently retains source and target states/keys");
        }
    }

    private static async Task VerifySavedTargetWrongOwnerAsync(SqlFixture fixture, SelectedStateAccess access,
        RelationalStateStore state, long wrongSnapshotKey, long workbenchKey, Action<bool, string> check)
    {
        var workspace = Required(await state.ReadWorkspaceAsync(), "pre-rejection workspace");
        var workbench = Required(await state.ReadWorkbenchAsync(workbenchKey), "pre-rejection workbench");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var beforeRows = await SavedTargetRowEvidenceAsync(fixture);
            if (attempt == 0)
            {
                await using var edit = StateAccessEdit(await access.LoadWorkspaceAsync(), check, "wrong-owner workspace copy");
                var invalid = edit.Snapshot(); invalid.CanvasZoom = 9.25;
                invalid.OpenDocuments[1].BoundSnapshotKey = wrongSnapshotKey;
                await SavedTargetForeignKeyAsync(() => state.SaveWorkspaceAsync(invalid, workspace.Token, Guid.NewGuid()), check,
                    "Workspace window wrong-owner composite FK");
            }
            else
            {
                await using var edit = StateAccessEdit(await access.LoadWorkbenchAsync(workbenchKey), check, "wrong-owner workbench copy");
                var invalid = edit.Snapshot(); invalid.Workbench.Name = "must roll back";
                if (attempt == 1) invalid.Workbench.OpenDocuments[1].BoundSnapshotKey = wrongSnapshotKey;
                if (attempt == 2) invalid.Workbench.ReferenceConnectionLines[0].SourceBoundSnapshotKey = wrongSnapshotKey;
                if (attempt == 3) invalid.Workbench.ReferenceConnectionLines[0].TargetBoundSnapshotKey = wrongSnapshotKey;
                await SavedTargetForeignKeyAsync(() => state.SaveWorkbenchAsync(invalid, workbench.Token, Guid.NewGuid()), check,
                    "Workbench " + (attempt == 1 ? "window" : attempt == 2 ? "reference source" : "reference target") + " wrong-owner composite FK");
            }
            var afterWorkspace = Required(await state.ReadWorkspaceAsync(), "post-rejection workspace");
            var afterWorkbench = Required(await state.ReadWorkbenchAsync(workbenchKey), "post-rejection workbench");
            check(StateAccessSameToken(workspace.Token, afterWorkspace.Token) && StateAccessSameToken(workbench.Token, afterWorkbench.Token),
                "Wrong-owner rejection " + attempt + " rolls back both owner versions and publication IDs");
            SameModel(workspace.Value, afterWorkspace.Value, check, "Wrong-owner rejection leaves workspace fields unchanged: " + attempt);
            SameModel(workbench.Value.Workbench, afterWorkbench.Value.Workbench, check,
                "Wrong-owner rejection leaves workbench graph/layout/line fields unchanged: " + attempt);
            SameImages(workbench.Value.PastedImages, afterWorkbench.Value.PastedImages, check,
                "Wrong-owner rejection leaves embedded bytes unchanged: " + attempt);
            var afterRows = await SavedTargetRowEvidenceAsync(fixture);
            check(beforeRows.All(x => afterRows.TryGetValue(x.Key, out var value) && x.Value.SequenceEqual(value)),
                "Wrong-owner rejection " + attempt + " is atomic for existing child identities, bindings, filters, catalogue token and revisions");
        }
    }

    private static async Task SavedTargetForeignKeyAsync(Func<Task> write, Action<bool, string> check, string name)
    {
        bool rejected = false;
        try { await write(); }
        catch (SqlException error) when (error.Number == 547 && error.Message.Contains("SnapshotResource", StringComparison.Ordinal))
        { rejected = true; }
        check(rejected, name + " rejects an existing resource paired with an unrelated existing snapshot (SQL 547)");
    }

    private static async Task<Dictionary<string, byte[]>> SavedTargetRowEvidenceAsync(SqlFixture fixture)
    {
        // Full-row hashes are restricted to this small generated fixture, avoiding a parallel schema-column contract.
        (string Table, string Order)[] tables =
        [
            ("WorkspaceSession", "WorkspaceSessionKey"), ("Workbench", "WorkbenchKey"),
            ("StateCatalogueGeneration", "ProfileKey,Kind"), ("DocumentWindowState", "DocumentWindowKey"),
            ("DocumentWindowFilter", "DocumentWindowKey,SortOrdinal"),
            ("WorkspaceUnloadedResource", "WorkspaceUnloadedResourceKey"),
            ("ReferenceConnectionLine", "ReferenceConnectionLineKey"), ("DiagramRevision", "DiagramRevisionKey"),
            ("DiagramObject", "DiagramObjectKey")
        ];
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (table, order) in tables)
            hashes.Add(table, (byte[])(await fixture.DestinationSqlAsync($"""
                SELECT HASHBYTES('SHA2_256',CONVERT(nvarchar(max),
                    (SELECT * FROM surf.[{table}] ORDER BY {order} FOR JSON PATH,INCLUDE_NULL_VALUES)));
                """) ?? throw new InvalidOperationException("Fixture row hash missing: " + table)));
        return hashes;
    }
}

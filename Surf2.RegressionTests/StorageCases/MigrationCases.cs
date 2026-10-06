using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

public static partial class StorageRegressionSuite
{
    private sealed record MigrationFixture(DatabaseSnapshotLibrary Snapshots, ScopeLibrary Scopes,
        DiagramLibrary Diagrams, WorkbenchLibrary Workbenches, WorkspaceState Workspace, AppSettings Settings)
    {
        public IReadOnlyDictionary<string, string> Documents() => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["database-snapshots"] = JsonSerializer.Serialize(Snapshots, ModelJson),
            ["scope-library"] = JsonSerializer.Serialize(Scopes, ModelJson),
            ["diagram-library"] = JsonSerializer.Serialize(Diagrams, ModelJson),
            ["workbench-library"] = JsonSerializer.Serialize(Workbenches, ModelJson),
            ["workspace-state"] = JsonSerializer.Serialize(Workspace, ModelJson),
            ["app-settings"] = JsonSerializer.Serialize(Settings, ModelJson)
        };
    }

    private static MigrationFixture MigrationModels()
    {
        var snapshots = MixedHistoryLibrary();
        // Unit tests can preserve ambiguous evidence. The migration success case
        // is canonical so rejection gates cannot silently decide which duplicate wins.
        var current = snapshots.Snapshots[0];
        current.Objects.RemoveAt(2);
        current.Tables.RemoveAt(2);
        current.Columns.RemoveAt(2);
        current.PrimaryKeys.RemoveAt(2);
        current.TableDataSets.RemoveAt(2);
        current.FullDataTableNames.RemoveAt(2);
        var diagram = Diagram("migrated diagram").Document;
        foreach (var item in diagram.Objects) item.PastedImageFileName = "";
        var scope = new Scope
        {
            ScopeId = "migration-scope", Name = "migrated scope", Description = "description\r\n\u00e9",
            Resources =
            [
                new() { ResourceId = "snapshot-resource", Kind = ResourceKind.DatabaseSnapshot, Path = current.SnapshotId,
                    DisplayNameOverride = "snapshot label", DetailsOverride = "snapshot details", AddedAtUtc = EvidenceTime, IncludeChildren = false },
                new() { ResourceId = "diagram-resource", Kind = ResourceKind.Diagram, Path = diagram.DiagramId,
                    DisplayNameOverride = "diagram label", DetailsOverride = "diagram details", AddedAtUtc = EvidenceTime, IncludeChildren = true }
            ],
            VirtualFolders = [new() { VirtualFolderId = "migration-folder", Name = "group", ParentNodeKey = "root",
                ChildNodeKeys = ["snapshot-resource", "diagram-resource", "snapshot-resource"] }]
        };
        var workspace = new WorkspaceState { LastFolderPath = null, CanvasZoom = 1.25, ViewportHorizontalOffset = 3.5,
            ViewportVerticalOffset = -2.75, UnloadedResourceIds = ["unloaded", "unloaded"], OpenDocuments = [Window("fixture-only.csv")] };
        var workbench = new WorkbenchState
        {
            WorkbenchId = "migration-workbench", Name = "migrated workbench", ScopeId = scope.ScopeId, ScopeName = scope.Name,
            CreatedAtUtc = EvidenceTime, UpdatedAtUtc = EvidenceTime.AddTicks(1), SavedAtUtc = EvidenceTime.AddTicks(2),
            ActiveDiagramId = diagram.DiagramId, ActiveDiagramName = diagram.Name, ActiveDiagramSnapshot = diagram,
            IsDefaultForScope = true, IsDiagramViewVisible = true, IsDiagramLocked = false, CodeCanvasZoom = 1.5,
            DiagramCanvasZoom = 1.75, DiagramViewportHorizontalOffset = -9.125,
            UnloadedResourceIds = ["unloaded", "unloaded"], OpenDocuments = [Window("fixture-only.sql")]
        };
        var settings = new AppSettings
        {
            LoadMostRecentWorkbenchOnStartup = true, Appearance = new() { Theme = "Dark" },
            ResourceComparison = new() { IgnoreCaseByDefault = true, IgnoreWhitespaceByDefault = false },
            Diagnostics = new() { EnableInternalLogging = true },
            CodeWindows = new() { DefaultBackcolor = "#123456", BackcolorsByExtension =
                [new() { Extension = ".sql", Backcolor = "#ABCDEF", Language = "SQL Server" }] },
            DiagramImages = new() { Images = [new() { Id = "migration-image", Name = "fixture image", SortOrder = 1,
                NameRegex = "fixture.*", ContentRegex = "select.+", OriginalFileName = "fixture.png", ImageDataBase64 = Convert.ToBase64String(Png(59)) }] }
        };
        settings.KeyboardShortcuts.EnableCanvasCtrlMousePanning = false;
        return new(snapshots, new() { SchemaVersion = 1, LastActiveScopeId = scope.ScopeId, Scopes = [scope] },
            new() { Diagrams = [diagram] }, new() { Workbenches = [workbench] }, workspace, settings);
    }

    private static RelationalMigrationRequest MigrationRequest(SqlFixture fixture, string? resume = null)
    {
        string local = Path.Combine(fixture.OwnedDirectory, "empty-local-inputs");
        string staging = Path.Combine(fixture.OwnedDirectory, "staging");
        Directory.CreateDirectory(Path.Combine(local, "PastedDiagramImages"));
        Directory.CreateDirectory(staging);
        return new(fixture.SourceConnectionString, fixture.DestinationConnectionString, local, staging,
            CreateDestination: false, ResumeStageDirectory: resume);
    }

    private static async Task VerifyMigrationAsync(Action<bool, string> check)
    {
        var models = MigrationModels();
        var sourceDocuments = models.Documents();
        await using (var fixture = await SqlFixture.CreateAsync(sourceDocuments))
        {
            var migrator = new RelationalMigrator();
            var request = MigrationRequest(fixture);
            using var cancellation = new CancellationTokenSource();
            bool cancelledAtCheckpoint = false;
            var progress = new InlineProgress(message =>
            {
                if (message.StartsWith("Converting scopes,", StringComparison.Ordinal))
                {
                    cancelledAtCheckpoint = true;
                    cancellation.Cancel();
                }
            });
            await ThrowsAsync<OperationCanceledException>(() => migrator.ConvertAsync(request, progress, cancellation.Token), check,
                "Production migration cancellation leaves activation unfinished");
            check(cancelledAtCheckpoint, "Cancellation occurs after committed snapshot units, before state import");
            check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Incomplete &&
                Equals(await fixture.DestinationSqlAsync("SELECT State FROM surf.StorageFormatInfo;"), "Migrating"),
                "Cancelled migration remains Migrating rather than Ready");
            var session = new RelationalSession(fixture.DestinationConnectionString);
            await ThrowsAsync<InvalidOperationException>(() => new RelationalSnapshotStore(session, new RelationalContentStore()).ListSnapshotsAsync(), check,
                "Cancelled destination refuses runtime snapshot reads despite staged published snapshots");
            await ThrowsAsync<InvalidOperationException>(() => new RelationalStateStore(session).ReadSettingsAsync(), check,
                "Cancelled destination refuses runtime state reads");
            await fixture.VerifySourceUnchangedAsync(check);
            string[] directories = Directory.GetDirectories(request.StagingRoot);
            check(directories.Length == 1, "Cancellation retains exactly one fixture-owned frozen staging directory");
            string stageDirectory = directories.Single();
            Guid identity;
            byte[] fingerprint;
            Dictionary<string, byte[]> sourceHashes;
            await using (var reopened = await LegacySourceStage.ReopenAsync(stageDirectory))
            {
                identity = reopened.MigrationIdentity;
                fingerprint = reopened.Fingerprint.ToArray();
                sourceHashes = reopened.StagedDocuments.ToDictionary(x => x.Key, x => x.Value.SourceHash.ToArray(), StringComparer.Ordinal);
                string localRoot = Path.GetFullPath(request.LocalAppDataDirectory);
                check(reopened.SourceOrigin == "Sql" && reopened.StagedDocuments.Count == 6 &&
                    reopened.StagedDocuments.Values.All(x => x.SourceKind == "Sql" && x.LocalSourcePath != null &&
                        Path.IsPathFullyQualified(x.LocalSourcePath) &&
                        string.Equals(Path.GetFullPath(x.LocalSourcePath), Path.Combine(localRoot, LegacySourceStage.Documents[x.Key].FileName),
                            StringComparison.OrdinalIgnoreCase) &&
                        x.SourceHash.SequenceEqual(SHA256.HashData(Encoding.Unicode.GetBytes(sourceDocuments[x.Key])))),
                    "Frozen manifest retains all six authoritative SQL hashes and known fixture-local image-origin paths, without document fallback");
                check(string.Equals(reopened.SourceImageDirectory, Path.Combine(localRoot, "PastedDiagramImages"), StringComparison.OrdinalIgnoreCase),
                    "Reopened SQL stage resolves its exact original fixture-owned source image directory");
                bool guardEntered = false;
                await reopened.WithVerifiedSourceAsync(fixture.SourceConnectionString, async () =>
                {
                    guardEntered = true;
                    await VerifyMigrationSourceGuardAsync(fixture, check);
                });
                check(guardEntered, "Verified source guard invokes publication callback only after SQL and frozen-file checks");
            }
            long stagedSnapshots = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DatabaseSnapshot;"));
            long stagedDatasets = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DataSet;"));
            long stagedRows = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COALESCE(SUM(ActualRowCount),0) FROM surf.DataSet;"));
            check(stagedSnapshots == 1 && stagedDatasets > 0 && stagedRows > 0, "Cancellation preserves committed snapshot/capture work for resume");
            string differentCallerRoot = Path.Combine(fixture.OwnedDirectory, "different-resume-inputs");
            var result = await migrator.ConvertAsync(MigrationRequest(fixture, stageDirectory) with { LocalAppDataDirectory = differentCallerRoot });
            long expectedRows = models.Snapshots.Snapshots.Sum(x => x.TableDataSets.Sum(d => (long)d.Rows.Count)) +
                models.Snapshots.Histories.Sum(h => h.Versions.Sum(v => v.Changes.Sum(c => (long)(c.PreviousPayload?.TableDataSet?.Rows.Count ?? 0))));
            check(result.MigrationIdentity == identity && result.StageDirectory == stageDirectory && result.CapturedRows == expectedRows && !result.AlreadyCompleted,
                "Production resume reuses frozen migration identity, directory and exact current/history captured row count");
            check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DatabaseSnapshot;")) == stagedSnapshots &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.DataSet;")) == stagedDatasets &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COALESCE(SUM(ActualRowCount),0) FROM surf.DataSet;")) == stagedRows,
                "Resume reuses committed snapshot mappings and datasets without duplicates or replayed rows");
            await using (var reopened = await LegacySourceStage.ReopenAsync(stageDirectory))
            {
                check(reopened.Fingerprint.SequenceEqual(fingerprint) && reopened.StagedDocuments.All(x => sourceHashes[x.Key].SequenceEqual(x.Value.SourceHash)),
                    "Resume retains frozen manifest fingerprint and each SQL root hash");
                check(string.Equals(reopened.SourceImageDirectory, Path.Combine(request.LocalAppDataDirectory, "PastedDiagramImages"), StringComparison.OrdinalIgnoreCase) &&
                    !Directory.Exists(differentCallerRoot),
                    "Resume retains the manifest-pinned original image root instead of reading or creating the caller's different fixture path");
            }
            check((await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format == PersistenceFormat.Relational &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("""
                    SELECT COUNT_BIG(*) FROM surf.StorageFormatInfo f JOIN surf.MigrationRun r ON r.MigrationIdentity=f.CompletedMigrationIdentity
                    WHERE f.State='Ready' AND r.Status='Complete' AND r.FinishedAtUtc IS NOT NULL AND r.MigrationIdentity=@Migration;
                    """, RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, identity))) == 1,
                "Production migrator validates and atomically publishes its own Ready/completed identity");
            check(Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.SourceProvenance WHERE SourceKind='Sql';")) == 6 &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationCheckpoint;")) == 6 &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIssue WHERE Severity='Error';")) == 0,
                "All six roots have provenance/checkpoints and no unresolved migration errors");
            await VerifyMigratedModelsAsync(session, models, check);
            long mappedUnits = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap;"));
            long contentUnits = Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.TextContent;"));
            var completed = await migrator.ConvertAsync(MigrationRequest(fixture, stageDirectory));
            check(completed.AlreadyCompleted && completed.MigrationIdentity == result.MigrationIdentity &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap;")) == mappedUnits &&
                Convert.ToInt64(await fixture.DestinationSqlAsync("SELECT COUNT_BIG(*) FROM surf.TextContent;")) == contentUnits,
                "Completed resume is idempotent and neither imports nor republishes units");
            await fixture.VerifySourceUnchangedAsync(check);
        }
        var duplicates = MigrationModels();
        duplicates.Snapshots.Snapshots[0].Tables.Add(MixedTable("a", 1));
        await VerifyRejectedMigrationAsync(duplicates, "Duplicate table identities", check,
            "Migration rejects duplicate table ownership instead of silently selecting a revision");
        var noncanonical = MigrationModels();
        noncanonical.Snapshots.Histories[0].Versions.Single(x => x.VersionId == "mixed-early").Changes[2].PreviousPayload =
            new() { Kind = DatabaseVersionedResourceKind.TableData, TableDataSet = MixedData("D", 0) };
        await VerifyRejectedMigrationAsync(noncanonical, "noncanonical historical payload", check,
            "Migration rejects an Added change with previous payload instead of discarding evidence");
    }

    private static async Task VerifyMigrationSourceGuardAsync(SqlFixture fixture, Action<bool, string> check)
    {
        await using var writer = new SqlConnection(fixture.SourceConnectionString);
        await writer.OpenAsync();
        await using var command = writer.CreateCommand();
        // A same-value write on the generated source avoids changing payload/timestamp evidence even
        // if the guard regresses. The SERIALIZABLE verification transaction must still block it.
        command.CommandText = """
            SET LOCK_TIMEOUT 500;
            UPDATE app.Surf2Documents SET UpdatedAtUtc=UpdatedAtUtc WHERE DocumentKey=N'database-snapshots';
            """;
        bool blocked = false;
        try { await command.ExecuteNonQueryAsync(); }
        catch (SqlException error) when (error.Number == 1222) { blocked = true; }
        check(blocked, "Frozen SQL source guard holds source range/row locks across its callback and blocks a competing fixture-only write");
    }

    private static async Task VerifyMigratedModelsAsync(RelationalSession session, MigrationFixture expected, Action<bool, string> check)
    {
        var snapshots = new RelationalSnapshotStore(session, new RelationalContentStore());
        var capture = new RelationalCaptureStore(session);
        var state = new RelationalStateStore(session);
        var heads = await snapshots.ListSnapshotsAsync();
        check(heads.Items.Count == 1 && heads.Items[0].SnapshotId == expected.Snapshots.Snapshots[0].SnapshotId,
            "Migrated snapshot catalogue preserves selected original ID");
        long snapshotKey = heads.Items.Single().SnapshotKey;
        var versions = await snapshots.ListVersionsAsync(snapshotKey);
        check(versions.Items.Select(x => x.VersionNumber).SequenceEqual(expected.Snapshots.Histories[0].Versions.Select(x => x.VersionNumber)),
            "Production migration preserves original history collection order separately from version number");
        await VerifyHistoryOracleAsync(session, snapshots, capture, expected.Snapshots, snapshotKey,
            versions.Items.ToDictionary(x => x.VersionId, x => x.VersionKey, StringComparer.Ordinal), check);
        var scopes = await state.ListScopesAsync();
        var diagrams = await state.ListDiagramsAsync();
        var workbenches = await state.ListWorkbenchesAsync();
        check(scopes.Items.Count == 1 && diagrams.Items.Count == 1 && workbenches.Items.Count == 1, "Production migration imports selected scope/diagram/workbench catalogues exactly once");
        SameModel(expected.Scopes.Scopes[0], (await state.ReadScopeAsync(scopes.Items.Single().Token.Key))!.Value, check,
            "Production scope import preserves fields, ordered resources and folder memberships");
        SameModel(expected.Diagrams.Diagrams[0], (await state.ReadDiagramAsync(diagrams.Items.Single().Token.Key))!.Value.Document, check,
            "Production diagram import preserves images, all subtype fields, fonts, workflows and queries");
        SameModel(expected.Workbenches.Workbenches[0], (await state.ReadWorkbenchAsync(workbenches.Items.Single().Token.Key))!.Value.Workbench, check,
            "Production workbench import preserves its independent embedded diagram and window fonts");
        SameModel(expected.Workspace, (await state.ReadWorkspaceAsync())!.Value, check, "Production workspace import preserves all layout/window/filter fields");
        SameModel(expected.Settings, (await state.ReadSettingsAsync())!.Value, check, "Production settings import preserves preferences and image definitions without adding defaults");
        var selection = (await state.ReadScopeSelectionAsync())!.Value;
        check(selection.SchemaVersion == expected.Scopes.SchemaVersion && selection.LastActiveScopeId == expected.Scopes.LastActiveScopeId,
            "Production scope selection preserves source version and selected identity");
        await using var connection = await session.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM surf.ScopeResource WHERE (Kind=2 AND SnapshotKey IS NOT NULL) OR (Kind=3 AND DiagramKey IS NOT NULL);";
        check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 2, "Production importer resolves unambiguous scope snapshot/diagram targets");
    }

    private static async Task VerifyRejectedMigrationAsync(MigrationFixture models, string reason, Action<bool, string> check, string label)
    {
        await using var fixture = await SqlFixture.CreateAsync(models.Documents());
        RelationalMigrationException? rejection = null;
        try { await new RelationalMigrator().ConvertAsync(MigrationRequest(fixture)); }
        catch (RelationalMigrationException ex) { rejection = ex; }
        check(rejection?.InnerException is InvalidDataException error && error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase), label);
        var format = (await new PersistenceFormatProbe().ProbeAsync(fixture.DestinationConnectionString)).Format;
        check(format is PersistenceFormat.Empty or PersistenceFormat.Incomplete, "Rejected migration never publishes Ready: " + reason);
        check(rejection != null && Path.GetFullPath(rejection.StageDirectory).StartsWith(fixture.OwnedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Rejected migration retains evidence only in its fixture-owned staging directory: " + reason);
        await fixture.VerifySourceUnchangedAsync(check);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

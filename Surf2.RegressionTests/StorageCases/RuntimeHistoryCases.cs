using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services;
using Surf2.Services.RelationalSnapshots;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

public static partial class StorageRegressionSuite
{
    private static async Task VerifyRuntimeHistoryAsync(SqlFixture fixture, RelationalRuntime runtime,
        RelationalCaptureResult first, RelationalCaptureResult second, List<JsonElement> firstRows,
        CaptureLimits limits, Action<bool, string> check)
    {
        var service = new RelationalSnapshotHistoryService(runtime, new() { Capture = limits });
        var histories = await service.ListHistoriesAsync(first.Snapshot.SnapshotKey, 1);
        var versions = await service.ListVersionsAsync(first.Snapshot.SnapshotKey, histories.Items.Single().HistoryKey, 1);
        check(versions.Items.Count == 1 && versions.Next != null,
            "Runtime history browser reads selected history versions with an explicit one-row page/cursor");
        var changes = await service.ListChangesAsync(first.Snapshot.SnapshotKey, second.VersionKey, 1);
        check(changes.Items.Count == 1 && changes.Next != null, "Runtime history change catalogue obeys selected-version page bounds");
        var previous = await service.ReadPreviousPayloadAsync(first.Snapshot.SnapshotKey, changes.Items[0]);
        check(previous.Text!.Contains("first captured definition", StringComparison.Ordinal),
            "Runtime history reads one previous change payload rather than a snapshot library");
        Directory.CreateDirectory(fixture.OwnedDirectory);
        string codeFile = Path.Combine(fixture.OwnedDirectory, "selected-history.sql");
        string zipFile = Path.Combine(fixture.OwnedDirectory, "selected-history.zip");
        await using (var selected = await service.OpenVersionAsync(first.Snapshot.SnapshotKey, first.VersionKey))
        {
            var objects = await selected.ReadPageAsync(HistoricalCollection.Objects, 1);
            var code = objects.Items.Single();
            var text = await service.ReadPreviewAsync(selected, code);
            check(text.Text!.Contains('\uD800'), "Selected history preview retains an unmatched raw UTF-16 code unit in its definition");
            var target = await service.ResourceTargetAsync(selected, code);
            check(target.Epoch == runtime.Session.Epoch && target.SnapshotKey == first.Snapshot.SnapshotKey &&
                target.VersionKey == first.VersionKey && target.HistoricalEntryKey == code.EntryKey && target.RevisionKey == code.RevisionKey,
                "History open/compare target pins its epoch, selected version, entry and immutable revision");
            var collection = service.VersionTarget(first.Snapshot, first.VersionKey, "First selected version");
            check(collection.Resource.IsCollection && collection.VersionKey == first.VersionKey && collection.SnapshotKey == first.Snapshot.SnapshotKey,
                "Whole-version comparison uses a typed selected collection locator, not reconstructed graphs");
            var data = (await selected.ReadPageAsync(HistoricalCollection.TableDataSets, 1)).Items.Single();
            check((await service.ReadPreviewAsync(selected, data)).CapturedData!.Summary.ActualRowCount == 3,
                "Historical data preview is a selected lazy descriptor, not resident captured rows");
            await service.ExportResourceAsync(selected, code, codeFile);
            check(RuntimeHistoryRawText(await File.ReadAllBytesAsync(codeFile)) == text.Text,
                "Selected SQL export writes a BOM and raw UTF-16 chunks without replacing unmatched code units");
            await File.WriteAllTextAsync(codeFile, "existing-destination-marker");
            var tiny = new RelationalSnapshotHistoryService(runtime, new() { MaximumDefinitionBytes = 2, Capture = limits });
            await ThrowsAsync<SnapshotReadLimitException>(() => tiny.ExportResourceAsync(selected, code, codeFile), check,
                "History export rejects a selected over-budget definition before destination publication");
            check(await File.ReadAllTextAsync(codeFile) == "existing-destination-marker",
                "Failed selected resource export preserves the existing destination file");
            await ThrowsAsync<ArgumentException>(() => service.ReadPreviewAsync(selected, code with { RevisionKey = long.MaxValue }), check,
                "Historical preview refuses a forged selected entry/revision locator");
        }
        await service.ExportVersionAsync(first.Snapshot.SnapshotKey, first.VersionKey, zipFile);
        using (var zip = ZipFile.OpenRead(zipFile))
        {
            check(zip.Entries.Count == 8 && zip.Entries.All(e => e.FullName.StartsWith("resources/", StringComparison.Ordinal) &&
                !e.FullName.Contains("..", StringComparison.Ordinal) && !e.FullName.Contains('\\')),
                "Selected version ZIP streams four resources with generated nontraversing paths and separate bounded metadata");
            using var raw = new MemoryStream();
            using var codeStream = zip.GetEntry("resources/00000000.sql")!.Open(); await codeStream.CopyToAsync(raw);
            check(RuntimeHistoryRawText(raw.ToArray()).Contains('\uD800'), "Selected version archive retains raw UTF-16 definition code units");
            var csv = zip.Entries.Single(e => e.FullName.EndsWith(".csv", StringComparison.Ordinal));
            using var reader = new StreamReader(csv.Open(), Encoding.UTF8);
            string data = await reader.ReadToEndAsync();
            check(data.Contains("1.2300", StringComparison.Ordinal) && data.Contains("line one\r\nline two", StringComparison.Ordinal),
                "Selected version CSV export streams the first version's data rather than the current replacement");
        }
        await File.WriteAllTextAsync(zipFile, "existing-archive-marker");
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new RuntimeCaptureProgress(p => { if (p.Phase == "Exporting selected version") cancel.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => service.ExportVersionAsync(first.Snapshot.SnapshotKey,
                first.VersionKey, zipFile, progress, cancel.Token), check, "Mid-stream selected version export cancellation does not publish a partial archive");
        }
        check(await File.ReadAllTextAsync(zipFile) == "existing-archive-marker" &&
            Directory.GetFiles(fixture.OwnedDirectory, ".surf2-history-*.pending").Length == 0,
            "Cancelled version export preserves the destination and removes only its generated pending file");

        var expected = new SnapshotRuntimeToken(runtime.Session.Epoch, second.Snapshot.SnapshotKey, second.Snapshot.RowVersion);
        var restored = await service.RestoreVersionAsync(expected, first.VersionKey, "Restored first capture");
        var oldData = (await runtime.Snapshots.ListResourcesAsync(restored.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Single();
        var restoredRows = await RuntimeCaptureRowsAsync(runtime, limits, oldData.RevisionKey, check);
        check(restoredRows.Select(r => r.GetRawText()).SequenceEqual(firstRows.Select(r => r.GetRawText())) &&
            restored.VersionKey != first.VersionKey && restored.VersionKey != second.VersionKey,
            "Whole-version restore publishes a new version while reusing the selected immutable lossless captured dataset");
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.RestoreVersionAsync(expected, first.VersionKey), check,
            "History restore refuses a stale expected snapshot token");
        string beforeCancel = await RuntimeCapturePublishedStateAsync(fixture);
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new RuntimeCaptureProgress(p => { if (p.Phase == "Publishing") cancel.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => service.RestoreVersionAsync(
                new(runtime.Session.Epoch, restored.Snapshot.SnapshotKey, restored.Snapshot.RowVersion), second.VersionKey,
                progress: progress, ct: cancel.Token), check, "Staged history restore cancellation leaves the published head unchanged");
        }
        check(await RuntimeCapturePublishedStateAsync(fixture) == beforeCancel, "Cancelled history restore publishes no head or version changes");
        var procedure = (await runtime.Snapshots.ListResourcesAsync(restored.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items.Single();
        var resourceRestore = await service.RestoreResourceAsync(new(runtime.Session.Epoch, restored.Snapshot.SnapshotKey,
            restored.Snapshot.RowVersion), second.VersionKey, procedure.ResourceKey);
        var currentObjects = await runtime.Snapshots.ListObjectsAsync(restored.Snapshot.SnapshotKey);
        check(currentObjects.Items.Select(o => o.Resource.ObjectName).SequenceEqual(new[] { "fixture_v", "fixture_p" }) &&
            (await runtime.Snapshots.ReadObjectAsync(currentObjects.Items.Last().Resource.RevisionKey)).Definition.Contains("replacement captured definition", StringComparison.Ordinal),
            "Selected object restore uses remove-all plus append ordering and leaves unrelated objects in place");
        await using (var prior = await service.OpenVersionAsync(restored.Snapshot.SnapshotKey, restored.VersionKey))
        {
            var priorObjects = await prior.ReadPageAsync(HistoricalCollection.Objects, 10);
            check(priorObjects.Items.Select(o => o.Name).SequenceEqual(new[] { "fixture_p", "fixture_v" }),
                "Resource restore reverse history preserves the prior object collection order");
        }
        var metadata = (await runtime.Snapshots.ListResourcesAsync(restored.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableMetadata)).Items.Single();
        var metadataRestore = await service.RestoreResourceAsync(new(runtime.Session.Epoch, resourceRestore.Snapshot.SnapshotKey,
            resourceRestore.Snapshot.RowVersion), first.VersionKey, metadata.ResourceKey);
        check((await runtime.Snapshots.ListResourcesAsync(restored.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Count == 0 &&
            (await runtime.Snapshots.ListFullDataSelectionsAsync(restored.Snapshot.SnapshotKey)).Items.Count == 0,
            "Selected metadata restore preserves legacy graph-wide removal of companion data and full-data selections");
        var dataRestore = await service.RestoreResourceAsync(new(runtime.Session.Epoch, metadataRestore.Snapshot.SnapshotKey,
            metadataRestore.Snapshot.RowVersion), second.VersionKey, oldData.ResourceKey);
        var tableAfterData = (await runtime.Snapshots.ListTablesAsync(restored.Snapshot.SnapshotKey)).Items.Single();
        var currentData = (await runtime.Snapshots.ListResourcesAsync(restored.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Single();
        var replacementRows = await RuntimeCaptureRowsAsync(runtime, limits, currentData.RevisionKey, check);
        check(tableAfterData.HasFullData && tableAfterData.FullDataRowCount == 3 && replacementRows.All(r =>
            r.GetProperty("FixtureDecimal").GetRawText() == "9.5000") && dataRestore.Snapshot.CurrentVersionKey == dataRestore.VersionKey,
            "Selected data restore updates only its table capture fields and publishes the chosen replacement row stream");

        var scope = Required(await runtime.StateStore.ReadScopeAsync(first.UpdatedScopeToken.Key), "runtime history copy scope");
        var copied = await service.RestoreVersionAsCopyAsync(first.Snapshot.SnapshotKey, first.VersionKey,
            scope.Value, scope.Token, "Copied first capture");
        var copiedData = (await runtime.Snapshots.ListResourcesAsync(copied.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Single();
        var copiedRows = await RuntimeCaptureRowsAsync(runtime, limits, copiedData.RevisionKey, check);
        var sourceDescriptor = Required(await runtime.CapturedData.GetForRevisionAsync(oldData.RevisionKey), "source history dataset");
        var copiedDescriptor = Required(await runtime.CapturedData.GetForRevisionAsync(copiedData.RevisionKey), "copied history dataset");
        check(copied.Snapshot.SnapshotKey != first.Snapshot.SnapshotKey && copied.CapturedRows == 3 &&
            copiedRows.Select(r => r.GetRawText()).SequenceEqual(firstRows.Select(r => r.GetRawText())) &&
            copiedDescriptor.Summary.DataSetKey != sourceDescriptor.Summary.DataSetKey && copiedData.RevisionKey != oldData.RevisionKey,
            "Whole-version copy streams lossless rows into independent destination-owned revisions and datasets");
        var copiedScope = Required(await runtime.StateStore.ReadScopeAsync(scope.Token.Key), "published history copy membership");
        check(copiedScope.Value.Resources.Any(r => r.Kind == ResourceKind.DatabaseSnapshot && r.Path == copied.Snapshot.SnapshotId) &&
            copiedScope.Token.Version.AsSpan().SequenceEqual(copied.UpdatedScopeToken.Version),
            "Whole-version copy atomically publishes a fresh scope member and returns its exact scope token");
        var copiedProcedure = (await runtime.Snapshots.ListResourcesAsync(copied.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.StoredProcedure)).Items.Single();
        check((await runtime.Snapshots.ReadObjectAsync(copiedProcedure.RevisionKey)).Definition.Contains('\uD800'),
            "Whole-version copy retains a selected raw UTF-16 definition without sharing the old resource owner");
        string currentFile = Path.Combine(fixture.OwnedDirectory, "current-copy.zip");
        await service.ExportCurrentAsync(new(runtime.Session.Epoch, copied.Snapshot.SnapshotKey, copied.Snapshot.RowVersion), currentFile);
        using (var archive = ZipFile.OpenRead(currentFile))
        {
            check(archive.Entries.Count == 8 && archive.Entries.All(e => e.FullName.StartsWith("resources/", StringComparison.Ordinal)),
                "Current-head export streams selected published resource handles without requiring legacy history graphs");
            using var raw = new MemoryStream(); using var code = archive.GetEntry("resources/00000000.sql")!.Open(); await code.CopyToAsync(raw);
            check(RuntimeHistoryRawText(raw.ToArray()).Contains('\uD800'), "Current-head export retains raw UTF-16 definition code units");
        }
        const string edited = "SELECT N'edited copy'; /* raw \uD800 fixture */";
        var copiedObject = await service.RestoreResourceAsCopyAsync(new(runtime.Session.Epoch, copied.Snapshot.SnapshotKey,
            copied.Snapshot.RowVersion), first.Snapshot.SnapshotKey, first.VersionKey, procedure.ResourceKey, "dbo", "copied_fixture_p", edited);
        var copiedObjects = await runtime.Snapshots.ListObjectsAsync(copied.Snapshot.SnapshotKey);
        check(copiedObjects.Items.Select(o => o.Resource.ObjectName).SequenceEqual(new[] { "fixture_p", "fixture_v", "copied_fixture_p" }) &&
            (await runtime.Snapshots.ReadObjectAsync(copiedObjects.Items.Last().Resource.RevisionKey)).Definition == edited,
            "Selected object copy preserves siblings and appends its renamed, explicitly edited raw definition");
        await File.WriteAllTextAsync(currentFile, "existing-current-export-marker");
        await ThrowsAsync<SnapshotConcurrencyException>(() => service.ExportCurrentAsync(new(runtime.Session.Epoch,
            copied.Snapshot.SnapshotKey, copied.Snapshot.RowVersion), currentFile), check,
            "Current-head export refuses a stale expected snapshot token");
        check(await File.ReadAllTextAsync(currentFile) == "existing-current-export-marker",
            "Rejected current-head export leaves an existing destination unchanged");
        var copiedReplacement = await service.RestoreResourceAsCopyAsync(new(runtime.Session.Epoch, copiedObject.Snapshot.SnapshotKey,
            copiedObject.Snapshot.RowVersion), first.Snapshot.SnapshotKey, second.VersionKey, oldData.ResourceKey, "app", "Surf2Documents");
        var replacementData = (await runtime.Snapshots.ListResourcesAsync(copied.Snapshot.SnapshotKey, DatabaseVersionedResourceKind.TableData)).Items.Single();
        check((await RuntimeCaptureRowsAsync(runtime, limits, replacementData.RevisionKey, check)).Select(r => r.GetRawText())
            .SequenceEqual(replacementRows.Select(r => r.GetRawText())) &&
            (await runtime.Snapshots.ListTablesAsync(copied.Snapshot.SnapshotKey)).Items.Single().HasFullData,
            "Selected dataset copy streams the chosen historical rows and updates only destination table capture fields");
        check((await runtime.Snapshots.GetSnapshotAsync(first.Snapshot.SnapshotKey))!.RowVersion.AsSpan()
            .SequenceEqual(dataRestore.Snapshot.RowVersion) && copiedReplacement.Snapshot.SnapshotKey == copied.Snapshot.SnapshotKey,
            "Historical copy operations leave the source snapshot's published head untouched");
        string beforeCopyCancel = await RuntimeCapturePublishedStateAsync(fixture);
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new RuntimeCaptureProgress(p => { if (p.Phase == "Copying captured rows") cancel.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => service.RestoreVersionAsCopyAsync(first.Snapshot.SnapshotKey,
                first.VersionKey, copiedScope.Value, copiedScope.Token, "Cancelled version copy", progress, cancel.Token), check,
                "Mid-stream historical copy cancellation leaves the staged snapshot unpublished");
        }
        check(await RuntimeCapturePublishedStateAsync(fixture) == beforeCopyCancel,
            "Cancelled whole-version copy changes neither published heads, history versions nor scope membership");
    }
    private static string RuntimeHistoryRawText(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xfe || bytes.Length % 2 != 0)
            throw new InvalidDataException("Expected raw UTF-16LE history text with BOM.");
        var chars = new char[(bytes.Length - 2) / 2];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 + i * 2, 2));
        return new(chars);
    }
}

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Surf2.Services.RelationalDocuments;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalSnapshots;

public sealed partial class RelationalSnapshotHistoryService
{
    public async Task ExportCurrentAsync(SnapshotRuntimeToken expected, string destinationPath,
        IProgress<RelationalCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        if (expected.Epoch != _runtime.Session.Epoch) throw new ArgumentException("Export selection belongs to another epoch.");
        var header = await _runtime.Snapshots.GetSnapshotAsync(expected.SnapshotKey, ct).ConfigureAwait(false) ?? throw new SnapshotConcurrencyException();
        if (!header.RowVersion.AsSpan().SequenceEqual(expected.Version)) throw new SnapshotConcurrencyException();
        var budget = new SnapshotOperationBudget(_limits);
        await AtomicExportAsync(destinationPath, async output =>
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true); long ordinal = 0;
            await foreach (var resource in SnapshotStaging.Pages(c => _runtime.Snapshots.ListResourcesAsync(expected.SnapshotKey,
                pageSize: 128, cursor: c, ct: ct), ct))
            {
                budget.Add(resource.SchemaName, resource.ObjectName);
                bool data = resource.Kind == Surf2.Models.DatabaseVersionedResourceKind.TableData;
                string stem = "resources/" + (ordinal++).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                await using (var stream = zip.CreateEntry(stem + (data ? ".csv" : ".sql")).Open())
                {
                    if (data)
                    {
                        var descriptor = await _capture.GetForRevisionAsync(resource.RevisionKey, ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Selected current dataset is not Ready.");
                        await _capture.WriteDelimitedAsync(descriptor.Summary.DataSetKey, stream, cancellationToken: ct).ConfigureAwait(false);
                    }
                    else
                    {
                        string text = resource.Kind == Surf2.Models.DatabaseVersionedResourceKind.TableMetadata
                            ? RelationalDocumentService.RenderTable(await _runtime.Snapshots.ReadTableMetadataAsync(resource.RevisionKey, ct).ConfigureAwait(false), _limits.MaximumDefinitionBytes)
                            : (await _selectedReads.ReadObjectAsync(resource.RevisionKey, ct).ConfigureAwait(false)).Definition;
                        await WriteRawUtf16Async(stream, text, ct).ConfigureAwait(false);
                    }
                }
                await using (var stream = zip.CreateEntry(stem + ".json").Open())
                    await JsonSerializer.SerializeAsync(stream, new { resource.SnapshotKey, VersionKey=header.CurrentVersionKey,
                        resource.ResourceKey, resource.RevisionKey, resource.Kind, resource.SortOrdinal,
                        SchemaUtf16Le=RawName(resource.SchemaName), NameUtf16Le=RawName(resource.ObjectName),
                        TextEncoding=data?"utf-8":"utf-16le-raw" }, cancellationToken: ct).ConfigureAwait(false);
                progress?.Report(new("Exporting current snapshot", budget.Units, 0));
            }
            var current = await _runtime.Snapshots.GetSnapshotAsync(expected.SnapshotKey, ct).ConfigureAwait(false) ?? throw new SnapshotConcurrencyException();
            if (!current.RowVersion.AsSpan().SequenceEqual(expected.Version)) throw new SnapshotConcurrencyException();
        }, ct).ConfigureAwait(false);
    }

    public async Task ExportResourceAsync(HistoricalSnapshotContext version, HistoricalSnapshotEntry selected,
        string destinationPath, CancellationToken ct = default)
    {
        RequireContext(version);
        selected = await SelectedEntryAsync(version, selected, ct).ConfigureAwait(false);
        await AtomicExportAsync(destinationPath, stream => WriteResourceAsync(version, selected, stream, ct), ct).ConfigureAwait(false);
    }

    public async Task ExportVersionAsync(long snapshotKey, long versionKey, string destinationPath,
        IProgress<RelationalCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        await using var version = await OpenVersionAsync(snapshotKey, versionKey, ct).ConfigureAwait(false);
        var budget = new SnapshotOperationBudget(_limits);
        await AtomicExportAsync(destinationPath, async output =>
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            long ordinal = 0;
            foreach (var collection in new[] { HistoricalCollection.Objects, HistoricalCollection.Tables, HistoricalCollection.TableDataSets })
                await foreach (var entry in version.StreamAsync(collection, ct).ConfigureAwait(false))
                {
                    budget.Add(entry.SchemaName, entry.Name);
                    string stem = "resources/" + (ordinal++).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                    var body = zip.CreateEntry(stem + (collection == HistoricalCollection.TableDataSets ? ".csv" : ".sql"));
                    await using (var stream = body.Open()) await WriteResourceAsync(version, entry, stream, ct).ConfigureAwait(false);
                    var metadata = zip.CreateEntry(stem + ".json");
                    await using (var stream = metadata.Open())
                    {
                        // Raw UTF-16 labels remain lossless even for legacy unmatched code units. Paths use generated ordinals only.
                        await JsonSerializer.SerializeAsync(stream, new
                        {
                            SnapshotKey = snapshotKey, VersionKey = versionKey, entry.Collection, entry.ResourceKey, entry.RevisionKey,
                            entry.EntryKey, entry.SortOrdinal, SchemaUtf16Le = RawName(entry.SchemaName), NameUtf16Le = RawName(entry.Name),
                            TextEncoding = collection == HistoricalCollection.TableDataSets ? "utf-8" : "utf-16le-raw"
                        }, cancellationToken: ct).ConfigureAwait(false);
                    }
                    progress?.Report(new("Exporting selected version", budget.Units, 0));
                }
            ct.ThrowIfCancellationRequested();
        }, ct).ConfigureAwait(false);
    }
    private async Task WriteResourceAsync(HistoricalSnapshotContext version, HistoricalSnapshotEntry entry, Stream output, CancellationToken ct)
    {
        if (entry.Collection == HistoricalCollection.TableDataSets)
        {
            var data = await _capture.GetForRevisionAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Selected historical dataset is not Ready.");
            await _capture.WriteDelimitedAsync(data.Summary.DataSetKey, output, cancellationToken: ct).ConfigureAwait(false);
            return;
        }
        string text;
        if (entry.Collection == HistoricalCollection.Tables)
            text = RelationalDocumentService.RenderTable(await version.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false), _limits.MaximumDefinitionBytes);
        else if (entry.Collection == HistoricalCollection.Objects)
        {
            text = (await _selectedReads.ReadObjectAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)).Definition;
            if (text.Length * 2L > _limits.MaximumDefinitionBytes) throw new InvalidOperationException("Selected definition exceeds its export budget.");
        }
        else throw new ArgumentException("Select a historical resource.");
        await WriteRawUtf16Async(output, text, ct).ConfigureAwait(false);
    }
    private string RawName(string value)
    {
        if (value.Length * 2L > _limits.MaximumDefinitionBytes) throw new InvalidOperationException("Selected export label exceeds its byte budget.");
        var bytes = new byte[checked(value.Length * 2)];
        for (int i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), value[i]);
        return Convert.ToBase64String(bytes);
    }
    private static async Task WriteRawUtf16Async(Stream stream, string value, CancellationToken ct)
    {
        await stream.WriteAsync(new byte[] { 0xff, 0xfe }, ct).ConfigureAwait(false);
        var bytes = new byte[8192];
        for (int start = 0; start < value.Length; start += bytes.Length / 2)
        {
            int length = Math.Min(bytes.Length / 2, value.Length - start);
            for (int i = 0; i < length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), value[start + i]);
            await stream.WriteAsync(bytes.AsMemory(0, length * 2), ct).ConfigureAwait(false);
        }
    }
    private static async Task AtomicExportAsync(string destination, Func<Stream, Task> write, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Export requires an explicit absolute destination path.");
        string path = Path.GetFullPath(destination), directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        string pending = Path.Combine(directory, ".surf2-history-" + Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            { await write(output).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); }
            ct.ThrowIfCancellationRequested(); File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}

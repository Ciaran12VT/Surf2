using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Surf2.Models;
using Surf2.Services.RelationalComparison;
using Surf2.Services.RelationalDocuments;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Services.RelationalSnapshots;

public sealed record RelationalHistoryFileSummary(SnapshotVersionSummary Version, long ResourceKey,
    string OriginalResourceKey, DatabaseVersionedResourceKind Kind, string DisplayName, string RelativePath,
    DatabaseSnapshotResourceChangeKind ChangeKind, HistoricalSnapshotEntry? Entry,
    HistoricalSnapshotEntry? PreviousEntry, long? PreviousVersionKey)
{
    internal Guid Epoch { get; init; }
    internal byte[] HeadVersion { get; init; } = [];
    internal SnapshotResourceSummary? IssuedResource { get; init; }
    internal long? IssuedVersionKey { get; init; }
    internal long? IssuedEntryKey { get; init; }
    internal HistoryLineCountKey? CountCacheKey { get; init; }
}
public sealed record RelationalHistoryFileLineCount(RelationalHistoryFileSummary File, int Added, int Removed, string? Error = null);

public sealed partial class RelationalSnapshotHistoryService
{
    /// <summary>The master's first history, newest first, with a read-only initial row when no versions were saved.</summary>
    public async Task<IReadOnlyList<SnapshotVersionSummary>> GetDisplayedVersionsAsync(SnapshotSummary snapshot, CancellationToken ct = default)
    {
        var histories = await ListHistoriesAsync(snapshot.SnapshotKey, 1, ct: ct).ConfigureAwait(false);
        var versions = new List<SnapshotVersionSummary>(); var budget = new SnapshotOperationBudget(_limits);
        if (histories.Items.FirstOrDefault() is { } history)
            await foreach (var version in SnapshotStaging.Pages(cursor => ListVersionsAsync(snapshot.SnapshotKey, history.HistoryKey, 128, cursor, ct), ct))
            { budget.Add(version.VersionId, version.VersionName); versions.Add(version); }
        if (versions.Count == 0)
        {
            await using var current = await OpenVersionAsync(snapshot.SnapshotKey, 0, ct).ConfigureAwait(false);
            versions.Add(new(0, snapshot.SnapshotKey, string.Empty, "V1", 1, snapshot.ImportedAtUtc, true, 0, 0));
        }
        var head = await _runtime.Snapshots.GetSnapshotAsync(snapshot.SnapshotKey, ct).ConfigureAwait(false);
        if (head == null || !head.RowVersion.AsSpan().SequenceEqual(snapshot.RowVersion)) throw new SnapshotConcurrencyException();
        return versions.OrderByDescending(version => version.VersionNumber).ToArray();
    }

    /// <summary>Identity metadata only. Line counts and selected content have separate, bounded reads.</summary>
    public async Task<IReadOnlyList<RelationalHistoryFileSummary>> ListFileRowsAsync(SnapshotVersionSummary version,
        CancellationToken ct = default)
    {
        await using var current = await OpenVersionAsync(version.SnapshotKey, version.VersionKey, ct).ConfigureAwait(false);
        if (version.VersionKey == 0 && (!version.IsInitial || version.VersionNumber != 1)) throw new SnapshotConcurrencyException();
        long? previousKey = version.VersionKey == 0 ? null : await PreviousFileVersionAsync(version, ct).ConfigureAwait(false);
        var budget = new SnapshotOperationBudget(_limits);
        var currentEntries = await FileEntriesAsync(current, budget, ct).ConfigureAwait(false);
        await using var previous = !version.IsInitial && previousKey.HasValue
            ? await OpenVersionAsync(version.SnapshotKey, previousKey.Value, ct).ConfigureAwait(false) : null;
        Dictionary<string, HistoricalSnapshotEntry> previousEntries = new(StringComparer.OrdinalIgnoreCase);
        if (previous != null)
        {
            if (!previous.Header.RowVersion.AsSpan().SequenceEqual(current.Header.RowVersion)) throw new SnapshotConcurrencyException();
            previousEntries = await FileEntriesAsync(previous, budget, ct).ConfigureAwait(false);
        }
        var rows = new List<RelationalHistoryFileSummary>();
        if (version.IsInitial)
        {
            foreach (var (key, entry) in currentEntries)
            {
                var kind = EntryKind(entry);
                string name = SqlName.FormatPlainMultipartName(entry.SchemaName, entry.Name);
                Add(new(version, entry.ResourceKey!.Value, key, kind, name, HistoryFilePath(kind, name),
                    DatabaseSnapshotResourceChangeKind.Added, entry, null, null));
            }
        }
        else
            await foreach (var change in SnapshotStaging.Pages(cursor => ListChangesAsync(version.SnapshotKey, version.VersionKey, 128, cursor, ct), ct))
            {
                currentEntries.TryGetValue(change.OriginalResourceKey, out var entry);
                previousEntries.TryGetValue(change.OriginalResourceKey, out var previousEntry);
                Add(new(version, change.ResourceKey, change.OriginalResourceKey, change.Kind, change.DisplayName, change.RelativePath,
                    change.ChangeKind, entry, previousEntry, previousKey));
            }
        var summaries = new Dictionary<long, SnapshotResourceSummary>();
        foreach (var batch in rows.Where(row => row.Entry != null).Select(row => row.Entry!).DistinctBy(entry => entry.EntryKey).Chunk(128))
            foreach (var pair in await current.ResourceSummariesAsync(batch, ct).ConfigureAwait(false)) summaries.Add(pair.Key, pair.Value);
        var head = await _runtime.Snapshots.GetSnapshotAsync(version.SnapshotKey, ct).ConfigureAwait(false);
        if (head == null || !head.RowVersion.AsSpan().SequenceEqual(current.Header.RowVersion)) throw new SnapshotConcurrencyException();
        return rows.Select(row => row with { IssuedResource = row.Entry == null ? null : summaries.GetValueOrDefault(row.Entry.EntryKey) })
            .OrderBy(row => DatabaseHistoryDisplay.GetResourceTypeSortOrder(row.Kind))
            .ThenBy(row => DatabaseHistoryDisplay.GetFileNameDisplay(row.DisplayName, row.RelativePath), StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(RelationalHistoryFileSummary row)
        {
            budget.Add(row.OriginalResourceKey, row.DisplayName, row.RelativePath);
            rows.Add(row with { Epoch = _runtime.Session.Epoch, HeadVersion = current.Header.RowVersion.ToArray(),
                IssuedVersionKey = version.VersionKey, IssuedEntryKey = row.Entry?.EntryKey,
                CountCacheKey = LineCountKey(row, current, previous, ct) });
        }
    }

    private static async Task<Dictionary<string, HistoricalSnapshotEntry>> FileEntriesAsync(HistoricalSnapshotContext context,
        SnapshotOperationBudget budget, CancellationToken ct)
    {
        var result = new Dictionary<string, HistoricalSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in new[] { HistoricalCollection.Objects, HistoricalCollection.Tables, HistoricalCollection.TableDataSets })
            await foreach (var entry in context.StreamAsync(collection, ct).ConfigureAwait(false))
            {
                if (collection == HistoricalCollection.Objects && SnapshotIdentity.ResourceKind(entry.ObjectKind ?? SqlDatabaseObjectKind.Unknown) == null) continue;
                string key = SnapshotIdentity.LegacyResourceKey(EntryKind(entry), entry.SchemaName, entry.Name);
                budget.Add(key, entry.SchemaName, entry.Name);
                if (!result.TryAdd(key, entry)) throw new InvalidOperationException("The historical version contains ambiguous duplicate file identities.");
            }
        return result;
    }

    private async Task<long?> PreviousFileVersionAsync(SnapshotVersionSummary selected, CancellationToken ct)
    {
        await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT v.VersionNumber,v.IsInitial,
                (SELECT TOP(1) p.VersionKey FROM surf.SnapshotVersion p
                 WHERE p.HistoryKey=v.HistoryKey AND p.VersionNumber<v.VersionNumber
                 ORDER BY p.VersionNumber DESC,p.SortOrdinal,p.VersionKey)
            FROM surf.SnapshotVersion v WHERE v.SnapshotKey=@Snapshot AND v.VersionKey=@Version;
            """, Key("@Snapshot", selected.SnapshotKey), Key("@Version", selected.VersionKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) != selected.VersionNumber || reader.GetBoolean(1) != selected.IsInitial)
            throw new SnapshotConcurrencyException();
        return reader.IsDBNull(2) ? null : reader.GetInt64(2);
    }

    public async Task<(int Added, int Removed)> CountFileLinesAsync(RelationalHistoryFileSummary file, CancellationToken ct = default)
    {
        await RequireFileHeadAsync(file, ct).ConfigureAwait(false);
        if (file.CountCacheKey is { } key)
        {
            var saved = await _historyCounts.ReadLinesAsync(file.Version, [key], ct).ConfigureAwait(false);
            if (saved.TryGetValue(key, out var cached))
            {
                await RequireFileHeadAsync(file, ct).ConfigureAwait(false);
                return cached;
            }
        }
        var counts = await CountFileLinesCoreAsync(file, null, ct).ConfigureAwait(false);
        await RequireFileHeadAsync(file, ct).ConfigureAwait(false);
        if (file.CountCacheKey is { } identity)
            await _historyCounts.WriteLinesAsync(file.Version, file.HeadVersion,
                [new(identity.ResourceKey, identity.Kind, identity.InputHash, counts.Added, counts.Removed)], ct).ConfigureAwait(false);
        return counts;
    }

    /// <summary>Count visible children of one version while reusing at most two metadata projections.</summary>
    public async IAsyncEnumerable<RelationalHistoryFileLineCount> CountFileLinesAsync(IReadOnlyList<RelationalHistoryFileSummary> files,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (files.Count > _limits.MaximumMetadataUnits || files.Any(file => file.Version.SnapshotKey != files[0].Version.SnapshotKey ||
            file.Version.VersionKey != files[0].Version.VersionKey)) throw new ArgumentException("Select file rows from one version.", nameof(files));
        var contexts = new Dictionary<long, HistoricalSnapshotContext>();
        try
        {
            if (files.Count == 0) yield break;
            var selected = files[0];
            if (files.Any(file => file.Epoch != selected.Epoch || !file.HeadVersion.AsSpan().SequenceEqual(selected.HeadVersion)))
                throw new SnapshotConcurrencyException();
            foreach (var candidates in files.Chunk(64))
            {
                await RequireFileHeadAsync(selected, ct).ConfigureAwait(false);
                var keys = candidates.Select(file => file.CountCacheKey).OfType<HistoryLineCountKey>().Distinct().ToArray();
                var cached = await _historyCounts.ReadLinesAsync(selected.Version, keys, ct).ConfigureAwait(false);
                bool IsCached(RelationalHistoryFileSummary file) => file.CountCacheKey is { } key && cached.ContainsKey(key);
                var revisions = candidates.Where(file => !IsCached(file)).SelectMany(ObjectRevisions).Distinct().ToArray();
                var identities = await _selectedReads.ReadObjectTextIdentitiesAsync(revisions, ct).ConfigureAwait(false);
                int offset = 0;
                while (offset < candidates.Length)
                {
                    var group = new List<RelationalHistoryFileSummary>(); var reads = new HashSet<long>(); long bytes = 0;
                    while (offset < candidates.Length)
                    {
                        var file = candidates[offset];
                        var needed = IsCached(file) || SameObjectText(file, identities) ? [] : ObjectRevisions(file).Distinct().ToArray();
                        bool oversized = needed.Any(key => identities[key].ByteCount > _limits.MaximumDefinitionBytes);
                        if (oversized) needed = [];
                        long additional = needed.Where(key => !reads.Contains(key)).Sum(key => identities[key].ByteCount);
                        if (group.Count != 0 && additional > RelationalSnapshotStore.MaximumDefinitionBatchBytes - bytes) break;
                        group.Add(file); offset++; bytes += additional; reads.UnionWith(needed);
                    }
                    var definitions = await _selectedReads.ReadObjectDefinitionsAsync(reads.ToArray(), ct).ConfigureAwait(false);
                    var completed = new List<RelationalHistoryFileLineCount>(group.Count);
                    var stored = new Dictionary<HistoryLineCountKey, StoredHistoryLineCount>();
                    foreach (var file in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (file.CountCacheKey is { } key && cached.TryGetValue(key, out var saved))
                        {
                            completed.Add(new(file, saved.Added, saved.Removed));
                            continue;
                        }
                        try
                        {
                            if (!SameObjectText(file, identities) && ObjectRevisions(file).Any(key => identities[key].ByteCount > _limits.MaximumDefinitionBytes))
                                throw new SnapshotReadLimitException(_limits.MaximumDefinitionBytes);
                            var counts = await CountFileLinesCoreAsync(file, contexts, ct, definitions, identities).ConfigureAwait(false);
                            completed.Add(new(file, counts.Added, counts.Removed));
                            if (file.CountCacheKey is { } identity)
                                stored.TryAdd(identity, new(identity.ResourceKey, identity.Kind, identity.InputHash, counts.Added, counts.Removed));
                        }
                        catch (Exception error) when (error is not SnapshotConcurrencyException and not RelationalSessionChangedException &&
                            error is InvalidOperationException or IOException)
                        { completed.Add(new(file, 0, 0, error.Message)); }
                    }
                    await RequireFileHeadAsync(selected, ct).ConfigureAwait(false);
                    if (stored.Count != 0)
                        await _historyCounts.WriteLinesAsync(selected.Version, selected.HeadVersion, stored.Values.ToArray(), ct).ConfigureAwait(false);
                    foreach (var result in completed) { ct.ThrowIfCancellationRequested(); yield return result; }
                }
            }
        }
        finally { foreach (var context in contexts.Values) await context.DisposeAsync().ConfigureAwait(false); }
    }

    private static IEnumerable<long> ObjectRevisions(RelationalHistoryFileSummary file)
    {
        if (file.Entry is { Collection: HistoricalCollection.Objects, RevisionKey: { } current }) yield return current;
        if (file.PreviousEntry is { Collection: HistoricalCollection.Objects, RevisionKey: { } previous }) yield return previous;
    }
    private static bool SameObjectText(RelationalHistoryFileSummary file, IReadOnlyDictionary<long, SnapshotObjectTextIdentity> identities) =>
        file.Entry is { Collection: HistoricalCollection.Objects, RevisionKey: { } current } &&
        file.PreviousEntry is { Collection: HistoricalCollection.Objects, RevisionKey: { } previous } &&
        identities[current].ContentKey == identities[previous].ContentKey;

    private async Task<(int Added, int Removed)> CountFileLinesCoreAsync(RelationalHistoryFileSummary file,
        Dictionary<long, HistoricalSnapshotContext>? contexts, CancellationToken ct,
        IReadOnlyDictionary<long, string>? definitions = null,
        IReadOnlyDictionary<long, SnapshotObjectTextIdentity>? identities = null)
    {
        if (identities != null && SameObjectText(file, identities)) return (0, 0);
        string oldText = file.PreviousEntry == null || file.PreviousVersionKey == null ? string.Empty :
            await ReadForCountsAsync(file.PreviousVersionKey.Value, file.PreviousEntry).ConfigureAwait(false);
        string newText = file.Entry == null ? string.Empty :
            await ReadForCountsAsync(file.Version.VersionKey, file.Entry).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        int oldLines = HistoryLineCount(oldText), newLines = HistoryLineCount(newText);
        if (oldLines > 100_000 || newLines > 100_000) throw new InvalidOperationException("This file exceeds the 100,000-line history count limit.");
        (int Added, int Removed) result;
        if (oldText.Length == 0) result = (newLines, 0);
        else if (newText.Length == 0) result = (0, oldLines);
        else
        {
            // Use the same DiffPlex defaults as the master history summary.
            var diff = InlineDiffBuilder.Diff(oldText, newText);
            result = (diff.Lines.Count(line => line.Type == ChangeType.Inserted), diff.Lines.Count(line => line.Type == ChangeType.Deleted));
        }
        return result;

        async Task<string> ReadForCountsAsync(long versionKey, HistoricalSnapshotEntry entry)
        {
            if (definitions != null && entry.Collection == HistoricalCollection.Objects) return definitions[entry.RevisionKey!.Value];
            if (contexts == null || entry.Collection == HistoricalCollection.Objects)
                return await ReadHistoryEntryTextAsync(file.Version.SnapshotKey, versionKey, entry, ct).ConfigureAwait(false);
            if (!contexts.TryGetValue(versionKey, out var context))
            {
                context = await OpenVersionAsync(file.Version.SnapshotKey, versionKey, ct).ConfigureAwait(false);
                contexts.Add(versionKey, context);
            }
            if (!context.Header.RowVersion.AsSpan().SequenceEqual(file.HeadVersion)) throw new SnapshotConcurrencyException();
            return await ReadHistoryStructuredTextAsync(context, entry, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> ReadFileTextAsync(RelationalHistoryFileSummary file, CancellationToken ct = default)
    {
        await RequireFileHeadAsync(file, ct).ConfigureAwait(false);
        string text = file.Entry == null ? file.RelativePath + " does not exist in " + file.Version.VersionName + "." :
            await ReadHistoryEntryTextAsync(file.Version.SnapshotKey, file.Version.VersionKey, file.Entry, ct).ConfigureAwait(false);
        await RequireFileHeadAsync(file, ct).ConfigureAwait(false);
        return text;
    }

    public async Task<string> ReadFileTextAsync(HistoricalSnapshotContext context, HistoricalSnapshotEntry entry,
        CancellationToken ct = default)
    {
        RequireContext(context);
        entry = await SelectedEntryAsync(context, entry, ct).ConfigureAwait(false);
        string text = entry.Collection == HistoricalCollection.Objects
            ? (await _selectedReads.ReadObjectAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)).Definition
            : await ReadHistoryStructuredTextAsync(context, entry, ct).ConfigureAwait(false);
        var head = await _runtime.Snapshots.GetSnapshotAsync(context.Header.SnapshotKey, ct).ConfigureAwait(false);
        if (head == null || !head.RowVersion.AsSpan().SequenceEqual(context.Header.RowVersion)) throw new SnapshotConcurrencyException();
        return text;
    }

    public async Task<RelationalComparisonTarget> FileTargetAsync(RelationalHistoryFileSummary file, CancellationToken ct = default)
    {
        await RequireFileHeadAsync(file, ct).ConfigureAwait(false);
        if (file.Entry == null) throw new InvalidOperationException(file.RelativePath + " does not exist in " + file.Version.VersionName + ".");
        var resource = file.IssuedResource ?? throw new SnapshotConcurrencyException();
        if (file.Version.VersionKey != file.IssuedVersionKey || file.Entry.EntryKey != file.IssuedEntryKey ||
            resource.SnapshotKey != file.Version.SnapshotKey ||
            resource.ResourceKey != file.Entry.ResourceKey || resource.RevisionKey != file.Entry.RevisionKey ||
            resource.Kind != file.Kind || !StringComparer.Ordinal.Equals(resource.SchemaName, file.Entry.SchemaName) ||
            !StringComparer.Ordinal.Equals(resource.ObjectName, file.Entry.Name)) throw new SnapshotConcurrencyException();
        var result = RelationalComparisonService.FromResource(resource, file.Epoch, file.Version.VersionKey, file.Entry.EntryKey);
        return result with { Resource = result.Resource with {
            DisplayName = DatabaseHistoryDisplay.GetFileNameDisplay(file.DisplayName, file.RelativePath) + " (" + file.Version.VersionName + ")" } };
    }

    private async Task RequireFileHeadAsync(RelationalHistoryFileSummary file, CancellationToken ct)
    {
        if (file.Epoch != _runtime.Session.Epoch) throw new SnapshotConcurrencyException();
        var snapshot = await _runtime.Snapshots.GetSnapshotAsync(file.Version.SnapshotKey, ct).ConfigureAwait(false);
        if (snapshot == null || !snapshot.RowVersion.AsSpan().SequenceEqual(file.HeadVersion)) throw new SnapshotConcurrencyException();
    }

    private async Task<string> ReadHistoryEntryTextAsync(long snapshotKey, long versionKey, HistoricalSnapshotEntry entry, CancellationToken ct)
    {
        if (entry.Collection == HistoricalCollection.Objects)
            return (await _selectedReads.ReadObjectAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)).Definition;
        await using var context = await OpenVersionAsync(snapshotKey, versionKey, ct).ConfigureAwait(false);
        return await ReadHistoryStructuredTextAsync(context, entry, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadHistoryStructuredTextAsync(HistoricalSnapshotContext context, HistoricalSnapshotEntry entry, CancellationToken ct)
    {
        entry = await SelectedEntryAsync(context, entry, ct).ConfigureAwait(false);
        if (entry.Collection == HistoricalCollection.Tables)
            return RelationalDocumentService.RenderTable(await context.ReadTableMetadataAsync(entry, ct).ConfigureAwait(false), _limits.MaximumDefinitionBytes);
        if (entry.Collection != HistoricalCollection.TableDataSets) throw new ArgumentException("Select a historical file.");
        var data = await _capture.GetForRevisionAsync(entry.RevisionKey!.Value, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected historical data is not ready.");
        var headers = await HistoryDataHeadersAsync(context, entry, ct).ConfigureAwait(false);
        var text = new StringBuilder(); bool wroteHeader = false;
        await foreach (var row in _capture.StreamRowsAsync(data.Summary.DataSetKey, cancellationToken: ct).ConfigureAwait(false))
        {
            if (!wroteHeader)
            {
                // Master falls back only to the FIRST captured row's properties.
                if (headers.Count == 0 && row.Value.ValueKind == JsonValueKind.Object)
                    headers = row.Value.EnumerateObject().Select(property => property.Name).ToList();
                AppendRecord(headers); wroteHeader = true;
            }
            if (row.Value.ValueKind == JsonValueKind.Object) AppendRecord(headers.Select(header => CaptureDisplay.Cell(row.Value, header)));
        }
        if (!wroteHeader) AppendRecord(headers);
        return text.ToString();

        void AppendRecord(IEnumerable<string> values)
        {
            bool first = true;
            foreach (string value in values)
            {
                ct.ThrowIfCancellationRequested();
                if (!first) Append(","); first = false;
                Append(CaptureDisplay.EscapeDelimited(value, ','));
            }
            Append(Environment.NewLine);
        }
        void Append(string value)
        {
            if ((long)text.Length + value.Length > _limits.MaximumDefinitionBytes / 2)
                throw new InvalidOperationException("This history file exceeds the selected-text read limit.");
            text.Append(value);
        }
    }

    private async Task<List<string>> HistoryDataHeadersAsync(HistoricalSnapshotContext context, HistoricalSnapshotEntry data, CancellationToken ct)
    {
        var children = new List<long>(); var budget = new SnapshotOperationBudget(_limits);
        foreach (var column in await context.FindEntriesAsync(HistoricalCollection.Columns, data.SchemaName, data.Name, ct).ConfigureAwait(false))
        { budget.Add(32); children.Add(column.ChildKey!.Value); }
        if (children.Count == 0) return [];
        var values = new Dictionary<long, (string Name, int Ordinal)>();
        await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
        foreach (long[] batch in children.Distinct().Chunk(128))
        {
            await using var command = Command(connection, null, """
                SELECT c.ColumnRevisionKey,DATALENGTH(c.ColumnName),c.ColumnName,c.SourceOrdinal
                FROM OPENJSON(@Keys) WITH (ColumnRevisionKey bigint '$') k
                JOIN surf.TableColumnRevision c ON c.ColumnRevisionKey=k.ColumnRevisionKey;
                """, Text("@Keys", JsonSerializer.Serialize(batch)));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long key = reader.GetInt64(0); budget.Add(reader.GetInt64(1) + 64);
                values.Add(key, (reader.GetString(2), reader.GetInt32(3)));
            }
        }
        if (children.Any(key => !values.ContainsKey(key))) throw new SnapshotConcurrencyException();
        return children.Select(key => values[key]).OrderBy(value => value.Ordinal).Select(value => value.Name).ToList();
    }

    private static int HistoryLineCount(string text)
    {
        if (text.Length == 0) return 0;
        int count = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\r') { count++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (text[i] == '\n') count++;
        return count;
    }

    private static string HistoryFilePath(DatabaseVersionedResourceKind kind, string name) => (kind switch
    {
        DatabaseVersionedResourceKind.StoredProcedure => "Stored Procedures", DatabaseVersionedResourceKind.Function => "Functions",
        DatabaseVersionedResourceKind.View => "Views", DatabaseVersionedResourceKind.Trigger => "Triggers",
        DatabaseVersionedResourceKind.TableMetadata => "Tables", _ => "Table Data"
    }) + "/" + name + (kind == DatabaseVersionedResourceKind.TableData ? ".csv" : ".txt");
}

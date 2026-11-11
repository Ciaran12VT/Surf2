using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Surf2.Models;
using Surf2.Services.RelationalDocuments;
using Surf2.Services.RelationalExplorer;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalComparison;

public sealed partial class RelationalComparisonService
{
    private const long SnapshotComparisonMetadataBytes = 32L * 1024 * 1024;
    private const int SnapshotComparisonMetadataRows = 100_000;
    private readonly object _comparisonCacheGate = new();
    private readonly Dictionary<long, string> _comparisonDefinitions = [];
    private readonly Dictionary<(long Content, ComparisonOptions Options), string> _comparisonDigests = [];
    private long _comparisonDefinitionBytes;
    private readonly Dictionary<(long DataSet, string Headers, ComparisonOptions Options), string> _comparisonDataDigests = [];
    private long _comparisonDataDigestBytes;

    private static long RetainedStrings(params string?[] values) => values.Sum(value => value == null ? 0 : 32L + value.Length * 2L);

    // Both sides are selected before reading bodies. The common case can compare
    // immutable content identities without downloading identical definitions.
    private async Task<ComparisonResultStore?> CompareSnapshotCollectionsAsync(RelationalComparisonTarget left,
        RelationalComparisonTarget right, ComparisonOptions options, CancellationToken ct)
    {
        RequireEpoch(left); RequireEpoch(right);
        SnapshotComparisonCatalogue? a = null, b = null;
        try
        {
            try
            {
                a = await SnapshotComparisonCatalogue.OpenAsync(this, left, ct).ConfigureAwait(false);
                b = await SnapshotComparisonCatalogue.OpenAsync(this, right, ct).ConfigureAwait(false);
            }
            catch (SnapshotComparisonMetadataLimitException) { return null; } // bounded streaming fallback
            var leftByPath = FirstPaths(a.Rows); var rightByPath = FirstPaths(b.Rows);
            var needed = new Dictionary<long, SnapshotObjectTextIdentity>();
            foreach (var row in a.Rows.Concat(b.Rows))
            {
                if (!row.IsObject || row.TextIdentity == null) continue;
                var otherRows = ReferenceEquals(row.Catalogue, a) ? rightByPath : leftByPath;
                if (!otherRows.TryGetValue(row.Path, out var other) || other.IsCollection) { row.Digest = "unmatched"; continue; }
                if (other.TextIdentity?.ContentKey == row.TextIdentity.ContentKey) { row.Digest = "same-content"; continue; }
                if (TryCachedDigest(row.TextIdentity.ContentKey, options, out var digest)) row.Digest = digest;
                else needed.TryAdd(row.Summary!.RevisionKey, row.TextIdentity);
            }
            var digests = await ReadComparisonDigestsAsync(needed.Values, options, ct).ConfigureAwait(false);
            foreach (var row in leftByPath.Values.Where(row => row.Target.Resource.IsTableData))
            {
                if (!rightByPath.TryGetValue(row.Path, out var other) || !other.Target.Resource.IsTableData) continue;
                var x = await row.Catalogue.PrepareTableDataAsync(row, ct).ConfigureAwait(false);
                var y = await other.Catalogue.PrepareTableDataAsync(other, ct).ConfigureAwait(false);
                if (x.Descriptor.Summary.DataSetKey == y.Descriptor.Summary.DataSetKey && x.HeaderIdentity == y.HeaderIdentity)
                    row.Digest = other.Digest = "same-data";
            }
            foreach (var row in a.Rows.Concat(b.Rows))
            {
                ct.ThrowIfCancellationRequested();
                if (row.IsObject)
                {
                    if (row.Digest == null) row.Digest = digests[row.TextIdentity!.ContentKey];
                    continue;
                }
                if (row.IsCollection) { row.Digest = ""; continue; }
                if (row.Digest != null) continue;
                var otherRows = ReferenceEquals(row.Catalogue, a) ? rightByPath : leftByPath;
                if (!otherRows.TryGetValue(row.Path, out var other) || other.IsCollection) { row.Digest = "unmatched"; continue; }
                row.Digest = await row.Catalogue.StructuredDigestAsync(row, options, ct).ConfigureAwait(false);
            }
            // A same-content shortcut applies to one matched path only. Duplicate
            // paths preserve the engine's first-entry-wins semantics.
            await a.ValidateAsync(ct).ConfigureAwait(false); await b.ValidateAsync(ct).ConfigureAwait(false);
            var result = await ComparisonCollectionEngine.BuildAsync(Entries(a), Entries(b), Limits, ct).ConfigureAwait(false);
            try { await a.ValidateAsync(ct).ConfigureAwait(false); await b.ValidateAsync(ct).ConfigureAwait(false); return result; }
            catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally
        {
            if (b != null) await b.DisposeAsync().ConfigureAwait(false);
            if (a != null) await a.DisposeAsync().ConfigureAwait(false);
        }

        static Dictionary<string, SnapshotComparisonRow> FirstPaths(IEnumerable<SnapshotComparisonRow> rows)
        {
            var result = new Dictionary<string, SnapshotComparisonRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows) result.TryAdd(row.Path, row);
            return result;
        }
        static async IAsyncEnumerable<ComparisonCollectionEntry> Entries(SnapshotComparisonCatalogue catalogue,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            foreach (var row in catalogue.Rows)
            { token.ThrowIfCancellationRequested(); yield return new(row.Path, row.IsCollection, row.Digest!, row.Target); }
            await Task.CompletedTask;
        }
    }

    private bool TryCachedDigest(long content, ComparisonOptions options, out string digest)
    { lock (_comparisonCacheGate) return _comparisonDigests.TryGetValue((content, options), out digest!); }

    private async Task<Dictionary<long, string>> ReadComparisonDigestsAsync(IEnumerable<SnapshotObjectTextIdentity> requested,
        ComparisonOptions options, CancellationToken ct)
    {
        var values = requested.GroupBy(value => value.ContentKey).Select(group => group.First()).ToArray();
        var result = new Dictionary<long, string>();
        var pending = new List<SnapshotObjectTextIdentity>(); long bytes = 0;
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            if (value.ByteCount > Limits.MaximumTextCharacters * 2L) throw new ComparisonLimitException("Selected code exceeds the comparison text limit.");
            string? cached;
            lock (_comparisonCacheGate) _comparisonDefinitions.TryGetValue(value.ContentKey, out cached);
            if (cached != null) { Add(value.ContentKey, ComparisonTextHash.Digest(cached, options)); continue; }
            if (pending.Count == RelationalSnapshotStore.MaximumObjectBatchSize ||
                bytes + value.ByteCount > RelationalSnapshotStore.MaximumDefinitionBatchBytes) await FlushAsync().ConfigureAwait(false);
            pending.Add(value); bytes += value.ByteCount;
        }
        if (pending.Count != 0) await FlushAsync().ConfigureAwait(false);
        return result;

        async Task FlushAsync()
        {
            var texts = await _runtime.Snapshots.ReadObjectDefinitionsAsync(pending.Select(value => value.RevisionKey).ToArray(), ct).ConfigureAwait(false);
            foreach (var value in pending)
            {
                string text = texts[value.RevisionKey];
                if (text.Length > Limits.MaximumTextCharacters) throw new ComparisonLimitException("Selected code exceeds the comparison text limit.");
                Add(value.ContentKey, ComparisonTextHash.Digest(text, options));
                long size = text.Length * 2L + 128;
                lock (_comparisonCacheGate)
                {
                    if (size <= 8L * 1024 * 1024 && !_comparisonDefinitions.ContainsKey(value.ContentKey))
                    {
                        if (size > 8L * 1024 * 1024 - _comparisonDefinitionBytes) { _comparisonDefinitions.Clear(); _comparisonDefinitionBytes = 0; }
                        _comparisonDefinitions.Add(value.ContentKey, text); _comparisonDefinitionBytes += size;
                    }
                }
            }
            pending.Clear(); bytes = 0;
        }
        void Add(long content, string digest)
        {
            result[content] = digest;
            lock (_comparisonCacheGate)
            {
                if (_comparisonDigests.Count >= 10_000) _comparisonDigests.Clear();
                _comparisonDigests[(content, options)] = digest;
            }
        }
    }

    private sealed class SnapshotComparisonMetadataLimitException : Exception { }
    private sealed record PreparedComparisonTableData(CaptureDataSetDescriptor Descriptor, string[] Headers, string HeaderIdentity);
    private sealed class SnapshotComparisonRow(SnapshotComparisonCatalogue catalogue, string path,
        RelationalComparisonTarget target, SnapshotResourceSummary? summary = null, HistoricalSnapshotEntry? historical = null)
    {
        public SnapshotComparisonCatalogue Catalogue { get; } = catalogue;
        public string Path { get; } = path;
        public RelationalComparisonTarget Target { get; } = target;
        public SnapshotResourceSummary? Summary { get; } = summary;
        public HistoricalSnapshotEntry? Historical { get; } = historical;
        public bool IsCollection => Summary == null;
        public bool IsObject => Summary?.Kind is DatabaseVersionedResourceKind.StoredProcedure or DatabaseVersionedResourceKind.View
            or DatabaseVersionedResourceKind.Function or DatabaseVersionedResourceKind.Trigger;
        public SnapshotObjectTextIdentity? TextIdentity { get; set; }
        public string? Digest { get; set; }
        public PreparedComparisonTableData? TableData { get; set; }
    }

    private sealed class SnapshotComparisonCatalogue(RelationalComparisonService owner, RelationalComparisonTarget target) : IAsyncDisposable
    {
        private HistoricalSnapshotContext? _historical;
        private SnapshotSummary _head = null!;
        private readonly Dictionary<(string Schema, string Name), SnapshotComparisonRow> _tables = new(TableNameComparer.Instance);
        private readonly Dictionary<(long Revision, long? Entry), TableMetadataDetails> _details = [];
        private long _bytes; private int _units;
        public List<SnapshotComparisonRow> Rows { get; } = [];

        public static async Task<SnapshotComparisonCatalogue> OpenAsync(RelationalComparisonService owner,
            RelationalComparisonTarget target, CancellationToken ct)
        {
            var result = new SnapshotComparisonCatalogue(owner, target);
            try { await result.LoadAsync(ct).ConfigureAwait(false); return result; }
            catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
        }
        private async Task LoadAsync(CancellationToken ct)
        {
            _head = await owner._runtime.Snapshots.GetSnapshotAsync(target.SnapshotKey!.Value, ct).ConfigureAwait(false)
                ?? throw new SnapshotConcurrencyException();
            var resources = new List<(SnapshotResourceSummary Summary, HistoricalSnapshotEntry? Entry)>();
            var fullData = new HashSet<(string, string)>(TableNameComparer.Instance);
            if (target.VersionKey.HasValue)
            {
                _historical = await owner._runtime.Snapshots.OpenHistoricalSnapshotAsync(target.SnapshotKey.Value, target.VersionKey.Value, ct).ConfigureAwait(false);
                if (!_head.RowVersion.AsSpan().SequenceEqual(_historical.Header.RowVersion)) throw new SnapshotConcurrencyException();
                foreach (var collection in new[] { HistoricalCollection.Objects, HistoricalCollection.Tables, HistoricalCollection.TableDataSets })
                {
                    var batch = new List<HistoricalSnapshotEntry>();
                    await foreach (var entry in _historical.StreamAsync(collection, ct).ConfigureAwait(false))
                    {
                        AddBudget(entry.SchemaName, entry.Name);
                        if (collection == HistoricalCollection.Tables && entry.Table?.HasFullData == true) fullData.Add((entry.SchemaName, entry.Name));
                        batch.Add(entry);
                        if (batch.Count == 128) await FlushAsync().ConfigureAwait(false);
                    }
                    if (batch.Count != 0) await FlushAsync().ConfigureAwait(false);
                    async Task FlushAsync()
                    {
                        var summaries = await _historical.ResourceSummariesAsync(batch, ct).ConfigureAwait(false);
                        foreach (var entry in batch) resources.Add((summaries[entry.EntryKey], entry));
                        batch.Clear();
                    }
                }
            }
            else
            {
                SnapshotCursor? cursor = null;
                do
                {
                    var page = await owner._runtime.Snapshots.ListResourcesAsync(target.SnapshotKey.Value, pageSize: 1000, cursor: cursor, ct: ct).ConfigureAwait(false);
                    foreach (var resource in page.Items) { AddBudget(resource.SchemaName, resource.ObjectName, resource.OriginalResourceKey); resources.Add((resource, null)); }
                    cursor = page.Next;
                } while (cursor != null);
                do
                {
                    var page = await owner._runtime.Snapshots.ListTablesAsync(target.SnapshotKey.Value, pageSize: 1000, cursor: cursor, ct: ct).ConfigureAwait(false);
                    foreach (var table in page.Items.Where(table => table.HasFullData)) fullData.Add((table.Resource.SchemaName, table.Resource.ObjectName));
                    cursor = page.Next;
                } while (cursor != null);
            }
            bool all = target.Resource.Kind == ComparisonResourceKind.DatabaseSnapshot;
            foreach (var category in Enum.GetValues<ExplorerCategory>())
            {
                if (!all && target.Category != category) continue;
                string folder = ExplorerCompatibility.CategoryName(category);
                if (all) AddRow(new(this, folder, Folder(folder, target, category).Target));
                var kind = category == ExplorerCategory.Tables ? DatabaseVersionedResourceKind.TableMetadata : SnapshotIdentity.ResourceKind(ObjectKind(category)!.Value);
                foreach (var item in resources.Where(item => item.Summary.Kind == kind))
                {
                    var leaf = FromResource(item.Summary, target.Epoch, target.VersionKey, item.Entry?.EntryKey);
                    var row = new SnapshotComparisonRow(this, (all ? folder + "/" : "") + leaf.Resource.DatabaseObjectName + ".sql", leaf, item.Summary, item.Entry);
                    AddRow(row);
                    if (kind == DatabaseVersionedResourceKind.TableMetadata) _tables.TryAdd((item.Summary.SchemaName, item.Summary.ObjectName), row);
                }
            }
            if (all)
            {
                bool added = false;
                // Preserve table order and first matching dataset, as the original collection route did.
                var data = resources.Where(item => item.Summary.Kind == DatabaseVersionedResourceKind.TableData)
                    .GroupBy(item => (item.Summary.SchemaName, item.Summary.ObjectName), TableNameComparer.Instance)
                    .ToDictionary(group => group.Key, group => group.First(), TableNameComparer.Instance);
                foreach (var table in resources.Where(item => item.Summary.Kind == DatabaseVersionedResourceKind.TableMetadata))
                {
                    var name = (table.Summary.SchemaName, table.Summary.ObjectName);
                    if (!fullData.Contains(name) || !data.TryGetValue(name, out var item)) continue;
                    if (!added) { AddRow(new(this, "Full Table Data", Folder("Full Table Data", target, ExplorerCategory.Tables).Target)); added = true; }
                    var leaf = FromResource(item.Summary, target.Epoch, target.VersionKey, item.Entry?.EntryKey);
                    AddRow(new(this, "Full Table Data/" + leaf.Resource.DatabaseObjectName + ".csv", leaf, item.Summary, item.Entry));
                }
            }
            foreach (long[] batch in Rows.Where(row => row.IsObject).Select(row => row.Summary!.RevisionKey).Distinct().Chunk(128))
            {
                var identities = await owner._runtime.Snapshots.ReadObjectTextIdentitiesAsync(batch, ct).ConfigureAwait(false);
                foreach (var row in Rows.Where(row => row.IsObject && identities.ContainsKey(row.Summary!.RevisionKey))) row.TextIdentity = identities[row.Summary!.RevisionKey];
            }
            await ValidateAsync(ct).ConfigureAwait(false);
        }

        private void AddBudget(params string[] values)
        {
            _bytes = checked(_bytes + 1536 + values.Sum(value => value.Length * 6L));
            if (++_units > SnapshotComparisonMetadataRows || _bytes > SnapshotComparisonMetadataBytes) throw new SnapshotComparisonMetadataLimitException();
        }
        private void AddRow(SnapshotComparisonRow row)
        {
            var resource = row.Target.Resource;
            _bytes = checked(_bytes + 512 + RetainedStrings(row.Path, resource.DisplayName, resource.TypeDisplay, resource.Path,
                resource.ComparisonTypeKey, resource.IdentityKey, resource.SnapshotId, resource.DatabaseFolderName,
                resource.DatabaseObjectName, resource.TableSchemaName, resource.TableName, resource.SyntaxPath));
            if (_bytes > SnapshotComparisonMetadataBytes) throw new SnapshotComparisonMetadataLimitException();
            Rows.Add(row);
        }
        private async Task<TableMetadataDetails> DetailsAsync(SnapshotComparisonRow row, CancellationToken ct)
        {
            var key = (row.Summary!.RevisionKey, row.Historical?.EntryKey);
            if (_details.TryGetValue(key, out var cached)) return cached;
            var details = _historical == null
                ? await owner._runtime.Snapshots.ReadTableMetadataAsync(row.Summary.RevisionKey, ct).ConfigureAwait(false)
                : await _historical.ReadTableMetadataAsync(row.Historical!, ct).ConfigureAwait(false);
            long size = 512 + RetainedStrings(details.Table.SchemaName, details.Table.TableName) +
                details.Columns.Sum(column => 512L + RetainedStrings(column.Column.SchemaName, column.Column.TableName,
                    column.Column.ColumnName, column.Column.DataType)) +
                details.PrimaryKeys.Sum(key => 384L + RetainedStrings(key.Column.SchemaName, key.Column.TableName,
                    key.Column.ColumnName, key.Column.ConstraintName));
            if (size <= SnapshotComparisonMetadataBytes - _bytes) { _bytes += size; _details.Add(key, details); }
            return details;
        }
        public async Task<PreparedComparisonTableData> PrepareTableDataAsync(SnapshotComparisonRow row, CancellationToken ct)
        {
            if (row.TableData != null) return row.TableData;
            var data = await owner._runtime.CapturedData.GetForRevisionAsync(row.Summary!.RevisionKey, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The selected table has no complete captured dataset.");
            TableMetadataDetails? details = null;
            if (_tables.TryGetValue((row.Summary.SchemaName, row.Summary.ObjectName), out var table)) details = await DetailsAsync(table, ct).ConfigureAwait(false);
            var headers = details?.Columns.Count > 0 ? details.Columns.OrderBy(column => column.Column.Ordinal).Select(column => column.Column.ColumnName).ToArray()
                : data.Columns.Select(column => column.SourceName).ToArray();
            string headerIdentity = string.Concat(headers.Select(header => header.Length + ":" + header));
            var value = new PreparedComparisonTableData(data, headers, headerIdentity);
            long size = 768 + RetainedStrings(headerIdentity) + headers.Sum(header => 64L + RetainedStrings(header)) +
                data.Columns.Sum(column => 256L + RetainedStrings(column.SourceName, column.SourceDataType));
            if (size <= SnapshotComparisonMetadataBytes - _bytes) { _bytes += size; row.TableData = value; }
            return value;
        }
        public async Task<string> StructuredDigestAsync(SnapshotComparisonRow row, ComparisonOptions options, CancellationToken ct)
        {
            if (!row.Target.Resource.IsTableData)
                return ComparisonTextHash.Digest(RelationalDocumentService.RenderTable(await DetailsAsync(row, ct).ConfigureAwait(false), owner.Limits.MaximumTextCharacters * 2L), options);
            var prepared = await PrepareTableDataAsync(row, ct).ConfigureAwait(false);
            var key = (prepared.Descriptor.Summary.DataSetKey, prepared.HeaderIdentity, options);
            lock (owner._comparisonCacheGate)
                if (owner._comparisonDataDigests.TryGetValue(key, out var cached)) return cached;
            using var hash = new ComparisonTextHash(options);
            Append(prepared.Headers);
            await foreach (var value in owner._runtime.CapturedData.StreamRowsAsync(prepared.Descriptor.Summary.DataSetKey, cancellationToken: ct).ConfigureAwait(false))
                if (value.Value.ValueKind == JsonValueKind.Object) Append(prepared.Headers.Select(header => ComparisonTableEngine.Value(value.Value, header)));
            string digest = hash.Finish();
            long cacheBytes = 512 + RetainedStrings(prepared.HeaderIdentity);
            lock (owner._comparisonCacheGate)
            {
                if (cacheBytes <= 8L * 1024 * 1024 && !owner._comparisonDataDigests.ContainsKey(key))
                {
                    if (cacheBytes > 8L * 1024 * 1024 - owner._comparisonDataDigestBytes)
                    { owner._comparisonDataDigests.Clear(); owner._comparisonDataDigestBytes = 0; }
                    owner._comparisonDataDigests.Add(key, digest); owner._comparisonDataDigestBytes += cacheBytes;
                }
            }
            return digest;
            void Append(IEnumerable<string> cells)
            {
                bool first = true;
                foreach (string cell in cells) { if (!first) hash.Append(","); first = false; hash.Append(CaptureDisplay.EscapeDelimited(cell, ',')); }
                hash.Append(Environment.NewLine);
            }
        }
        public async Task ValidateAsync(CancellationToken ct)
        {
            var latest = await owner._runtime.Snapshots.GetSnapshotAsync(_head.SnapshotKey, ct).ConfigureAwait(false);
            if (latest == null || !latest.RowVersion.AsSpan().SequenceEqual(_head.RowVersion)) throw new SnapshotConcurrencyException();
        }
        public async ValueTask DisposeAsync() { if (_historical != null) await _historical.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class TableNameComparer : IEqualityComparer<(string Schema, string Name)>
    {
        public static readonly TableNameComparer Instance = new();
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        public int GetHashCode((string Schema, string Name) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}

using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Snapshots;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Migration;

/// <summary>
/// Read-only, pre-publication model fidelity check. Capture's ImportRows fidelity check is a prerequisite,
/// not replaced by the dataset header/count checks here. This class never publishes a format marker.
/// </summary>
public sealed class RelationalMigrationValidator
{
    private const string Snapshots = "database-snapshots";
    private const int MetadataLimit = 100_000;
    private const long MetadataBytes = 64L * 1024 * 1024;
    private const int ValidationPageSize = 256;
    private const long HistoricalRowBytes = 2L * StreamingJsonCursor.MaximumValueBytes + 1024;
    private readonly RelationalSession _session;
    private readonly LegacySourceStage _source;
    private readonly RelationalSnapshotStore _snapshots;
    private readonly RelationalStateStore _state;
    private readonly RelationalCaptureStore _capture;
    private MigrationMappingReader _mappings;
    private readonly Dictionary<string, Dictionary<long, ChildOwner>> _childOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<long, RevisionOwner> _revisionOwners = [];
    internal MigrationProgressReporter? Progress { get; init; }
    private readonly Dictionary<string, long> _units = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastOrdinals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StagedLegacyAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedMissingAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, SourceTarget>> _sourceTargets = new(StringComparer.Ordinal);
    private string? _pastedDirectory;
    private long _assetMetadataBytes;
    private long _actualDataRows;
    private int _running;
    private static readonly JsonSerializerOptions ModelJson = CreateModelJson();

    internal RelationalMigrationValidator(RelationalSession validationSession, LegacySourceStage source)
    {
        _session = validationSession ?? throw new ArgumentNullException(nameof(validationSession));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _snapshots = new(_session, new RelationalContentStore(), new SnapshotReadLimits { SelectedDefinitionBytes = 128L * 1024 * 1024 });
        _state = new(_session);
        _capture = new(_session);
        _mappings = new(_session, _source.MigrationIdentity);
    }

    internal async Task<MigrationValidationReceipt> ValidateAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("Migration validation is already running.");
        var pins = new List<FileStream>(7);
        try
        {
            _units.Clear(); _lastOrdinals.Clear(); _rows.Clear(); _assets.Clear(); _usedAssets.Clear(); _usedMissingAssets.Clear(); _sourceTargets.Clear();
            _pastedDirectory = null; _assetMetadataBytes = 0; _actualDataRows = 0;
            _childOwners.Clear(); _revisionOwners.Clear();
            _mappings = new(_session, _source.MigrationIdentity);
            _source.ValidateSourceOrigin();
            _source.RequirePastedImagesFrozen();
            await _source.VerifyFrozenPastedImagesAsync(ct);
            using var assetPaths = _source.PinPastedImageDirectories();
            Guid databaseIdentity = await RequireConvertingAsync(ct);
            Check(_source.StagedDocuments.Count == 6 && LegacySourceStage.Documents.Keys.All(_source.StagedDocuments.ContainsKey),
                "stage", "six authoritative document roots");
            long expectedDataRows = 0;
            Progress?.StartPhase("Validating staged source files", _source.StagedDocuments.Values.Sum(d => d.ByteCount), "bytes");
            foreach (string document in LegacySourceStage.Documents.Keys)
            {
                var staged = _source.StagedDocuments[document];
                Check(staged.Key == document && staged.SourceKind is "Sql" or "Local" or "Package" or "Default" &&
                    staged.SourceHash.Length == 32 && staged.StagingHash.Length == 32 && WithinStage(staged.FilePath),
                    document, "staged identity/hash");
                pins.Add(Pin(staged.FilePath));
                Progress?.SetDetail("Checking source hash: " + document);
                await VerifyStagedAsync(staged, ct);
                await using var cursor = _source.OpenDocument(document);
                long scanned = 0;
                var inventory = await new LegacySchemaInspector().InspectAsync(document, cursor, ct, position =>
                {
                    long next = Math.Clamp(position, scanned, staged.ByteCount);
                    Progress?.Advance("Checking source fields: " + document, next - scanned);
                    scanned = next;
                });
                Progress?.Advance("Checked source fields: " + document, staged.ByteCount - scanned);
                expectedDataRows = checked(expectedDataRows + inventory.DataRows);
                await ValidateProvenanceAsync(staged, inventory, ct);
            }
            Check(Bytes(SourceFingerprint(), _source.Fingerprint), "stage", "descriptor-derived source fingerprint");
            string manifest = Path.Combine(_source.DirectoryPath, "manifest.json");
            pins.Add(Pin(manifest));
            await ReadManifestAsync(manifest, ct);
            long expectedUnits = await ScalarAsync("SELECT COALESCE(SUM(RecordCount),0) FROM surf.MigrationCheckpoint WHERE MigrationIdentity=@Migration;", ct, Migration());
            Progress?.StartPhase("Validating migrated content and reverse history", expectedUnits, "mapped units checked");
            Progress?.SetDetail("Resolving source target identities");
            await ReadSourceTargetsAsync(ct);
            await ValidateSnapshotsAsync(ct);
            await ValidateHistoriesAsync(ct);
            await ValidateStateAsync(ct);
            Progress?.StartPhase("Checking relational coverage and ownership");
            await ValidateCountsAsync(ct);
            Check(_actualDataRows == expectedDataRows, Snapshots, "Capture/source row coverage (not row fidelity)");
            // A package inventories all safe PNG inputs, including unreferenced legacy cache files.
            Check(_source.SourceOrigin == "Package" || _usedAssets.Count == _assets.Count,
                "manifest", "every non-package staged PNG has an authoritative owner");
            Check(_usedMissingAssets.Count == _source.StagedPastedImageReferences.Count(reference => reference.IsMissing),
                "manifest", "every frozen-absent PNG has an authoritative owner");
            Progress?.StartPhase("Rechecking source files and image hashes", _assets.Count + (long)_source.StagedDocuments.Count + 1, "checks");
            foreach (var asset in _assets.Values)
            {
                Check(Bytes(await LegacySourceStage.HashFileAsync(asset.FilePath, ct), asset.Hash), "manifest", "staged PNG hash");
                Progress?.Advance("Rechecked staged image hash");
            }
            foreach (var staged in _source.StagedDocuments.Values)
            {
                Progress?.SetDetail("Rechecking source hash: " + staged.Key);
                await VerifyStagedAsync(staged, ct);
                Progress?.Advance("Rechecked source hash: " + staged.Key);
            }
            await _source.VerifyFrozenPastedImagesAsync(ct);
            Progress?.Advance("Rechecked frozen image origins");
            Guid finalIdentity = await RequireConvertingAsync(ct);
            Check(finalIdentity == databaseIdentity, "migration", "unchanged destination database identity");
            return new(_source.MigrationIdentity, _source.Fingerprint, _actualDataRows, finalIdentity, _session.Epoch);
        }
        finally
        {
            foreach (var pin in pins) await pin.DisposeAsync();
            _sourceTargets.Clear(); _assets.Clear(); _usedAssets.Clear(); _usedMissingAssets.Clear();
            _childOwners.Clear(); _revisionOwners.Clear();
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<Guid> RequireConvertingAsync(CancellationToken ct)
    {
        await _session.RequireReadyAsync(ct);
        return await OneAsync("""
SELECT f.DatabaseIdentity FROM surf.MigrationRun m JOIN surf.StorageFormatInfo f ON f.Singleton=1
WHERE m.MigrationIdentity=@Migration AND m.SourceFingerprint=@Fingerprint
  AND m.ConverterVersion=1 AND m.Status='Converting' AND f.State='Migrating' AND f.SchemaVersion=1;
""", r =>
        {
            Guid identity = r.GetGuid(0);
            Check(identity != Guid.Empty, "migration", "nonempty destination database identity");
            return identity;
        }, "matching converting run and unpublished marker", ct, Migration(),
            RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, _source.Fingerprint, 32));
    }

    private async Task VerifyStagedAsync(StagedLegacyDocument staged, CancellationToken ct)
    {
        Check(new FileInfo(staged.FilePath).Length == staged.ByteCount &&
            Bytes(await LegacySourceStage.HashFileAsync(staged.FilePath, ct), staged.StagingHash), staged.Key, "staged bytes");
    }

    private async Task ValidateProvenanceAsync(StagedLegacyDocument staged, LegacyInventory inventory, CancellationToken ct)
    {
        await OneAsync("""
SELECT DocumentKey,SourceKind,SourceSchemaVersion,OriginalTimestamp,ContentHash,ByteCount
FROM surf.SourceProvenance WHERE MigrationIdentity=@Migration AND DocumentKey=@Document;
""", r =>
        {
            Check(r.GetString(0) == staged.Key && r.GetString(1) == staged.SourceKind &&
                NullableInt(r, 2) == inventory.SourceSchemaVersion && ExactDate(NullableDate(r, 3), staged.OriginalTimestamp) &&
                Bytes((byte[])r.GetValue(4), staged.SourceHash) && r.GetInt64(5) == staged.ByteCount,
                staged.Key, "provenance fields");
            return true;
        }, staged.Key, ct, Migration(), Text("@Document", staged.Key));
    }

    private async Task<long?> ResolveAsync(string document, string path, string kind, long ordinal, CancellationToken ct,
        bool count = true, bool optional = false)
    {
        string identity = FormattableString.Invariant($"{document.Length}:{document}{path.Length}:{path}");
        var mapping = await _mappings.FindAsync(document, path, ct);
        if (mapping == null)
        {
            if (optional) return null;
            throw Failure(document + ":" + path, "required journal mapping");
        }
        Check(mapping.SourceIdentity == identity && mapping.EntityKind == kind && mapping.DestinationKey > 0,
            document + ":" + path, "journal identity/kind/key");
        long key = mapping.DestinationKey;
        if (count)
        {
            _units[document] = checked(_units.GetValueOrDefault(document) + 1);
            _lastOrdinals[document] = Math.Max(_lastOrdinals.GetValueOrDefault(document), ordinal);
            Progress?.Advance(document + ":" + path);
        }
        return key;
    }

    private async Task<long> MapAsync(string document, string path, string kind, long ordinal, CancellationToken ct,
        bool count = true) => (await ResolveAsync(document, path, kind, ordinal, ct, count))!.Value;

    private async Task ValidateSnapshotsAsync(CancellationToken ct)
    {
        _ = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotLibrary>(File(Snapshots), 0, ["Snapshots", "Histories"], ct);
        Check(await MapAsync(Snapshots, "header", "SnapshotLibrary", 0, ct) == 1, Snapshots, "root wrapper mapping");
        await using var catalogue = SnapshotItems(c => _snapshots.ListSnapshotsAsync(pageSize: ValidationPageSize, cursor: c, ct: ct), ct).GetAsyncEnumerator(ct);
        await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), 0, "Snapshots", async (ordinal, cursor, token) =>
        {
            string path = P($"snapshots/{ordinal}");
            long key = await MapAsync(Snapshots, path, "Snapshot", ordinal, token);
            var header = await LegacyProjectionReader.HeaderAsync<DatabaseMetadataSnapshot>(File(Snapshots), cursor.TokenOffset,
                ["Objects", "Tables", "Columns", "PrimaryKeys", "TableDataSets", "FullDataTableNames"], token);
            var actual = await NextAsync(catalogue, path);
            Check(actual.SnapshotKey == key && actual.SortOrdinal == ordinal, path, "snapshot mapping/order");
            EqualModel(header, new DatabaseMetadataSnapshot
            {
                SnapshotId = actual.SnapshotId, DisplayName = actual.DisplayName, DatabaseName = actual.DatabaseName,
                ImportedAtUtc = actual.ImportedAtUtc
            }, path, token);
            Add("DatabaseSnapshot");
            Check(await MapAsync(Snapshots, path + "/publish", "SnapshotPublication", ordinal, token) == key,
                path, "snapshot publication mapping");
            await ValidateCurrentAsync(key, ordinal, cursor.TokenOffset, token);
            await cursor.SkipValueAsync(token);
        }, ct);
        await EndAsync(catalogue, Snapshots);
    }

    private async Task ValidateCurrentAsync(long snapshot, long snapshotOrdinal, long offset, CancellationToken ct)
    {
        string path = P($"snapshots/{snapshotOrdinal}");
        await using var objects = SnapshotItems(c => _snapshots.ListObjectsAsync(snapshot, pageSize: ValidationPageSize, cursor: c, ct: ct), ct).GetAsyncEnumerator(ct);
        await SourceArrayAsync<SqlDatabaseObject>(offset, "Objects", async (i, value, token) =>
        {
            string item = path + P($"/objects/{i}");
            long revision = await MapAsync(Snapshots, item, "ObjectRevision", snapshotOrdinal, token);
            var actual = (await NextAsync(objects, item)).Resource;
            Check(actual.RevisionKey == revision && actual.SortOrdinal == i, item, "object mapping/order");
            await ValidateCurrentResourceAsync(actual, SnapshotIdentity.ResourceKind(value.Kind), value.SchemaName, value.ObjectName, item, token);
            EqualModel(value, await _snapshots.ReadObjectAsync(revision, token), item, token);
            await RevisionAsync(revision, snapshot, actual.ResourceKey, item, token);
            Add("SnapshotResource"); Add("SnapshotResourceRevision"); Add("DatabaseObjectRevision");
        }, ct);
        await EndAsync(objects, path + "/Objects");

        var tables = new List<TableOwner>();
        long tableBytes = 0;
        await using var tablePage = SnapshotItems(c => _snapshots.ListTablesAsync(snapshot, pageSize: ValidationPageSize, cursor: c, ct: ct), ct).GetAsyncEnumerator(ct);
        await SourceArrayAsync<SqlTable>(offset, "Tables", async (i, value, token) =>
        {
            string item = path + P($"/tables/{i}");
            long revision = await MapAsync(Snapshots, item, "TableRevision", snapshotOrdinal, token);
            var actual = await NextAsync(tablePage, item);
            Check(actual.Resource.RevisionKey == revision && actual.Resource.SortOrdinal == i, item, "table mapping/order");
            await ValidateCurrentResourceAsync(actual.Resource, DatabaseVersionedResourceKind.TableMetadata, value.SchemaName, value.TableName, item, token);
            EqualModel(value, new SqlTable { SchemaName = actual.Resource.SchemaName, TableName = actual.Resource.ObjectName,
                HasFullData = actual.HasFullData, FullDataRowCount = actual.FullDataRowCount, FullDataImportedAtUtc = actual.FullDataImportedAtUtc }, item, token);
            // Also check the selected subtype; catalogue names can be COALESCE projections.
            EqualModel(value, await ReadTableAsync(revision, item, token), item, token);
            Check(await MapAsync(Snapshots, item + "/seal", "SealedRevision", snapshotOrdinal, token) == revision, item, "table seal mapping");
            await RevisionAsync(revision, snapshot, actual.Resource.ResourceKey, item, token);
            tableBytes = checked(tableBytes + 64L + (value.SchemaName.Length + (long)value.TableName.Length) * 2);
            Check(tables.Count < MetadataLimit && tableBytes <= MetadataBytes, path, "bounded table owner catalogue");
            Check(!tables.Any(t => Names(t.Schema, t.Name, value.SchemaName, value.TableName)), item, "unambiguous column/key owner");
            tables.Add(new(revision, value.SchemaName, value.TableName));
            Add("SnapshotResource"); Add("SnapshotResourceRevision"); Add("TableMetadataRevision");
        }, ct);
        await EndAsync(tablePage, path + "/Tables");

        await using var columns = SnapshotItems(c => _snapshots.ListCurrentColumnsAsync(snapshot, ValidationPageSize, c, ct), ct).GetAsyncEnumerator(ct);
        await SourceArrayAsync<SqlColumn>(offset, "Columns", async (i, value, token) =>
        {
            string item = path + P($"/columns/{i}");
            long key = await MapAsync(Snapshots, item, "Column", snapshotOrdinal, token);
            var actual = await NextAsync(columns, item);
            Check(actual.ColumnRevisionKey == key && actual.SortOrdinal == i, item, "column mapping/order");
            EqualModel(value, actual.Column, item, token);
            await ChildOwnerAsync("TableColumnRevision", "ColumnRevisionKey", key, snapshot,
                tables.FirstOrDefault(t => Names(t.Schema, t.Name, value.SchemaName, value.TableName))?.Revision, i, item, token);
            Add("TableColumnRevision"); Add("SnapshotCurrentColumn");
        }, ct);
        await EndAsync(columns, path + "/Columns");
        await using var keys = SnapshotItems(c => _snapshots.ListCurrentPrimaryKeysAsync(snapshot, ValidationPageSize, c, ct), ct).GetAsyncEnumerator(ct);
        await SourceArrayAsync<SqlPrimaryKeyColumn>(offset, "PrimaryKeys", async (i, value, token) =>
        {
            string item = path + P($"/keys/{i}");
            long key = await MapAsync(Snapshots, item, "PrimaryKey", snapshotOrdinal, token);
            var actual = await NextAsync(keys, item);
            Check(actual.KeyColumnKey == key && actual.SortOrdinal == i, item, "primary key mapping/order");
            EqualModel(value, actual.Column, item, token);
            await ChildOwnerAsync("PrimaryKeyColumn", "KeyColumnKey", key, snapshot,
                tables.FirstOrDefault(t => Names(t.Schema, t.Name, value.SchemaName, value.TableName))?.Revision, i, item, token);
            Add("PrimaryKeyColumn"); Add("SnapshotCurrentPrimaryKey");
        }, ct);
        await EndAsync(keys, path + "/PrimaryKeys");
        await using var selections = SnapshotItems(c => _snapshots.ListFullDataSelectionsAsync(snapshot, ValidationPageSize, c, ct), ct).GetAsyncEnumerator(ct);
        await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), offset, "FullDataTableNames", async (i, cursor, token) =>
        {
            string item = path + P($"/selections/{i}");
            var actual = await NextAsync(selections, item);
            Check(actual.SelectionKey == await MapAsync(Snapshots, item, "FullDataSelection", snapshotOrdinal, token) &&
                actual.SortOrdinal == i && actual.TableName == (await cursor.ReadValueAsync(token)).GetString(), item, "table selection fields/order");
            Add("FullDataTableSelection");
        }, ct);
        await EndAsync(selections, path + "/FullDataTableNames");
        await using var dataPage = SnapshotItems(c => _snapshots.ListResourcesAsync(snapshot, DatabaseVersionedResourceKind.TableData,
            pageSize: ValidationPageSize, cursor: c, ct: ct), ct).GetAsyncEnumerator(ct);
        await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), offset, "TableDataSets", async (i, cursor, token) =>
        {
            string item = path + P($"/datasets/{i}");
            long revision = await MapAsync(Snapshots, item, "DataRevision", snapshotOrdinal, token);
            var actual = await NextAsync(dataPage, item);
            var header = await LegacyProjectionReader.HeaderAsync<SqlTableDataSet>(File(Snapshots), cursor.TokenOffset, ["Rows"], token);
            Check(actual.RevisionKey == revision && actual.SortOrdinal == i, item, "dataset mapping/order");
            await ValidateCurrentResourceAsync(actual, DatabaseVersionedResourceKind.TableData, header.SchemaName, header.TableName, item, token);
            await RevisionAsync(revision, snapshot, actual.ResourceKey, item, token);
            Check(await MapAsync(Snapshots, item + "/seal", "CurrentDataRevision", snapshotOrdinal, token) == revision, item, "dataset seal mapping");
            await ValidateDataAsync(revision, header, null, cursor.TokenOffset, offset, item, token);
            Add("SnapshotResource");
            await cursor.SkipValueAsync(token);
        }, ct);
        await EndAsync(dataPage, path + "/TableDataSets");
    }

    private async Task ValidateCurrentResourceAsync(SnapshotResourceSummary actual, DatabaseVersionedResourceKind? kind,
        string schema, string name, string path, CancellationToken ct)
    {
        string original = kind.HasValue ? SnapshotIdentity.LegacyResourceKey(kind.Value, schema, name) : string.Empty;
        Check(actual.Kind == kind && actual.SchemaName == schema && actual.ObjectName == name && actual.OriginalResourceKey == original,
            path, "current resource identity");
        await OneAsync("""
SELECT SnapshotKey,Kind,SchemaName,ObjectName,OriginalResourceKey,CurrentRevisionKey,CurrentSortOrdinal,
       NameHash,ResourceKeyHash FROM surf.SnapshotResource WHERE ResourceKey=@Key;
""", r =>
        {
            Check(r.GetInt64(0) == actual.SnapshotKey && NullableInt(r, 1) == (int?)kind && r.GetString(2) == schema &&
                r.GetString(3) == name && r.GetString(4) == original && NullableLong(r, 5) == actual.RevisionKey &&
                r.GetInt64(6) == actual.SortOrdinal && Bytes((byte[])r.GetValue(7), SnapshotIdentity.NameHash(schema, name)) &&
                Bytes((byte[])r.GetValue(8), SnapshotIdentity.Hash(original)), path, "typed current resource owner/head/hashes");
            return true;
        }, path, ct, Key(actual.ResourceKey));
    }

    private async Task ValidateHistoriesAsync(CancellationToken ct)
    {
        var priorHistoryResources = new Dictionary<long, long>();
        await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), 0, "Histories", async (ordinal, cursor, token) =>
        {
            long offset = cursor.TokenOffset;
            string path = P($"histories/{ordinal}");
            var header = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotHistory>(File(Snapshots), offset, ["Versions"], token);
            long snapshot = await SourceSnapshotAsync(header.SnapshotId, token);
            long key = await MapAsync(Snapshots, path, "History", ordinal, token);
            bool firstHistory = priorHistoryResources.TryAdd(snapshot, 0);
            var actual = await _snapshots.GetHistoryByKeyAsync(key, token) ?? throw Failure(path, "selected mapped history");
            Check(actual.HistoryKey == key && actual.SnapshotKey == snapshot && actual.SortOrdinal == ordinal &&
                actual.SnapshotId == header.SnapshotId && actual.NextVersionNumber == header.NextVersionNumber, path, "history fields/owner/order");
            Add("SnapshotHistory");
            var resources = await ReadPriorHistoryResourcesAsync(snapshot, priorHistoryResources[snapshot], path, token);
            long resourceBytes = resources.Sum(r => 64 + r.Original.Length * 2L);
            long? latest = null;
            int? latestNumber = null;
            await using var versions = SnapshotItems(c => _snapshots.ListHistoryVersionsAsync(snapshot, key, ValidationPageSize, c, token), token).GetAsyncEnumerator(token);
            await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), offset, "Versions", async (i, versionCursor, versionToken) =>
            {
                string versionPath = path + P($"/versions/{i}");
                var version = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotVersion>(File(Snapshots), versionCursor.TokenOffset, ["Changes"], versionToken);
                long versionKey = await MapAsync(Snapshots, versionPath, "Version", ordinal, versionToken);
                var read = await NextAsync(versions, versionPath);
                Check(read.VersionKey == versionKey && read.SnapshotKey == snapshot && read.SortOrdinal == i &&
                    read.VersionId == version.VersionId && read.VersionName == version.VersionName && read.VersionNumber == version.VersionNumber &&
                    read.CreatedAtUtc.EqualsExact(version.CreatedAtUtc) && read.IsInitial == version.IsInitial, versionPath, "version fields/owner/order");
                await OneAsync("SELECT HistoryKey FROM surf.SnapshotVersion WHERE VersionKey=@Key;", r =>
                { Check(r.GetInt64(0) == key, versionPath, "version history FK"); return true; }, versionPath, versionToken, Key(versionKey));
                if (latestNumber == null || version.VersionNumber > latestNumber) { latest = versionKey; latestNumber = version.VersionNumber; }
                Add("SnapshotVersion");
                long changes = 0;
                await using var changePage = SnapshotItems(c => _snapshots.ListChangesAsync(snapshot, versionKey, ValidationPageSize, c, versionToken), versionToken).GetAsyncEnumerator(versionToken);
                await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), versionCursor.TokenOffset, "Changes", async (j, changeCursor, changeToken) =>
                {
                    string changePath = versionPath + P($"/changes/{j}");
                    var change = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotResourceChange>(File(Snapshots), changeCursor.TokenOffset, ["PreviousPayload"], changeToken);
                    var readChange = await NextAsync(changePage, changePath);
                    Check(readChange.ChangeKey == await MapAsync(Snapshots, changePath, "Change", ordinal, changeToken) &&
                        readChange.VersionKey == versionKey && readChange.SortOrdinal == j && readChange.Kind == change.Kind &&
                        readChange.ChangeKind == change.ChangeKind && readChange.OriginalResourceKey == change.ResourceKey &&
                        readChange.DisplayName == change.DisplayName && readChange.RelativePath == change.RelativePath, changePath, "change fields/owner/order");
                    DatabaseSnapshotResourcePayload? payload = null;
                    long? payloadOffset = null;
                    await LegacyProjectionReader.ValuePropertyAsync(File(Snapshots), changeCursor.TokenOffset, "PreviousPayload", async (value, inner) =>
                    {
                        if (value.TokenType != JsonTokenType.Null)
                        {
                            payloadOffset = value.TokenOffset;
                            payload = await LegacyProjectionReader.HeaderAsync<DatabaseSnapshotResourcePayload>(File(Snapshots), value.TokenOffset,
                                ["Columns", "PrimaryKeys", "TableDataSet"], inner);
                        }
                        await value.SkipValueAsync(inner);
                    }, changeToken);
                    var resource = resources.FirstOrDefault(r => r.Kind == change.Kind && string.Equals(r.Original, change.ResourceKey, StringComparison.OrdinalIgnoreCase));
                    if (resource == null)
                    {
                        long resourceKey = await MapAsync(Snapshots, changePath + "/resource", "HistoricalResource", ordinal, changeToken);
                        (string schema, string name) = HistoricalName(change, payload);
                        await ValidateHistoricalResourceAsync(resourceKey, snapshot, change.Kind, change.ResourceKey, schema, name, j, changePath, changeToken);
                        resource = new(resourceKey, change.Kind, change.ResourceKey);
                        AddResource(resources, resource, ref resourceBytes, changePath);
                        priorHistoryResources[snapshot] = Math.Max(priorHistoryResources[snapshot], resourceKey);
                        Add("SnapshotResource");
                    }
                    else
                        Check(await ResolveAsync(Snapshots, changePath + "/resource", "HistoricalResource", ordinal, changeToken, optional: true) == null,
                            changePath, "no spurious historical resource mapping");
                    Check(readChange.ResourceKey == resource.Key, changePath, "change logical resource mapping");
                    await OneAsync("SELECT SnapshotKey,VersionNumber FROM surf.SnapshotChange WHERE ChangeKey=@Key;", r =>
                    { Check(r.GetInt64(0) == snapshot && r.GetInt32(1) == version.VersionNumber, changePath, "change snapshot/version FK"); return true; },
                        changePath, changeToken, Key(readChange.ChangeKey));
                    if (payload == null) Check(readChange.PreviousRevisionKey == null, changePath, "null previous payload");
                    else
                    {
                        Check(payload.Kind == change.Kind && change.ChangeKind != DatabaseSnapshotResourceChangeKind.Added, changePath, "canonical previous payload kind");
                        long previous = await ValidatePayloadAsync(snapshot, resource.Key, payload, payloadOffset!.Value, changePath + "/previous", ordinal, changeToken);
                        Check(readChange.PreviousRevisionKey == previous, changePath, "previous revision mapping");
                    }
                    changes++; Add("SnapshotChange");
                    await changeCursor.SkipValueAsync(changeToken);
                }, versionToken);
                await EndAsync(changePage, versionPath + "/Changes");
                Check(read.ChangeCount == changes, versionPath, "change coverage");
                await versionCursor.SkipValueAsync(versionToken);
            }, token);
            await EndAsync(versions, path + "/Versions");
            if (firstHistory)
                Check((await _snapshots.GetSnapshotAsync(snapshot, token))?.CurrentVersionKey == latest, path, "first source history's latest stable source version");
            await cursor.SkipValueAsync(token);
        }, ct);
        // Owners with no source history must have no invented current version.
        Check(await ScalarAsync("""
SELECT COUNT_BIG(*) FROM surf.DatabaseSnapshot s WHERE s.CurrentVersionKey IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM surf.SnapshotHistory h WHERE h.SnapshotKey=s.SnapshotKey);
""", ct) == 0, Snapshots, "history-free snapshot head");
    }

    private async Task<List<LogicalResource>> ReadPriorHistoryResourcesAsync(long snapshot, long priorMaximum, string path, CancellationToken ct)
    {
        var resources = new List<LogicalResource>();
        long bytes = 0;
        await using var connection = await _session.OpenAsync(ct);
        // Later duplicate histories reuse resources created by earlier ones. Exclude future histories' creations.
        await using var command = Command(connection, """
SELECT ResourceKey,Kind,CONVERT(bigint,DATALENGTH(OriginalResourceKey)),OriginalResourceKey FROM surf.SnapshotResource
WHERE SnapshotKey=@Key AND (CurrentRevisionKey IS NOT NULL OR ResourceKey<=@Prior)
ORDER BY CurrentSortOrdinal,ResourceKey;
""", Key(snapshot), Key(priorMaximum, "@Prior"));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            long key = reader.GetInt64(0);
            DatabaseVersionedResourceKind? kind = reader.IsDBNull(1) ? null : (DatabaseVersionedResourceKind)reader.GetInt32(1);
            Check(resources.Count < MetadataLimit && reader.GetInt64(2) <= MetadataBytes - bytes - 64, path, "bounded prior-history resource text");
            using var text = reader.GetTextReader(3);
            AddResource(resources, new(key, kind, await text.ReadToEndAsync(ct)), ref bytes, path);
        }
        return resources;
    }

    private async Task<long> ValidatePayloadAsync(long snapshot, long resource, DatabaseSnapshotResourcePayload header,
        long offset, string path, long ordinal, CancellationToken ct)
    {
        long revision;
        if (header.Kind is DatabaseVersionedResourceKind.TableMetadata)
        {
            Check(header.Table != null && header.DatabaseObject == null, path, "table payload fields");
            await RequireNullPropertyAsync(offset, "TableDataSet", path, ct);
            revision = await MapAsync(Snapshots, path, "PreviousTableRevision", ordinal, ct);
            EqualModel(header.Table, await ReadTableAsync(revision, path, ct), path, ct);
            await HistoricalColumnsAsync(snapshot, revision, offset, path, ct);
            Add("TableMetadataRevision");
        }
        else if (header.Kind is DatabaseVersionedResourceKind.TableData)
        {
            Check(header.DatabaseObject == null, path, "data payload fields");
            await RequireEmptyArrayAsync(offset, "Columns", path, ct);
            await RequireEmptyArrayAsync(offset, "PrimaryKeys", path, ct);
            revision = await MapAsync(Snapshots, path, "PreviousDataRevision", ordinal, ct);
            bool found = false;
            await LegacyProjectionReader.ValuePropertyAsync(File(Snapshots), offset, "TableDataSet", async (value, token) =>
            {
                Check(value.TokenType != JsonTokenType.Null, path, "previous dataset present");
                var data = await LegacyProjectionReader.HeaderAsync<SqlTableDataSet>(File(Snapshots), value.TokenOffset, ["Rows"], token);
                await ValidateDataAsync(revision, data, header.Table, value.TokenOffset, null, path, token);
                found = true;
                await value.SkipValueAsync(token);
            }, ct);
            Check(found && await MapAsync(Snapshots, path + "/seal", "SealedPreviousData", ordinal, ct) == revision, path, "previous dataset/seal mapping");
            // ValidateDataAsync accounts for the base revision.
            await RevisionAsync(revision, snapshot, resource, path, ct);
            return revision;
        }
        else
        {
            Check(header.DatabaseObject != null && header.Table == null && Enum.IsDefined(header.Kind), path, "code payload fields");
            await RequireNullPropertyAsync(offset, "TableDataSet", path, ct);
            await RequireEmptyArrayAsync(offset, "Columns", path, ct);
            await RequireEmptyArrayAsync(offset, "PrimaryKeys", path, ct);
            revision = await MapAsync(Snapshots, path, "PreviousObjectRevision", ordinal, ct);
            EqualModel(header.DatabaseObject, await _snapshots.ReadObjectAsync(revision, ct), path, ct);
            Check(SnapshotIdentity.ResourceKind(header.DatabaseObject!.Kind) == header.Kind, path, "code payload discriminator");
            Add("DatabaseObjectRevision");
        }
        await RevisionAsync(revision, snapshot, resource, path, ct);
        Add("SnapshotResourceRevision");
        return revision;
    }

    private async Task HistoricalColumnsAsync(long snapshot, long revision, long offset, string path, CancellationToken ct)
    {
        await using var columns = HistoricalItems("TableColumnRevision", "ColumnRevisionKey", revision,
            "SchemaName,TableName,ColumnName,DataType,MaxLength,NumericPrecision,NumericScale,IsNullable,IsIdentity,SourceOrdinal",
            "DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ColumnName)+DATALENGTH(DataType)",
            r => new SqlColumn { SchemaName = r.GetString(4), TableName = r.GetString(5), ColumnName = r.GetString(6),
                DataType = r.GetString(7), MaxLength = r.GetInt32(8), NumericPrecision = r.GetByte(9), NumericScale = r.GetInt32(10),
                IsNullable = r.GetBoolean(11), IsIdentity = r.GetBoolean(12), Ordinal = r.GetInt32(13) }, ct).GetAsyncEnumerator(ct);
        await SourceArrayAsync<SqlColumn>(offset, "Columns", async (i, value, token) =>
        {
            var actual = await NextAsync(columns, path);
            Check(actual.Snapshot == snapshot && actual.Order == i, path, "historical column owner/order");
            EqualModel(value, actual.Value, path + P($"/Columns/{i}"), token);
            Add("TableColumnRevision");
            Progress?.SetDetail(path + P($"/Columns/{i}"));
        }, ct);
        await EndAsync(columns, path + "/Columns");
        await using var keys = HistoricalItems("PrimaryKeyColumn", "KeyColumnKey", revision,
            "SchemaName,TableName,ConstraintName,ColumnName,KeyOrdinal",
            "DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ConstraintName)+DATALENGTH(ColumnName)",
            r => new SqlPrimaryKeyColumn { SchemaName = r.GetString(4), TableName = r.GetString(5), ConstraintName = r.GetString(6),
                ColumnName = r.GetString(7), KeyOrdinal = r.GetInt32(8) }, ct).GetAsyncEnumerator(ct);
        await SourceArrayAsync<SqlPrimaryKeyColumn>(offset, "PrimaryKeys", async (i, value, token) =>
        {
            var actual = await NextAsync(keys, path);
            Check(actual.Snapshot == snapshot && actual.Order == i, path, "historical key owner/order");
            EqualModel(value, actual.Value, path + P($"/PrimaryKeys/{i}"), token);
            Add("PrimaryKeyColumn");
            Progress?.SetDetail(path + P($"/PrimaryKeys/{i}"));
        }, ct);
        await EndAsync(keys, path + "/PrimaryKeys");
    }

    private async Task ValidateDataAsync(long revision, SqlTableDataSet header, SqlTable? table, long offset,
        long? snapshotOffset, string path, CancellationToken ct)
    {
        var actual = await _snapshots.ReadTableDataMetadataAsync(revision, ct);
        Check(actual.SchemaName == header.SchemaName && actual.TableName == header.TableName, path, "dataset source names");
        EqualModel(table, actual.Table, path + "/Table", ct);
        var data = await _capture.GetForRevisionAsync(revision, ct) ?? throw Failure(path, "selected Capture dataset");
        Check(data.Epoch == _session.Epoch && data.Summary.RevisionKey == revision && data.Summary.State == "Ready" &&
            data.Summary.ReportedRowCount == header.RowCount && data.Summary.ImportedAtUtc.EqualsExact(header.ImportedAtUtc), path, "Capture header/owner/readiness");
        var sourceColumns = new List<CaptureColumnDefinition>();
        if (snapshotOffset.HasValue)
            await SourceArrayAsync<SqlColumn>(snapshotOffset.Value, "Columns", (i, value, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (Names(value.SchemaName, value.TableName, header.SchemaName, header.TableName))
                {
                    Check(sourceColumns.Count < CaptureLimits.MaximumColumns, path, "bounded source Capture column metadata");
                    sourceColumns.Add(new(value.ColumnName, value.DataType, value.MaxLength, value.NumericPrecision, value.NumericScale,
                        value.IsNullable, value.Ordinal, value.IsIdentity));
                }
                return Task.CompletedTask;
            }, ct);
        IReadOnlyList<CaptureColumnDefinition> expectedColumns = CaptureImportColumns.Select(Array.Empty<string>(), sourceColumns);
        long sourceRows = 0;
        await LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), offset, "Rows", async (i, row, token) =>
        {
            if (i == 0 && expectedColumns.Count == 0)
                expectedColumns = CaptureColumns.FromFirstRow(await row.ReadValueAsync(token));
            else await row.SkipValueAsync(token);
            sourceRows = checked(sourceRows + 1);
            if ((sourceRows & 255) == 0) Progress?.SetDetail(path + $": checked {sourceRows:N0} source captured rows");
        }, ct);
        Check(data.Summary.ActualRowCount == sourceRows && data.Summary.DisplayFormatVersion == CaptureDisplay.FormatVersion &&
            data.Columns.SequenceEqual(expectedColumns), path, "per-dataset source count and exact column layout");
        _actualDataRows = checked(_actualDataRows + data.Summary.ActualRowCount);
        Add("SnapshotResourceRevision"); Add("TableDataRevision");
        if (table != null) Add("TableDataRevisionMetadata");
        Add("DataSet");
    }

    // Only compact IDs and journal keys are retained, never a library or its selected aggregates.
    private async Task ReadSourceTargetsAsync(CancellationToken ct)
    {
        long bytes = 0;
        foreach (var (document, array, kind) in new[]
            { (Snapshots, "Snapshots", "Snapshot"), ("scope-library", "Scopes", "Scope"), ("diagram-library", "Diagrams", "Diagram") })
        {
            int rows = 0;
            var targets = new Dictionary<string, SourceTarget>(StringComparer.OrdinalIgnoreCase);
            _sourceTargets.Add(document, targets);
            await LegacyProjectionReader.ArrayPropertyAsync(File(document), 0, array, async (i, cursor, token) =>
            {
                string id = document switch
                {
                    Snapshots => (await LegacyProjectionReader.HeaderAsync<DatabaseMetadataSnapshot>(File(document), cursor.TokenOffset,
                        ["Objects", "Tables", "Columns", "PrimaryKeys", "TableDataSets", "FullDataTableNames"], token)).SnapshotId,
                    "scope-library" => (await LegacyProjectionReader.HeaderAsync<Scope>(File(document), cursor.TokenOffset, ["Resources", "VirtualFolders"], token)).ScopeId,
                    _ => (await LegacyProjectionReader.HeaderAsync<DiagramDocument>(File(document), cursor.TokenOffset, ["Objects", "Workflows"], token)).DiagramId
                };
                bytes = checked(bytes + 128 + id.Length * 2L);
                Check(++rows <= MetadataLimit && bytes <= MetadataBytes, document, "bounded source target identity catalogue");
                long key = await MapAsync(document, document == Snapshots ? P($"snapshots/{i}") : StatePath(array, i), kind, i, token, count: false);
                if (!targets.TryAdd(id, new(key, StateLinkResolution.Resolved))) targets[id] = new(null, StateLinkResolution.Ambiguous);
                await cursor.SkipValueAsync(token);
            }, ct);
        }
    }

    private Task<long> SourceSnapshotAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = SourceTargetFor(Snapshots, id);
        return Task.FromResult(target.Key ?? throw Failure("history", "unambiguous source snapshot target"));
    }

    private async Task ValidateHistoricalResourceAsync(long key, long snapshot, DatabaseVersionedResourceKind kind,
        string original, string schema, string name, long ordinal, string path, CancellationToken ct) =>
        _ = await OneAsync("""
SELECT SnapshotKey,Kind,SchemaName,ObjectName,OriginalResourceKey,CurrentRevisionKey,CurrentSortOrdinal,NameHash,ResourceKeyHash
FROM surf.SnapshotResource WHERE ResourceKey=@Key;
""", r =>
        {
            Check(r.GetInt64(0) == snapshot && r.GetInt32(1) == (int)kind && r.GetString(2) == schema && r.GetString(3) == name &&
                r.GetString(4) == original && r.IsDBNull(5) && r.GetInt64(6) == ordinal &&
                Bytes((byte[])r.GetValue(7), SnapshotIdentity.NameHash(schema, name)) && Bytes((byte[])r.GetValue(8), SnapshotIdentity.Hash(original)),
                path, "historical resource identity/owner");
            return true;
        }, path, ct, Key(key));

    private async Task RevisionAsync(long revision, long snapshot, long resource, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_revisionOwners.TryGetValue(revision, out var actual))
        {
            _revisionOwners.Clear();
            await using var connection = await _session.OpenAsync(ct);
            await using var command = Command(connection, """
SELECT TOP (@Take) RevisionKey,SnapshotKey,ResourceKey,IsSealed
FROM surf.SnapshotResourceRevision WHERE RevisionKey>=@Key ORDER BY RevisionKey;
""", Key(revision), RelationalSession.Parameter("@Take", SqlDbType.Int, ValidationPageSize));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                _revisionOwners.Add(reader.GetInt64(0), new(reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3)));
            Check(_revisionOwners.TryGetValue(revision, out actual), path, "selected revision owner");
        }
        Check(actual!.Snapshot == snapshot && actual.Resource == resource && actual.Sealed, path, "sealed revision typed owner");
    }

    private Task<SqlTable> ReadTableAsync(long revision, string path, CancellationToken ct) => OneAsync("""
SELECT SchemaName,TableName,HasFullData,FullDataRowCount,FullDataImportedAtUtc FROM surf.TableMetadataRevision WHERE RevisionKey=@Key;
""", r => new SqlTable { SchemaName = r.GetString(0), TableName = r.GetString(1), HasFullData = r.GetBoolean(2),
            FullDataRowCount = r.GetInt64(3), FullDataImportedAtUtc = NullableDate(r, 4) }, path, ct, Key(revision));

    // SQL fills the missing public single-child/scalar revision APIs; never hydrates revision child lists.
    private async Task ChildOwnerAsync(string table, string keyColumn, long key, long snapshot, long? revision, long ordinal,
        string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_childOwners.TryGetValue(table, out var owners)) _childOwners.Add(table, owners = []);
        if (!owners.TryGetValue(key, out var actual))
        {
            owners.Clear();
            await using var connection = await _session.OpenAsync(ct);
            await using var command = Command(connection,
                $"SELECT TOP (@Take) [{keyColumn}],SnapshotKey,TableMetadataRevisionKey,SortOrdinal FROM surf.[{table}] WHERE [{keyColumn}]>=@Key ORDER BY [{keyColumn}];",
                Key(key), RelationalSession.Parameter("@Take", SqlDbType.Int, ValidationPageSize));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                owners.Add(reader.GetInt64(0), new(reader.GetInt64(1), NullableLong(reader, 2), reader.GetInt64(3)));
            Check(owners.TryGetValue(key, out actual), path, "selected child owner");
        }
        Check(actual!.Snapshot == snapshot && actual.Revision == revision && actual.Order == ordinal, path, "child typed owner/order");
    }

    private async Task ValidateStateAsync(CancellationToken ct)
    {
        var settings = await SingletonAsync<AppSettings>("app-settings", ct);
        var actualSettings = await _state.ReadSettingsAsync(ct) ?? throw Failure("app-settings", "selected settings");
        Check(actualSettings.Token.Key == await MapAsync("app-settings", "$", "Settings", 0, ct), "app-settings", "settings mapping");
        EqualModel(settings, actualSettings.Value, "app-settings:$", ct);
        Add("ApplicationPreference"); Add("ExtensionAppearance", settings.CodeWindows.BackcolorsByExtension.Count);
        Add("ReferenceStyle", settings.ReferenceHighlights.Styles.Count); Add("DiagramImageDefinition", settings.DiagramImages.Images.Count);
        // Release these two selected aggregates before reading any diagram/workbench.
        settings = null!; actualSettings = null!;
        var workspace = await SingletonAsync<WorkspaceState>("workspace-state", ct);
        var actualWorkspace = await _state.ReadWorkspaceAsync(ct) ?? throw Failure("workspace-state", "selected workspace");
        Check(actualWorkspace.Token.Key == await MapAsync("workspace-state", "$", "WorkspaceSession", 0, ct), "workspace-state", "workspace mapping");
        EqualModel(workspace, actualWorkspace.Value, "workspace-state:$", ct);
        Add("WorkspaceSession"); CountLayout(workspace.OpenDocuments, workspace.UnloadedResourceIds);
        workspace = null!; actualWorkspace = null!;
        var selection = await LegacyProjectionReader.HeaderAsync<ScopeLibrary>(File("scope-library"), 0, ["Scopes"], ct);
        var actualSelection = await _state.ReadScopeSelectionAsync(ct) ?? throw Failure("scope-library", "selected scope wrapper");
        Check(actualSelection.Token.Key == await MapAsync("scope-library", "$", "ScopeSelection", 0, ct) &&
            actualSelection.Value.SchemaVersion == selection.SchemaVersion && actualSelection.Value.LastActiveScopeId == selection.LastActiveScopeId,
            "scope-library:$", "scope wrapper fields/mapping");
        Add("ScopeCatalogueState");
        await using var scopes = StateItems(c => _state.ListScopesAsync(ValidationPageSize, c, ct), ct).GetAsyncEnumerator(ct);
        await StateArrayAsync<Scope>("scope-library", "Scopes", async (i, value, path, token) =>
        {
            long key = await MapAsync("scope-library", path, "Scope", i, token);
            var summary = await NextAsync(scopes, path);
            Check(summary.Token.Key == key && summary.SortOrdinal == i && summary.ScopeId == value.ScopeId && summary.Name == value.Name,
                path, "scope catalogue mapping/order");
            var read = await _state.ReadScopeAsync(key, token) ?? throw Failure(path, "selected scope");
            EqualModel(value, read.Value, path, token);
            Check(await MapAsync("scope-library", path + "/targets", "ScopeTargetResolution", i, token) == key, path, "scope target journal mapping");
            await ValidateScopeTargetsAsync(key, value, path, token);
            await ValidateFolderLinksAsync(key, value, path, token);
            Add("Scope"); Add("ScopeResource", value.Resources.Count); Add("VirtualFolder", value.VirtualFolders.Count);
            foreach (var folder in value.VirtualFolders) Add("VirtualFolderMember", folder.ChildNodeKeys.Count);
        }, ct);
        await EndAsync(scopes, "scope-library");
        _ = await LegacyProjectionReader.HeaderAsync<DiagramLibrary>(File("diagram-library"), 0, ["Diagrams"], ct);
        Check(await MapAsync("diagram-library", "$", "DiagramLibraryHeader", 0, ct) == 1, "diagram-library", "diagram wrapper mapping");
        await using var diagrams = StateItems(c => _state.ListDiagramsAsync(ValidationPageSize, c, ct), ct).GetAsyncEnumerator(ct);
        await StateArrayAsync<DiagramDocument>("diagram-library", "Diagrams", async (i, value, path, token) =>
        {
            long key = await MapAsync("diagram-library", path, "Diagram", i, token);
            Check(await MapAsync("diagram-library", path + "/references", "DiagramReferenceResolution", i, token) == key,
                path, "diagram reference-resolution mapping");
            var summary = await NextAsync(diagrams, path);
            Check(summary.Token.Key == key && summary.SortOrdinal == i && summary.DiagramId == value.DiagramId && summary.Name == value.Name &&
                summary.CreatedAtUtc.EqualsExact(value.CreatedAtUtc) && summary.UpdatedAtUtc.EqualsExact(value.UpdatedAtUtc) &&
                summary.Token.PublicationId == Publication("diagram-library", path + "/references"), path, "diagram catalogue mapping/order/reference publication");
            var read = await _state.ReadDiagramAsync(key, token) ?? throw Failure(path, "selected diagram");
            EqualModel(value, read.Value.Document, path, token);
            await ValidateImagesAsync(value, read.Value.PastedImages, read.Value.PastedImageFallbacks, path, token);
            await ValidateDiagramLinksAsync(summary.RevisionKey, value, path, token);
            CountDiagram(value); Add("Diagram");
            await OneAsync("SELECT DiagramKey,WorkbenchKey,Origin FROM surf.DiagramRevision WHERE DiagramRevisionKey=@Key;", r =>
            { Check(NullableLong(r, 0) == key && r.IsDBNull(1) && r.GetInt32(2) == 0, path, "diagram revision typed owner"); return true; }, path, token, Key(summary.RevisionKey));
        }, ct);
        await EndAsync(diagrams, "diagram-library");
        _ = await LegacyProjectionReader.HeaderAsync<WorkbenchLibrary>(File("workbench-library"), 0, ["Workbenches"], ct);
        Check(await MapAsync("workbench-library", "$", "WorkbenchLibraryHeader", 0, ct) == 1, "workbench-library", "workbench wrapper mapping");
        await using var workbenches = StateItems(c => _state.ListWorkbenchesAsync(ValidationPageSize, c, ct), ct).GetAsyncEnumerator(ct);
        await StateArrayAsync<WorkbenchState>("workbench-library", "Workbenches", async (i, value, path, token) =>
        {
            long key = await MapAsync("workbench-library", path, "Workbench", i, token);
            Check(await MapAsync("workbench-library", path + "/references", "WorkbenchReferenceResolution", i, token) == key,
                path, "workbench reference-resolution mapping");
            var summary = await NextAsync(workbenches, path);
            Check(summary.Token.Key == key && summary.SortOrdinal == i && summary.WorkbenchId == value.WorkbenchId && summary.Name == value.Name &&
                summary.ScopeId == value.ScopeId && summary.ScopeName == value.ScopeName && summary.IsDefaultForScope == value.IsDefaultForScope &&
                summary.CreatedAtUtc.EqualsExact(value.CreatedAtUtc) && summary.UpdatedAtUtc.EqualsExact(value.UpdatedAtUtc) && summary.SavedAtUtc.EqualsExact(value.SavedAtUtc) &&
                summary.Token.PublicationId == Publication("workbench-library", path + "/references"),
                path, "workbench catalogue mapping/order");
            var read = await _state.ReadWorkbenchAsync(key, token) ?? throw Failure(path, "selected workbench");
            EqualModel(value, read.Value.Workbench, path, token);
            if (value.ActiveDiagramSnapshot != null)
            {
                Check(summary.EmbeddedDiagramRevisionKey != null, path, "embedded revision present");
                await ValidateImagesAsync(value.ActiveDiagramSnapshot, read.Value.PastedImages, read.Value.PastedImageFallbacks, path, token);
                await ValidateDiagramLinksAsync(summary.EmbeddedDiagramRevisionKey!.Value, value.ActiveDiagramSnapshot, path, token);
                CountDiagram(value.ActiveDiagramSnapshot);
                await OneAsync("SELECT DiagramKey,WorkbenchKey,Origin FROM surf.DiagramRevision WHERE DiagramRevisionKey=@Key;", r =>
                { Check(r.IsDBNull(0) && NullableLong(r, 1) == key && r.GetInt32(2) == 1, path, "embedded revision typed owner"); return true; },
                    path, token, Key(summary.EmbeddedDiagramRevisionKey!.Value));
            }
            else Check(summary.EmbeddedDiagramRevisionKey == null && read.Value.PastedImages.Count == 0 &&
                (read.Value.PastedImageFallbacks?.Count ?? 0) == 0, path, "no invented embedded diagram/images");
            var expectedScope = await SourceLogicalTargetAsync("scope-library", value.ScopeId, token);
            var actualScope = await _state.ReadWorkbenchScopeTargetAsync(key, token) ?? throw Failure(path, "workbench scope relationship");
            Check(actualScope.ScopeKey == expectedScope.Key && actualScope.Resolution == expectedScope.Resolution, path, "workbench source scope target/resolution");
            CountLayout(value.OpenDocuments, value.UnloadedResourceIds); Add("ReferenceConnectionLine", value.ReferenceConnectionLines.Count); Add("Workbench");
        }, ct);
        await EndAsync(workbenches, "workbench-library");
    }

    private async Task ValidateFolderLinksAsync(long scope, Scope source, string path, CancellationToken ct)
    {
        var folders = await ChildKeysAsync("VirtualFolder", "VirtualFolderKey", "ScopeKey", scope, source.VirtualFolders.Count, path, ct);
        var resources = await ChildKeysAsync("ScopeResource", "ScopeResourceKey", "ScopeKey", scope, source.Resources.Count, path, ct);
        var actual = await _state.ReadScopeFolderRelationshipsAsync(scope, ct);
        var plan = StateRelationshipResolver.Folders(source);
        Check(actual.ScopeKey == scope && actual.Parents.Count == source.VirtualFolders.Count &&
            actual.Members.Count == source.VirtualFolders.Sum(f => f.ChildNodeKeys.Count), path, "folder relationship coverage");
        var parents = actual.Parents.ToDictionary(p => p.VirtualFolderKey);
        var members = actual.Members.ToDictionary(p => (p.VirtualFolderKey, p.SortOrdinal));
        foreach (var link in plan)
        {
            ct.ThrowIfCancellationRequested();
            long key = folders[link.FolderOrdinal];
            long? folder = link.TargetOrdinal.HasValue ? folders[link.TargetOrdinal.Value] : null;
            long? resource = link.ResourceOrdinal.HasValue ? resources[link.ResourceOrdinal.Value] : null;
            if (link.MemberOrdinal.HasValue)
            {
                Check(members.TryGetValue((key, link.MemberOrdinal.Value), out var member) && member.ChildVirtualFolderKey == folder &&
                    member.ChildScopeResourceKey == resource && member.Resolution == link.Resolution, path, "folder member typed target/resolution");
            }
            else Check(parents.TryGetValue(key, out var parent) && parent.ParentVirtualFolderKey == folder &&
                parent.ParentScopeResourceKey == resource && parent.Resolution == link.Resolution, path, "folder parent typed target/resolution");
        }
    }

    private async Task ValidateDiagramLinksAsync(long revision, DiagramDocument source, string path, CancellationToken ct)
    {
        var objects = await ChildKeysAsync("DiagramObject", "DiagramObjectKey", "DiagramRevisionKey", revision, source.Objects.Count, path, ct);
        var workflows = await ChildKeysAsync("Workflow", "WorkflowKey", "DiagramRevisionKey", revision, source.Workflows.Count, path, ct);
        var items = new long[workflows.Length][];
        for (int i = 0; i < workflows.Length; i++)
            items[i] = await ChildKeysAsync("WorkflowItem", "WorkflowItemKey", "WorkflowKey", workflows[i], source.Workflows[i].Items.Count, path, ct);
        var actual = await _state.ReadDiagramRelationshipsAsync(revision, ct);
        var plan = StateRelationshipResolver.Workflows(source);
        var portals = StateRelationshipResolver.Portals(source);
        Check(actual.DiagramRevisionKey == revision && actual.WorkflowBindings.Count == plan.Bindings.Count && actual.ItemMarkers.Count == plan.Markers.Count &&
            actual.PortalTargets.Count == portals.Count, path, "diagram relationship coverage");
        var bindings = actual.WorkflowBindings.ToDictionary(v => v.DiagramObjectKey);
        foreach (var link in plan.Bindings)
        {
            ct.ThrowIfCancellationRequested();
            long? workflow = link.WorkflowOrdinal.HasValue ? workflows[link.WorkflowOrdinal.Value] : null;
            long? item = link.ItemOrdinal.HasValue ? items[link.WorkflowOrdinal!.Value][link.ItemOrdinal.Value] : null;
            Check(bindings.TryGetValue(objects[link.ObjectOrdinal], out var read) && read.WorkflowKey == workflow && read.WorkflowItemKey == item &&
                read.WorkflowResolution == link.WorkflowResolution && read.ItemResolution == link.ItemResolution, path, "workflow binding typed targets/resolutions");
        }
        var markers = actual.ItemMarkers.ToDictionary(v => v.WorkflowItemKey);
        foreach (var link in plan.Markers)
        {
            ct.ThrowIfCancellationRequested();
            long? target = link.ObjectOrdinal.HasValue ? objects[link.ObjectOrdinal.Value] : null;
            Check(markers.TryGetValue(items[link.WorkflowOrdinal][link.ItemOrdinal], out var read) && read.WorkflowKey == workflows[link.WorkflowOrdinal] &&
                read.MarkerDiagramObjectKey == target && read.Resolution == link.Resolution, path, "workflow marker typed target/resolution");
        }
        var targets = actual.PortalTargets.ToDictionary(v => v.DiagramObjectKey);
        foreach (var link in portals)
        {
            ct.ThrowIfCancellationRequested();
            long? diagram = null;
            var resolution = link.Resolution;
            if (link.LogicalDiagramId != null)
                (diagram, resolution) = await SourceLogicalTargetAsync("diagram-library", link.LogicalDiagramId, ct);
            long? target = link.TargetObjectOrdinal.HasValue ? objects[link.TargetObjectOrdinal.Value] : null;
            Check(targets.TryGetValue(objects[link.ObjectOrdinal], out var read) && read.TargetDiagramKey == diagram &&
                read.TargetRevisionKey == (target.HasValue ? revision : null) && read.TargetObjectKey == target && read.Resolution == resolution,
                path, "portal logical/embedded target/resolution");
        }
        Add("DiagramWorkflowBinding", plan.Bindings.Count); Add("WorkflowItemMarker", plan.Markers.Count); Add("DiagramPortalTarget", portals.Count);
    }

    private async Task<long[]> ChildKeysAsync(string table, string keyColumn, string ownerColumn, long owner, int count, string path, CancellationToken ct)
    {
        Check(count >= 0 && count <= MetadataLimit, path, "bounded selected relationship key map");
        var keys = new long[count];
        int index = 0;
        await using var connection = await _session.OpenAsync(ct);
        await using var command = Command(connection,
            $"SELECT [{keyColumn}],SortOrdinal FROM surf.[{table}] WHERE [{ownerColumn}]=@Key ORDER BY SortOrdinal,[{keyColumn}];", Key(owner));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            Check(index < count && reader.GetInt64(1) == index, path, "relationship source ordinal/key coverage");
            keys[index++] = reader.GetInt64(0);
        }
        Check(index == count, path, "relationship child key count");
        return keys;
    }

    private Task<(long? Key, StateLinkResolution Resolution)> SourceLogicalTargetAsync(string document, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = string.IsNullOrWhiteSpace(id) ? new SourceTarget(null, StateLinkResolution.None) : SourceTargetFor(document, id);
        return Task.FromResult((target.Key, target.Resolution));
    }

    private async Task ValidateScopeTargetsAsync(long key, Scope scope, string path, CancellationToken ct)
    {
        for (int i = 0; i < scope.Resources.Count; i++)
        {
            var source = scope.Resources[i];
            long? target = source.Kind switch
            {
                ResourceKind.DatabaseSnapshot => await UniqueSourceTargetAsync(Snapshots, source.Path, ct),
                ResourceKind.Diagram => await UniqueSourceTargetAsync("diagram-library", source.Path, ct),
                _ => null
            };
            await OneAsync("SELECT SnapshotKey,DiagramKey FROM surf.ScopeResource WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal;", r =>
            {
                Check(NullableLong(r, 0) == (source.Kind == ResourceKind.DatabaseSnapshot ? target : null) &&
                    NullableLong(r, 1) == (source.Kind == ResourceKind.Diagram ? target : null), path + P($"/Resources/{i}"), "source-derived typed target");
                return true;
            }, path, ct, Key(key), Key(i, "@Ordinal"));
        }
    }

    private Task<long?> UniqueSourceTargetAsync(string document, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(SourceTargetFor(document, id).Key);
    }

    private async Task ReadManifestAsync(string path, CancellationToken ct)
    {
        int documents = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool identity = false, fingerprint = false, assets = false, documentArray = false, origin = false,
            packagePath = false, packageHash = false, pastedFreeze = false;
        await using var cursor = LegacySourceStage.OpenAt(path, 0);
        Check(await cursor.MoveNextAsync(ct), "manifest", "manifest root");
        await cursor.ReadObjectAsync(async (name, value, token) =>
        {
            switch (name)
            {
                case "SourceOrigin":
                    Check(!origin && (await value.ReadValueAsync(token)).GetString() == _source.SourceOrigin,
                        "manifest", "source origin");
                    origin = true; break;
                case "MigrationIdentity":
                    Check(!identity && (await value.ReadValueAsync(token)).GetGuid() == _source.MigrationIdentity, "manifest", "migration identity");
                    identity = true; break;
                case "Fingerprint":
                    Check(!fingerprint && Bytes((await value.ReadValueAsync(token)).GetBytesFromBase64(), _source.Fingerprint), "manifest", "source fingerprint");
                    fingerprint = true; break;
                case "PackagePath":
                    Check(!packagePath && _source.SourceOrigin == "Package" &&
                        (await value.ReadValueAsync(token)).GetString() == _source.PackagePath, "manifest", "package path binding");
                    packagePath = true; break;
                case "PackageHash":
                    Check(!packageHash && _source.SourceOrigin == "Package" &&
                        Bytes((await value.ReadValueAsync(token)).GetBytesFromBase64(), _source.PackageHash), "manifest", "package hash binding");
                    packageHash = true; break;
                case "Documents":
                    Check(!documentArray, "manifest", "single documents array"); documentArray = true;
                    await value.ReadArrayAsync(async (_, item, inner) =>
                    {
                        var entry = JsonSerializer.Deserialize<StagedLegacyDocument>(await item.ReadValueAsync(inner)) ?? throw Failure("manifest", "document record");
                        Check(seen.Add(entry.Key) && _source.StagedDocuments.ContainsKey(entry.Key), "manifest", "unique known document");
                        var staged = _source.StagedDocuments[entry.Key];
                        Check(entry.SourceKind == staged.SourceKind && entry.ByteCount == staged.ByteCount && entry.LocalSourcePath == staged.LocalSourcePath &&
                            ExactDate(entry.OriginalTimestamp, staged.OriginalTimestamp) && Bytes(entry.SourceHash, staged.SourceHash) &&
                            Bytes(entry.StagingHash, staged.StagingHash) && Path.GetFullPath(entry.FilePath) == Path.GetFullPath(staged.FilePath), entry.Key, "manifest/stage binding");
                        documents++;
                    }, token); break;
                case "Assets":
                    Check(!assets, "manifest", "single assets array"); assets = true;
                    await value.ReadArrayAsync(async (_, item, inner) =>
                    {
                        var entry = JsonSerializer.Deserialize<StagedLegacyAsset>(await item.ReadValueAsync(inner)) ?? throw Failure("manifest", "asset record");
                        Check(entry.Hash is { Length: 32 }, "manifest", "PNG hash shape");
                        string original = Path.GetFullPath(entry.OriginalPath);
                        string directory = Path.GetDirectoryName(original)!;
                        string leaf = Path.GetFileName(original);
                        Check(string.Equals(StateImages.LeafPath(directory, leaf), original, StringComparison.OrdinalIgnoreCase), "manifest", "PNG original leaf path");
                        _pastedDirectory ??= directory;
                        Check(string.Equals(_pastedDirectory, directory, StringComparison.OrdinalIgnoreCase), "manifest", "one protected pasted-image directory");
                        string staged = Path.GetFullPath(Path.Combine(_source.DirectoryPath, "assets", Convert.ToHexString(entry.Hash).ToLowerInvariant() + ".png"));
                        Check(string.Equals(staged, Path.GetFullPath(entry.FilePath), StringComparison.OrdinalIgnoreCase), "manifest", "PNG frozen path");
                        _assetMetadataBytes = checked(_assetMetadataBytes + 96 + (original.Length + (long)staged.Length) * 2);
                        Check(_assets.Count < MetadataLimit && _assetMetadataBytes <= MetadataBytes && _assets.TryAdd(leaf, entry), "manifest", "bounded unique PNG metadata");
                    }, token); break;
                case "PastedImageFreeze":
                    Check(!pastedFreeze, "manifest", "single PNG freeze descriptor"); pastedFreeze = true;
                    await _source.CheckPastedFreezeManifestAsync(value, token);
                    _pastedDirectory = _source.SourceImageDirectory;
                    break;
                default: throw Failure("manifest", "known manifest property");
            }
        }, ct);
        Check(identity && fingerprint && documentArray && assets && pastedFreeze && (origin || _source.SourceOrigin == "Sql") &&
            (packagePath == (_source.SourceOrigin == "Package")) && (packageHash == (_source.SourceOrigin == "Package")) &&
            documents == 6 && !await cursor.MoveNextAsync(ct), "manifest", "six-root manifest/origin coverage");
    }

    private async Task ValidateImagesAsync(DiagramDocument source, IReadOnlyDictionary<int, byte[]> pasted,
        IReadOnlyDictionary<int, PastedImageFallback>? fallbacks, string path, CancellationToken ct)
    {
        Check(pasted.Keys.All(i => i >= 0 && i < source.Objects.Count) &&
            (fallbacks == null || fallbacks.Keys.All(i => i >= 0 && i < source.Objects.Count)), path, "image owner ordinals");
        for (int i = 0; i < source.Objects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = source.Objects[i];
            bool captured = pasted.TryGetValue(i, out var bytes);
            PastedImageFallback? fallback = null;
            bool hasFallback = fallbacks?.TryGetValue(i, out fallback) ?? false;
            if (string.IsNullOrWhiteSpace(item.PastedImageFileName))
            { Check(!captured && !hasFallback, path, "no invented pasted asset"); continue; }
            // Validate the source leaf even when the legitimate missing-file fallback has no manifest asset.
            string original = StateImages.LeafPath(_pastedDirectory ?? Path.Combine(_source.DirectoryPath, "PastedDiagramImages"), item.PastedImageFileName);
            Check(captured != hasFallback, path, "one pasted-image resolution");
            if (_assets.TryGetValue(item.PastedImageFileName, out var asset))
            {
                Check(captured && !hasFallback && bytes != null, path, "frozen PNG resolution");
                using var pin = Pin(asset.FilePath);
                Check(pin.Length > 0 && pin.Length <= 16 * 1024 * 1024 && pin.Length == bytes!.LongLength &&
                    Bytes(SHA256.HashData(bytes), asset.Hash), path, "pasted asset size/hash");
                var frozen = new byte[(int)pin.Length];
                await pin.ReadExactlyAsync(frozen, ct);
                Check(Bytes(frozen, bytes) && Bytes(SHA256.HashData(frozen), asset.Hash), path, "pasted PNG exact frozen bytes");
                _usedAssets.Add(item.PastedImageFileName);
            }
            else
            {
                Check(_source.IsFrozenPastedImageMissing(original), path, "explicit frozen-absent PNG descriptor");
                _usedMissingAssets.Add(original);
                Check(!captured && hasFallback && fallback != null, path, "missing local PNG fallback");
                byte[]? expected = Decode(item.ImageDataBase64, path);
                PastedImageResolution resolution = PastedImageResolution.MissingUseInline;
                if (expected == null)
                {
                    resolution = PastedImageResolution.MissingUseDefinition;
                    expected = await SourceDefinitionBytesAsync(item.ImageDefinitionId, path, ct);
                }
                Check(expected != null && fallback!.Resolution == resolution && Bytes(expected, fallback.Bytes), path, "source-derived fallback exact bytes");
            }
        }
    }

    private async Task<byte[]?> SourceDefinitionBytesAsync(string id, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        byte[]? result = null;
        bool found = false;
        await LegacyProjectionReader.ValuePropertyAsync(File("app-settings"), 0, "DiagramImages", async (cursor, token) =>
        {
            await LegacyProjectionReader.ArrayPropertyAsync(File("app-settings"), cursor.TokenOffset, "Images", async (_, item, inner) =>
            {
                var definition = LegacySchemaInspector.Deserialize<DiagramImageDefinition>(await item.ReadValueAsync(inner));
                if (!found && string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase))
                { found = true; result = Decode(definition.ImageDataBase64, path); }
            }, token);
            await cursor.SkipValueAsync(token);
        }, ct);
        return result;
    }

    private static byte[]? Decode(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Check(text.Length * 3L / 4 <= 16 * 1024 * 1024 + 2L, path, "bounded fallback base64");
        try { return Convert.FromBase64String(text); }
        catch (FormatException ex) { throw new InvalidDataException("Migration validation failed: invalid source fallback base64 at " + path + ".", ex); }
    }

    private void CountLayout(IEnumerable<OpenDocumentState> documents, IReadOnlyCollection<string> unloaded)
    {
        Add("WorkspaceUnloadedResource", unloaded.Count);
        foreach (var document in documents)
        { Add("DocumentWindowState"); Add("DocumentWindowFilter", document.SpreadsheetFilters.Count); }
    }

    private void CountDiagram(DiagramDocument diagram)
    {
        Add("DiagramRevision"); Add("DiagramObject", diagram.Objects.Count); Add("Workflow", diagram.Workflows.Count);
        foreach (var item in diagram.Objects) Add("QueryItem", item.Metadata.Queries.Count);
        foreach (var workflow in diagram.Workflows)
        {
            Add("WorkflowItem", workflow.Items.Count);
            foreach (var item in workflow.Items) Add("QueryItem", item.Queries.Count);
        }
    }

    private async Task ValidateCountsAsync(CancellationToken ct)
    {
        Check(await ScalarAsync("SELECT COUNT_BIG(*) FROM surf.SourceProvenance WHERE MigrationIdentity=@Migration;", ct, Migration()) == 6,
            "provenance", "six root rows");
        Check(await ScalarAsync("SELECT COUNT_BIG(*) FROM surf.MigrationCheckpoint WHERE MigrationIdentity=@Migration;", ct, Migration()) == 6,
            "checkpoint", "six root checkpoints");
        Check(await ScalarAsync("SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap WHERE MigrationIdentity=@Migration;", ct, Migration()) == _units.Values.Sum(),
            "journal", "no extra or missing mapped units");
        foreach (string document in LegacySourceStage.Documents.Keys)
        {
            await OneAsync("""
SELECT SourceHash,RecordCount,LastOrdinal FROM surf.MigrationCheckpoint WHERE MigrationIdentity=@Migration AND SourceDocument=@Document;
""", r =>
            {
                Check(Bytes((byte[])r.GetValue(0), _source.StagedDocuments[document].SourceHash) &&
                    r.GetInt64(1) == _units.GetValueOrDefault(document) && r.GetInt64(2) == _lastOrdinals.GetValueOrDefault(document),
                    document, "checkpoint source/count/order");
                return true;
            }, document, ct, Migration(), Text("@Document", document));
            string prefix = FormattableString.Invariant($"{document.Length}:{document}");
            Check(await ScalarAsync("""
SELECT COUNT_BIG(*) FROM surf.MigrationIdentityMap WHERE MigrationIdentity=@Migration
AND LEFT(SourceIdentity,LEN(@Prefix)) COLLATE Latin1_General_100_BIN2=@Prefix COLLATE Latin1_General_100_BIN2;
""", ct, Migration(), Text("@Prefix", prefix)) == _units.GetValueOrDefault(document), document, "per-root journal coverage");
        }
        // Fixed internal identifiers, never source-controlled SQL identifiers. Zero counts also reject invented empty-root children.
        string[] tables = ["DatabaseSnapshot", "SnapshotResource", "SnapshotResourceRevision", "DatabaseObjectRevision", "TableMetadataRevision",
            "TableDataRevision", "TableDataRevisionMetadata", "TableColumnRevision", "PrimaryKeyColumn", "SnapshotCurrentColumn", "SnapshotCurrentPrimaryKey",
            "FullDataTableSelection", "SnapshotHistory", "SnapshotVersion", "SnapshotChange", "ApplicationPreference", "ExtensionAppearance", "ReferenceStyle",
            "DiagramImageDefinition", "WorkspaceSession", "ScopeCatalogueState", "Scope", "ScopeResource", "VirtualFolder", "VirtualFolderMember", "Diagram",
            "Workbench", "DiagramRevision", "DiagramObject", "Workflow", "WorkflowItem", "QueryItem", "WorkspaceUnloadedResource", "DocumentWindowState",
            "DocumentWindowFilter", "ReferenceConnectionLine", "DataSet", "DiagramWorkflowBinding", "WorkflowItemMarker", "DiagramPortalTarget"];
        foreach (string table in tables)
        {
            string identifier = "surf.[" + table + "]";
            Progress?.SetDetail("Checking row coverage: " + table);
            Check(await ScalarAsync("SELECT COUNT_BIG(*) FROM " + identifier + ";", ct) == _rows.GetValueOrDefault(table), table, "authoritative mapped row coverage");
            Progress?.Advance("Checked row coverage: " + table);
        }
        Check(await ScalarAsync("""
SELECT COUNT_BIG(*) FROM surf.PrimaryKeyConstraint k WHERE
 NOT EXISTS(SELECT 1 FROM surf.PrimaryKeyColumn c WHERE c.ConstraintKey=k.ConstraintKey)
 OR EXISTS(SELECT 1 FROM surf.PrimaryKeyColumn c WHERE c.ConstraintKey=k.ConstraintKey AND
  (c.SnapshotKey<>k.SnapshotKey OR ISNULL(c.TableMetadataRevisionKey,-1)<>ISNULL(k.TableMetadataRevisionKey,-1)
   OR DATALENGTH(c.ConstraintName)<>DATALENGTH(k.ConstraintName)
   OR c.ConstraintName COLLATE Latin1_General_100_BIN2<>k.ConstraintName COLLATE Latin1_General_100_BIN2));
""", ct) == 0, "PrimaryKeyConstraint", "constraint owner/name and no orphan constraints");
        Check(await ScalarAsync("""
SELECT COUNT_BIG(*) FROM surf.PrimaryKeyConstraint k WHERE
 k.ConstraintNameHash<>HASHBYTES('SHA2_256',CONVERT(varbinary(max),k.ConstraintName)) OR
 (k.TableMetadataRevisionKey IS NOT NULL AND EXISTS(SELECT 1 FROM surf.PrimaryKeyConstraint other
  WHERE other.ConstraintKey<k.ConstraintKey AND other.SnapshotKey=k.SnapshotKey
   AND other.TableMetadataRevisionKey=k.TableMetadataRevisionKey
   AND DATALENGTH(other.ConstraintName)=DATALENGTH(k.ConstraintName)
   AND other.ConstraintName COLLATE Latin1_General_100_BIN2=k.ConstraintName COLLATE Latin1_General_100_BIN2));
""", ct) == 0, "PrimaryKeyConstraint", "exact constraint grouping/hash");
        foreach (var (table, owners) in new (string, string)[]
        {
            ("ScopeResource", "ScopeKey"), ("VirtualFolder", "ScopeKey"), ("VirtualFolderMember", "VirtualFolderKey"),
            ("DiagramObject", "DiagramRevisionKey"), ("Workflow", "DiagramRevisionKey"), ("WorkflowItem", "WorkflowKey"),
            ("QueryItem", "DiagramObjectKey,WorkflowItemKey"), ("DocumentWindowState", "WorkspaceSessionKey,WorkbenchKey"),
            ("WorkspaceUnloadedResource", "WorkspaceSessionKey,WorkbenchKey"), ("DocumentWindowFilter", "DocumentWindowKey"),
            ("ReferenceConnectionLine", "WorkbenchKey"), ("ExtensionAppearance", "ProfileKey"), ("ReferenceStyle", "ProfileKey"),
            ("DiagramImageDefinition", "ProfileKey")
        })
            Check(await ScalarAsync($"""
SELECT COUNT_BIG(*) FROM (SELECT {owners} FROM surf.[{table}] GROUP BY {owners}
 HAVING MIN(SortOrdinal)<>0 OR MAX(SortOrdinal)<>COUNT_BIG(*)-1 OR COUNT(DISTINCT SortOrdinal)<>COUNT_BIG(*)) bad;
""", ct) == 0, table, "contiguous source collection ordinals per typed owner");
    }

    private async Task<T> SingletonAsync<T>(string document, CancellationToken ct) where T : class
    {
        await using var cursor = _source.OpenDocument(document);
        Check(await cursor.MoveNextAsync(ct), document, "source root");
        T value = LegacySchemaInspector.Deserialize<T>(await cursor.ReadValueAsync(ct));
        Check(!await cursor.MoveNextAsync(ct), document, "single source root");
        return value;
    }

    private Task SourceArrayAsync<T>(long offset, string property, Func<long, T, CancellationToken, Task> process, CancellationToken ct) where T : class =>
        LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), offset, property, async (i, cursor, token) =>
            await process(i, LegacySchemaInspector.Deserialize<T>(await cursor.ReadValueAsync(token)), token), ct);

    private Task StateArrayAsync<T>(string document, string array, Func<long, T, string, CancellationToken, Task> process, CancellationToken ct) where T : class =>
        LegacyProjectionReader.ArrayPropertyAsync(File(document), 0, array, async (i, cursor, token) =>
            await process(i, LegacySchemaInspector.Deserialize<T>(await cursor.ReadValueAsync(token)), StatePath(array, i), token), ct);

    private Task RequireNullPropertyAsync(long offset, string property, string path, CancellationToken ct) =>
        LegacyProjectionReader.ValuePropertyAsync(File(Snapshots), offset, property, async (value, token) =>
        { Check(value.TokenType == JsonTokenType.Null, path, "no unexpected " + property); await value.SkipValueAsync(token); }, ct);

    private Task RequireEmptyArrayAsync(long offset, string property, string path, CancellationToken ct) =>
        LegacyProjectionReader.ArrayPropertyAsync(File(Snapshots), offset, property, (_, _, _) => throw Failure(path, "empty " + property), ct);

    private async IAsyncEnumerable<(long Snapshot, long Order, T Value)> HistoricalItems<T>(string table, string keyColumn,
        long revision, string fields, string byteSizes, Func<SqlDataReader, T> read, [EnumeratorCancellation] CancellationToken ct)
    {
        long afterOrder = -1, afterKey = 0;
        bool more;
        do
        {
            var page = new List<(long Key, long Snapshot, long Order, T Value)>(ValidationPageSize);
            more = false;
            long bytes = 0;
            await using (var connection = await _session.OpenAsync(ct))
            await using (var command = Command(connection, $"""
SELECT TOP (@Take) CONVERT(bigint,256)+{byteSizes},[{keyColumn}],SnapshotKey,SortOrdinal,{fields}
FROM surf.[{table}] WHERE TableMetadataRevisionKey=@Key
AND (SortOrdinal>@AfterOrder OR SortOrdinal=@AfterOrder AND [{keyColumn}]>@AfterKey)
ORDER BY SortOrdinal,[{keyColumn}];
""", Key(revision), Key(afterOrder, "@AfterOrder"), Key(afterKey, "@AfterKey"),
                RelationalSession.Parameter("@Take", SqlDbType.Int, ValidationPageSize + 1)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct))
                {
                    if (page.Count == ValidationPageSize) { more = true; SnapshotReadGuard.StopReading(command); break; }
                    long rowBytes = reader.GetInt64(0);
                    // Legacy validation allowed a selected historical row larger than the catalogue page budget.
                    Check(rowBytes >= 0 && rowBytes <= HistoricalRowBytes, table, "bounded historical metadata row");
                    if (page.Count > 0 && rowBytes > RelationalSnapshotStore.MaximumMetadataPageBytes - bytes)
                    { more = true; SnapshotReadGuard.StopReading(command); break; }
                    bytes += rowBytes;
                    page.Add((reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), read(reader)));
                }
            }
            Check(!more || page.Count > 0, table, "progressing historical metadata page");
            foreach (var item in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return (item.Snapshot, item.Order, item.Value);
            }
            if (page.Count > 0) (afterOrder, afterKey) = (page[^1].Order, page[^1].Key);
        } while (more);
    }

    private static async IAsyncEnumerable<T> SnapshotItems<T>(Func<SnapshotCursor?, Task<SnapshotPage<T>>> read,
        [EnumeratorCancellation] CancellationToken ct)
    {
        SnapshotCursor? cursor = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await read(cursor);
            Check(page.Items.Count <= ValidationPageSize && (page.Next == null || page.Items.Count > 0) &&
                (page.Next == null || cursor == null || page.Next.SortOrdinal > cursor.SortOrdinal ||
                    page.Next.SortOrdinal == cursor.SortOrdinal && page.Next.EntryKey > cursor.EntryKey), "snapshot page", "bounded progressing page");
            foreach (var item in page.Items) yield return item;
            cursor = page.Next;
        } while (cursor != null);
    }

    private static async IAsyncEnumerable<T> StateItems<T>(Func<StateCursor?, Task<StatePage<T>>> read,
        [EnumeratorCancellation] CancellationToken ct)
    {
        StateCursor? cursor = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await read(cursor);
            Check(page.Items.Count <= ValidationPageSize && (page.Next == null || page.Items.Count > 0) &&
                (page.Next == null || cursor == null || page.Next.SortOrdinal > cursor.SortOrdinal ||
                    page.Next.SortOrdinal == cursor.SortOrdinal && page.Next.Key > cursor.Key), "state page", "bounded progressing page");
            foreach (var item in page.Items) yield return item;
            cursor = page.Next;
        } while (cursor != null);
    }

    private static async Task<T> NextAsync<T>(IAsyncEnumerator<T> enumerator, string path) =>
        await enumerator.MoveNextAsync() ? enumerator.Current : throw Failure(path, "selected item missing");
    private static async Task EndAsync<T>(IAsyncEnumerator<T> enumerator, string path) =>
        Check(!await enumerator.MoveNextAsync(), path, "no extra ordered items");

    private async Task<T> OneAsync<T>(string sql, Func<SqlDataReader, T> read, string path, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var connection = await _session.OpenAsync(ct);
        await using var command = Command(connection, sql, parameters);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Check(await reader.ReadAsync(ct), path, "selected typed row");
        T result = read(reader);
        Check(!await reader.ReadAsync(ct), path, "single typed row");
        return result;
    }

    private async Task<long> ScalarAsync(string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var connection = await _session.OpenAsync(ct);
        await using var command = Command(connection, sql, parameters);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        return (long)(await command.ExecuteScalarAsync(ct) ?? throw Failure("query", "scalar row"));
    }

    private static SqlCommand Command(SqlConnection connection, string sql, params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddRange(parameters); return command;
    }

    private static JsonSerializerOptions CreateModelJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Kind != JsonTypeInfoKind.Object) return;
            for (int i = type.Properties.Count - 1; i >= 0; i--)
                if (type.Properties[i].AttributeProvider is PropertyInfo property && property.SetMethod == null)
                    type.Properties.RemoveAt(i);
        });
        return new(LegacySchemaInspector.JsonOptions) { TypeInfoResolver = resolver };
    }

    // Compare exact serialized mutable MODEL fields, including list/dictionary order, offset spelling and base64 case.
    // Computed getters (Exists, DisplayName, WholeDisplayDate, etc.) must never run during validation.
    private static void EqualModel<T>(T expected, T actual, string path, CancellationToken ct)
    {
        using var left = new ModelBuffer(ct); using var right = new ModelBuffer(ct);
        JsonSerializer.Serialize(left, expected, ModelJson); JsonSerializer.Serialize(right, actual, ModelJson);
        Check(left.Length == right.Length && left.GetBuffer().AsSpan(0, (int)left.Length).SequenceEqual(right.GetBuffer().AsSpan(0, (int)right.Length)),
            path, "exact mutable model fields/collection order");
    }

    private sealed class ModelBuffer(CancellationToken ct) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Budget(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Budget(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Budget(1); base.WriteByte(value); }
        private void Budget(int count)
        { ct.ThrowIfCancellationRequested(); if (Length + count > MetadataBytes) throw Failure("model", "bounded selected aggregate JSON"); }
    }

    private sealed record TableOwner(long Revision, string Schema, string Name);
    private sealed record ChildOwner(long Snapshot, long? Revision, long Order);
    private sealed record RevisionOwner(long Snapshot, long Resource, bool Sealed);
    private sealed record LogicalResource(long Key, DatabaseVersionedResourceKind? Kind, string Original);
    private sealed record SourceTarget(long? Key, StateLinkResolution Resolution);
    private SourceTarget SourceTargetFor(string document, string id) => _sourceTargets[document].TryGetValue(id, out var target)
        ? target : new(null, StateLinkResolution.Missing);
    private static void AddResource(List<LogicalResource> resources, LogicalResource resource, ref long bytes, string path)
    {
        bytes = checked(bytes + 64 + resource.Original.Length * 2L);
        Check(resources.Count < MetadataLimit && bytes <= MetadataBytes, path, "bounded historical identity metadata");
        resources.Add(resource);
    }
    private static (string Schema, string Name) HistoricalName(DatabaseSnapshotResourceChange change, DatabaseSnapshotResourcePayload? payload)
    {
        int separator = change.ResourceKey.IndexOf('|');
        string name = separator < 0 ? string.Empty : change.ResourceKey[(separator + 1)..];
        int dot = name.IndexOf('.');
        if (dot > 0 && dot < name.Length - 1) return (name[..dot], name[(dot + 1)..]);
        if (payload?.DatabaseObject is { } code) return (code.SchemaName, code.ObjectName);
        if (payload?.Table is { } table) return (table.SchemaName, table.TableName);
        return (string.Empty, name);
    }
    private void Add(string table, long count = 1) => _rows[table] = checked(_rows.GetValueOrDefault(table) + count);
    private string File(string document) => _source.StagedDocuments[document].FilePath;
    private SqlParameter Migration() => RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, _source.MigrationIdentity);
    private static SqlParameter Key(long value, string name = "@Key") => RelationalSession.Parameter(name, SqlDbType.BigInt, value);
    private static SqlParameter Text(string name, string value) => RelationalSession.Parameter(name, SqlDbType.NVarChar, value, -1);
    private static long? NullableLong(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static int? NullableInt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static DateTimeOffset? NullableDate(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static bool ExactDate(DateTimeOffset? a, DateTimeOffset? b) => a.HasValue == b.HasValue && (!a.HasValue || a.Value.EqualsExact(b!.Value));
    private static bool Bytes(byte[]? a, byte[]? b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
    private static bool Names(string a, string b, string c, string d) => string.Equals(a, c, StringComparison.OrdinalIgnoreCase) && string.Equals(b, d, StringComparison.OrdinalIgnoreCase);
    private static string P(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    private static string StatePath(string array, long ordinal) => P($"$.{array}[{ordinal}]");
    private Guid Publication(string document, string path) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{_source.MigrationIdentity:N}:{document}:{path}")).AsSpan(0, 16));
    private static FileStream Pin(string path)
    {
        for (var ancestor = new FileInfo(Path.GetFullPath(path)).Directory; ancestor != null; ancestor = ancestor.Parent)
            Check((ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "stage", "no redirected staging ancestor");
        Check((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "stage", "no redirected staged file");
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    private bool WithinStage(string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(_source.DirectoryPath)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private byte[] SourceFingerprint() => _source.ComputeSourceFingerprint();
    private static void Check(bool condition, string path, string invariant) { if (!condition) throw Failure(path, invariant); }
    private static InvalidDataException Failure(string path, string invariant) =>
        new("Migration validation failed at " + path + ": " + invariant + ". Destination remains unpublished.");
}

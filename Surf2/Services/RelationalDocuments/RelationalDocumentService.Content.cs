using System.Collections.Immutable;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using Surf2.Models;
using Surf2.Services.RelationalExplorer;
using Surf2.Services.RelationalGrid;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Access;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalDocuments;

public sealed partial class RelationalDocumentService
{
    public async Task<DocumentTextPlan> DescribeTextAsync(RelationalDocumentAddress address, CancellationToken ct = default)
    {
        RequireEpoch(address);
        if (address.Identity is SnapshotDocumentIdentity { IsTableData: true })
            throw new InvalidOperationException("Table data requires a grid provider, not resident CSV text.");
        if (address.Resource == null)
        {
            var before = new FileInfo(address.DocumentPath);
            before.Refresh();
            if (!before.Exists) throw new FileNotFoundException("The source file was not found.", address.DocumentPath);
            // A conservative decoded-size estimate is available before allocating text.
            // Hashing is streamed and read-only; the subsequent locked read verifies this hash.
            await using var file = new FileStream(address.DocumentPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > RelationalContentStore.MaximumSingleContentBytes) throw new DocumentTextLimitException(Limits.MaximumTextBytes);
            long length = file.Length;
            byte[] hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            var after = new FileInfo(address.DocumentPath);
            after.Refresh();
            if (!after.Exists || after.Length != length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
                throw new GridSourceChangedException("The source file changed while its identity was being read.");
            return new(address, new(new PhysicalDocumentLocator(Epoch, address.DocumentPath, Convert.ToHexString(hash)), "file-utf8-bom-v1",
                PhysicalDocumentIdentity.From(Epoch, address.DocumentPath).CanonicalPath), checked(length * 2));
        }
        address.Resource.RequireSession(_runtime.Session);
        var current = await _runtime.Snapshots.ResolveResourceAsync(address.Resource.ResourceKey, ct: ct).ConfigureAwait(false)
            ?? throw Missing("The selected resource was removed.");
        if (current.RevisionKey != address.Resource.RevisionKey) throw new SnapshotConcurrencyException();
        if (current.Kind == DatabaseVersionedResourceKind.TableMetadata)
        {
            var key = new DocumentTextKey(address.Resource, RelationalIndexStore.TableRendererVersion);
            if (_text.TryDescribe(key, out long cachedBytes)) return new(address, key, cachedBytes);
            var table = await _runtime.Snapshots.ReadTableMetadataAsync(current.RevisionKey, ct).ConfigureAwait(false);
            long estimate = EstimateTableTextBytes(table);
            if (estimate > Limits.MaximumTextBytes) throw new DocumentTextLimitException(Limits.MaximumTextBytes);
            return new(address, key, estimate, table);
        }
        await _runtime.Session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT DATALENGTH(t.Text) FROM surf.DatabaseObjectRevision o
JOIN surf.TextContent t ON t.ContentKey=o.DefinitionContentKey
JOIN surf.SnapshotResourceRevision rr ON rr.RevisionKey=o.RevisionKey
JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=rr.SnapshotKey
WHERE o.RevisionKey=@Revision AND rr.ResourceKey=@Resource AND rr.SnapshotKey=@Snapshot
AND rr.IsSealed=1 AND s.IsPublished=1;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Revision", SqlDbType.BigInt, current.RevisionKey));
        command.Parameters.Add(RelationalSession.Parameter("@Resource", SqlDbType.BigInt, current.ResourceKey));
        command.Parameters.Add(RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, current.SnapshotKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        object? size = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (size is not long bytes) throw Missing("The selected definition is not published.");
        if (bytes > Limits.MaximumTextBytes) throw new DocumentTextLimitException(Limits.MaximumTextBytes);
        // Empty/whitespace definitions render a comment with bracket-escaped names instead.
        long estimateBytes = Math.Max(bytes, checked((long)address.DisplayName.Length * 4 + 256));
        if (estimateBytes > Limits.MaximumTextBytes) throw new DocumentTextLimitException(Limits.MaximumTextBytes);
        return new(address, new(address.Resource, "definition-legacy-empty-v1"), estimateBytes);
    }

    public Task<SelectedDocumentText> ReadTextAsync(DocumentTextPlan plan, CancellationToken ct = default)
    {
        RequireEpoch(plan.Address);
        plan.Key.Locator.RequireSession(_runtime.Session);
        return _text.ReadAsync(plan.Key, async token =>
        {
            if (plan.Key.Locator is PhysicalDocumentLocator physical)
            {
                await using var file = await IndexedFileRead.OpenAsync(plan.Address.DocumentPath,
                    checked((int)(Limits.MaximumTextBytes / 2)), token).ConfigureAwait(false);
                if (DocumentDigest.Parse(file.Fingerprint.Sha256) != physical.Fingerprint) throw new DocumentReferenceChangedException();
                return file.Text;
            }
            if (plan.Key.RendererPolicy == RelationalIndexStore.TableRendererVersion)
            {
                var table = plan.Table ?? await _runtime.Snapshots.ReadTableMetadataAsync(((SnapshotResourceLocator)plan.Key.Locator).RevisionKey, token).ConfigureAwait(false);
                return await Task.Run(() => RenderTable(table, Limits.MaximumTextBytes), token).ConfigureAwait(false);
            }
            var snapshots = Limits.MaximumTextBytes <= 16 * 1024 * 1024 ? _runtime.Snapshots :
                new RelationalSnapshotStore(_runtime.Session, new RelationalContentStore(), new SnapshotReadLimits { SelectedDefinitionBytes = Limits.MaximumTextBytes });
            var value = await snapshots.ReadObjectAsync(((SnapshotResourceLocator)plan.Key.Locator).RevisionKey, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value.Definition) ? $"-- No definition imported for {value.FullName}." : value.Definition;
        }, ct);
    }

    // The adapter contains ONE selected metadata aggregate. No library, history or rows are constructed.
    public static string RenderTable(TableMetadataDetails details, long maximumBytes)
    {
        if (EstimateTableTextBytes(details) > maximumBytes) throw new DocumentTextLimitException(maximumBytes);
        var selected = new DatabaseMetadataSnapshot
        {
            Columns = details.Columns.Select(c => c.Column).ToList(),
            PrimaryKeys = details.PrimaryKeys.Select(c => c.Column).ToList()
        };
        string text = new DatabaseDocumentService().CreateTableDocument(selected, details.Table);
        if ((long)text.Length * 2 > maximumBytes) throw new DocumentTextLimitException(maximumBytes);
        return text;
    }

    public static long EstimateTableTextBytes(TableMetadataDetails details)
    {
        // Bracket escaping can double a name. Fixed slack covers numeric/type/nullability comments.
        long chars = 256 + ((long)details.Table.SchemaName.Length + details.Table.TableName.Length) * 4;
        foreach (var column in details.Columns)
            chars = checked(chars + (long)column.Column.ColumnName.Length * 2 + column.Column.DataType.Length + 160);
        return checked(chars * 2);
    }

    public async Task<IDataGridSource> OpenGridAsync(RelationalDocumentAddress address, CancellationToken ct = default)
    {
        RequireEpoch(address);
        if (address.Resource == null)
            return await CsvGridSource.OpenAsync(address.DocumentPath, cancellationToken: ct).ConfigureAwait(false);
        var identity = address.Resource;
        var current = await _runtime.Snapshots.ResolveResourceAsync(identity.ResourceKey, ct: ct).ConfigureAwait(false);
        if (current == null || current.Kind != DatabaseVersionedResourceKind.TableData || current.RevisionKey != identity.RevisionKey)
            throw new SnapshotConcurrencyException();
        var descriptor = await _runtime.CapturedData.GetForRevisionAsync(identity.RevisionKey, ct).ConfigureAwait(false)
            ?? throw Missing("This table has no complete captured dataset.");
        var metadata = await FindByNameAsync(identity.SnapshotKey, DatabaseVersionedResourceKind.TableMetadata,
            SqlName.FormatPlainMultipartName(current.SchemaName, current.ObjectName), ct).ConfigureAwait(false);
        IReadOnlyList<GridColumn>? columns = null;
        if (metadata != null)
        {
            var table = await _runtime.Snapshots.ReadTableMetadataAsync(metadata.RevisionKey, ct).ConfigureAwait(false);
            // The old generated CSV uses metadata SourceOrdinal order, stable within ties.
            if (table.Columns.Count != 0) columns = table.Columns.OrderBy(c => c.Column.Ordinal)
                .Select((c, i) => new GridColumn(i, c.Column.ColumnName, c.Column.ColumnName)).ToArray();
        }
        return await CapturedGridSource.OpenAsync(_runtime.CapturedData, descriptor.Summary.DataSetKey,
            cancellationToken: ct, displayColumns: columns).ConfigureAwait(false);
    }

    public async Task<RelationalDocumentAddress?> RelatedTableDataAsync(RelationalDocumentAddress table,
        ExplorerScope? scope, CancellationToken ct = default)
    {
        if (table.Resource == null || table.Identity is not SnapshotDocumentIdentity { IsTableData: false }) return null;
        var metadata = await _runtime.Snapshots.ResolveResourceAsync(table.Resource.ResourceKey, ct: ct).ConfigureAwait(false);
        if (metadata?.Kind != DatabaseVersionedResourceKind.TableMetadata) return null;
        var detail = await _runtime.Snapshots.ReadTableMetadataAsync(metadata.RevisionKey, ct).ConfigureAwait(false);
        if (!detail.Table.HasFullData) return null;
        var data = await FindByNameAsync(metadata.SnapshotKey, DatabaseVersionedResourceKind.TableData,
            SqlName.FormatPlainMultipartName(metadata.SchemaName, metadata.ObjectName), ct).ConfigureAwait(false);
        return data == null ? null : await AddressAsync(data, scope, ct, table.ExplorerAddress?.Node.ScopeResourceKey).ConfigureAwait(false);
    }

    public async Task<IndexedDocumentSummary> ReferenceDocumentAsync(ExplorerScope scope, SymbolSummary symbol, CancellationToken ct = default)
    {
        if (symbol.Freshness != IndexFreshness.Indexed) throw new DocumentReferenceChangedException();
        var context = scope.Context with { DocumentKeys = ImmutableArray.Create(symbol.DocumentKey), RestrictDocumentKeys = true };
        var page = await _runtime.Index.ReadDocumentsPageAsync(context, token: ct).ConfigureAwait(false);
        var selected = page.Items.FirstOrDefault(d => d.DocumentKey == symbol.DocumentKey);
        if (selected == null || selected.RevisionKey != symbol.RevisionKey || selected.Freshness != IndexFreshness.Indexed)
            throw new DocumentReferenceChangedException();
        return selected;
    }

    public static void ValidateReferenceSource(IndexedDocumentSummary indexed, RelationalDocumentAddress address, DocumentTextPlan plan)
    {
        if (indexed.Freshness != IndexFreshness.Indexed || indexed.RevisionKey == null) throw new DocumentReferenceChangedException();
        if (address.Resource != null)
        {
            if (indexed.SourceRevisionKey != address.Resource.RevisionKey) throw new DocumentReferenceChangedException();
        }
        else if (plan.Key.Locator is not PhysicalDocumentLocator physical ||
            indexed.Fingerprint == null || DocumentDigest.Parse(indexed.Fingerprint) != physical.Fingerprint) throw new DocumentReferenceChangedException();
    }

    public async Task<RelationalDocumentAddress> ResolveReferenceAddressAsync(ExplorerScope scope,
        IndexedDocumentSummary indexed, CancellationToken ct = default)
    {
        if (indexed.Kind == IndexedDocumentKind.File)
            return await ResolveAsync(indexed.PhysicalPath ?? indexed.Locator, scope, ct: ct).ConfigureAwait(false);
        if (indexed.Kind is not (IndexedDocumentKind.Definition or IndexedDocumentKind.TableCode)) throw Missing("This reference is not a code document.");
        await _runtime.Session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotKey,SnapshotResourceKey FROM surf.Document WHERE DocumentKey=@Document AND CurrentRevisionKey=@Revision;";
        command.Parameters.Add(RelationalSession.Parameter("@Document", SqlDbType.BigInt, indexed.DocumentKey));
        command.Parameters.Add(RelationalSession.Parameter("@Revision", SqlDbType.BigInt, indexed.RevisionKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        long snapshot, resource;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1)) throw new DocumentReferenceChangedException();
            snapshot = reader.GetInt64(0); resource = reader.GetInt64(1);
        }
        return await ResolveResourceAsync(snapshot, resource, scope, ct).ConfigureAwait(false);
    }

    public static ReferenceEntity Reference(SymbolSummary symbol, string documentPath)
    {
        var value = symbol.Definition;
        return new() { Name = value.Name, QualifiedName = value.QualifiedName, Kind = value.Kind, FilePath = documentPath,
            LineNumber = value.LineNumber, ColumnNumber = value.ColumnNumber, EndLineNumber = value.EndLineNumber,
            EndColumnNumber = value.EndColumnNumber, ParameterCount = value.ParameterCount,
            MinimumArgumentCount = value.MinimumArgumentCount, MaximumArgumentCount = value.MaximumArgumentCount,
            Language = value.Language, ContainerName = value.ContainerName };
    }

    public async Task ValidateAddressAsync(RelationalDocumentAddress address, ExplorerScope? scope, CancellationToken ct = default)
    {
        RequireEpoch(address);
        await RequireCurrentAsync(scope, ct).ConfigureAwait(false);
        if (address.Resource != null)
        {
            var current = await _runtime.Snapshots.ResolveResourceAsync(address.Resource.ResourceKey, ct: ct).ConfigureAwait(false);
            if (current == null || current.RevisionKey != address.Resource.RevisionKey || current.SnapshotKey != address.Resource.SnapshotKey)
                throw new SnapshotConcurrencyException();
        }
    }

    public async Task ValidateTextPlanAsync(DocumentTextPlan plan, ExplorerScope? scope, CancellationToken ct = default)
    {
        await ValidateAddressAsync(plan.Address, scope, ct).ConfigureAwait(false);
        if (plan.Key.Locator is not PhysicalDocumentLocator file) return;
        await using var source = new FileStream(plan.Address.DocumentPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > RelationalContentStore.MaximumSingleContentBytes) throw new DocumentTextLimitException(Limits.MaximumTextBytes);
        var hash = await SHA256.HashDataAsync(source, ct).ConfigureAwait(false);
        if (DocumentDigest.Parse(Convert.ToHexString(hash)) != file.Fingerprint) throw new DocumentReferenceChangedException();
    }

    public async Task<ImmutableArray<SymbolSummary>> PrioritizeReferencesAsync(ImmutableArray<SymbolSummary> candidates,
        OpenDocumentState? source, string sourcePath, CancellationToken ct = default)
    {
        if (source?.TargetState != SavedDocumentTargetState.Resolved || source.BoundSnapshotKey is not > 0 || source.BoundResourceKey is not > 0)
            return candidates.OrderByDescending(s => s.Definition.Locator.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        await _runtime.Session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocumentKey FROM surf.Document WHERE SnapshotKey=@Snapshot AND SnapshotResourceKey=@Resource AND Kind IN (1,2);";
        command.Parameters.Add(RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, source.BoundSnapshotKey));
        command.Parameters.Add(RelationalSession.Parameter("@Resource", SqlDbType.BigInt, source.BoundResourceKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        var keys = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetInt64(0));
        return candidates.OrderByDescending(s => keys.Contains(s.DocumentKey)).ToImmutableArray();
    }
    private void RequireEpoch(RelationalDocumentAddress address)
    {
        if (address.Identity.Epoch != Epoch) throw new InvalidOperationException("The document belongs to another database session.");
    }
}

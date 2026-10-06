using System.Collections.Immutable;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Capture;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public sealed class RelationalExplorerSearchSources : IExplorerSearchSources
{
    private readonly RelationalSession _session;
    private readonly RelationalIndexStore _index;
    private readonly RelationalSnapshotStore _snapshots;
    private readonly RelationalCaptureStore _capture;
    private readonly ExplorerLimits _limits;
    public RelationalExplorerSearchSources(RelationalSession session, RelationalIndexStore index,
        RelationalSnapshotStore snapshots, RelationalCaptureStore capture, ExplorerLimits? limits = null)
    {
        _session = session; _index = index; _snapshots = snapshots; _capture = capture;
        _limits = limits ?? new(); _limits.Validate();
    }

    public async Task<ImmutableArray<ExplorerIndexBinding>> BindIndexDocumentsAsync(ExplorerScope scope,
        ImmutableArray<ExplorerNodeSummary> nodes, CancellationToken ct)
    {
        if (nodes.Length > 128) throw new ArgumentException("Document binding batches contain at most 128 occurrences.");
        if (nodes.IsEmpty) return [];
        var result = ImmutableArray.CreateBuilder<ExplorerIndexBinding>();
        var budget = new ExplorerMetadataBudget(_limits);
        var bound = new HashSet<string>(StringComparer.Ordinal);
        long resourceAfter = 0, documentAfter = 0;
        bool exhausted;
        do
        {
            await _session.RequireReadyAsync(ct).ConfigureAwait(false);
            await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
            await IndexSql.CheckContextAsync(connection, transaction, _session, scope.Context, ct).ConfigureAwait(false);
            await using var command = IndexSql.Command(connection, transaction, $"""
SELECT TOP (@Take) m.ScopeResourceKey,d.DocumentKey,d.Kind,d.SnapshotResourceKey,d.DiagramRevisionKey,
 LEFT(f.OriginalPath,65537),r.SourceRevisionKey,{IndexSql.FreshnessExpression}
FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
JOIN surf.Document d ON d.DocumentKey=m.DocumentKey
LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
LEFT JOIN surf.FileSource f ON f.FileSourceKey=d.FileSourceKey
{IndexSql.OwnerJoins}
WHERE {IndexSql.ScopePredicate}
 AND EXISTS(SELECT 1 FROM @Owners o WHERE o.Id=m.ScopeResourceKey)
 AND (d.Kind IN (1,2) AND EXISTS(SELECT 1 FROM @SnapshotResources k WHERE k.Id=d.SnapshotResourceKey)
   OR d.Kind=3 AND EXISTS(SELECT 1 FROM @DiagramRevisions k WHERE k.Id=d.DiagramRevisionKey)
   OR d.Kind=0 AND EXISTS(SELECT 1 FROM @Paths n WHERE n.IsAscii=0 OR f.PathIsAscii=0 OR f.PathHash=n.NameHash))
 AND (m.ScopeResourceKey>@AfterResource OR m.ScopeResourceKey=@AfterResource AND d.DocumentKey>@AfterDocument)
ORDER BY m.ScopeResourceKey,d.DocumentKey;
""");
            IndexSql.ScopeParameters(command, scope.Context);
            IndexSql.Keys(command, "@Owners", nodes.Where(n => n.ScopeResourceKey.HasValue).Select(n => n.ScopeResourceKey!.Value));
            IndexSql.Keys(command, "@SnapshotResources", nodes.Where(n => n.SnapshotResourceKey.HasValue).Select(n => n.SnapshotResourceKey!.Value));
            IndexSql.Keys(command, "@DiagramRevisions", nodes.Where(n => n.DiagramRevisionKey.HasValue).Select(n => n.DiagramRevisionKey!.Value));
            var paths = new DataTable();
            paths.Columns.Add("Ordinal", typeof(int)); paths.Columns.Add("NameHash", typeof(byte[])); paths.Columns.Add("IsAscii", typeof(bool));
            foreach (var node in nodes.Where(IsPhysicalFile))
            {
                string path = Path.GetFullPath(node.FullPath);
                paths.Rows.Add(paths.Rows.Count, ReferenceMetadata.LookupHash(path), ReferenceMetadata.IsAscii(path));
            }
            command.Parameters.Add(new SqlParameter("@Paths", SqlDbType.Structured) { TypeName = "surf.IndexNameBatch", Value = paths });
            IndexSql.Add(command, "@Take", SqlDbType.Int, _limits.PageSize);
            IndexSql.Add(command, "@AfterResource", SqlDbType.BigInt, resourceAfter);
            IndexSql.Add(command, "@AfterDocument", SqlDbType.BigInt, documentAfter);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            int count = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                count++; resourceAfter = reader.GetInt64(0); documentAfter = reader.GetInt64(1);
                int kind = reader.GetInt32(2);
                long? resource = Key(reader, 3), diagram = Key(reader, 4), revision = Key(reader, 6);
                string? path = reader.IsDBNull(5) ? null : reader.GetString(5);
                if (path?.Length > 65536) throw new ExplorerLimitException("A physical locator exceeds its metadata limit.");
                budget.Add(path ?? "");
                foreach (var node in nodes)
                {
                    if (node.ScopeResourceKey != resourceAfter || bound.Contains(node.OccurrenceKey)) continue;
                    bool matches = kind switch
                    {
                        0 => IsPhysicalFile(node) && path != null && Path.GetFullPath(node.FullPath).Equals(path, StringComparison.OrdinalIgnoreCase),
                        1 => node.Category != ExplorerCategory.Tables && node.SnapshotResourceKey == resource,
                        2 => node.Category == ExplorerCategory.Tables && node.SnapshotResourceKey == resource,
                        3 => node.Role == ExplorerNodeRole.Diagram && node.DiagramRevisionKey == diagram, _ => false
                    };
                    if (!matches) continue;
                    bound.Add(node.OccurrenceKey);
                    result.Add(new(node.OccurrenceKey, documentAfter, resourceAfter, revision, (IndexFreshness)reader.GetInt32(7)));
                }
            }
            exhausted = count < _limits.PageSize;
        } while (!exhausted);
        return result.ToImmutable();
    }

    public IAsyncEnumerable<IndexSearchBatch> SearchIndexAsync(IndexSearchRequest request, CancellationToken ct) => _index.SearchAsync(request, ct);

    public async Task<string> ReadCurrentContentAsync(ExplorerNodeSummary node, int maximumCharacters, CancellationToken ct)
    {
        if (IsPhysicalFile(node))
        {
            await using var file = await IndexedFileRead.OpenAsync(node.FullPath, maximumCharacters, ct).ConfigureAwait(false);
            return file.Text;
        }
        if (node.Role == ExplorerNodeRole.Diagram)
            return node.DiagramRevisionKey == null ? "" : (await _index.PrepareDiagramAsync(node.DiagramRevisionKey.Value, ct).ConfigureAwait(false)).Text;
        if (node.Role == ExplorerNodeRole.DatabaseDocument && node.SourceRevisionKey.HasValue)
            return (await _index.PrepareDefinitionAsync(node.SourceRevisionKey.Value, node.FullPath, "", maximumCharacters, ct).ConfigureAwait(false)).Text;
        throw new ArgumentException("This node has no searchable document content.", nameof(node));
    }

    public async Task<ExplorerTableSearch> SearchTableAsync(ExplorerNodeSummary node, IndexTextMatcher matcher,
        int maximumCharacters, CancellationToken ct)
    {
        if (node.Category != ExplorerCategory.Tables || node.SourceRevisionKey == null || node.SnapshotKey == null)
            throw new ArgumentException("A selected table metadata revision is required.", nameof(node));
        bool codeMatched = false;
        IndexScanStatus? failure = null;
        var columns = new SortedSet<int>();
        try
        {
            var code = await _index.PrepareTableCodeAsync(node.SourceRevisionKey.Value, node.FullPath, "", ct).ConfigureAwait(false);
            if (code.Text.Length > maximumCharacters) throw new IndexDocumentTooLargeException();
            codeMatched = matcher.IsMatch(code.Text);
        }
        catch (Exception ex) when (SearchFailure(ex) != null) { failure = SearchFailure(ex); }
        try
        {
            var metadata = await _snapshots.ReadTableMetadataAsync(node.SourceRevisionKey.Value, ct).ConfigureAwait(false);
            var headers = metadata.Columns.Where(c => c.Column.SchemaName.Equals(node.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    c.Column.TableName.Equals(node.ObjectName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Column.Ordinal).Select(c => c.Column.ColumnName).ToImmutableArray();
            var data = await ResolveTableDataAsync(node, ct).ConfigureAwait(false);
            if (data != null)
            {
                var descriptor = await _capture.GetForRevisionAsync(data.RevisionKey, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The selected captured dataset is missing.");
                bool first = true;
                await foreach (var row in _capture.StreamRowsAsync(descriptor.Summary.DataSetKey, cancellationToken: ct).ConfigureAwait(false))
                {
                    if (first)
                    {
                        if (headers.IsEmpty) headers = InferLegacyHeaders(row.Value);
                        var budget = new ExplorerMetadataBudget(_limits);
                        foreach (string header in headers) budget.Add(header);
                        first = false;
                    }
                    if (row.EstimatedBytes > (long)maximumCharacters * 2) { failure = IndexScanStatus.TooLarge; continue; }
                    foreach (int column in ExplorerCompatibility.MatchingColumns(row.Value, headers, matcher, ct, columns)) columns.Add(column);
                    if (columns.Count == headers.Length) break;
                }
            }
        }
        catch (Exception ex) when (SearchFailure(ex) != null) { failure ??= SearchFailure(ex); }
        ct.ThrowIfCancellationRequested();
        return new(codeMatched, columns.ToImmutableArray(), failure ??
            (codeMatched || columns.Count != 0 ? IndexScanStatus.Matched : IndexScanStatus.NotMatched), failure == null);
    }

    public async Task<SnapshotResourceSummary?> ResolveTableDataAsync(ExplorerNodeSummary table, CancellationToken ct = default)
    {
        if (!table.SnapshotKey.HasValue) throw new ArgumentException("A snapshot membership is required.", nameof(table));
        SnapshotCursor? cursor = null;
        var budget = new ExplorerMetadataBudget(_limits);
        do
        {
            var page = await _snapshots.ListResourcesAsync(table.SnapshotKey.Value, DatabaseVersionedResourceKind.TableData,
                pageSize: _limits.PageSize, cursor: cursor, ct: ct).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                budget.Add(item.SchemaName, item.ObjectName);
                if (item.SchemaName.Equals(table.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    item.ObjectName.Equals(table.ObjectName, StringComparison.OrdinalIgnoreCase)) return item;
            }
            cursor = page.Next;
        } while (cursor != null);
        return null;
    }

    public static ImmutableArray<string> InferLegacyHeaders(JsonElement firstRow) => firstRow.ValueKind == JsonValueKind.Object
        ? firstRow.EnumerateObject().Select(p => p.Name).ToImmutableArray() : [];
    internal static bool IsPhysicalFile(ExplorerNodeSummary node) => !node.IsDirectory &&
        (node.Role == ExplorerNodeRole.PhysicalFile || node.Role == ExplorerNodeRole.Resource && node.ResourceKind == ResourceKind.File);
    internal static IndexScanStatus? SearchFailure(Exception ex) => ex switch
    {
        RegexMatchTimeoutException => IndexScanStatus.TimedOut,
        IndexDocumentTooLargeException or ExplorerLimitException or CaptureLimitException or SnapshotReadLimitException => IndexScanStatus.TooLarge,
        SnapshotConcurrencyException => IndexScanStatus.Changed,
        FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => IndexScanStatus.Missing,
        UnauthorizedAccessException => IndexScanStatus.Inaccessible,
        IOException => IndexScanStatus.Changed, _ => null
    };
    private static long? Key(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}

using System.Collections.Immutable;
using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed record ExplorerResourceCandidate
{
    internal ExplorerResourceCandidate(ExistingScopeResourceCandidate header, long? sourceScopeKey,
        long? sourceScopeResourceKey, long? snapshotKey, long? diagramKey, ExplorerResourceCatalogueCursor origin)
    {
        if ((sourceScopeKey.HasValue != sourceScopeResourceKey.HasValue) || sourceScopeKey <= 0 || sourceScopeResourceKey <= 0 ||
            snapshotKey <= 0 || diagramKey <= 0 || snapshotKey.HasValue && diagramKey.HasValue ||
            snapshotKey.HasValue && header.Kind != ResourceKind.DatabaseSnapshot || diagramKey.HasValue && header.Kind != ResourceKind.Diagram ||
            sourceScopeKey == null && snapshotKey == null && diagramKey == null)
            throw new ArgumentException("Resource header has an invalid typed owner.");
        Header = header; SourceScopeKey = sourceScopeKey; SourceScopeResourceKey = sourceScopeResourceKey;
        SnapshotKey = snapshotKey; DiagramKey = diagramKey; Origin = origin;
    }
    public ExistingScopeResourceCandidate Header { get; }
    public long? SourceScopeKey { get; }
    public long? SourceScopeResourceKey { get; }
    public long? SnapshotKey { get; }
    public long? DiagramKey { get; }
    public ExplorerResourceCatalogueCursor Origin { get; }
}
public sealed record ExplorerResourceCatalogueCursor(Guid Epoch, string ScopeCatalogueVersion,
    string SnapshotCatalogueVersion, string DiagramCatalogueVersion, int SourceKind, long AfterSourceKey);
public sealed record ExplorerResourceCataloguePage(ImmutableArray<ExplorerResourceCandidate> Items,
    ExplorerResourceCatalogueCursor? Next);

// A query picker reads only one bounded header page. Typed nullable source/target keys survive selection;
// the state owner, not this read-only catalogue, publishes the chosen relationship.
public sealed class ExplorerResourceCatalogue(RelationalSession session, ExplorerLimits? limits = null)
{
    private readonly ExplorerLimits _limits = limits ?? new();
    public async Task<ExplorerResourceCataloguePage> ListAsync(ExplorerScope target,
        ExplorerResourceCatalogueCursor? cursor = null, int pageSize = 64, CancellationToken ct = default)
    {
        _limits.Validate();
        if (pageSize is < 1 or > 128 || cursor is { SourceKind: < 0 or > 2 } || cursor is { AfterSourceKey: < 0 })
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await session.RequireReadyAsync(ct).ConfigureAwait(false);
        if (target.Context.Epoch != session.Epoch) throw new IndexGenerationChangedException();
        await using var connection = await session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        string scopeVersion, snapshotVersion, diagramVersion;
        await using (var head = IndexSql.Command(connection, transaction, """
SELECT COALESCE(s.Version,CONVERT(binary(8),0)),h.RowVersion,COALESCE(d.Version,CONVERT(binary(8),0))
FROM surf.SnapshotCatalogueHead h LEFT JOIN surf.StateCatalogueGeneration s ON s.ProfileKey=h.UserKey AND s.Kind=0
LEFT JOIN surf.StateCatalogueGeneration d ON d.ProfileKey=h.UserKey AND d.Kind=1 WHERE h.UserKey=1;
"""))
        {
            using var cancel = RelationalSession.CancelCommand(head, ct);
            await using var reader = await head.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("Source catalogue heads are missing.");
            scopeVersion = IndexSql.Hex((byte[])reader[0]); snapshotVersion = IndexSql.Hex((byte[])reader[1]); diagramVersion = IndexSql.Hex((byte[])reader[2]);
        }
        if (cursor != null && (cursor.Epoch != session.Epoch || cursor.ScopeCatalogueVersion != scopeVersion ||
            cursor.SnapshotCatalogueVersion != snapshotVersion || cursor.DiagramCatalogueVersion != diagramVersion))
            throw new IndexGenerationChangedException();
        await using var command = IndexSql.Command(connection, transaction, """
WITH Headers AS (
 SELECT 0 SourceKind,sr.ScopeResourceKey SourceKey,s.ScopeKey,s.ScopeId SourceScopeId,s.Name SourceName,sr.Kind,sr.Path,
 sr.DisplayNameOverride,dc.Text DetailsOverride,sr.IncludeChildren,sr.SnapshotKey,sr.DiagramKey,
 db.DisplayName DatabaseNameLabel,db.DatabaseName,di.Name DiagramName
 FROM surf.ScopeResource sr JOIN surf.Scope s ON s.ScopeKey=sr.ScopeKey
 JOIN surf.TextContent dc ON dc.ContentKey=sr.DetailsOverrideContentKey
 LEFT JOIN surf.DatabaseSnapshot db ON db.SnapshotKey=sr.SnapshotKey AND db.IsPublished=1
 LEFT JOIN surf.Diagram di ON di.DiagramKey=sr.DiagramKey WHERE s.ProfileKey=1
 UNION ALL
 SELECT 1,db.SnapshotKey,NULL,NULL,N'Database Snapshots',2,db.OriginalSnapshotId,db.DisplayName,db.DatabaseName,
 CONVERT(bit,1),db.SnapshotKey,NULL,db.DisplayName,db.DatabaseName,NULL FROM surf.DatabaseSnapshot db WHERE db.UserKey=1 AND db.IsPublished=1
 UNION ALL
 SELECT 2,di.DiagramKey,NULL,NULL,N'Diagrams',3,di.DiagramId,di.Name,N'Diagram',CONVERT(bit,1),NULL,di.DiagramKey,
 NULL,NULL,di.Name FROM surf.Diagram di WHERE di.ProfileKey=1 AND di.CurrentRevisionKey IS NOT NULL
)
SELECT TOP(@Take) SourceKind,SourceKey,ScopeKey,LEFT(SourceScopeId,65537),LEFT(SourceName,65537),Kind,LEFT(Path,65537),
 LEFT(DisplayNameOverride,65537),LEFT(DetailsOverride,65537),IncludeChildren,SnapshotKey,DiagramKey,
 LEFT(DatabaseNameLabel,65537),LEFT(DatabaseName,65537),LEFT(DiagramName,65537)
FROM Headers WHERE SourceKind>@SourceKind OR (SourceKind=@SourceKind AND SourceKey>@After)
ORDER BY SourceKind,SourceKey;
""");
        IndexSql.Add(command, "@Take", SqlDbType.Int, pageSize);
        IndexSql.Add(command, "@SourceKind", SqlDbType.Int, cursor?.SourceKind ?? 0);
        IndexSql.Add(command, "@After", SqlDbType.BigInt, cursor?.AfterSourceKey ?? 0);
        var items = ImmutableArray.CreateBuilder<ExplorerResourceCandidate>();
        var budget = new ExplorerMetadataBudget(_limits); int count = 0, sourceKind = 0; long after = 0;
        using (var cancel = RelationalSession.CancelCommand(command, ct))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                count++; sourceKind = reader.GetInt32(0); after = reader.GetInt64(1);
                string Field(int ordinal)
                {
                    string value = reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
                    if (value.Length > 65536) throw new ExplorerLimitException("Resource header exceeds its metadata limit.");
                    return value;
                }
                long? Key(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
                string sourceId = Field(3), sourceName = Field(4), path = Field(6), alias = Field(7), detailsOverride = Field(8);
                var kind = (ResourceKind)reader.GetInt32(5);
                string databaseLabel = Field(12), database = Field(13), diagram = Field(14);
                budget.Add(sourceId, sourceName, path, alias, detailsOverride, databaseLabel, database, diagram);
                if (sourceKind == 0 && sourceId.Equals(target.ScopeId, StringComparison.OrdinalIgnoreCase)) continue;
                if (target.Resources.Any(r => SameIdentity(r.Kind, r.Path, kind, path))) continue;
                string name = !string.IsNullOrWhiteSpace(alias) ? alias : kind == ResourceKind.DatabaseSnapshot
                    ? !string.IsNullOrWhiteSpace(databaseLabel) ? databaseLabel : !string.IsNullOrWhiteSpace(database) ? database : string.IsNullOrWhiteSpace(path) ? "Database" : path
                    : kind == ResourceKind.Diagram ? !string.IsNullOrWhiteSpace(diagram) ? diagram : string.IsNullOrWhiteSpace(path) ? "Diagram" : path
                    : string.IsNullOrWhiteSpace(Path.GetFileName(path)) ? path : Path.GetFileName(path);
                string details = !string.IsNullOrWhiteSpace(detailsOverride) ? detailsOverride : kind == ResourceKind.DatabaseSnapshot
                    ? string.IsNullOrEmpty(database) ? path : database : kind == ResourceKind.Diagram && Key(11) != null ? "Diagram" : path;
                var header = new ExistingScopeResourceCandidate(name, kind == ResourceKind.DatabaseSnapshot ? "Database" : kind.ToString(),
                    details, string.IsNullOrWhiteSpace(sourceName) ? "Scope" : sourceName, kind, path, alias, detailsOverride, reader.GetBoolean(9));
                items.Add(new(header, Key(2), sourceKind == 0 ? after : null, Key(10), Key(11),
                    new(session.Epoch, scopeVersion, snapshotVersion, diagramVersion, sourceKind, after)));
            }
        return new(items.ToImmutable(), count < pageSize ? null : new(session.Epoch, scopeVersion, snapshotVersion, diagramVersion, sourceKind, after));
    }

    internal static bool SameIdentity(ResourceKind firstKind, string first, ResourceKind secondKind, string second)
    {
        if (firstKind != secondKind) return false;
        static string Normalize(ResourceKind kind, string path)
        {
            if (kind is not (ResourceKind.File or ResourceKind.Folder)) return path.Trim();
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }
        return Normalize(firstKind, first).Equals(Normalize(secondKind, second), StringComparison.OrdinalIgnoreCase);
    }
}

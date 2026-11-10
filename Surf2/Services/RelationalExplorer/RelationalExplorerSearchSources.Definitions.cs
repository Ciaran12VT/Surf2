using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalExplorerSearchSources
{
    public async Task<ImmutableArray<ExplorerSearchOutcome>> SearchDefinitionsAsync(ExplorerScope scope,
        ImmutableArray<ExplorerNodeSummary> nodes, IndexTextMatcher matcher, int maximumCharacters, CancellationToken ct)
    {
        var definitions = nodes.Where(node => node.Role == ExplorerNodeRole.DatabaseDocument &&
            node.Category != ExplorerCategory.Tables && node.SourceRevisionKey.HasValue).ToArray();
        if (definitions.Length == 0) return [];
        if (definitions.Length > 128 || maximumCharacters < 1 || maximumCharacters > RelationalContentStore.MaximumSingleContentBytes / 2)
            throw new ArgumentOutOfRangeException(nameof(nodes));
        foreach (var node in definitions)
            if (!scope.Resources.Any(resource => resource.IsLoaded && resource.ScopeResourceKey == node.ScopeResourceKey &&
                    resource.Snapshot?.SnapshotKey == node.SnapshotKey))
                throw new IndexGenerationChangedException();
        var outcomes = new Dictionary<long, IndexScanStatus>();
        var requested = definitions.Select(node => new { ResourceKey = node.SnapshotResourceKey!.Value,
            RevisionKey = node.SourceRevisionKey!.Value, SnapshotKey = node.SnapshotKey!.Value }).Distinct().ToArray();
        await _session.RequireReadyAsync(ct).ConfigureAwait(false);
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var command = IndexSql.Command(connection, null, """
SELECT requested.ResourceKey, DATALENGTH(c.Text), CASE WHEN DATALENGTH(c.Text)<=@MaximumBytes THEN CONVERT(varbinary(max),c.Text) END
FROM OPENJSON(@Resources) WITH (ResourceKey bigint '$.ResourceKey', RevisionKey bigint '$.RevisionKey', SnapshotKey bigint '$.SnapshotKey') requested
JOIN surf.SnapshotResource r ON r.ResourceKey=requested.ResourceKey AND r.SnapshotKey=requested.SnapshotKey
 AND r.CurrentRevisionKey=requested.RevisionKey
JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey AND s.IsPublished=1
JOIN surf.SnapshotResourceRevision revision ON revision.RevisionKey=r.CurrentRevisionKey AND revision.IsSealed=1
JOIN surf.DatabaseObjectRevision definition ON definition.RevisionKey=revision.RevisionKey
JOIN surf.TextContent c ON c.ContentKey=definition.DefinitionContentKey;
""");
        IndexSql.Add(command, "@Resources", SqlDbType.NVarChar, JsonSerializer.Serialize(requested), -1);
        IndexSql.Add(command, "@MaximumBytes", SqlDbType.BigInt, (long)maximumCharacters * 2);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long key = reader.GetInt64(0);
            long byteCount = reader.GetInt64(1);
            if (byteCount > (long)maximumCharacters * 2)
            {
                outcomes[key] = IndexScanStatus.TooLarge;
                continue;
            }
            await using var textStream = reader.GetStream(2);
            string text = await IndexTextMatcher.ReadRawUtf16Async(textStream, byteCount, maximumCharacters, ct).ConfigureAwait(false);
            try { outcomes[key] = matcher.IsMatch(text) ? IndexScanStatus.Matched : IndexScanStatus.NotMatched; }
            catch (RegexMatchTimeoutException) { outcomes[key] = IndexScanStatus.TimedOut; }
        }
        return definitions.Select(node =>
        {
            var status = outcomes.GetValueOrDefault(node.SnapshotResourceKey!.Value, IndexScanStatus.Changed);
            return new ExplorerSearchOutcome(node.OccurrenceKey, status, IndexFreshness.Unindexed,
                status is IndexScanStatus.Matched or IndexScanStatus.NotMatched);
        }).ToImmutableArray();
    }
}

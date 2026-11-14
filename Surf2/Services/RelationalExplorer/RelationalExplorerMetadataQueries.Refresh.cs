using System.Collections.Immutable;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;
using Surf2.Storage.Relational.Snapshots;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalExplorerMetadataQueries
{
    private async Task<ExplorerScope> ReadRefreshDisplayAsync(ExplorerScope scope, CancellationToken ct)
    {
        var keys = scope.Resources.Where(r => r.Snapshot != null).Select(r => r.Snapshot!.SnapshotKey).Distinct().ToArray();
        if (keys.Length == 0) return scope;
        var display = new Dictionary<long, SnapshotRefreshDisplay>();
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        foreach (var batch in keys.Chunk(IndexSql.MaximumBatch))
        {
            await using var command = IndexSql.Command(connection, null, """
                SELECT s.SnapshotKey,v.VersionNumber,v.CreatedAtUtc,COALESCE(attempt.CompletedAtUtc,attempt.StartedAtUtc),attempt.Outcome,success.CompletedAtUtc,
                    completed.CompletedAtUtc,completed.Outcome
                FROM surf.DatabaseSnapshot s JOIN @Keys wanted ON wanted.Id=s.SnapshotKey
                JOIN surf.SnapshotVersion v ON v.VersionKey=s.CurrentVersionKey
                OUTER APPLY(SELECT TOP(1) e.StartedAtUtc,e.CompletedAtUtc,e.Outcome FROM surf.SnapshotCaptureEvent e
                    WHERE e.SnapshotKey=s.SnapshotKey ORDER BY e.StartedAtUtc DESC,e.EventId) attempt
                OUTER APPLY(SELECT TOP(1) e.CompletedAtUtc FROM surf.SnapshotCaptureEvent e
                    WHERE e.SnapshotKey=s.SnapshotKey AND e.Outcome IN (1,2)
                    ORDER BY e.StartedAtUtc DESC,e.EventId) success
                OUTER APPLY(SELECT TOP(1) e.CompletedAtUtc,e.Outcome FROM surf.SnapshotCaptureEvent e
                    WHERE e.SnapshotKey=s.SnapshotKey AND e.CompletedAtUtc IS NOT NULL
                    ORDER BY e.CompletedAtUtc DESC,e.EventId) completed;
                """);
            IndexSql.Keys(command, "@Keys", batch);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                display.Add(reader.GetInt64(0), new(reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : (SnapshotRefreshOutcome)reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : (SnapshotRefreshOutcome)reader.GetInt32(7)));
        }
        return scope with { Resources = scope.Resources.Select(r => r.Snapshot is { } s && display.TryGetValue(s.SnapshotKey, out var label)
            ? r with { Snapshot = s with { RefreshDisplay = label } } : r).ToImmutableArray() };
    }
}

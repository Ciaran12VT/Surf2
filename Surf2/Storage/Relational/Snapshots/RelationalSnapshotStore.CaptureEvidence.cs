using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public enum SnapshotRefreshOutcome { Running, Changed, Unchanged, Failed, Cancelled, Interrupted }
public sealed record SnapshotRefreshEvent(Guid EventId, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc,
    SnapshotRefreshOutcome Outcome, long? VersionKey, DatabaseOrigin? Origin, string? Details);

public sealed partial class RelationalSnapshotStore
{
    public async Task<SnapshotRefreshDisplay> ReadRefreshDisplayAsync(SnapshotSummary snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await Open(ct).ConfigureAwait(false);
        using var command = Command(connection, null, """
            SELECT COALESCE(v.VersionNumber,1),COALESCE(v.CreatedAtUtc,s.ImportedAtUtc),
                COALESCE(attempt.CompletedAtUtc,attempt.StartedAtUtc),attempt.Outcome,
                success.CompletedAtUtc,completed.CompletedAtUtc,completed.Outcome
            FROM surf.DatabaseSnapshot s LEFT JOIN surf.SnapshotVersion v ON v.VersionKey=s.CurrentVersionKey
            OUTER APPLY(SELECT TOP(1) e.StartedAtUtc,e.CompletedAtUtc,e.Outcome FROM surf.SnapshotCaptureEvent e
                WHERE e.SnapshotKey=s.SnapshotKey ORDER BY e.StartedAtUtc DESC,e.EventId) attempt
            OUTER APPLY(SELECT TOP(1) e.CompletedAtUtc FROM surf.SnapshotCaptureEvent e
                WHERE e.SnapshotKey=s.SnapshotKey AND e.Outcome IN (1,2)
                ORDER BY e.StartedAtUtc DESC,e.EventId) success
            OUTER APPLY(SELECT TOP(1) e.CompletedAtUtc,e.Outcome FROM surf.SnapshotCaptureEvent e
                WHERE e.SnapshotKey=s.SnapshotKey AND e.CompletedAtUtc IS NOT NULL
                ORDER BY e.CompletedAtUtc DESC,e.EventId) completed
            WHERE s.SnapshotKey=@Snapshot AND s.RowVersion=@Expected;
            """, Key("@Snapshot", snapshot.SnapshotKey), Token("@Expected", snapshot.RowVersion));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new SnapshotConcurrencyException();
        return new(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1), NullableDate(reader, 2),
            reader.IsDBNull(3) ? null : (SnapshotRefreshOutcome)reader.GetInt32(3), NullableDate(reader, 4),
            NullableDate(reader, 5), reader.IsDBNull(6) ? null : (SnapshotRefreshOutcome)reader.GetInt32(6));
    }

    public async Task StartRefreshAsync(long snapshotKey, Guid eventId, Guid? sourceId, CancellationToken ct = default)
    {
        session.RejectValidationWrite(); await using var connection = await Open(ct).ConfigureAwait(false);
        await ScalarAsync(connection, null, """
            INSERT surf.SnapshotCaptureEvent(EventId,SnapshotKey,SourceId,StartedAtUtc,Outcome)
            VALUES(@Event,@Snapshot,@Source,SYSDATETIMEOFFSET(),0);
            """, ct, EventParameter(eventId), Key("@Snapshot", snapshotKey), RelationalSession.Parameter("@Source", SqlDbType.UniqueIdentifier, sourceId)).ConfigureAwait(false);
    }
    public async Task FailRefreshAsync(Guid eventId, bool cancelled, string? details, CancellationToken ct = default)
    {
        session.RejectValidationWrite(); await using var connection = await Open(ct).ConfigureAwait(false);
        await ScalarAsync(connection, null, """
            UPDATE surf.SnapshotCaptureEvent SET CompletedAtUtc=SYSDATETIMEOFFSET(),Outcome=@Outcome,Details=@Details
            WHERE EventId=@Event AND Outcome=0;
            """, ct, EventParameter(eventId), Int("@Outcome", cancelled ? 4 : 3), Text("@Details", details?.Length > 4096 ? details[..4096] : details)).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<SnapshotRefreshEvent>> ReadRefreshEventsAsync(long snapshotKey, int maximum = 50, CancellationToken ct = default)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        await using var connection = await Open(ct).ConfigureAwait(false);
        using var command = Command(connection, null, """
            SELECT TOP (@Max) EventId,StartedAtUtc,CompletedAtUtc,Outcome,VersionKey,OriginJson,Details
            FROM surf.SnapshotCaptureEvent WHERE SnapshotKey=@Snapshot ORDER BY StartedAtUtc DESC,EventId;
            """, Int("@Max", maximum), Key("@Snapshot", snapshotKey));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); var values = new List<SnapshotRefreshEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) values.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1),
            NullableDate(reader, 2), (SnapshotRefreshOutcome)reader.GetInt32(3), NullableKey(reader, 4), DatabaseOrigin.FromJson(reader.IsDBNull(5) ? null : reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return values;
    }
    public async Task<DatabaseOrigin?> ReadOriginAsync(long versionKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        return DatabaseOrigin.FromJson(await ScalarAsync(connection, null, "SELECT OriginJson FROM surf.SnapshotVersionOrigin WHERE VersionKey=@Version;", ct, Key("@Version", versionKey)).ConfigureAwait(false) as string);
    }
    public async Task<DatabaseOrigin?> ReadSnapshotOriginAsync(long snapshotKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        return DatabaseOrigin.FromJson(await ScalarAsync(connection, null, """
            SELECT COALESCE((SELECT TOP(1) e.OriginJson FROM surf.SnapshotCaptureEvent e WHERE e.SnapshotKey=s.SnapshotKey
              AND e.Outcome IN (1,2) AND e.OriginJson IS NOT NULL ORDER BY e.StartedAtUtc DESC,e.EventId),o.OriginJson)
            FROM surf.DatabaseSnapshot s LEFT JOIN surf.SnapshotVersionOrigin o ON o.VersionKey=s.CurrentVersionKey WHERE s.SnapshotKey=@Snapshot;
            """, ct, Key("@Snapshot", snapshotKey)).ConfigureAwait(false) as string);
    }
    private static SqlParameter EventParameter(Guid value) => RelationalSession.Parameter("@Event", SqlDbType.UniqueIdentifier, value);
    private static async Task PublishCaptureEvidenceAsync(SqlConnection connection, SqlTransaction transaction, RuntimeSnapshotPlan plan,
        long? versionKey, bool changed, CancellationToken ct)
    {
        if (changed && versionKey != null && plan.Origin != null)
            await ScalarAsync(connection, transaction, "INSERT surf.SnapshotVersionOrigin(VersionKey,OriginJson) VALUES(@Version,@Origin);", ct,
                Key("@Version", versionKey), Text("@Origin", plan.Origin.ToJson())).ConfigureAwait(false);
        if (plan.CaptureEventId is Guid id)
            await ScalarAsync(connection, transaction, """
                UPDATE surf.SnapshotCaptureEvent SET VersionKey=@Version,CompletedAtUtc=SYSDATETIMEOFFSET(),Outcome=@Outcome,OriginJson=@Origin
                WHERE EventId=@Event AND SnapshotKey=@Snapshot AND Outcome=0;
                """, ct, EventParameter(id), Key("@Snapshot", plan.SnapshotKey), Key("@Version", versionKey),
                Int("@Outcome", changed ? 1 : 2), Text("@Origin", plan.Origin?.ToJson())).ConfigureAwait(false);
    }
}

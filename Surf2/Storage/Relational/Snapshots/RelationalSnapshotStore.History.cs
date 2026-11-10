using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots;

public sealed partial class RelationalSnapshotStore
{
    public async Task<SnapshotHistorySummary?> GetHistoryByKeyAsync(long historyKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT CONVERT(bigint,256)+DATALENGTH(h.OriginalSnapshotId),
                h.HistoryKey, h.SnapshotKey, h.OriginalSnapshotId, h.NextVersionNumber, h.SortOrdinal, h.RowVersion
            FROM surf.SnapshotHistory h JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=h.SnapshotKey
            WHERE h.HistoryKey=@History AND s.IsPublished=1;
            """, Key("@History", historyKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        return new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5), (byte[])reader.GetValue(6));
    }

    public Task<SnapshotPage<SnapshotHistorySummary>> ListHistoriesAsync(long snapshotKey, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, null, "histories", null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(OriginalSnapshotId),
            HistoryKey, SnapshotKey, OriginalSnapshotId, NextVersionNumber, SortOrdinal, RowVersion
        FROM surf.SnapshotHistory WHERE SnapshotKey=@Owner
            AND (SortOrdinal>@AfterOrder OR SortOrdinal=@AfterOrder AND HistoryKey>@AfterKey)
        ORDER BY SortOrdinal, HistoryKey;
        """, r => new SnapshotHistorySummary(r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5), (byte[])r.GetValue(6)),
        x => (x.SortOrdinal, x.HistoryKey), ct);

    public async Task<SnapshotHistorySummary?> GetHistoryAsync(long snapshotKey, CancellationToken ct = default)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var command = Command(connection, null, """
            SELECT TOP (1) CONVERT(bigint,256)+DATALENGTH(h.OriginalSnapshotId),
                h.HistoryKey, h.SnapshotKey, h.OriginalSnapshotId, h.NextVersionNumber, h.SortOrdinal, h.RowVersion
            FROM surf.SnapshotHistory h JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=h.SnapshotKey
            WHERE h.SnapshotKey=@Snapshot AND s.IsPublished=1
            ORDER BY h.SortOrdinal, h.HistoryKey;
            """, Key("@Snapshot", snapshotKey));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
        return new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5), (byte[])reader.GetValue(6));
    }

    public Task<SnapshotPage<SnapshotVersionSummary>> ListVersionsAsync(long snapshotKey, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, null, "versions", null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(v.OriginalVersionId)+DATALENGTH(v.VersionName),
            v.VersionKey, v.SnapshotKey, v.OriginalVersionId, v.VersionName, v.VersionNumber,
            v.CreatedAtUtc, v.IsInitial, v.SortOrdinal,
            (SELECT COUNT_BIG(*) FROM surf.SnapshotChange c WHERE c.VersionKey=v.VersionKey)
        FROM surf.SnapshotVersion v WHERE v.SnapshotKey=@Owner
            AND v.HistoryKey=(SELECT TOP (1) h.HistoryKey FROM surf.SnapshotHistory h
                WHERE h.SnapshotKey=@Owner ORDER BY h.SortOrdinal, h.HistoryKey)
            AND (v.SortOrdinal>@AfterOrder OR v.SortOrdinal=@AfterOrder AND v.VersionKey>@AfterKey)
        ORDER BY v.SortOrdinal, v.VersionKey;
        """, r => new SnapshotVersionSummary(r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
            r.GetFieldValue<DateTimeOffset>(6), r.GetBoolean(7), r.GetInt64(8), r.GetInt64(9)), x => (x.SortOrdinal, x.VersionKey), ct);

    public Task<SnapshotPage<SnapshotVersionSummary>> ListHistoryVersionsAsync(long snapshotKey, long historyKey, int pageSize = 100,
        SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, null,
        FormattableString.Invariant($"history-versions:{historyKey}"), null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(v.OriginalVersionId)+DATALENGTH(v.VersionName),
            v.VersionKey, v.SnapshotKey, v.OriginalVersionId, v.VersionName, v.VersionNumber,
            v.CreatedAtUtc, v.IsInitial, v.SortOrdinal,
            (SELECT COUNT_BIG(*) FROM surf.SnapshotChange c WHERE c.VersionKey=v.VersionKey)
        FROM surf.SnapshotVersion v WHERE v.SnapshotKey=@Owner AND v.HistoryKey=@History
            AND (v.SortOrdinal>@AfterOrder OR v.SortOrdinal=@AfterOrder AND v.VersionKey>@AfterKey)
        ORDER BY v.SortOrdinal, v.VersionKey;
        """, r => new SnapshotVersionSummary(r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
            r.GetFieldValue<DateTimeOffset>(6), r.GetBoolean(7), r.GetInt64(8), r.GetInt64(9)),
        x => (x.SortOrdinal, x.VersionKey), ct, historyKey: historyKey);

    /// <summary>First source-ordered OrdinalIgnoreCase ID match within the first source-ordered history.</summary>
    public async Task<long?> ResolveVersionKeyAsync(long snapshotKey, string originalVersionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalVersionId);
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await Head(connection, transaction, snapshotKey, null, false, ct).ConfigureAwait(false);
        object? history = await ScalarAsync(connection, transaction, """
            SELECT TOP (1) HistoryKey FROM surf.SnapshotHistory WHERE SnapshotKey=@Snapshot ORDER BY SortOrdinal,HistoryKey;
            """, ct, Key("@Snapshot",snapshotKey)).ConfigureAwait(false);
        if (history is not long historyKey) return null;

        // An exact-hash hit bounds the source-order scan, but cannot win ahead of
        // an earlier differently cased ID. Full-value verification excludes hash collisions.
        long? boundOrder = null, boundKey = null;
        await using (var command = Command(connection, transaction, """
            SELECT TOP (1) SortOrdinal,VersionKey FROM surf.SnapshotVersion
            WHERE HistoryKey=@History AND VersionIdHash=@Hash AND DATALENGTH(OriginalVersionId)=DATALENGTH(@Id)
                AND OriginalVersionId COLLATE Latin1_General_100_BIN2=@Id COLLATE Latin1_General_100_BIN2
            ORDER BY SortOrdinal,VersionKey;
            """, Key("@History",historyKey), Hash("@Hash",SnapshotIdentity.Hash(originalVersionId)), Text("@Id",originalVersionId)))
        {
            using var cancel = RelationalSession.CancelCommand(command,ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false)) { boundOrder=reader.GetInt64(0); boundKey=reader.GetInt64(1); }
        }
        long afterOrder=-1, afterKey=0;
        while (true)
        {
            await using var command = Command(connection, transaction, """
                SELECT TOP (128) SortOrdinal,VersionKey,DATALENGTH(OriginalVersionId),OriginalVersionId FROM surf.SnapshotVersion
                WHERE HistoryKey=@History AND (SortOrdinal>@AfterOrder OR SortOrdinal=@AfterOrder AND VersionKey>@AfterKey)
                    AND (@BoundOrder IS NULL OR SortOrdinal<@BoundOrder OR SortOrdinal=@BoundOrder AND VersionKey<=@BoundKey)
                ORDER BY SortOrdinal,VersionKey;
                """, Key("@History",historyKey), Key("@AfterOrder",afterOrder), Key("@AfterKey",afterKey),
                Key("@BoundOrder",boundOrder), Key("@BoundKey",boundKey));
            using var cancel = RelationalSession.CancelCommand(command,ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct).ConfigureAwait(false);
            int count=0; long bytes=0; bool budgetFull=false;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long order=reader.GetInt64(0), key=reader.GetInt64(1);
                long size=checked(256+reader.GetInt64(2));
                SnapshotReadGuard.RequireBytes(size,MaximumMetadataPageBytes,command);
                if (size>MaximumMetadataPageBytes-bytes) { budgetFull=true; SnapshotReadGuard.StopReading(command); break; }
                bytes+=size; count++; afterOrder=order; afterKey=key;
                if (string.Equals(reader.GetString(3),originalVersionId,StringComparison.OrdinalIgnoreCase)) { SnapshotReadGuard.StopReading(command); return key; }
            }
            if (count<128 && !budgetFull) return null;
        }
    }

    public Task<SnapshotPage<SnapshotChangeSummary>> ListChangesAsync(long snapshotKey, long versionKey,
        int pageSize = 100, SnapshotCursor? cursor = null, CancellationToken ct = default) => PageAsync(snapshotKey, versionKey, "changes", null, pageSize, cursor, """
        SELECT TOP (@Take) CONVERT(bigint,256)+DATALENGTH(OriginalResourceKey)+DATALENGTH(DisplayName)+DATALENGTH(RelativePath),
            ChangeKey, VersionKey, ResourceKey, Kind, ChangeKind, OriginalResourceKey, DisplayName,
            RelativePath, SortOrdinal, PreviousRevisionKey
        FROM surf.SnapshotChange WHERE SnapshotKey=@Owner AND VersionKey=@Version
            AND (SortOrdinal>@AfterOrder OR SortOrdinal=@AfterOrder AND ChangeKey>@AfterKey)
        ORDER BY SortOrdinal, ChangeKey;
        """, r => new SnapshotChangeSummary(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), (DatabaseVersionedResourceKind)r.GetInt32(4),
            (DatabaseSnapshotResourceChangeKind)r.GetInt32(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt64(9), NullableKey(r, 10)),
        x => (x.SortOrdinal, x.ChangeKey), ct);

    /// <summary>Atomic targeted save. The expected snapshot rowversion covers metadata, heads and history in this transaction.</summary>
    public async Task<byte[]> ExecuteMutationAsync(long snapshotKey, byte[] expectedRowVersion,
        Func<RelationalSnapshotWriter, CancellationToken, Task> mutation, CancellationToken ct = default)
    {
        session.RejectValidationWrite();
        ArgumentNullException.ThrowIfNull(mutation);
        if (expectedRowVersion is not { Length: 8 }) throw new ArgumentException("Expected snapshot rowversion must contain eight bytes.", nameof(expectedRowVersion));
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        // Lock ordering is catalogue -> snapshot -> resource. This matches the
        // read-page head locks and prevents mixed-generation page results.
        await ScalarAsync(connection, transaction, """
            UPDATE h SET UserKey=h.UserKey FROM surf.SnapshotCatalogueHead h
            JOIN surf.DatabaseSnapshot s ON s.UserKey=h.UserKey WHERE s.SnapshotKey=@Snapshot;
            """, ct, Key("@Snapshot", snapshotKey)).ConfigureAwait(false);
        object? head = await ScalarAsync(connection, transaction, """
            UPDATE surf.DatabaseSnapshot SET DisplayName=DisplayName OUTPUT INSERTED.RowVersion
            WHERE SnapshotKey=@Snapshot AND RowVersion=@Expected AND IsPublished=1;
            """, ct, Key("@Snapshot", snapshotKey), Token("@Expected", expectedRowVersion)).ConfigureAwait(false);
        if (head is not byte[]) throw new SnapshotConcurrencyException();
        var writer = new RelationalSnapshotWriter(connection, transaction, content, snapshotKey);
        await mutation(writer, ct).ConfigureAwait(false);
        await writer.ValidateSnapshotAsync(snapshotKey, ct).ConfigureAwait(false);
        byte[] token = (byte[])(await ScalarAsync(connection, transaction,
            "SELECT RowVersion FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Snapshot;", ct, Key("@Snapshot", snapshotKey)).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Snapshot not found."));
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return token;
    }
}

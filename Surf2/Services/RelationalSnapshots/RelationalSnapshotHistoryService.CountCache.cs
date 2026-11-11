using System.Globalization;
using System.Text;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Snapshots;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Services.RelationalSnapshots;

public sealed partial class RelationalSnapshotHistoryService
{
    // Bump this when changing a table/CSV renderer, DiffPlex count policy or line splitting.
    private const string LineCountPolicy = "Surf2.HistoryLineCounts.v1";

    private static HistoryLineCountKey? LineCountKey(RelationalHistoryFileSummary file, HistoricalSnapshotContext current,
        HistoricalSnapshotContext? previous, CancellationToken ct)
    {
        if (file.Version.VersionKey <= 0) return null;
        var signature = new StringBuilder(256);
        Append(LineCountPolicy);
        Append(CultureInfo.CurrentCulture.Name);
        Append(Environment.NewLine);
        Number(file.PreviousVersionKey.GetValueOrDefault());
        Source(file.Entry, current);
        Source(file.PreviousEntry, previous);
        return new(file.ResourceKey, (int)file.Kind, Convert.ToHexString(SnapshotIdentity.Hash(signature.ToString())));

        void Append(string value) => signature.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        void Number(long value) => Append(value.ToString(CultureInfo.InvariantCulture));
        void Source(HistoricalSnapshotEntry? entry, HistoricalSnapshotContext? context)
        {
            ct.ThrowIfCancellationRequested();
            Number(entry?.RevisionKey ?? 0);
            if (entry == null) return;
            Number((int)entry.Collection);
            if (entry.Collection == HistoricalCollection.Objects) return;
            Append(entry.SchemaName);
            Append(entry.Name);
            Number((int)entry.TableSource);
            Number(entry.Table?.HasFullData == true ? 1 : 0);
            Number(entry.Table?.FullDataRowCount ?? 0);
            var columns = context!.BufferedHistoryChildren(entry, HistoricalCollection.Columns);
            if (columns == null)
            {
                // Oversized projections stream their metadata. An exact replay baseline
                // is immutable; a legacy replay off the live head is only valid for that head.
                Append("projection");
                if (context.ReplayBaselineVersionKey is long baseline) Number(baseline);
                else Append(Convert.ToHexString(context.Header.RowVersion));
                return;
            }
            Append("children");
            Children(columns);
            if (entry.Collection == HistoricalCollection.Tables)
                Children(context.BufferedHistoryChildren(entry, HistoricalCollection.PrimaryKeys)!);
        }

        void Children(IReadOnlyList<HistoricalSnapshotEntry> children)
        {
            Number(children.Count);
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                Number(child.ChildKey.GetValueOrDefault());
            }
        }
    }

    /// <summary>Initial versions show all files; subsequent headings already have their stored change-row count.</summary>
    public async Task<long> CountVersionFilesAsync(SnapshotSummary snapshot, SnapshotVersionSummary version, CancellationToken ct = default)
    {
        if (version.SnapshotKey != snapshot.SnapshotKey) throw new ArgumentException("Select a version of this snapshot.", nameof(version));
        if (!version.IsInitial) return version.ChangeCount;
        byte[]? dependency = null;
        if (version.VersionKey > 0)
        {
            await using var connection = await _runtime.Session.OpenAsync(ct).ConfigureAwait(false);
            await using var command = Command(connection, null, """
                SELECT (SELECT TOP (1) p.VersionKey FROM surf.SnapshotVersion p
                    WHERE p.HistoryKey=v.HistoryKey AND p.VersionNumber>v.VersionNumber AND p.HasExactBaseline=1
                    ORDER BY p.VersionNumber,p.SortOrdinal,p.VersionKey)
                FROM surf.SnapshotVersion v JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=v.SnapshotKey
                WHERE v.SnapshotKey=@Snapshot AND v.VersionKey=@Version AND v.IsInitial=1 AND v.VersionNumber=@Number
                    AND s.IsPublished=1 AND s.RowVersion=@Head;
                """, Key("@Snapshot", snapshot.SnapshotKey), Key("@Version", version.VersionKey), Int("@Number", version.VersionNumber),
                Token("@Head", snapshot.RowVersion));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            long? baseline;
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new SnapshotConcurrencyException();
                baseline = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            }
            dependency = SnapshotIdentity.Hash("Surf2.InitialHistoryFileCount.v1:" + (baseline is long key
                ? "baseline:" + key.ToString(CultureInfo.InvariantCulture) : "head:" + Convert.ToHexString(snapshot.RowVersion)));
            if (await _historyCounts.ReadInitialAsync(version, dependency, ct).ConfigureAwait(false) is long cached)
            {
                var head = await _runtime.Snapshots.GetSnapshotAsync(snapshot.SnapshotKey, ct).ConfigureAwait(false);
                if (head == null || !head.RowVersion.AsSpan().SequenceEqual(snapshot.RowVersion)) throw new SnapshotConcurrencyException();
                return cached;
            }
        }
        await using var context = await OpenVersionAsync(snapshot.SnapshotKey, version.VersionKey, ct).ConfigureAwait(false);
        if (!context.Header.RowVersion.AsSpan().SequenceEqual(snapshot.RowVersion)) throw new SnapshotConcurrencyException();
        long count = await context.CountResourcesAsync(ct).ConfigureAwait(false);
        if (dependency != null)
            await _historyCounts.WriteInitialAsync(version, snapshot.RowVersion, dependency, count, ct).ConfigureAwait(false);
        return count;
    }
}

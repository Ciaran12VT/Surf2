using System.IO;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Surf2.Models;

namespace Surf2.Storage.Relational.Snapshots;

public sealed record SnapshotReverseChange(long ChangeKey, int VersionNumber, long SortOrdinal,
    DatabaseSnapshotResourceChangeKind ChangeKind, long? PreviousRevisionKey);

/// <summary>Isolated per-resource reverse-reference checks. Production historical reads use graph-aware HistoricalSnapshotReplay instead.</summary>
public static class SnapshotReverseLookup
{
    public static long? Resolve(long? currentRevisionKey, int targetVersionNumber, IEnumerable<SnapshotReverseChange> changes)
    {
        SnapshotReverseChange? first = null;
        foreach (var change in changes)
        {
            if (!Enum.IsDefined(change.ChangeKind)) throw new ArgumentOutOfRangeException(nameof(changes));
            if (change.VersionNumber <= targetVersionNumber ||
                change.ChangeKind != DatabaseSnapshotResourceChangeKind.Added && !change.PreviousRevisionKey.HasValue) continue;
            if (first == null || change.VersionNumber < first.VersionNumber || change.VersionNumber == first.VersionNumber &&
                (change.SortOrdinal > first.SortOrdinal || change.SortOrdinal == first.SortOrdinal && change.ChangeKey > first.ChangeKey)) first = change;
        }
        return first == null ? currentRevisionKey : first.ChangeKind == DatabaseSnapshotResourceChangeKind.Added ? null : first.PreviousRevisionKey;
    }
}

/// <summary>Hooks for the shared regression harness; these checks need no connection or UI.</summary>
public static class SnapshotContractChecks
{
    public static Task<IReadOnlyList<string>> RunHistoryAsync() => SnapshotHistoryFixtures.RunAsync();
    public static Task<IReadOnlyList<string>> RunReadLimitsAsync(CancellationToken ct = default) => SnapshotReadLimitChecks.RunAsync(ct);
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Snapshot contract failed: " + name);
            passed.Add(name);
        }
        Check(SnapshotIdentity.Hash("a\r\nb ").AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes("a\r\nb "))), "UTF-16LE exact hash contract");
        Check(!SnapshotIdentity.Hash("x").AsSpan().SequenceEqual(SnapshotIdentity.Hash("x ")), "Trailing spaces retained");
        Check(!SnapshotIdentity.Hash("A").AsSpan().SequenceEqual(SnapshotIdentity.Hash("a")), "Identity hashing does not fold case");
        Check(!SnapshotIdentity.Hash("\ud800").AsSpan().SequenceEqual(SnapshotIdentity.Hash("\ufffd")), "Unpaired code units not encoder-normalized");
        Check(!SnapshotIdentity.NameHash("a.b", "c").AsSpan().SequenceEqual(SnapshotIdentity.NameHash("a", "b.c")), "Multipart name hashes unambiguous");
        Check(SnapshotIdentity.ResourceKind(SqlDatabaseObjectKind.Unknown) == null &&
              SnapshotIdentity.ResourceKind(SqlDatabaseObjectKind.Trigger) == DatabaseVersionedResourceKind.Trigger, "Existing enum mapping");
        SnapshotReverseChange[] chain = [
            new(1, 3, 0, DatabaseSnapshotResourceChangeKind.Modified, 100),
            new(2, 8, 0, DatabaseSnapshotResourceChangeKind.Deleted, 200),
            new(3, 12, 0, DatabaseSnapshotResourceChangeKind.Added, null)];
        Check(SnapshotReverseLookup.Resolve(300, 1, chain) == 100, "Earlier version uses previous, not new payload");
        Check(SnapshotReverseLookup.Resolve(300, 3, chain) == 200, "Version gaps preserved");
        Check(SnapshotReverseLookup.Resolve(300, 8, chain) == null, "Deleted then re-added interval absent");
        Check(SnapshotReverseLookup.Resolve(300, 12, chain) == 300, "Latest version uses current revision");
        Check(SnapshotReverseLookup.Resolve(null, 3, chain[..2]) == 200, "History-only deleted resource resolved");
        Check(SnapshotReverseLookup.Resolve(9, 1, [new(1, 2, 0, DatabaseSnapshotResourceChangeKind.Modified, null)]) == 9, "Null PreviousPayload is no-op");
        Check(SnapshotReverseLookup.Resolve(9, 1, [new(1, 2, 0, DatabaseSnapshotResourceChangeKind.Modified, 7),
            new(2, 2, 1, DatabaseSnapshotResourceChangeKind.Modified, 8)]) == 8, "Last rollback change within a version wins");
        Check(SnapshotReverseLookup.Resolve(9, 1, [new(1, 2, 0, DatabaseSnapshotResourceChangeKind.Modified, 7),
            new(2, 2, 0, DatabaseSnapshotResourceChangeKind.Modified, 8)]) == 8, "Stable identity tie-breaker");
        foreach (Type type in new[] { typeof(SnapshotSummary), typeof(ObjectSummary), typeof(TableSummary), typeof(SnapshotVersionSummary), typeof(SnapshotChangeSummary) })
            Check(type.GetProperties().All(p => p.Name is not ("Definition" or "Rows" or "PreviousPayload" or "DatabaseObject")), type.Name + " is payload-free");
        return passed;
    }

    public static IReadOnlyList<string> ParseSql(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var input = new StringReader(sql);
        parser.Parse(input, out var errors);
        return errors.Select(e => $"{e.Line}:{e.Column}: {e.Message}").ToArray();
    }

    // Includes the dynamically composed listing SQL, so missing whitespace at
    // fragment boundaries is tested as well as the standalone DDL script.
    public static IReadOnlyList<string> ParseStoreQueries() => RelationalSnapshotStore.ParseQueryContracts();
}

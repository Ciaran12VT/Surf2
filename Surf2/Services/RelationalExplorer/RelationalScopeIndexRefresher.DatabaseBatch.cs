using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Index;

namespace Surf2.Services.RelationalExplorer;

public sealed partial class RelationalScopeIndexRefresher
{
    private sealed record DatabaseIndexEntry(DocumentRegistration Registration, IndexPolicy Policy, long SourceRevision,
        ImmutableArray<DocumentMembership> Memberships, ImmutableArray<IndexLocator> Locators, Func<Task<PreparedIndexSource>> Prepare);

    private DatabaseIndexEntry CreateDatabaseEntry(ExplorerScope scope, ExplorerResource root, ExplorerDatabaseItem item,
        ExplorerCategory category, CancellationToken ct = default)
    {
        var source = item.Resource;
        if (root.Snapshot == null || root.Snapshot.SnapshotKey != source.SnapshotKey)
            throw new ArgumentException("Database indexing requires the selected typed snapshot owner.");
        var aliases = scope.Resources.Where(r => r.Snapshot?.SnapshotKey == source.SnapshotKey).ToArray();
        string locator = ExplorerCompatibility.DatabaseLocator(root.Snapshot, category, source.SchemaName, source.ObjectName, canonical: true);
        string name = SqlName.FormatPlainMultipartName(source.SchemaName, source.ObjectName);
        var memberships = aliases.Select(r =>
        {
            string readable = ExplorerCompatibility.DatabaseLocator(r.Snapshot!, category, source.SchemaName, source.ObjectName);
            return new DocumentMembership(r.ScopeResourceKey, name + (item.HasFullData ? $" ({item.ReportedRowCount} rows)" : ""),
                readable, readable, r.Snapshot!.SnapshotId + "/" + ExplorerCompatibility.CategoryName(category), source.SortOrdinal);
        }).ToImmutableArray();
        var locators = aliases.SelectMany(r => new[]
        {
            new IndexLocator(r.ScopeResourceKey, 1, ExplorerCompatibility.DatabaseLocator(r.Snapshot!, category, source.SchemaName, source.ObjectName)),
            new IndexLocator(r.ScopeResourceKey, 2, locator)
        }).ToImmutableArray();
        bool table = category == ExplorerCategory.Tables;
        string container = root.Snapshot.DisplayName;
        var policy = new IndexPolicy("database-metadata-v1", table ? RelationalIndexStore.TableRendererVersion : "definition-v1",
            "explorer-v1:" + Convert.ToHexString(IndexSql.Hash(container)));
        return new(new(new SnapshotDocumentOwner(source.SnapshotKey, source.ResourceKey, table), name, "SQL Server"),
            policy, source.RevisionKey, memberships, locators, table
                ? () => _index.PrepareTableCodeAsync(source.RevisionKey, locator, container, ct)
                : () => _index.PrepareDefinitionAsync(source.RevisionKey, locator, container, _limits.MaximumDocumentCharacters, ct));
    }

    // The dictionary is keyed by SnapshotResource.ResourceKey, not by the derived DocumentKey.
    private async Task<Dictionary<long, PreparedDocument>> PrimeDatabaseBatchAsync(ExplorerScope scope,
        IReadOnlyList<DatabaseIndexEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entries.Count == 0) return [];
        var wanted = ValidateDatabaseEntries(scope, entries);
        var prepared = new Dictionary<long, PreparedDocument>();
        await using (var connection = await _session.OpenAsync(ct).ConfigureAwait(false))
        await using (var command = IndexSql.Command(connection, null, """
SELECT d.SnapshotResourceKey,d.Kind,d.DocumentKey,d.Version,d.Freshness,r.SourceFingerprint,
 r.ParserVersion,r.RendererVersion,r.PolicyVersion,r.SourceRevisionKey,d.Language
FROM surf.Document d JOIN @Resources wanted ON wanted.Id=d.SnapshotResourceKey
LEFT JOIN surf.DocumentRevision r ON r.DocumentRevisionKey=d.CurrentRevisionKey
WHERE d.SnapshotKey=@Snapshot AND d.Kind IN(1,2);
"""))
        {
            IndexSql.Keys(command, "@Resources", wanted.Keys);
            IndexSql.Add(command, "@Snapshot", SqlDbType.BigInt, ((SnapshotDocumentOwner)entries[0].Registration.Owner).SnapshotKey);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var budget = new ExplorerMetadataBudget(_limits);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long resource = reader.GetInt64(0);
                var owner = (SnapshotDocumentOwner)wanted[resource].Registration.Owner;
                if (reader.GetInt32(1) != (int)(owner.IsTableCode ? IndexedDocumentKind.TableCode : IndexedDocumentKind.Definition)) continue;
                var state = ReadPublished(reader, 4);
                budget.Add(state.Language, state.Fingerprint ?? "", state.Policy?.ParserVersion ?? "",
                    state.Policy?.RendererVersion ?? "", state.Policy?.PolicyVersion ?? "");
                if (!prepared.TryAdd(resource, new(new(reader.GetInt64(2), IndexSql.Hex((byte[])reader[3])), state)))
                    throw new InvalidOperationException("The typed database document identity is ambiguous.");
            }
        }
        foreach (var entry in entries)
        {
            long resource = ((SnapshotDocumentOwner)entry.Registration.Owner).ResourceKey;
            if (!prepared.ContainsKey(resource))
                prepared.Add(resource, await GetOrRegisterAsync(entry.Registration, ct).ConfigureAwait(false));
        }
        await SetDatabaseBatchMembershipsAsync(scope, entries, prepared, ct).ConfigureAwait(false);
        return prepared;
    }

    private Dictionary<long, DatabaseIndexEntry> ValidateDatabaseEntries(ExplorerScope scope, IReadOnlyList<DatabaseIndexEntry> entries)
    {
        if (entries.Count > 128) throw new ExplorerLimitException("A captured-database metadata page exceeds its batch limit.");
        var resources = scope.Resources.ToDictionary(r => r.ScopeResourceKey);
        var result = new Dictionary<long, DatabaseIndexEntry>();
        long? snapshot = null;
        var budget = new ExplorerMetadataBudget(_limits);
        foreach (var entry in entries)
        {
            if (entry.Registration.Owner is not SnapshotDocumentOwner owner || owner.SnapshotKey <= 0 || owner.ResourceKey <= 0 ||
                entry.SourceRevision <= 0 || entry.Prepare == null || snapshot.HasValue && owner.SnapshotKey != snapshot.Value ||
                !result.TryAdd(owner.ResourceKey, entry))
                throw new ArgumentException("A database index batch requires distinct resources from one typed snapshot.");
            snapshot = owner.SnapshotKey;
            IndexSql.ShortValue(entry.Registration.Language, nameof(entry.Registration.Language));
            IndexSql.Policy(entry.Policy);
            if (entry.Registration.DisplayName.Length > 65536) throw new IndexDocumentTooLargeException();
            budget.Add(entry.Registration.DisplayName, entry.Registration.Language);
            if (entry.Memberships.IsDefault || entry.Locators.IsDefault || entry.Memberships.Length > IndexSql.MaximumBatch ||
                entry.Locators.Length > IndexSql.MaximumBatch ||
                entry.Memberships.Select(m => m.ScopeResourceKey).Distinct().Count() != entry.Memberships.Length ||
                entry.Memberships.Any(m => !resources.TryGetValue(m.ScopeResourceKey, out var root) || root.Snapshot?.SnapshotKey != owner.SnapshotKey) ||
                entry.Locators.Any(l => l.Kind is < 0 or > 2 || !entry.Memberships.Any(m => m.ScopeResourceKey == l.ScopeResourceKey)))
                throw new ExplorerLimitException("Database memberships exceed their scoped batch or typed owner budget.");
            foreach (var member in entry.Memberships)
            {
                if (member.DisplayName.Length > 65536 || member.Locator.Length > 65536 || member.NodeKey.Length > 65536 ||
                    member.ParentNodeKey.Length > 65536 || member.SortOrdinal < 0) throw new IndexDocumentTooLargeException();
                budget.Add(member.DisplayName, member.Locator, member.NodeKey, member.ParentNodeKey);
            }
            foreach (var locator in entry.Locators)
            {
                if (locator.Locator.Length > 65536) throw new IndexDocumentTooLargeException();
                budget.Add(locator.Locator);
            }
        }
        return result;
    }

    private async Task SetDatabaseBatchMembershipsAsync(ExplorerScope scope, IReadOnlyList<DatabaseIndexEntry> entries,
        Dictionary<long, PreparedDocument> prepared, CancellationToken ct)
    {
        var byDocument = entries.ToDictionary(entry => prepared[((SnapshotDocumentOwner)entry.Registration.Owner).ResourceKey].Handle.DocumentKey);
        var members = byDocument.Keys.ToDictionary(key => key, _ => new List<DocumentMembership>());
        var locators = byDocument.Keys.ToDictionary(key => key, _ => new List<IndexLocator>());
        await using var connection = await _session.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        await using (var head = IndexSql.Command(connection, transaction,
            "SELECT Generation FROM surf.IndexCatalogueHead WITH(UPDLOCK,HOLDLOCK) WHERE Singleton=1;"))
        {
            using var cancel = RelationalSession.CancelCommand(head, ct);
            if (await head.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long)
                throw new InvalidOperationException("Index schema is not installed.");
        }
        await FenceDomainAsync(connection, transaction, scope.Context, ct).ConfigureAwait(false);
        await using (var command = IndexSql.Command(connection, transaction, """
SELECT d.DocumentKey,d.Version,d.Kind,d.SnapshotKey,d.SnapshotResourceKey
FROM surf.Document d JOIN @Documents wanted ON wanted.Id=d.DocumentKey;
SELECT TOP (@MaximumRows) m.DocumentKey,m.ScopeResourceKey,LEFT(m.DisplayName,65537),LEFT(m.Locator,65537),
 LEFT(m.NodeKey,65537),LEFT(m.ParentNodeKey,65537),m.SortOrdinal
FROM surf.ResourceDocument m JOIN @Documents wanted ON wanted.Id=m.DocumentKey
JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
WHERE sr.ScopeKey=@Scope ORDER BY m.DocumentKey,m.ScopeResourceKey;
SELECT TOP (@MaximumRows) l.DocumentKey,l.ScopeResourceKey,l.Kind,LEFT(l.OriginalLocator,65537)
FROM surf.DocumentLocator l JOIN @Documents wanted ON wanted.Id=l.DocumentKey
JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=l.ScopeResourceKey
WHERE sr.ScopeKey=@Scope ORDER BY l.DocumentKey,l.ScopeResourceKey,l.Kind;
"""))
        {
            IndexSql.Keys(command, "@Documents", byDocument.Keys);
            IndexSql.Add(command, "@Scope", SqlDbType.BigInt, scope.Context.ScopeKey);
            IndexSql.Add(command, "@MaximumRows", SqlDbType.BigInt,
                Math.Min((long)_limits.MaximumMetadataRows, (long)entries.Count * IndexSql.MaximumBatch) + 1);
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            int versions = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var entry = byDocument[reader.GetInt64(0)];
                var owner = (SnapshotDocumentOwner)entry.Registration.Owner;
                var document = prepared[owner.ResourceKey];
                if (IndexSql.Hex((byte[])reader[1]) != document.Handle.Version ||
                    reader.GetInt32(2) != (int)(owner.IsTableCode ? IndexedDocumentKind.TableCode : IndexedDocumentKind.Definition) ||
                    reader.GetInt64(3) != owner.SnapshotKey || reader.GetInt64(4) != owner.ResourceKey)
                    throw new IndexGenerationChangedException();
                versions++;
            }
            if (versions != entries.Count) throw new IndexGenerationChangedException();
            var budget = new ExplorerMetadataBudget(_limits);
            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var member = new DocumentMembership(reader.GetInt64(1), DatabaseMetadataString(reader, 2), DatabaseMetadataString(reader, 3),
                    DatabaseMetadataString(reader, 4), DatabaseMetadataString(reader, 5), reader.GetInt64(6));
                budget.Add(member.DisplayName, member.Locator, member.NodeKey, member.ParentNodeKey);
                var selected = members[reader.GetInt64(0)];
                if (selected.Count == IndexSql.MaximumBatch) throw new ExplorerLimitException("Existing database memberships exceed their batch limit.");
                selected.Add(member);
            }
            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var locator = new IndexLocator(reader.GetInt64(1), reader.GetInt32(2), DatabaseMetadataString(reader, 3));
                budget.Add(locator.Locator);
                var selected = locators[reader.GetInt64(0)];
                if (selected.Count == IndexSql.MaximumBatch) throw new ExplorerLimitException("Existing database locators exceed their batch limit.");
                selected.Add(locator);
            }
        }
        long[] changed = byDocument.Where(pair =>
            !members[pair.Key].SequenceEqual(pair.Value.Memberships.OrderBy(m => m.ScopeResourceKey)) ||
            !OrderedDatabaseLocators(locators[pair.Key]).SequenceEqual(OrderedDatabaseLocators(pair.Value.Locators)))
            .Select(pair => pair.Key).ToArray();
        if (changed.Length == 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }
        await using (var staging = IndexSql.Command(connection, transaction, """
CREATE TABLE #Membership(DocumentKey bigint NOT NULL,ScopeResourceKey bigint NOT NULL,
 DisplayName nvarchar(max) NOT NULL,Locator nvarchar(max) NOT NULL,NodeKey nvarchar(max) NOT NULL,
 ParentNodeKey nvarchar(max) NOT NULL,SortOrdinal bigint NOT NULL,PRIMARY KEY(DocumentKey,ScopeResourceKey));
CREATE TABLE #Locators(DocumentKey bigint NOT NULL,ScopeResourceKey bigint NOT NULL,Kind int NOT NULL,
 OriginalLocator nvarchar(max) NOT NULL,LocatorHash binary(32) NOT NULL,IsAscii bit NOT NULL);
"""))
        {
            using var cancel = RelationalSession.CancelCommand(staging, ct);
            await staging.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using var membershipTable = DatabaseMembershipTable();
        using var locatorTable = DatabaseLocatorTable();
        foreach (long document in changed)
        {
            foreach (var member in byDocument[document].Memberships)
                membershipTable.Rows.Add(document, member.ScopeResourceKey, member.DisplayName, member.Locator, member.NodeKey, member.ParentNodeKey, member.SortOrdinal);
            foreach (var locator in byDocument[document].Locators)
                locatorTable.Rows.Add(document, locator.ScopeResourceKey, locator.Kind, locator.Locator,
                    ReferenceMetadata.LookupHash(locator.Locator), ReferenceMetadata.IsAscii(locator.Locator));
        }
        await WriteDatabaseStagingAsync(connection, transaction, "#Membership", membershipTable, ct).ConfigureAwait(false);
        await WriteDatabaseStagingAsync(connection, transaction, "#Locators", locatorTable, ct).ConfigureAwait(false);
        await using (var guard = IndexSql.Command(connection, transaction, """
IF EXISTS(SELECT 1 FROM #Membership m LEFT JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 LEFT JOIN surf.Document d ON d.DocumentKey=m.DocumentKey
 WHERE sr.ScopeKey IS NULL OR sr.ScopeKey<>@Scope OR d.DocumentKey IS NULL OR d.Kind NOT IN(1,2)
 OR sr.SnapshotKey IS NULL OR d.SnapshotKey IS NULL OR sr.SnapshotKey<>d.SnapshotKey)
 THROW 51100,'Database document membership has a different typed owner.',1;
IF EXISTS(SELECT 1 FROM #Locators l WHERE l.Kind NOT BETWEEN 0 AND 2 OR NOT EXISTS
 (SELECT 1 FROM #Membership m WHERE m.DocumentKey=l.DocumentKey AND m.ScopeResourceKey=l.ScopeResourceKey))
 THROW 51100,'Database locator has no scoped membership.',1;
"""))
        {
            IndexSql.Add(guard, "@Scope", SqlDbType.BigInt, scope.Context.ScopeKey);
            using var cancel = RelationalSession.CancelCommand(guard, ct);
            await guard.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await IndexSql.MutateHeadAsync(connection, transaction, ct).ConfigureAwait(false);
        await using (var write = IndexSql.Command(connection, transaction, """
DELETE l FROM surf.DocumentLocator l JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=l.ScopeResourceKey
 JOIN @Changed wanted ON wanted.Id=l.DocumentKey WHERE sr.ScopeKey=@Scope;
DELETE m FROM surf.ResourceDocument m JOIN surf.ScopeResource sr ON sr.ScopeResourceKey=m.ScopeResourceKey
 JOIN @Changed wanted ON wanted.Id=m.DocumentKey WHERE sr.ScopeKey=@Scope;
INSERT surf.ResourceDocument(ScopeResourceKey,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal)
 SELECT ScopeResourceKey,DocumentKey,DisplayName,Locator,NodeKey,ParentNodeKey,SortOrdinal FROM #Membership;
INSERT surf.DocumentLocator(DocumentKey,ScopeResourceKey,Kind,OriginalLocator,LocatorHash,IsAscii)
 SELECT DocumentKey,ScopeResourceKey,Kind,OriginalLocator,LocatorHash,IsAscii FROM #Locators;
UPDATE surf.ScopeIndexState SET ReconciledScopeVersion=NULL,ReconciledAtUtc=NULL WHERE ScopeKey=@Scope;
DROP TABLE #Locators;
DROP TABLE #Membership;
"""))
        {
            IndexSql.Keys(write, "@Changed", changed);
            IndexSql.Add(write, "@Scope", SqlDbType.BigInt, scope.Context.ScopeKey);
            using var cancel = RelationalSession.CancelCommand(write, ct);
            await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static IEnumerable<IndexLocator> OrderedDatabaseLocators(IEnumerable<IndexLocator> locators) =>
        locators.OrderBy(l => l.ScopeResourceKey).ThenBy(l => l.Kind).ThenBy(l => l.Locator, StringComparer.Ordinal);

    private static string DatabaseMetadataString(SqlDataReader reader, int ordinal)
    {
        string value = reader.GetString(ordinal);
        if (value.Length > 65536) throw new ExplorerLimitException("Existing database membership metadata exceeds its character limit.");
        return value;
    }

    private static DataTable DatabaseMembershipTable()
    {
        var table = new DataTable();
        table.Columns.Add("DocumentKey", typeof(long)); table.Columns.Add("ScopeResourceKey", typeof(long));
        table.Columns.Add("DisplayName", typeof(string)); table.Columns.Add("Locator", typeof(string));
        table.Columns.Add("NodeKey", typeof(string)); table.Columns.Add("ParentNodeKey", typeof(string)); table.Columns.Add("SortOrdinal", typeof(long));
        return table;
    }

    private static DataTable DatabaseLocatorTable()
    {
        var table = new DataTable();
        table.Columns.Add("DocumentKey", typeof(long)); table.Columns.Add("ScopeResourceKey", typeof(long)); table.Columns.Add("Kind", typeof(int));
        table.Columns.Add("OriginalLocator", typeof(string)); table.Columns.Add("LocatorHash", typeof(byte[])); table.Columns.Add("IsAscii", typeof(bool));
        return table;
    }

    private static async Task WriteDatabaseStagingAsync(SqlConnection connection, SqlTransaction transaction,
        string destination, DataTable table, CancellationToken ct)
    {
        if (table.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls, transaction)
        { DestinationTableName = destination, BatchSize = table.Rows.Count, BulkCopyTimeout = 30 };
        foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }
}

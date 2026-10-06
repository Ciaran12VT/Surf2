using System.Data;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.State;
using static Surf2.Storage.Relational.Snapshots.SnapshotSql;

namespace Surf2.Storage.Relational.Snapshots
{
    public sealed class SnapshotPublicationOutcomeUnknownException(long snapshotKey, Guid publication, Exception cause)
        : InvalidOperationException("Snapshot publication outcome is unknown. Reload the selected snapshot and scope before retrying.", cause)
    {
        public long SnapshotKey { get; } = snapshotKey;
        public Guid PublicationId { get; } = publication;
    }
    public sealed class SnapshotRuntimeToken
    {
        private readonly byte[] _version;
        public SnapshotRuntimeToken(Guid epoch, long snapshotKey, byte[] version)
        {
            if (epoch == Guid.Empty || snapshotKey < 1 || version.Length != 8) throw new ArgumentException("Invalid snapshot expectation.");
            Epoch = epoch; SnapshotKey = snapshotKey; _version = version.ToArray();
        }
        public Guid Epoch { get; }
        public long SnapshotKey { get; }
        public byte[] Version => _version.ToArray();
    }

    internal sealed record RuntimeSnapshotResource(long ResourceKey, long RevisionKey, DatabaseVersionedResourceKind Kind,
        string SchemaName, string Name, long SortOrdinal);
    internal sealed class RuntimeSnapshotPlan
    {
        public required Guid Epoch { get; init; }
        public required long SnapshotKey { get; init; }
        public required byte[] ExpectedVersion { get; init; }
        public required bool IsNew { get; init; }
        public required SnapshotHeader Header { get; init; }
        public List<RuntimeSnapshotResource> Previous { get; } = [];
        public List<RuntimeSnapshotResource> Resources { get; } = [];
        public List<long> Columns { get; } = [];
        public List<long> PrimaryKeys { get; } = [];
        public List<string> FullDataSelections { get; } = [];
        private Dictionary<(DatabaseVersionedResourceKind, string, string), RuntimeSnapshotResource>? _previousNames;
        public RuntimeSnapshotResource? FindPrevious(DatabaseVersionedResourceKind kind, string schema, string name)
        {
            _previousNames ??= Previous.ToDictionary(x => (x.Kind, x.SchemaName, x.Name), RuntimeSnapshotNameComparer.Instance);
            return _previousNames.GetValueOrDefault((kind, schema, name));
        }
    }
    internal sealed class RuntimeSnapshotNameComparer : IEqualityComparer<(DatabaseVersionedResourceKind Kind, string Schema, string Name)>
    {
        public static RuntimeSnapshotNameComparer Instance { get; } = new();
        public bool Equals((DatabaseVersionedResourceKind Kind, string Schema, string Name) a, (DatabaseVersionedResourceKind Kind, string Schema, string Name) b) =>
            a.Kind == b.Kind && StringComparer.OrdinalIgnoreCase.Equals(a.Schema, b.Schema) && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);
        public int GetHashCode((DatabaseVersionedResourceKind Kind, string Schema, string Name) value) =>
            HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
    internal sealed record RuntimeSnapshotPublication(SnapshotSummary Snapshot, StateToken? ScopeToken, long VersionKey);

    public sealed partial class RelationalSnapshotStore
    {
        internal async Task<T> StageRuntimeUnitAsync<T>(Func<RelationalSnapshotWriter, CancellationToken, Task<T>> write, CancellationToken ct)
        {
            session.RejectValidationWrite();
            await using var connection = await Open(ct).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var value = await write(new(connection, transaction, content), ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return value;
        }

        internal async Task<RuntimeSnapshotPlan> BeginRuntimeStageAsync(SnapshotRuntimeToken? expected,
            string displayName, string databaseName, DateTimeOffset importedAt, CancellationToken ct)
        {
            if (expected != null && expected.Epoch != Epoch) throw new ArgumentException("Snapshot expectation belongs to another epoch.");
            if (expected != null)
            {
                var current = await GetSnapshotAsync(expected.SnapshotKey, ct).ConfigureAwait(false) ?? throw new SnapshotConcurrencyException();
                if (!current.RowVersion.AsSpan().SequenceEqual(expected.Version)) throw new SnapshotConcurrencyException();
                return new() { Epoch = Epoch, SnapshotKey = current.SnapshotKey, ExpectedVersion = expected.Version,
                    IsNew = false, Header = new(current.SnapshotId, displayName, databaseName, importedAt, current.SortOrdinal) };
            }
            string id = Guid.NewGuid().ToString("N");
            var header = new SnapshotHeader(id, displayName, databaseName, importedAt, 0);
            return await StageRuntimeUnitAsync(async (writer, token) =>
            {
                long key = await writer.CreateSnapshotAsync(header, token).ConfigureAwait(false);
                var version = (byte[])(await ScalarAsync(writer.Connection, writer.Transaction,
                    "SELECT RowVersion FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Key;", token, Key("@Key", key)).ConfigureAwait(false))!;
                return new RuntimeSnapshotPlan { Epoch = Epoch, SnapshotKey = key, ExpectedVersion = version,
                    IsNew = true, Header = header };
            }, ct).ConfigureAwait(false);
        }

        internal async Task<long> RuntimeResourceAsync(RelationalSnapshotWriter writer, RuntimeSnapshotPlan plan,
            DatabaseVersionedResourceKind kind, string schema, string name, long ordinal, CancellationToken ct)
        {
            if (plan.Epoch != Epoch) throw new ArgumentException("Staging plan belongs to another epoch.");
            var previous = plan.FindPrevious(kind, schema, name);
            if (previous != null) return previous.ResourceKey;
            long? retained = null;
            await using (var command = Command(writer.Connection, writer.Transaction, """
                SELECT TOP(129) CONVERT(bigint,256)+DATALENGTH(r.SchemaName)+DATALENGTH(r.ObjectName),r.ResourceKey,r.SchemaName,r.ObjectName FROM surf.SnapshotResource r
                WHERE r.SnapshotKey=@Snapshot AND r.Kind=@Kind AND r.CurrentRevisionKey IS NULL
                    AND r.SchemaName=@Schema AND r.ObjectName=@Name
                    AND EXISTS(SELECT 1 FROM surf.SnapshotChange c WHERE c.ResourceKey=r.ResourceKey AND c.SnapshotKey=r.SnapshotKey)
                ORDER BY r.ResourceKey;
                """, Key("@Snapshot", plan.SnapshotKey), Int("@Kind", (int)kind),
                Text("@Schema", schema), Text("@Name", name)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                int candidates = 0;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (++candidates > 128) throw new InvalidOperationException("Retained historical identity lookup exceeds its candidate budget.");
                    SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(schema, reader.GetString(2)) ||
                        !StringComparer.OrdinalIgnoreCase.Equals(name, reader.GetString(3))) continue;
                    if (retained != null) throw new InvalidOperationException("Ambiguous retained historical resource identity.");
                    retained = reader.GetInt64(1);
                }
            }
            return retained ?? await writer.CreateResourceAsync(plan.SnapshotKey, kind,
                schema, name, SnapshotIdentity.LegacyResourceKey(kind, schema, name), ordinal, ct).ConfigureAwait(false);
        }

        internal async Task<SqlColumn> ReadRuntimeColumnAsync(long snapshotKey, long childKey, CancellationToken ct)
        {
            await using var connection = await Open(ct).ConfigureAwait(false);
            await using var command = Command(connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ColumnName)+DATALENGTH(DataType),
                    SchemaName,TableName,ColumnName,DataType,MaxLength,NumericPrecision,NumericScale,IsNullable,IsIdentity,SourceOrdinal
                FROM surf.TableColumnRevision WHERE SnapshotKey=@Snapshot AND ColumnRevisionKey=@Child;
                """, Key("@Snapshot", snapshotKey), Key("@Child", childKey));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Selected source column owner not found.");
            SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
            return new() { SchemaName=reader.GetString(1),TableName=reader.GetString(2),ColumnName=reader.GetString(3),DataType=reader.GetString(4),
                MaxLength=reader.GetInt32(5),NumericPrecision=reader.GetByte(6),NumericScale=reader.GetInt32(7),IsNullable=reader.GetBoolean(8),
                IsIdentity=reader.GetBoolean(9),Ordinal=reader.GetInt32(10) };
        }
        internal async Task<SqlPrimaryKeyColumn> ReadRuntimeKeyAsync(long snapshotKey, long childKey, CancellationToken ct)
        {
            await using var connection = await Open(ct).ConfigureAwait(false);
            await using var command = Command(connection, null, """
                SELECT CONVERT(bigint,256)+DATALENGTH(SchemaName)+DATALENGTH(TableName)+DATALENGTH(ConstraintName)+DATALENGTH(ColumnName),
                    SchemaName,TableName,ConstraintName,ColumnName,KeyOrdinal
                FROM surf.PrimaryKeyColumn WHERE SnapshotKey=@Snapshot AND KeyColumnKey=@Child;
                """, Key("@Snapshot", snapshotKey), Key("@Child", childKey));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new KeyNotFoundException("Selected source key owner not found.");
            SnapshotReadGuard.RequireBytes(reader.GetInt64(0), MaximumMetadataPageBytes, command);
            return new() { SchemaName=reader.GetString(1),TableName=reader.GetString(2),ConstraintName=reader.GetString(3),ColumnName=reader.GetString(4),
                KeyOrdinal=reader.GetInt32(5) };
        }

        internal async Task<RuntimeSnapshotPublication> PublishRuntimeStageAsync(RuntimeSnapshotPlan plan, Scope? membership,
            StateToken? expectedScope, Guid publication, string? versionName, CancellationToken ct)
        {
            session.RejectValidationWrite();
            if (plan.Epoch != Epoch || publication == Guid.Empty || (membership == null) != (expectedScope == null))
                throw new ArgumentException("Invalid staging publication owner/epoch.");
            if (plan.Resources.Count > 100_000 || plan.Previous.Count > 100_000 || plan.Columns.Count > 100_000 ||
                plan.PrimaryKeys.Count > 100_000 || plan.FullDataSelections.Count > 100_000)
                throw new InvalidOperationException("Snapshot publication exceeds its metadata unit budget.");
            if (plan.Resources.Select(x => x.ResourceKey).Distinct().Count() != plan.Resources.Count ||
                plan.Resources.Select(x => (x.Kind, x.SchemaName, x.Name)).Distinct(RuntimeSnapshotNameComparer.Instance).Count() != plan.Resources.Count)
                throw new InvalidOperationException("Ambiguous staged resource identities cannot be published implicitly.");
            if (plan.Resources.Any(x => x.SchemaName.Contains('.', StringComparison.Ordinal)))
                throw new InvalidOperationException("A dotted schema name cannot be represented losslessly by the current reverse-history identity format.");
            await using var connection = await Open(ct).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await ScalarAsync(connection, transaction,
                "UPDATE surf.SnapshotCatalogueHead SET UserKey=UserKey WHERE UserKey=1;", ct).ConfigureAwait(false);
            var state = new RelationalStateStore(session);
            if (expectedScope != null) await state.RequireSnapshotScopeAsync(connection, transaction, expectedScope, publication, ct).ConfigureAwait(false);
            object? head = await ScalarAsync(connection, transaction, """
                SELECT RowVersion FROM surf.DatabaseSnapshot WITH(UPDLOCK,HOLDLOCK)
                WHERE SnapshotKey=@Snapshot AND IsPublished=@Published;
                """, ct, Key("@Snapshot", plan.SnapshotKey), Bit("@Published", !plan.IsNew)).ConfigureAwait(false);
            if (head is not byte[] bytes || !bytes.AsSpan().SequenceEqual(plan.ExpectedVersion)) throw new SnapshotConcurrencyException();
            if (!plan.IsNew && expectedScope != null && (long)(await ScalarAsync(connection, transaction,
                "SELECT COUNT_BIG(*) FROM surf.ScopeResource WHERE ScopeKey=@Scope AND SnapshotKey=@Snapshot;", ct,
                Key("@Scope", expectedScope.Key), Key("@Snapshot", plan.SnapshotKey)).ConfigureAwait(false))! == 0)
                throw new StateConflictException("replacement snapshot membership");
            var writer = new RelationalSnapshotWriter(connection, transaction, content, plan.SnapshotKey);
            long history; byte[] historyVersion; int next; long versionOrdinal;
            await using (var command = Command(connection, transaction, """
                SELECT TOP(1) HistoryKey,NextVersionNumber,RowVersion FROM surf.SnapshotHistory WITH(UPDLOCK,HOLDLOCK)
                WHERE SnapshotKey=@Snapshot ORDER BY SortOrdinal,HistoryKey;
                """, Key("@Snapshot", plan.SnapshotKey)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                { history = reader.GetInt64(0); next = reader.GetInt32(1); historyVersion = (byte[])reader.GetValue(2); }
                else { history = 0; next = 1; historyVersion = []; }
            }
            if (history == 0)
            {
                history = await writer.InsertHistoryAsync(plan.SnapshotKey, plan.Header.SnapshotId, plan.IsNew ? 2 : 3, 0, ct).ConfigureAwait(false);
                if (!plan.IsNew)
                {
                    var imported = (DateTimeOffset)(await ScalarAsync(connection, transaction,
                        "SELECT ImportedAtUtc FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Snapshot;", ct, Key("@Snapshot", plan.SnapshotKey)).ConfigureAwait(false))!;
                    await writer.InsertVersionAsync(history, new(Guid.NewGuid().ToString("N"), "V1", 1, imported, true, 0), ct).ConfigureAwait(false);
                }
                next = plan.IsNew ? 1 : 2;
            }
            await using (var command = Command(connection, transaction, """
                SELECT COALESCE(MAX(VersionNumber),0),COALESCE(MAX(SortOrdinal),CONVERT(bigint,-1))
                FROM surf.SnapshotVersion WHERE HistoryKey=@History;
                """, Key("@History", history)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                next = Math.Max(next, checked(reader.GetInt32(0) + 1)); versionOrdinal = checked(reader.GetInt64(1) + 1);
            }
            long version = await writer.InsertVersionAsync(history, new(Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(versionName) ? "V" + next : versionName, next, plan.Header.ImportedAtUtc, plan.IsNew, versionOrdinal), ct).ConfigureAwait(false);
            if (!plan.IsNew)
            {
                long order = 0;
                var previousKeys = plan.Previous.Select(x => x.ResourceKey).ToHashSet();
                var desiredKeys = plan.Resources.Select(x => x.ResourceKey).ToHashSet();
                foreach (var value in plan.Resources.Where(x => !previousKeys.Contains(x.ResourceKey)))
                    await RuntimeChangeAsync(writer, version, value, DatabaseSnapshotResourceChangeKind.Added, null, order++, ct).ConfigureAwait(false);
                // Metadata reversals remove companion data. Restore all previous data LAST.
                foreach (var value in plan.Previous.OrderBy(x => x.Kind == DatabaseVersionedResourceKind.TableData ? 2 :
                    x.Kind == DatabaseVersionedResourceKind.TableMetadata ? 1 : 0).ThenBy(x => x.SortOrdinal))
                    await RuntimeChangeAsync(writer, version, value,
                        desiredKeys.Contains(value.ResourceKey) ? DatabaseSnapshotResourceChangeKind.Modified : DatabaseSnapshotResourceChangeKind.Deleted,
                        value.RevisionKey, order++, ct).ConfigureAwait(false);
            }
            await ScalarAsync(connection, transaction, """
                UPDATE surf.SnapshotResource SET CurrentRevisionKey=NULL WHERE SnapshotKey=@Snapshot AND CurrentRevisionKey IS NOT NULL;
                DELETE FROM surf.SnapshotCurrentColumn WHERE SnapshotKey=@Snapshot;
                DELETE FROM surf.SnapshotCurrentPrimaryKey WHERE SnapshotKey=@Snapshot;
                DELETE FROM surf.FullDataTableSelection WHERE SnapshotKey=@Snapshot;
                """, ct, Key("@Snapshot", plan.SnapshotKey)).ConfigureAwait(false);
            foreach (var value in plan.Resources)
                await writer.SetCurrentRevisionAsync(value.ResourceKey, value.RevisionKey, value.SortOrdinal, ct: ct).ConfigureAwait(false);
            for (int i = 0; i < plan.Columns.Count; i++)
                await ScalarAsync(connection, transaction, """
                    INSERT surf.SnapshotCurrentColumn(SnapshotKey,ColumnRevisionKey,SortOrdinal) VALUES(@Snapshot,@Child,@Order);
                    """, ct, Key("@Snapshot", plan.SnapshotKey), Key("@Child", plan.Columns[i]), Key("@Order", i)).ConfigureAwait(false);
            for (int i = 0; i < plan.PrimaryKeys.Count; i++)
                await ScalarAsync(connection, transaction, """
                    INSERT surf.SnapshotCurrentPrimaryKey(SnapshotKey,KeyColumnKey,SortOrdinal) VALUES(@Snapshot,@Child,@Order);
                    """, ct, Key("@Snapshot", plan.SnapshotKey), Key("@Child", plan.PrimaryKeys[i]), Key("@Order", i)).ConfigureAwait(false);
            for (int i = 0; i < plan.FullDataSelections.Count; i++)
                await writer.InsertFullDataSelectionAsync(plan.SnapshotKey, plan.FullDataSelections[i], i, ct).ConfigureAwait(false);
            long ordinal = plan.Header.SortOrdinal;
            if (plan.IsNew) ordinal = checked((long)(await ScalarAsync(connection, transaction,
                "SELECT COALESCE(MAX(SortOrdinal),CONVERT(bigint,-1)) FROM surf.DatabaseSnapshot WHERE IsPublished=1;", ct).ConfigureAwait(false))! + 1);
            await writer.UpdateSnapshotHeaderAsync(plan.SnapshotKey, plan.Header.DisplayName, plan.Header.DatabaseName,
                plan.Header.ImportedAtUtc, ordinal, ct).ConfigureAwait(false);
            if (historyVersion.Length == 8) await writer.SetHistoryNextVersionAsync(history, checked(next + 1), historyVersion, ct).ConfigureAwait(false);
            else await ScalarAsync(connection, transaction, "UPDATE surf.SnapshotHistory SET NextVersionNumber=@Next WHERE HistoryKey=@History;",
                ct, Key("@History", history), Int("@Next", checked(next + 1))).ConfigureAwait(false);
            await writer.PublishSnapshotAsync(plan.SnapshotKey, version, ct).ConfigureAwait(false);
            StateToken? scopeToken = expectedScope;
            if (plan.IsNew && expectedScope != null)
                scopeToken = await state.PublishSnapshotMembershipAsync(connection, transaction, membership!, expectedScope,
                    plan.SnapshotKey, plan.Header.SnapshotId, publication, ct).ConfigureAwait(false);
            Guid publicId; byte[] publishedVersion;
            await using (var command = Command(connection, transaction,
                "SELECT PublicId,RowVersion FROM surf.DatabaseSnapshot WHERE SnapshotKey=@Snapshot;", Key("@Snapshot", plan.SnapshotKey)))
            {
                using var cancel = RelationalSession.CancelCommand(command, ct);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                publicId = reader.GetGuid(0); publishedVersion = (byte[])reader.GetValue(1);
            }
            var summary = new SnapshotSummary(plan.SnapshotKey, publicId, plan.Header.SnapshotId, plan.Header.DisplayName,
                plan.Header.DatabaseName, plan.Header.ImportedAtUtc, ordinal, version, publishedVersion);
            try { await transaction.CommitAsync(ct).ConfigureAwait(false); }
            catch (Exception error) when (error is SqlException or OperationCanceledException or System.IO.IOException)
            { throw new SnapshotPublicationOutcomeUnknownException(plan.SnapshotKey, publication, error); }
            return new(summary, scopeToken, version);
        }

        private static Task<long> RuntimeChangeAsync(RelationalSnapshotWriter writer, long version, RuntimeSnapshotResource value,
            DatabaseSnapshotResourceChangeKind change, long? previous, long order, CancellationToken ct) => writer.InsertChangeAsync(version,
                value.ResourceKey, new(value.Kind, change, SnapshotIdentity.LegacyResourceKey(value.Kind, value.SchemaName, value.Name),
                    SqlName.FormatPlainMultipartName(value.SchemaName, value.Name), RuntimeRelativePath(value), order, previous), ct);
        internal static string RuntimeRelativePath(RuntimeSnapshotResource value) =>
            value.Kind + "/" + SqlName.FormatPlainMultipartName(value.SchemaName, value.Name) +
            (value.Kind == DatabaseVersionedResourceKind.TableData ? ".csv" : ".sql");
    }
}

namespace Surf2.Storage.Relational.State
{
    public sealed partial class RelationalStateStore
    {
        internal async Task RequireSnapshotScopeAsync(SqlConnection c, SqlTransaction t, StateToken expected, Guid publication, CancellationToken ct)
        {
            Expected(expected, publication);
            // Scope saves acquire this catalogue lock before their selected scope row.
            await ExecuteAsync(c, t, """
                SELECT Kind FROM surf.StateCatalogueGeneration WITH(UPDLOCK,HOLDLOCK)
                WHERE ProfileKey=1 AND Kind=0;
                """, ct).ConfigureAwait(false);
            var actual = await TokenAsync(c, t, "Scope", "ScopeKey", expected.Key, ct).ConfigureAwait(false);
            if (!SameRuntimeToken(actual, expected)) throw new StateConflictException("capture scope");
        }
        internal async Task<StateToken> PublishSnapshotMembershipAsync(SqlConnection c, SqlTransaction t, Scope membership,
            StateToken expected, long snapshotKey, string snapshotId, Guid publication, CancellationToken ct)
        {
            var copy = StateCopies.Scope(membership, new(_limits));
            var matches = copy.Resources.Select((value, ordinal) => (value, ordinal)).Where(x => x.value.Kind == ResourceKind.DatabaseSnapshot &&
                string.Equals(x.value.Path, snapshotId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ArgumentException("A new capture requires exactly one explicit selected scope membership.");
            await BumpAsync(c, t, 0, publication, ct).ConfigureAwait(false);
            await UpdateAsync(c, t, StateMaps.Scope, copy, expected, publication, ct).ConfigureAwait(false);
            await ReplaceScopeChildrenAsync(c, t, expected.Key, copy, ct).ConfigureAwait(false);
            await ExecuteAsync(c, t, "UPDATE surf.ScopeResource SET SnapshotKey=@Snapshot WHERE ScopeKey=@Key AND SortOrdinal=@Ordinal;", ct,
                Key(expected.Key), RelationalSession.Parameter("@Snapshot", SqlDbType.BigInt, snapshotKey),
                RelationalSession.Parameter("@Ordinal", SqlDbType.BigInt, matches[0].ordinal)).ConfigureAwait(false);
            return await TokenAsync(c, t, "Scope", "ScopeKey", expected.Key, ct).ConfigureAwait(false);
        }
    }
}

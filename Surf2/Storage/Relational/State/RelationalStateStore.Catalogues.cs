using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.State;

public sealed partial class RelationalStateStore
{
    public Task<StatePage<ScopeSummary>> ListScopesAsync(int pageSize = 100, StateCursor? after = null,
        CancellationToken cancellationToken = default) => ReadyAsync(async (connection, transaction) =>
    {
        var generation = await CatalogueGenerationAsync(connection, transaction, 0, pageSize, after, cancellationToken);
        return await PageAsync(connection, transaction, """
SELECT TOP (@Take) s.ScopeKey, s.SortOrdinal, s.Version, s.PublicationId,
       CONVERT(bigint, DATALENGTH(s.ScopeId)), s.ScopeId, CONVERT(bigint, DATALENGTH(s.Name)), s.Name
FROM surf.Scope s WHERE s.ProfileKey=1
  AND (s.SortOrdinal>@Ordinal OR (s.SortOrdinal=@Ordinal AND s.ScopeKey>@Key))
ORDER BY s.SortOrdinal, s.ScopeKey;
""", pageSize, after, generation, async (r, token, ordinal, budget, ct) =>
            new ScopeSummary(token, ordinal, (await StringAsync(r,4,budget,ct))!, (await StringAsync(r,6,budget,ct))!), cancellationToken);
    }, cancellationToken);

    public Task<StatePage<DiagramSummary>> ListDiagramsAsync(int pageSize = 100, StateCursor? after = null,
        CancellationToken cancellationToken = default) => ReadyAsync(async (connection, transaction) =>
    {
        var generation = await CatalogueGenerationAsync(connection, transaction, 1, pageSize, after, cancellationToken);
        return await PageAsync(connection, transaction, """
SELECT TOP (@Take) s.DiagramKey, s.SortOrdinal, s.Version, s.PublicationId,
       CONVERT(bigint, DATALENGTH(s.DiagramId)), s.DiagramId, CONVERT(bigint, DATALENGTH(s.Name)), s.Name,
       r.DiagramRevisionKey, r.CreatedAtUtc, r.UpdatedAtUtc
FROM surf.Diagram s LEFT JOIN surf.DiagramRevision r ON r.DiagramRevisionKey=s.CurrentRevisionKey AND r.DiagramKey=s.DiagramKey
WHERE s.ProfileKey=1 AND (s.SortOrdinal>@Ordinal OR (s.SortOrdinal=@Ordinal AND s.DiagramKey>@Key))
ORDER BY s.SortOrdinal, s.DiagramKey;
""", pageSize, after, generation, async (r, token, ordinal, budget, ct) =>
            new DiagramSummary(token, ordinal, (await StringAsync(r,4,budget,ct))!, (await StringAsync(r,6,budget,ct))!,
                r.GetInt64(8), r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10)), cancellationToken);
    }, cancellationToken);

    public Task<StatePage<WorkbenchSummary>> ListWorkbenchesAsync(int pageSize = 100, StateCursor? after = null,
        CancellationToken cancellationToken = default) => ReadyAsync(async (connection, transaction) =>
    {
        var generation = await CatalogueGenerationAsync(connection, transaction, 2, pageSize, after, cancellationToken);
        return await PageAsync(connection, transaction, """
SELECT TOP (@Take) s.WorkbenchKey, s.SortOrdinal, s.Version, s.PublicationId,
       CONVERT(bigint, DATALENGTH(s.WorkbenchId)), s.WorkbenchId, CONVERT(bigint, DATALENGTH(s.Name)), s.Name,
       CONVERT(bigint, DATALENGTH(s.ScopeId)), s.ScopeId, CONVERT(bigint, DATALENGTH(s.ScopeName)), s.ScopeName, s.IsDefaultForScope,
       s.CreatedAtUtc, s.UpdatedAtUtc, s.SavedAtUtc, s.EmbeddedDiagramRevisionKey
FROM surf.Workbench s WHERE s.ProfileKey=1
  AND (s.SortOrdinal>@Ordinal OR (s.SortOrdinal=@Ordinal AND s.WorkbenchKey>@Key))
ORDER BY s.SortOrdinal, s.WorkbenchKey;
""", pageSize, after, generation, async (r, token, ordinal, budget, ct) =>
            new WorkbenchSummary(token, ordinal, (await StringAsync(r,4,budget,ct))!, (await StringAsync(r,6,budget,ct))!,
                (await StringAsync(r,8,budget,ct))!, (await StringAsync(r,10,budget,ct))!, r.GetBoolean(12),
                r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14), r.GetFieldValue<DateTimeOffset>(15),
                r.IsDBNull(16) ? null : r.GetInt64(16)), cancellationToken);
    }, cancellationToken);

    private async Task<byte[]> CatalogueGenerationAsync(SqlConnection connection, SqlTransaction transaction,
        int kind, int pageSize, StateCursor? after, CancellationToken ct)
    {
        if (pageSize < 1 || pageSize > _limits.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (after != null && (after.Epoch != _session.Epoch || after.SortOrdinal < 0 || after.Key < 1))
            throw new ArgumentException("Invalid catalogue cursor.");
        await using var command = Command(connection, transaction,
            "SELECT Version FROM surf.StateCatalogueGeneration WHERE ProfileKey=1 AND Kind=@Kind;",
            RelationalSession.Parameter("@Kind", SqlDbType.Int, kind));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        byte[] version = (await command.ExecuteScalarAsync(ct)) as byte[] ?? [];
        if (after != null && !version.AsSpan().SequenceEqual(after.Generation))
            throw new StateConflictException("catalogue generation");
        return version;
    }

    private async Task<StatePage<T>> PageAsync<T>(SqlConnection connection, SqlTransaction transaction, string sql,
        int pageSize, StateCursor? after, byte[] generation,
        Func<SqlDataReader, StateToken, long, StateBudget, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql,
            Key((long)pageSize + 1, "@Take"), Key(after?.SortOrdinal ?? -1, "@Ordinal"), Key(after?.Key ?? 0));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var items = new List<T>();
        var budget = new StateBudget(_limits);
        StateCursor? last = null;
        while (await reader.ReadAsync(ct))
        {
            if (items.Count == pageSize) return new(items, last);
            budget.Row();
            long key = reader.GetInt64(0);
            long ordinal = reader.GetInt64(1);
            var token = new StateToken(key, _session.Epoch, (byte[])reader.GetValue(2), reader.GetGuid(3));
            items.Add(await read(reader, token, ordinal, budget, ct));
            last = new(_session.Epoch, ordinal, key, generation.ToArray());
        }
        return new(items, null);
    }

    // Startup can fetch preferences without any image-definition data or asset bytes.
    public Task<PreferenceSummary?> ReadPreferenceSummaryAsync(CancellationToken cancellationToken = default) =>
        ReadyAsync(async (connection, transaction) =>
        {
            var state = await HeadAsync(connection, transaction, StateMaps.Settings, 1,
                new StateBudget(_limits), cancellationToken);
            if (state == null) return null;
            var v = state.Value;
            return new PreferenceSummary(state.Token, v.Appearance.Theme, v.LoadMostRecentWorkbenchOnStartup,
                v.ResourceComparison.IgnoreWhitespaceByDefault, v.ResourceComparison.IgnoreCaseByDefault,
                v.Diagnostics.EnableInternalLogging, v.CodeWindows.DefaultBackcolor, v.KeyboardShortcuts);
        }, cancellationToken);
}

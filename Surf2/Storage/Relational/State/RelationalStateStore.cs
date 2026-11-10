using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.State;

/// <summary>Selected, bounded state only. Runtime access requires Ready; imports use a caller-owned transaction.</summary>
public sealed partial class RelationalStateStore
{
    private readonly RelationalSession _session;
    private readonly RelationalContentStore _content;
    private readonly StateLimits _limits;

    public RelationalStateStore(RelationalSession session, RelationalContentStore? content = null, StateLimits? limits = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _content = content ?? new RelationalContentStore();
        _limits = limits ?? new StateLimits();
        _limits.Validate();
    }

    private async Task<T> ReadyAsync<T>(Func<SqlConnection, SqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        await _session.RequireReadyAsync(ct);
        await using var connection = await _session.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        T result = await operation(connection, transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    private void ImportArguments(SqlConnection connection, SqlTransaction transaction, Guid publicationId, long ordinal = 0)
    {
        _session.RejectValidationWrite();
        if (transaction.Connection != connection || connection.State != ConnectionState.Open)
            throw new ArgumentException("Imports require an open connection and its active caller-owned transaction.");
        if (publicationId == Guid.Empty || ordinal < 0) throw new ArgumentException("Invalid publication identity or ordinal.");
    }

    private void Expected(StateToken token, Guid publicationId)
    {
        _session.RejectValidationWrite();
        if (token.Epoch != _session.Epoch) throw new ArgumentException("The state token belongs to a different connection epoch.");
        if (publicationId == Guid.Empty) throw new ArgumentException("A publication identity is required.");
    }

    private async Task<T> ImportUnitAsync<T>(SqlConnection connection, SqlTransaction transaction,
        Func<Task<T>> operation, CancellationToken ct)
    {
        string savepoint = "State_" + Guid.NewGuid().ToString("N")[..24];
        await ExecuteAsync(connection, transaction, $"SAVE TRANSACTION [{savepoint}];", ct);
        try { return await operation(); }
        catch (Exception error)
        {
            try { await ExecuteAsync(connection, transaction, $"ROLLBACK TRANSACTION [{savepoint}];", CancellationToken.None); }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException("The state import failed and requires full caller transaction rollback.",
                    new AggregateException(error, rollbackError));
            }
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql,
        params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }
    private static SqlParameter Key(long value, string name = "@Key") => RelationalSession.Parameter(name, SqlDbType.BigInt, value);
    private static SqlParameter Text(string? value, string name) => RelationalSession.Parameter(name, SqlDbType.NVarChar, value, -1);
    private static SqlParameter Publication(Guid value) => RelationalSession.Parameter("@Publication", SqlDbType.UniqueIdentifier, value);

    private async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql,
        CancellationToken ct, params SqlParameter[] parameters)
    {
        _session.RejectValidationWrite();
        await using var command = Command(connection, transaction, sql, parameters);
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> InsertAsync<T>(SqlConnection connection, SqlTransaction transaction, StateRowMap<T> map,
        T value, CancellationToken ct, params (string Name, SqlDbType Type, object? Value)[] extra) where T : new()
    {
        _session.RejectValidationWrite();
        string names = string.Join(", ", extra.Select(p => $"[{p.Name}]"));
        string values = string.Join(", ", extra.Select((_, i) => $"@e{i}"));
        await using var command = Command(connection, transaction,
            $"INSERT surf.[{map.Table}] ({map.Columns}{(extra.Length == 0 ? "" : ", " + names)}) " +
            $"OUTPUT INSERTED.[{map.Key}] VALUES ({map.Values}{(extra.Length == 0 ? "" : ", " + values)});");
        await map.AddParametersAsync(command, value, _content, connection, transaction, ct);
        for (int i = 0; i < extra.Length; i++)
            command.Parameters.Add(RelationalSession.Parameter($"@e{i}", extra[i].Type, extra[i].Value,
                extra[i].Type == SqlDbType.NVarChar ? -1 : 0));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        return (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidDataException("No state identity was returned."));
    }

    private async Task UpdateAsync<T>(SqlConnection connection, SqlTransaction transaction, StateRowMap<T> map,
        T value, StateToken expected, Guid publication, CancellationToken ct) where T : new()
    {
        await using var command = Command(connection, transaction,
            $"UPDATE surf.[{map.Table}] SET {map.Assignments}, PublicationId=@Publication " +
            $"OUTPUT INSERTED.[{map.Key}] WHERE [{map.Key}]=@Key AND ProfileKey=1 AND Version=@Version;",
            Key(expected.Key), Publication(publication), RelationalSession.Parameter("@Version", SqlDbType.Binary, expected.Version, 8));
        await map.AddParametersAsync(command, value, _content, connection, transaction, ct);
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not long) throw new StateConflictException(map.Table);
    }

    private async Task<StateToken> TokenAsync(SqlConnection connection, SqlTransaction transaction, string table,
        string keyColumn, long key, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            $"SELECT Version, PublicationId FROM surf.[{table}] WHERE [{keyColumn}]=@Key AND ProfileKey=1;", Key(key));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("The selected state no longer exists.");
        return new(key, _session.Epoch, (byte[])reader.GetValue(0), reader.GetGuid(1));
    }

    private async Task<SelectedState<T>?> HeadAsync<T>(SqlConnection connection, SqlTransaction transaction,
        StateRowMap<T> map, long? key, StateBudget budget, CancellationToken ct) where T : new()
    {
        await using var command = Command(connection, transaction,
            $"SELECT s.[{map.Key}], s.Version, s.PublicationId, {map.Projection()} FROM surf.[{map.Table}] s " +
            $"WHERE s.ProfileKey=1{(key.HasValue ? $" AND s.[{map.Key}]=@Key" : "")};",
            key.HasValue ? [Key(key.Value)] : []);
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        if (!await reader.ReadAsync(ct)) return null;
        var token = new StateToken(reader.GetInt64(0), _session.Epoch, (byte[])reader.GetValue(1), reader.GetGuid(2));
        return new(await map.ReadAsync(reader, 3, budget, ct), token);
    }

    private async Task<List<(long Key, T Value)>> ChildrenAsync<T>(SqlConnection connection, SqlTransaction transaction,
        StateRowMap<T> map, string ownerColumn, long ownerKey, StateBudget budget, CancellationToken ct) where T : new()
    {
        await using var command = Command(connection, transaction,
            $"SELECT TOP (@Limit) s.[{map.Key}], {map.Projection()} FROM surf.[{map.Table}] s " +
            $"WHERE s.[{ownerColumn}]=@Key ORDER BY s.SortOrdinal, s.[{map.Key}];",
            Key(ownerKey), RelationalSession.Parameter("@Limit", SqlDbType.BigInt, (long)_limits.MaximumRows + 1));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var result = new List<(long, T)>();
        while (await reader.ReadAsync(ct))
        {
            long key = reader.GetInt64(0);
            result.Add((key, await map.ReadAsync(reader, 1, budget, ct)));
        }
        return result;
    }

    private static Task<string?> StringAsync(SqlDataReader reader, int lengthColumn, StateBudget budget, CancellationToken ct) =>
        StateText.ReadAsync(reader, lengthColumn, budget, ct);

    private async Task BumpAsync(SqlConnection connection, SqlTransaction transaction, int kind,
        Guid publication, CancellationToken ct) => await ExecuteAsync(connection, transaction, """
IF EXISTS (SELECT 1 FROM surf.StateCatalogueGeneration WITH (UPDLOCK, HOLDLOCK) WHERE ProfileKey=1 AND Kind=@Kind)
    UPDATE surf.StateCatalogueGeneration SET PublicationId=@Publication WHERE ProfileKey=1 AND Kind=@Kind;
ELSE
    INSERT surf.StateCatalogueGeneration(ProfileKey, Kind, PublicationId) VALUES (1, @Kind, @Publication);
""", ct, RelationalSession.Parameter("@Kind", SqlDbType.Int, kind), Publication(publication));
}
